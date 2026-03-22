using Google.Protobuf;
using Google.Protobuf.Collections;
namespace GPConf.Utilities;

public class CCUtils
{
    public static ByteString CreateUniqueId()
    {
        return Google.Protobuf.ByteString.CopyFrom(System.Guid.NewGuid().ToByteArray());
    }

    public static int GetDriverChampionshipPointSnapshotForRace(Season currentSeason, Race race, Driver driver)
    {
        var races = currentSeason.Races;
        int totalPoints = 0;
        
        foreach(var currentRace in races)
        {
            totalPoints += GetPointsForDriverInRace(currentSeason, currentRace, driver);
            
            if (currentRace.Id != race.Id) { continue; }

            break;
        }
        
        return totalPoints;
    }

    public static int GetPointsForDriverInRace(Season currentSeason, Race race, Driver driver)
    {
        var results = race.RaceResults;
        int totalPoints = 0;
        foreach (var result in results)
        {
            var scoringRules = GetPointsScoringRules(currentSeason, result.PointRulesId);
            if (scoringRules == null) { continue; }
            totalPoints += GetResultPointsForDriver(GenerateRaceOrder(result), driver, scoringRules);
        }
        return totalPoints;
    }

    public static int GetResultPointsForDriver(List<RaceDriverResult> raceOrder, Driver driver, PointsScoringRules rules)
    {
        int pos = GetDriverPositionInRace(driver, raceOrder);
        return GetPointsForPosition(pos, rules);
    }

    public static int GetDriverPositionInRace(Driver driver, List<RaceDriverResult> ordered)
    {
        int idx = ordered.FindIndex(r => r.DriverId == driver.Id);
        return idx >= 0 ? idx + 1 : 0;
    }

    public static List<RaceDriverResult> GenerateRaceOrder(RaceResult result)
    {
        var finishers = result.Results
            .Where(r => r.Status == FinishStatus.Finished)
            .OrderBy(r => r.LapsCompleted)
            .ThenBy(r => r.RaceTime);

        var nonFinishers = result.Results
            .Where(r => r.Status != FinishStatus.Finished)
            .OrderBy(r => r.LapsCompleted)
            .ThenBy(r => r.RaceTime);

        var ordered = finishers.Concat(nonFinishers).ToList();
        return ordered;
    }

    public static int GetPointsForPosition(int position, PointsScoringRules rules)
    {
        if(position <= 0) { return 0; }
        if(position > rules.Score.Count) { return 0; }

        return rules.Score[position - 1]; // position is 1-based; Score is 0-based
    }
    
    // Returns the calculated points for a single driver result within a race session,
    // using the session's assigned scoring rules and the computed finishing order.
    public static int CalculatePointsForResult(Season season, RaceResult raceResult, RaceDriverResult driverResult)
    {
        var rules = GetPointsScoringRules(season, raceResult.PointRulesId);
        if (rules == null) return 0;

        var order = GenerateRaceOrder(raceResult);
        int idx = order.FindIndex(r => r.DriverId == driverResult.DriverId);
        if (idx < 0 || idx >= rules.Score.Count) return 0;
        return rules.Score[idx]; // idx is 0-based; Score[0] = P1 points
    }

    private static PointsScoringRules? GetPointsScoringRules(Season season, ByteString pointsScoringRulesId)
    {
        PointsScoringRules? outRuleSet = null;
        foreach(var ruleSet in season.Rules)
        {
            if (ruleSet.Id != pointsScoringRulesId) { continue; }
            outRuleSet = ruleSet;
            break;
        }
        return outRuleSet;
    }
}