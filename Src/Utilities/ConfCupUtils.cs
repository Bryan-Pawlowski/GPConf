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

    // ── Confidence Cup pick scoring ───────────────────────────────────────────

    // Returns the base pick score × standings multiplier for a single pick slot.
    // champPos == 0 means no prior standings exist (R1); multiplier defaults to 1.0.
    public static float CalculatePickScore(PickRules rules, int pickIndex, int champPos)
    {
        if (pickIndex >= rules.BasePickScores.Count) return 0f;
        float baseScore = rules.BasePickScores[pickIndex];

        // R1 special case: no prior standings → multiplier is always 1.0
        if (champPos == 0) return baseScore;

        if (rules.StandingsMultipliers.Count == 0) return baseScore;

        var   ordered    = rules.StandingsMultipliers.OrderBy(kv => kv.Key).ToList();
        float multiplier = ordered[ordered.Count - 1].Value;
        foreach (var kv in ordered)
            if (champPos <= kv.Key) { multiplier = kv.Value; break; }

        return baseScore * multiplier;
    }

    // Scores a single pick across all complete result sessions in a race weekend.
    // Each session's contribution is scaled by its PointsScoringRules.ConfCupMultiplier
    // (falls back to 1.0 if unset). Only sessions where the driver scored F1 points count.
    public static float GetPickScoreFromResults(
        Season season, PickRules rules, int pickIndex, ByteString driverId, Race race, int champPos)
    {
        float total = 0f;
        foreach (RaceResult rr in race.RaceResults.Where(rr => rr.IsComplete))
        {
            RaceDriverResult? rdr = rr.Results.FirstOrDefault(r => r.DriverId == driverId);
            if (rdr == null) continue;

            float pts = CalculatePointsForResult(season, rr, rdr);
            if (pts <= 0) continue;

            PointsScoringRules? scoringRules = season.Rules.FirstOrDefault(r => r.Id == rr.PointRulesId);
            float confCupMultiplier = scoringRules is { ConfCupMultiplier: > 0 } ? scoringRules.ConfCupMultiplier : 1f;
            total += CalculatePickScore(rules, pickIndex, champPos) * confCupMultiplier;
        }
        return total;
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