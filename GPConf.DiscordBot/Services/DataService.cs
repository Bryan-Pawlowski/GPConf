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

    public Race? LatestRace(Season season) =>
        season.Races.OrderByDescending(r => r.Round).FirstOrDefault();

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

                    for (int i = 0; i < picks.DriverId.Count; i++)
                    {
                        var dId = picks.DriverId[i];
                        int pos = champPos.GetValueOrDefault(dId, 0);
                        float score = race.RaceResults.Any(rr => rr.IsComplete)
                            ? CCUtils.GetPickScoreFromResults(season, rules, i, dId, race, pos)
                            : CCUtils.CalculatePickScore(rules, i, pos);
                        playerScores[player.Id] = playerScores.GetValueOrDefault(player.Id) + score;
                    }
                }
            }
            if (race.Id == upToRace.Id) break;
        }
        return playerScores;
    }
}
