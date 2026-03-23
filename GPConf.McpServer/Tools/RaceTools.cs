using System.ComponentModel;
using System.Text.Json;
using GPConf.McpServer.DataAccess;
using ModelContextProtocol.Server;

namespace GPConf.McpServer.Tools;

/// <summary>
/// Parameter shapes used in tool calls.
/// </summary>
file record RaceResultInput(
    string DriverName,
    string TeamName,
    int    Position,
    float  Points,
    string Status,         // "Finished" | "DNF" | "DNS" | "DSQ"   
    float  RaceTime,
    float  FastestLap,
    int    LapsCompleted);

file record QualifyingResultInput(
    string DriverName,
    float  Q1Time,
    float  Q2Time,
    float  Q3Time,
    int    GridPosition,
    int    QualifyingStage,   // stage eliminated in (1-3); 0 = completed all
    string SessionName);

file record PracticeResultInput(
    string DriverName,
    float  FastestLap,
    float  AverageLap);

[McpServerToolType]
public class RaceTools(GpConfDataAccess data)
{
    [McpServerTool]
    [Description("Creates a race entry in a season if it does not exist, or updates its metadata if it does.")]
    public string UpsertRace(
        [Description("Season name or year")] string season,
        [Description("Race name, e.g. 'Australian Grand Prix'")] string raceName,
        [Description("Circuit name, e.g. 'Albert Park Circuit'")] string circuit,
        [Description("Round number within the season")] int round,
        [Description("Race date in ISO 8601 format: YYYY-MM-DD")] string date)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        var race = GpConfDataAccess.FindRace(s, raceName)
                ?? GpConfDataAccess.FindRace(s, round.ToString());

        if (race is null)
        {
            race = new Race { Id = GpConfDataAccess.NewId() };
            s.Races.Add(race);
        }

        race.Name    = raceName;
        race.Circuit = circuit;
        race.Round   = round;
        race.Date    = date;

