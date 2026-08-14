using Google.Protobuf;
using GPConf.McpServer.DataAccess;
using GPConf.Utilities;

namespace GPConf.DiscordBot.Services;

/// <summary>
/// Read-only data access for the bot. Loads gpconf.data via the shared
/// GpConfDataAccess and computes standings/scores via the shared CCUtils —
/// the exact same code the desktop app and MCP server use. The bot never
/// reimplements scoring or eligibility logic.
/// </summary>
public sealed class DataService
{
    private readonly GpConfDataAccess _data = new();

    public MainData Load() => _data.Load();

    public Season? FindSeason(MainData data, string season) =>
        GpConfDataAccess.FindSeason(data, season);

    public Race? FindRace(Season season, string race) =>
        GpConfDataAccess.FindRace(season, race);

    public Driver? FindDriver(Season season, string name) =>
        GpConfDataAccess.FindDriver(season, name);

    /// <summary>The most recent race with at least one completed session result — not
    /// simply the highest-numbered scheduled race, which may be a not-yet-run future round.</summary>
    public Race? LatestRace(Season season) =>
        season.Races
            .Where(r => r.RaceResults.Any(rr => rr.IsComplete))
            .OrderByDescending(r => r.Round)
            .FirstOrDefault();

    /// <summary>The earliest-round race with no race result recorded yet — the only race
    /// /pick-submit ever targets, so picking after an outcome is known is structurally
    /// impossible rather than merely guarded against. Deliberately checks for any recorded
    /// RaceDriverResult rather than RaceResult.IsComplete: some historical races (e.g. Round 1)
    /// have full results stored with IsComplete left False, a pre-existing data-entry quirk this
    /// must not be fooled by — a race with real classifications on record has clearly happened,
    /// regardless of that flag.</summary>
    public Race? NextPickableRace(Season season) =>
        season.Races.OrderBy(r => r.Round).FirstOrDefault(r => !r.RaceResults.Any(rr => rr.Results.Count > 0));

    /// <summary>Championship points per driver through the given race (inclusive).</summary>
    public Dictionary<ByteString, float> ChampionshipPoints(Season season, Race upToRace)
    {
        var points = new Dictionary<ByteString, float>();
        foreach (var race in season.Races.OrderBy(r => r.Round))
        {
            foreach (var rr in race.RaceResults)
                foreach (var dr in rr.Results)
                    if (!dr.DriverId.IsEmpty)
                        points[dr.DriverId] = points.GetValueOrDefault(dr.DriverId) + dr.Points;
            if (race.Id == upToRace.Id) break;
        }
        return points;
    }

    public Dictionary<ByteString, int> ChampionshipPositions(Dictionary<ByteString, float> points)
    {
        var positions = new Dictionary<ByteString, int>();
        int pos = 1;
        foreach (var kv in points.OrderByDescending(kv => kv.Value))
            positions[kv.Key] = pos++;
        return positions;
    }

    public (League? league, GameSeason? gs) FindGameSeason(MainData data, Season season, string? leagueName)
    {
        var leagues = leagueName is not null
            ? data.Leagues.Where(l => l.LeagueName.Equals(leagueName, StringComparison.OrdinalIgnoreCase))
            : data.Leagues.AsEnumerable();

        foreach (var league in leagues)
        {
            var gs = league.Seasons.FirstOrDefault(gs => gs.SeasonId == season.Id);
            if (gs is not null) return (league, gs);
        }
        return (null, null);
    }

    /// <summary>Every league that has this season configured, paired with that season's GameSeason.</summary>
    public List<(League league, GameSeason gs)> LeaguesForSeason(MainData data, Season season) =>
        data.Leagues
            .Select(l => (league: l, gs: l.Seasons.FirstOrDefault(gs => gs.SeasonId == season.Id)))
            .Where(x => x.gs is not null)
            .Select(x => (x.league, gs: x.gs!))
            .ToList();

    public Season? FindSeasonById(MainData data, ByteString id) =>
        data.Seasons.FirstOrDefault(s => s.Id == id);

    public Race? FindRaceById(Season season, ByteString id) =>
        season.Races.FirstOrDefault(r => r.Id == id);

    public League? FindLeagueById(MainData data, ByteString id) =>
        data.Leagues.FirstOrDefault(l => l.Id == id);

