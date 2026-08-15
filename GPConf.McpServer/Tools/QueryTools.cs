using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using GPConf.McpServer.DataAccess;
using GPConf.Utilities;
using ModelContextProtocol.Server;

namespace GPConf.McpServer.Tools;

[McpServerToolType]
public class QueryTools(GpConfDataAccess data)
{
    // ── Race Results ──────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Returns race results for a specific race, resolved to driver and team names. Multiple result sets are returned when the weekend includes a sprint.")]
    public string GetRaceResults(
        [Description("Season name or year")] string season,
        [Description("Race name or round number")] string race)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";
        var r = GpConfDataAccess.FindRace(s, race);
        if (r is null) return $"Race '{race}' not found in season '{s.Name}'.";
        if (r.RaceResults.Count == 0) return $"No race results stored for '{r.Name}'.";

        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d.Name);
        var teamMap   = s.Teams.ToDictionary(t => t.Id, t => t.Name);

        var sessions = r.RaceResults.Select(rr => new
        {
            session = rr.RaceName,
            results = rr.Results
                .OrderBy(dr => dr.Position)
                .Select(dr => new
                {
                    position   = dr.Position,
                    driver     = driverMap.GetValueOrDefault(dr.DriverId, "(unknown)"),
                    team       = teamMap.GetValueOrDefault(dr.TeamId, "(unknown)"),
                    laps       = dr.LapsCompleted,
                    raceTime   = dr.RaceTime,
                    fastestLap = dr.FastestLapSeconds,
                    points     = dr.Points,
                    status     = dr.Status.ToString(),
                })
                .ToList(),
        }).ToList();

        return JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Practice Results ──────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Returns practice session results for a race, sorted by fastest lap. sessionNumber: 1=FP1, 2=FP2, 3=FP3.")]
    public string GetPracticeResults(
        [Description("Season name or year")] string season,
        [Description("Race name or round number")] string race,
        [Description("Practice session number: 1, 2, or 3")] int sessionNumber)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";
        var r = GpConfDataAccess.FindRace(s, race);
        if (r is null) return $"Race '{race}' not found in season '{s.Name}'.";

        var session = r.Practices.FirstOrDefault(p => p.SessionNumber == sessionNumber);
        if (session is null) return $"No FP{sessionNumber} data stored for '{r.Name}'.";

        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d.Name);

        var results = session.LapData
            .Where(ld => ld.FastestLapSeconds > 0)
            .OrderBy(ld => ld.FastestLapSeconds)
            .Select((ld, idx) => new
            {
                position   = idx + 1,
                driver     = driverMap.GetValueOrDefault(ld.DriverId, "(unknown)"),
                fastestLap = ld.FastestLapSeconds,
                averageLap = ld.AverageLapSeconds > 0 ? (float?)ld.AverageLapSeconds : null,
            })
            .ToList();

        return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Qualifying Results ────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Returns qualifying results for a race ordered by grid position. Q3 finishers appear first (sorted by fastest lap), then Q2 eliminees, then Q1 eliminees.")]
    public string GetQualifyingResults(
        [Description("Season name or year")] string season,
        [Description("Race name or round number")] string race)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";
        var r = GpConfDataAccess.FindRace(s, race);
        if (r is null) return $"Race '{race}' not found in season '{s.Name}'.";
        if (r.QualifyingSessions.Count == 0) return $"No qualifying data stored for '{r.Name}'.";

        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d.Name);

        // Collect all driver entries; for drivers that appear in multiple sessions, keep the one
        // from the latest (highest) stage as it represents their best qualifying attempt.
        var allEntries = r.QualifyingSessions
            .SelectMany(qs => qs.LapData.Select(ld => (stage: qs.Stage, ld)))
            .GroupBy(x => x.ld.DriverId)
            .Select(g => g.OrderByDescending(x => x.stage).First())
            .ToList();

        // Stage 0 = made Q3; Stage 2 = eliminated Q2; Stage 1 = eliminated Q1.
        // Sort: Q3 drivers first by lap time ASC, then Q2, then Q1.
        var ordered = allEntries
            .OrderBy(x => x.stage == 0 ? 0 : x.stage == 2 ? 1 : 2)
            .ThenBy(x => x.ld.FastestLapSeconds > 0 ? x.ld.FastestLapSeconds : float.MaxValue)
            .Select((x, idx) => new
            {
                gridPosition    = idx + 1,
                driver          = driverMap.GetValueOrDefault(x.ld.DriverId, "(unknown)"),
                fastestLap      = x.ld.FastestLapSeconds > 0 ? (float?)x.ld.FastestLapSeconds : null,
                qualifyingStage = x.stage, // 0 = Q3 finisher, 1 = Q1 out, 2 = Q2 out
            })
            .ToList();

        return JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Championship Standings ────────────────────────────────────────────────

    [McpServerTool]
    [Description("Returns driver championship standings calculated from round 1 up to and including the specified race, using stored race points.")]
    public string GetChampionshipStandings(
        [Description("Season name or year")] string season,
        [Description("Race name or round number to calculate standings up to (inclusive)")] string race)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";
        var targetRace = GpConfDataAccess.FindRace(s, race);
        if (targetRace is null) return $"Race '{race}' not found in season '{s.Name}'.";

        var points    = ComputeChampionshipPoints(s, targetRace);
        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d.Name);

        var standings = points
            .Select(kv => new { driver = driverMap.GetValueOrDefault(kv.Key, "(unknown)"), points = kv.Value })
            .OrderByDescending(x => x.points)
            .Select((x, idx) => new { position = idx + 1, x.driver, x.points })
            .ToList();

        return JsonSerializer.Serialize(standings, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Pick Eligibility ──────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Returns the confidence-cup pick rules and the eligible driver pool for the upcoming (not-yet-decided) race in a season. Drivers ranked P1..PositionCutoff in the championship through the previous race are NOT pickable and are excluded from the eligible list; each eligible driver carries their standings multiplier. If leagueName is omitted, the first league linked to the season is used.")]
    public string GetPickEligibility(
        [Description("Season name or year")] string season,
        [Description("League name (optional)")] string? leagueName = null)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        var (league, gs, err) = FindLeagueAndGameSeason(mainData, s, leagueName);
        if (err is not null) return err;

        var rules = gs!.PickRules ?? new PickRules();

        // The next race with no recorded results is the one players are picking for.
        var target = s.Races
            .OrderBy(r => r.Round)
            .FirstOrDefault(r => r.RaceResults.All(rr => !rr.IsComplete));
        if (target is null) return $"No upcoming (undecided) race found for season '{s.Name}' — every race has complete results.";

        var prevRace = s.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < target.Round);
        var noPriorRace = prevRace is null;
        var eligiblePairs = CCUtils.GetEligibleDriversWithPos(s, prevRace, rules.PositionCutoff);

        var champPts = prevRace is not null ? ComputeChampionshipPoints(s, prevRace) : [];
        var champPos = BuildChampionshipPositions(champPts);

        // Ineligible (cutoff) drivers, for the caller's awareness — not recommended as picks.
        var ineligible = s.Drivers
            .Where(d => champPos.TryGetValue(d.Id, out var pos) && pos <= rules.PositionCutoff)
            .OrderBy(d => champPos[d.Id])
            .Select(d => new { driver = d.Name, champPosition = champPos[d.Id] })
            .ToList();

        var eligible = eligiblePairs
            .Select(x => new
            {
                driver = x.driver.Name,
                champPosition = x.champPos,
                multiplier = noPriorRace || x.champPos == 0
                    ? 1.0f
                    : CCUtils.GetStandingsMultiplier(rules, x.champPos),
            })
            .OrderBy(x => x.champPosition)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            league = league!.LeagueName,
            upcomingRace = target.Name,
            numPicks = rules.NumPicks,
            positionCutoff = rules.PositionCutoff,
            basePickScores = rules.BasePickScores,
            standingsMultipliers = rules.StandingsMultipliers
                .OrderBy(kv => kv.Key)
                .Select(kv => new { fromPosition = kv.Key, multiplier = kv.Value }),
            ineligibleDrivers = ineligible,
            eligibleDrivers = eligible,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Player Picks ──────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Returns each player's picks for a specific race with calculated confidence cup scores. If leagueName is omitted, the first league linked to the season is used.")]
    public string GetPlayerPicks(
        [Description("Season name or year")] string season,
        [Description("Race name or round number")] string race,
        [Description("League name (optional)")] string? leagueName = null)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";
        var r = GpConfDataAccess.FindRace(s, race);
        if (r is null) return $"Race '{race}' not found in season '{s.Name}'.";

        var (league, gs, err) = FindLeagueAndGameSeason(mainData, s, leagueName);
        if (err is not null) return err;

        var gameRace = gs!.Races.FirstOrDefault(gr => gr.RaceId == r.Id);
        if (gameRace is null) return $"No picks recorded for '{r.Name}' in league '{league!.LeagueName}'.";

        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d.Name);
        var rules     = gs.PickRules ?? new PickRules();

        var prevRace  = s.Races.OrderBy(x => x.Round).LastOrDefault(x => x.Round < r.Round);
        var champPts  = prevRace is not null ? ComputeChampionshipPoints(s, prevRace) : [];
        var champPos  = BuildChampionshipPositions(champPts);

        var result = gs.ParticipatingPlayers.Select(player =>
        {
            var picks   = gameRace.PicksPerPlayer.FirstOrDefault(pp => pp.PlayerId == player.Id);
            float total = 0f;
            var pickDetails = new List<object>();

            if (picks is not null)
            {
                bool hasResults = r.RaceResults.Any(rr => rr.IsComplete);
                for (int i = 0; i < picks.DriverId.Count; i++)
                {
                    var   dId    = picks.DriverId[i];
                    int   pos    = champPos.GetValueOrDefault(dId, 0);
                    float score  = CCUtils.ScorePickSlot(s, rules, i, dId, r, pos, hasResults);
                    total += score;
                    pickDetails.Add(new
                    {
                        pickNumber       = i + 1,
                        driver           = dId.IsEmpty ? "(no pick)" : driverMap.GetValueOrDefault(dId, "(unknown)"),
                        champPositionPre = pos > 0 ? (int?)pos : null,
                        score,
                    });
                }
            }

            return new { player = player.PlayerName, totalScore = total, picks = pickDetails };
        }).ToList();

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Player Scores ─────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Returns cumulative confidence cup scores per player, summed from round 1 through the specified race.")]
    public string GetPlayerScores(
        [Description("Season name or year")] string season,
        [Description("Race name or round number")] string race,
        [Description("League name (optional)")] string? leagueName = null)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";
        var targetRace = GpConfDataAccess.FindRace(s, race);
        if (targetRace is null) return $"Race '{race}' not found in season '{s.Name}'.";

        var (_, gs, err) = FindLeagueAndGameSeason(mainData, s, leagueName);
        if (err is not null) return err;

        var scores = ComputePlayerScores(s, gs!, targetRace);

        var result = gs!.ParticipatingPlayers
            .Select(p => new { player = p.PlayerName, score = scores.GetValueOrDefault(p.Id) })
            .OrderByDescending(x => x.score)
            .ToList();

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Player Rankings ───────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Returns player rankings (position 1 = highest score) as of a given race.")]
    public string GetPlayerRankings(
        [Description("Season name or year")] string season,
        [Description("Race name or round number")] string race,
        [Description("League name (optional)")] string? leagueName = null)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";
        var targetRace = GpConfDataAccess.FindRace(s, race);
        if (targetRace is null) return $"Race '{race}' not found in season '{s.Name}'.";

        var (_, gs, err) = FindLeagueAndGameSeason(mainData, s, leagueName);
        if (err is not null) return err;

        var scores = ComputePlayerScores(s, gs!, targetRace);

        var ranked = gs!.ParticipatingPlayers
            .Select(p => new { player = p.PlayerName, score = scores.GetValueOrDefault(p.Id) })
            .OrderByDescending(x => x.score)
            .Select((x, idx) => new { position = idx + 1, x.player, x.score })
            .ToList();

        return JsonSerializer.Serialize(ranked, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static Dictionary<ByteString, float> ComputeChampionshipPoints(Season season, Race upToRace)
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

    private static Dictionary<ByteString, int> BuildChampionshipPositions(Dictionary<ByteString, float> points)
    {
        var positions = new Dictionary<ByteString, int>();
        int pos = 1;
        foreach (var kv in points.OrderByDescending(kv => kv.Value))
            positions[kv.Key] = pos++;
        return positions;
    }

    private static (League? league, GameSeason? gs, string? error) FindLeagueAndGameSeason(
        MainData mainData, Season season, string? leagueName)
    {
        var leagues = leagueName is not null
            ? mainData.Leagues.Where(l => l.LeagueName.Equals(leagueName, StringComparison.OrdinalIgnoreCase))
            : mainData.Leagues.AsEnumerable();

        foreach (var league in leagues)
        {
            var gs = league.Seasons.FirstOrDefault(gs => gs.SeasonId == season.Id);
            if (gs is not null) return (league, gs, null);
        }

        string leagueDesc = leagueName is not null ? $"'{leagueName}'" : "any league";
        return (null, null, $"No game season found in {leagueDesc} linked to season '{season.Name}'.");
    }

    private static Dictionary<ByteString, float> ComputePlayerScores(Season season, GameSeason gs, Race upToRace)
    {
        var rules        = gs.PickRules ?? new PickRules();
        var playerScores = gs.ParticipatingPlayers.ToDictionary(p => p.Id, _ => 0f);

        foreach (var race in season.Races.OrderBy(r => r.Round))
        {
            var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == race.Id);
            if (gameRace is not null)
            {
                var prevRace = season.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < race.Round);
                var champPts = prevRace is not null ? ComputeChampionshipPoints(season, prevRace) : [];
                var champPos = BuildChampionshipPositions(champPts);

                bool hasResults = race.RaceResults.Any(rr => rr.IsComplete);
                foreach (var player in gs.ParticipatingPlayers)
                {
                    var picks = gameRace.PicksPerPlayer.FirstOrDefault(pp => pp.PlayerId == player.Id);
                    if (picks is null) continue;

                    for (int i = 0; i < picks.DriverId.Count; i++)
                    {
                        var   dId   = picks.DriverId[i];
                        int   pos   = champPos.GetValueOrDefault(dId, 0);
                        float score = CCUtils.ScorePickSlot(season, rules, i, dId, race, pos, hasResults);
                        playerScores[player.Id] = playerScores.GetValueOrDefault(player.Id) + score;
                    }
                }
            }

            if (race.Id == upToRace.Id) break;
        }

        return playerScores;
    }

}