        data.Save(mainData);
        return $"Race '{raceName}' (round {round}) saved to season '{s.Name}'.";
    }

    [McpServerTool]
    [Description("Sets the race results for a specific race. Pass results as a JSON array of objects with fields: DriverName, TeamName, Position, Points, Status (Finished|DNF|DNS|DSQ), RaceTime, FastestLap, LapsCompleted. Use sessionName to store sprint results separately (e.g. 'Chinese Grand Prix Sprint').")]
    public string SetRaceResults(
        [Description("Season name or year")] string season,
        [Description("Race name or round number")] string race,
        [Description("JSON array of race results")] string resultsJson,
        [Description("Optional session name override; defaults to the race name. Use e.g. 'Chinese Grand Prix Sprint' to store sprint results.")] string? sessionName = null)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        var r = GpConfDataAccess.FindRace(s, race);
        if (r is null) return $"Race '{race}' not found in season '{s.Name}'. Use upsert_race first.";

        List<RaceResultInput>? inputs;
        try { inputs = JsonSerializer.Deserialize<List<RaceResultInput>>(resultsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (Exception ex) { return $"Invalid results JSON: {ex.Message}"; }
        if (inputs is null) return "Results JSON was null.";

        var effectiveSessionName = string.IsNullOrWhiteSpace(sessionName) ? r.Name : sessionName;

        // Build a single RaceResult entry (or replace existing one).
        var raceResult = r.RaceResults.FirstOrDefault(rr => rr.RaceName == effectiveSessionName)
                      ?? new RaceResult { RaceName = effectiveSessionName };

        raceResult.Results.Clear();
        foreach (var inp in inputs)
        {
            var driver = GpConfDataAccess.FindDriver(s, inp.DriverName);
            var team   = GpConfDataAccess.FindTeam(s, inp.TeamName);

            raceResult.Results.Add(new RaceDriverResult
            {
                DriverId         = driver?.Id ?? Google.Protobuf.ByteString.Empty,
                TeamId           = team?.Id   ?? Google.Protobuf.ByteString.Empty,
                Position         = inp.Position,
                Points           = inp.Points,
                Status           = ParseStatus(inp.Status),
                RaceTime         = inp.RaceTime,
                FastestLapSeconds = inp.FastestLap,
                LapsCompleted    = inp.LapsCompleted,
            });
        }

        if (!r.RaceResults.Contains(raceResult))
            r.RaceResults.Add(raceResult);

        data.Save(mainData);
        return $"Saved {raceResult.Results.Count} results for '{effectiveSessionName}'.";
    }

    [McpServerTool]
    [Description("Sets qualifying results for a race. Pass results as a JSON array with fields: DriverName, Q1Time, Q2Time, Q3Time (seconds; 0 if did not participate), GridPosition, QualifyingStage (stage eliminated in, 0 = completed all), SessionName.")]
    public string SetQualifyingResults(
        [Description("Season name or year")] string season,
        [Description("Race name or round number")] string race,
        [Description("JSON array of qualifying results")] string resultsJson)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        var r = GpConfDataAccess.FindRace(s, race);
        if (r is null) return $"Race '{race}' not found in season '{s.Name}'. Use upsert_race first.";

        List<QualifyingResultInput>? inputs;
        try { inputs = JsonSerializer.Deserialize<List<QualifyingResultInput>>(resultsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (Exception ex) { return $"Invalid results JSON: {ex.Message}"; }
        if (inputs is null) return "Results JSON was null.";

        // Group by stage to build QualifyingSession entries.
        r.QualifyingSessions.Clear();
        var byStage = inputs.GroupBy(i => i.QualifyingStage);
        foreach (var group in byStage.OrderBy(g => g.Key))
        {
            var session = new QualifyingSession
            {
                Stage       = group.Key,
                SessionName = group.First().SessionName,
            };
            foreach (var inp in group)
            {
                var driver = GpConfDataAccess.FindDriver(s, inp.DriverName);
                session.LapData.Add(new LapData
                {
                    DriverId                  = driver?.Id ?? Google.Protobuf.ByteString.Empty,
                    FastestLapSeconds         = inp.Q1Time > 0 ? inp.Q1Time : inp.Q2Time > 0 ? inp.Q2Time : inp.Q3Time,
                    QualiSessionEliminated    = inp.QualifyingStage,
                });
            }
            r.QualifyingSessions.Add(session);
        }

        data.Save(mainData);
        return $"Saved qualifying data ({r.QualifyingSessions.Count} stage(s)) for '{r.Name}'.";
    }

    [McpServerTool]
    [Description("Sets practice session results. Pass results as a JSON array with fields: DriverName, FastestLap, AverageLap (seconds).")]
    public string SetPracticeResults(
        [Description("Season name or year")] string season,
        [Description("Race name or round number")] string race,
        [Description("Practice session number: 1, 2, or 3")] int sessionNumber,
        [Description("JSON array of practice results")] string resultsJson)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        var r = GpConfDataAccess.FindRace(s, race);
        if (r is null) return $"Race '{race}' not found in season '{s.Name}'. Use upsert_race first.";

        List<PracticeResultInput>? inputs;
        try { inputs = JsonSerializer.Deserialize<List<PracticeResultInput>>(resultsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (Exception ex) { return $"Invalid results JSON: {ex.Message}"; }
        if (inputs is null) return "Results JSON was null.";

        var existing = r.Practices.FirstOrDefault(p => p.SessionNumber == sessionNumber);
        if (existing is not null) r.Practices.Remove(existing);

        var session = new PracticeSession { SessionNumber = sessionNumber };
        foreach (var inp in inputs)
        {
            var driver = GpConfDataAccess.FindDriver(s, inp.DriverName);
            session.LapData.Add(new LapData
            {
                DriverId          = driver?.Id ?? Google.Protobuf.ByteString.Empty,
                FastestLapSeconds = inp.FastestLap,
                AverageLapSeconds = inp.AverageLap,
            });
        }
        r.Practices.Add(session);

        data.Save(mainData);
        return $"Saved FP{sessionNumber} data ({session.LapData.Count} drivers) for '{r.Name}'.";
    }

    private static FinishStatus ParseStatus(string status) => status.ToUpperInvariant() switch
    {
        "DNF" => FinishStatus.Dnf,
        "DNS" => FinishStatus.Dns,
        "DSQ" => FinishStatus.Dsq,
        _     => FinishStatus.Finished,
    };
}