    /// <summary>Cumulative player confidence-cup scores through the given race.</summary>
    public Dictionary<ByteString, float> PlayerScores(Season season, GameSeason gs, Race upToRace)
    {
        var rules = gs.PickRules ?? new PickRules();
        var playerScores = gs.ParticipatingPlayers.ToDictionary(p => p.Id, _ => 0f);

        foreach (var race in season.Races.OrderBy(r => r.Round))
        {
            var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == race.Id);
            if (gameRace is not null)
            {
                var prevRace = season.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < race.Round);
                var champPts = prevRace is not null ? ChampionshipPoints(season, prevRace) : [];
                var champPos = ChampionshipPositions(champPts);

                foreach (var player in gs.ParticipatingPlayers)
                {
                    var picks = gameRace.PicksPerPlayer.FirstOrDefault(pp => pp.PlayerId == player.Id);
                    if (picks is null) continue;

                    bool hasResults = race.RaceResults.Any(rr => rr.IsComplete);
                    for (int i = 0; i < picks.DriverId.Count; i++)
                    {
                        var dId = picks.DriverId[i];
                        int pos = champPos.GetValueOrDefault(dId, 0);
                        float score = CCUtils.ScorePickSlot(season, rules, i, dId, race, pos, hasResults);
                        playerScores[player.Id] = playerScores.GetValueOrDefault(player.Id) + score;
                    }
                }
            }
            if (race.Id == upToRace.Id) break;
        }
        return playerScores;
    }

    /// <summary>Per-race (non-cumulative) confidence-cup score per player. Computed independently
    /// per race via CCUtils.ScorePickSlot rather than diffing cumulative PlayerScores totals, to
    /// avoid float-subtraction drift and to distinguish "didn't submit picks that race" (null)
    /// from "submitted picks and scored exactly 0" (0f).</summary>
    public List<RacePlayerScores> PlayerScoresPerRace(Season season, GameSeason gs)
    {
        var rules = gs.PickRules ?? new PickRules();
        var result = new List<RacePlayerScores>();

        foreach (var race in season.Races.OrderBy(r => r.Round))
        {
            var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == race.Id);
            var scores = new Dictionary<ByteString, float?>();

            if (gameRace is not null)
            {
                var prevRace = season.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < race.Round);
                var champPts = prevRace is not null ? ChampionshipPoints(season, prevRace) : [];
                var champPos = ChampionshipPositions(champPts);
                bool hasResults = race.RaceResults.Any(rr => rr.IsComplete);

                foreach (var player in gs.ParticipatingPlayers)
                {
                    var picks = gameRace.PicksPerPlayer.FirstOrDefault(pp => pp.PlayerId == player.Id);
                    if (picks is null) continue;

                    float total = 0f;
                    for (int i = 0; i < picks.DriverId.Count; i++)
                    {
                        var dId = picks.DriverId[i];
                        int pos = champPos.GetValueOrDefault(dId, 0);
                        total += CCUtils.ScorePickSlot(season, rules, i, dId, race, pos, hasResults);
                    }
                    scores[player.Id] = total;
                }
            }

            result.Add(new RacePlayerScores(race, scores));
        }
        return result;
    }

    public enum SubmitPicksResult { Ok, NotFound, IneligibleDriver, StaleData }

    /// <summary>Resolves the calling player (auto-registering a brand-new one if needed) and
    /// writes their picks for the given race, atomically with a fresh reload of gpconf.data.
    /// Never trusts a caller-supplied Player — this is the only writer of Picks/GameRace data,
    /// so player identity and eligibility are both re-derived from a fresh load here, not from
    /// whatever a long-lived interactive session might be holding.</summary>
    public (SubmitPicksResult result, Player? player, bool isNewPlayer) SubmitPicks(
        ByteString seasonId, ByteString raceId, ByteString leagueId,
        string callerDisplayName, uint newPlayerColor, ByteString[] driverIds)
    {
        var (mainData, version) = _data.LoadWithVersion();
        var season = mainData.Seasons.FirstOrDefault(s => s.Id == seasonId);
        var race = season?.Races.FirstOrDefault(r => r.Id == raceId);
        var league = mainData.Leagues.FirstOrDefault(l => l.Id == leagueId);
        var gs = league?.Seasons.FirstOrDefault(x => x.SeasonId == seasonId);
        if (season is null || race is null || league is null || gs is null)
            return (SubmitPicksResult.NotFound, null, false);

        // Resolve the calling player, auto-registering if this is their first time:
        // 1) already participating this season -> use them.
        // 2) a known league member who just hasn't opted into this season yet -> opt them in.
        // 3) genuinely new -> create a Player (random Discord-palette color, chosen by the
        //    caller) in both League.Players and GameSeason.ParticipatingPlayers, matching how
        //    the two lists already relate for every existing player (same Id, duplicated by value).
        var isNewPlayer = false;
        var player = gs.ParticipatingPlayers.FirstOrDefault(p => p.PlayerName.Equals(callerDisplayName, StringComparison.OrdinalIgnoreCase));
        if (player is null)
        {
            player = league.Players.FirstOrDefault(p => p.PlayerName.Equals(callerDisplayName, StringComparison.OrdinalIgnoreCase));
            if (player is not null)
            {
                gs.ParticipatingPlayers.Add(player);
            }
            else
            {
                player = new Player { Id = CCUtils.CreateUniqueId(), PlayerName = callerDisplayName, Color = newPlayerColor };
                league.Players.Add(player);
                gs.ParticipatingPlayers.Add(player);
                isNewPlayer = true;
            }
        }

        var rules = gs.PickRules ?? new PickRules();
        var prevRace = season.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < race.Round);
        var eligibleIds = CCUtils.GetEligibleDriversWithPos(season, prevRace, rules.PositionCutoff)
            .Select(x => x.driver.Id).ToHashSet();
        if (driverIds.Any(id => !eligibleIds.Contains(id))) return (SubmitPicksResult.IneligibleDriver, player, isNewPlayer);

        var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == race.Id);
        if (gameRace is null) { gameRace = new GameRace { RaceId = race.Id }; gs.Races.Add(gameRace); }

        var picks = gameRace.PicksPerPlayer.FirstOrDefault(pp => pp.PlayerId == player.Id);
        if (picks is null) { picks = new Picks { PlayerId = player.Id }; gameRace.PicksPerPlayer.Add(picks); }
        picks.DriverId.Clear();
        picks.DriverId.AddRange(driverIds);

        try { _data.Save(mainData, version); }
        catch (ConcurrentSaveException) { return (SubmitPicksResult.StaleData, player, isNewPlayer); }
        return (SubmitPicksResult.Ok, player, isNewPlayer);
    }
}

/// <summary>One race's per-player confidence-cup score. A missing key in ScoreByPlayer means the
/// player had no Picks entry for that race at all (didn't submit), distinct from a stored 0f.</summary>
public sealed record RacePlayerScores(Race Race, Dictionary<ByteString, float?> ScoreByPlayer);
