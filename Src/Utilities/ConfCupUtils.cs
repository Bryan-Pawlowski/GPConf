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
        // Position is the authoritative classification (set at ingestion from the official
        // result). LapsCompleted/RaceTime aren't reliably populated — most classified finishers
        // share LapsCompleted == 0 ("laps behind the leader") by convention, which left this with
        // no real tiebreaker and silently fell back to raw storage order.
        return result.Results.OrderBy(r => r.Position).ToList();
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

    // True if a driver has ever appeared in a qualifying session AND started a race (any
    // RaceDriverResult with status != DNS) anywhere in the season. Guest/junior drivers who only
    // ran practice laps in a senior driver's car are registered in season.Drivers (required for
    // practice-result entry to resolve their name) but never satisfy this, so they don't count as
    // real season entrants for standings/pick-eligibility purposes.
    public static bool HasQualifiedAndStarted(Season season, Driver driver)
    {
        bool qualified = season.Races.Any(r =>
            r.QualifyingSessions.Any(qs => qs.LapData.Any(ld => ld.DriverId == driver.Id)));
        bool started = season.Races.Any(r =>
            r.RaceResults.Any(rr => rr.Results.Any(dr =>
                dr.DriverId == driver.Id && dr.Status != FinishStatus.Dns)));
        return qualified && started;
    }

    // Ranks every driver in the season by championship points through prevRace (the immediately
    // preceding race by round order, not "most recent race with results entered"), then filters
    // to positions strictly past positionCutoff. Round 1 (prevRace == null) has no cutoff at all
    // — every driver is eligible with champPos 0, unfiltered by HasQualifiedAndStarted: at season
    // start no driver has any participation history yet, so gating on it here would empty the
    // entire pick pool instead of just excluding guests. Ranks ALL season.Drivers (not just those
    // who appear in stored RaceResults), so a driver with zero results still gets a real position
    // — this matters for correctly excluding/including them relative to the cutoff. From round 2
    // onward the returned pool is filtered to HasQualifiedAndStarted so practice-only guest/junior
    // drivers who never actually raced drop out of eligibility once that record exists, without
    // disturbing everyone else's champPos.
    public static List<(Driver driver, int champPos)> GetEligibleDriversWithPos(
        Season season, Race? prevRace, int positionCutoff)
    {
        if (prevRace == null)
            return season.Drivers.Select(d => (d, 0)).ToList();

        var ordered = season.Drivers
            .Select(d => (driver: d, pts: GetDriverChampionshipPointSnapshotForRace(season, prevRace, d)))
            .OrderByDescending(x => x.pts)
            .ToList();

        var result = new List<(Driver, int)>();
        for (int i = 0; i < ordered.Count; i++)
        {
            int pos = i + 1;
            if (pos > positionCutoff && HasQualifiedAndStarted(season, ordered[i].driver))
                result.Add((ordered[i].driver, pos));
        }
        return result;
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

        return baseScore * GetStandingsMultiplier(rules, champPos);
    }

    // Looks up the standings-tier multiplier for a championship position (1-based).
    // Falls back to 1.0 if no tiers are configured, or the lowest-position tier's
    // multiplier if champPos falls past every configured threshold.
    public static float GetStandingsMultiplier(PickRules rules, int champPos)
    {
        if (rules.StandingsMultipliers.Count == 0) return 1.0f;

        var   ordered    = rules.StandingsMultipliers.OrderBy(kv => kv.Key).ToList();
        float multiplier = ordered[ordered.Count - 1].Value;
        foreach (var kv in ordered)
            if (champPos <= kv.Key) { multiplier = kv.Value; break; }

        return multiplier;
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

    // Scores one pick slot for a race, treating an unset driver pick as zero. Without this
    // guard, an empty driverId falls through to CalculatePickScore/GetPickScoreFromResults with
    // champPos defaulting to 0 — indistinguishable from the legitimate "Round 1, no prior
    // standings" case, which awards the full base score to a slot the player never filled in.
    public static float ScorePickSlot(
        Season season, PickRules rules, int pickIndex, ByteString driverId,
        Race race, int champPos, bool hasResults)
    {
        if (driverId.IsEmpty) return 0f;
        return hasResults
            ? GetPickScoreFromResults(season, rules, pickIndex, driverId, race, champPos)
            : CalculatePickScore(rules, pickIndex, champPos);
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