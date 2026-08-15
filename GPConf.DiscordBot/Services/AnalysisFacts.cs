using System.Text;
using Google.Protobuf;
using GPConf.Utilities;

namespace GPConf.DiscordBot.Services;

/// <summary>
/// Builds plain-text "facts blocks" for the LLM analysis commands. Every number, name, and event
/// here is computed from real protobuf data via DataService/CCUtils — nothing here is invented or
/// left for the model to compute. The model's only job downstream is narrative/tone, never
/// arithmetic. RaceDriverResult.LapsCompleted ("laps behind the leader," not an absolute lap
/// count) is deliberately never surfaced — it's a well-known trap for misreading as raw lap count.
/// </summary>
public static class AnalysisFacts
{
    public static string BuildDriverFacts(
        DataService data, Season season, Driver driver, Race cutoff, League? league = null, GameSeason? gs = null)
    {
        var team = season.Teams.FirstOrDefault(t => t.Id == driver.CurrentTeamId);
        var points = data.ChampionshipPoints(season, cutoff);
        var positions = data.ChampionshipPositions(points);
        var pos = positions.GetValueOrDefault(driver.Id, 0);
        var pts = points.GetValueOrDefault(driver.Id, 0f);
        var ordered = points.OrderByDescending(kv => kv.Value).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Driver: {driver.Name} (#{driver.Number}), {driver.Nationality}, team: {team?.Name ?? "unknown"}");
        if (pos > 0)
        {
            var leaderPts = ordered[0].Value;
            var gapText = pos == 1
                ? "championship leader"
                : $"{pts:F1} points, gap to leader {leaderPts - pts:F1}, gap to P{pos - 1} {ordered[pos - 2].Value - pts:F1}";
            sb.AppendLine($"Championship standing through {cutoff.Name}: P{pos}, {gapText}");
        }
        else
        {
            sb.AppendLine($"Championship standing through {cutoff.Name}: no points scored yet");
        }
        sb.AppendLine();
        sb.AppendLine("Race-by-race results this season:");

        var raceLines = new List<string>();
        int best = int.MaxValue, worst = int.MinValue, dnf = 0, dns = 0, dsq = 0, podiums = 0, scoringRaces = 0;
        var finishPositions = new List<int>();
        var qualiPositions = new List<int>();

        foreach (var race in season.Races.OrderBy(r => r.Round))
        {
            if (race.Round > cutoff.Round) break;

            var qualiOrder = QualifyingOrder(race);
            var qualiIdx = qualiOrder.IndexOf(driver.Id);
            var qualiStage = BestStageReached(race, driver.Id);
            var qualiText = qualiIdx >= 0
                ? $"qualified P{qualiIdx + 1}" + (qualiStage is int st ? $" (reached {StageName(st)})" : "")
                : "did not qualify";
            if (qualiIdx >= 0) qualiPositions.Add(qualiIdx + 1);

            var practiceText = PracticeSummary(race, driver.Id) is { } p ? $"practice: {p}" : "no practice data";

            var sessionLines = race.RaceResults
                .Where(rr => rr.IsComplete)
                .Select(rr => (rr.RaceName, dr: rr.Results.FirstOrDefault(x => x.DriverId == driver.Id)))
                .Where(x => x.dr is not null)
                .Select(x => $"{x.RaceName}: P{x.dr!.Position} ({FormatStatus(x.dr.Status)}), {x.dr.Points:F1} pts")
                .ToList();

            if (sessionLines.Count == 0 && qualiIdx < 0) continue; // driver didn't participate this weekend at all

            foreach (var (rr, dr) in race.RaceResults.Where(rr => rr.IsComplete).Select(rr => (rr, rr.Results.FirstOrDefault(x => x.DriverId == driver.Id))))
            {
                if (dr is null) continue;
                finishPositions.Add(dr.Position);
                if (dr.Position < best) best = dr.Position;
                if (dr.Position > worst) worst = dr.Position;
                if (dr.Position is >= 1 and <= 3) podiums++;
                if (dr.Points > 0) scoringRaces++;
                switch (dr.Status)
                {
                    case FinishStatus.Dnf: dnf++; break;
                    case FinishStatus.Dns: dns++; break;
                    case FinishStatus.Dsq: dsq++; break;
                }
            }

            var resultsText = sessionLines.Count > 0 ? string.Join("; ", sessionLines) : "no race result recorded";
            raceLines.Add($"- Round {race.Round} {race.Name}: {qualiText}, {practiceText}, {resultsText}");
        }
        sb.AppendLine(raceLines.Count > 0 ? string.Join("\n", raceLines) : "(no data recorded this season)");
        sb.AppendLine();

        sb.AppendLine("Season aggregates:");
        if (finishPositions.Count > 0)
        {
            sb.AppendLine($"- Best finish: P{best}, worst finish: P{worst}");
            sb.AppendLine($"- Podiums: {podiums}, points-scoring races: {scoringRaces}/{finishPositions.Count}");
            sb.AppendLine($"- Average race finish: P{finishPositions.Average():F1}");
        }
        else
        {
            sb.AppendLine("- No completed race results this season");
        }
        sb.AppendLine($"- DNF: {dnf}, DNS: {dns}, DSQ: {dsq}");
        sb.AppendLine(qualiPositions.Count > 0
            ? $"- Average qualifying position: P{qualiPositions.Average():F1}"
            : "- No qualifying data recorded");

        if (finishPositions.Count >= 2)
        {
            var recent = finishPositions.TakeLast(Math.Min(3, finishPositions.Count)).ToList();
            var trend = recent[^1] < recent[0] ? "improving" : recent[^1] > recent[0] ? "declining" : "steady";
            sb.AppendLine($"- Recent finishing trend (last {recent.Count} races, oldest to newest): " +
                $"{string.Join(", ", recent.Select(p => $"P{p}"))} ({trend})");
        }

        sb.AppendLine();
        sb.AppendLine("GPConf confidence-cup outlook:");
        if (gs is not null && league is not null)
        {
            var rules = gs.PickRules ?? new PickRules();
            // Eligibility for the *next* pickable race, not for `cutoff` itself — mirrors exactly
            // how PickCommands.StartSession derives prevRace (the race immediately before the one
            // being picked for), so this never disagrees with what the picker would actually offer.
            var nextRace = data.NextPickableRace(season);
            var prevRaceForEligibility = nextRace is not null
                ? season.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < nextRace.Round)
                : cutoff;
            var eligiblePairs = CCUtils.GetEligibleDriversWithPos(season, prevRaceForEligibility, rules.PositionCutoff);
            var eligibleEntry = eligiblePairs.FirstOrDefault(x => x.driver.Id == driver.Id);
            bool isEligible = eligibleEntry.driver is not null;
            bool qualifiedAndStarted = CCUtils.HasQualifiedAndStarted(season, driver);

            sb.AppendLine($"- League: {league.LeagueName}");
            sb.AppendLine($"- Pick eligibility for {nextRace?.Name ?? "the next race"}: {(isEligible ? "eligible" : "not eligible")}" +
                (!isEligible && !qualifiedAndStarted
                    ? " (has not yet qualified and started a race this season, so is not a confidence-cup pick option)"
                    : ""));
            if (isEligible && eligibleEntry.champPos > 0)
            {
                var mult = CCUtils.GetStandingsMultiplier(rules, eligibleEntry.champPos);
                sb.AppendLine($"- Standings multiplier if picked: x{mult:G} (entering at championship P{eligibleEntry.champPos})");
            }
            if (rules.PositionCutoff > 0)
                sb.AppendLine($"- League rule: drivers ranked P{rules.PositionCutoff} or better in the championship are not pick-eligible.");
        }
        else
        {
            sb.AppendLine("- No league resolved for this request — omit any pick-eligibility commentary.");
        }

        return sb.ToString();
    }

    public static string BuildPlayerFacts(DataService data, Season season, League league, GameSeason gs, Player player, Race cutoff)
    {
        var rules = gs.PickRules ?? new PickRules();
        var driverMap = season.Drivers.ToDictionary(d => d.Id, d => d);
        var perRace = data.PlayerScoresPerRace(season, gs);
        var cumulative = data.PlayerScores(season, gs, cutoff);
        var totalScore = cumulative.GetValueOrDefault(player.Id);
        var rank = gs.ParticipatingPlayers
            .Select(p => (p.Id, score: cumulative.GetValueOrDefault(p.Id)))
            .OrderByDescending(x => x.score)
            .ToList()
            .FindIndex(x => x.Id == player.Id) + 1;

        var sb = new StringBuilder();
        sb.AppendLine($"Player: {player.PlayerName}, league: {league.LeagueName}, season: {season.Name}");
        sb.AppendLine($"Standing through {cutoff.Name}: P{rank} of {gs.ParticipatingPlayers.Count}, {totalScore:F1} cumulative confidence-cup points");
        sb.AppendLine($"Picks per race: {rules.NumPicks}");
        sb.AppendLine();
        sb.AppendLine("Race-by-race score history:");

        var pickCounts = new Dictionary<ByteString, int>();
        var slotPositions = new Dictionary<int, List<int>>();
        var raceLines = new List<string>();
        float running = 0f, best = float.MinValue;
        string bestRace = "";
        int noPickRaces = 0, zeroScoreRaces = 0;

        foreach (var rps in perRace)
        {
            if (rps.Race.Round > cutoff.Round) break;

            if (!rps.ScoreByPlayer.TryGetValue(player.Id, out var scoreOpt) || scoreOpt is null)
            {
                noPickRaces++;
                raceLines.Add($"- Round {rps.Race.Round} {rps.Race.Name}: no picks submitted");
                continue;
            }
            var score = scoreOpt.Value;
            running += score;
            if (score <= 0) zeroScoreRaces++;
            if (score > best) { best = score; bestRace = rps.Race.Name; }
            raceLines.Add($"- Round {rps.Race.Round} {rps.Race.Name}: {score:F1} pts (running total {running:F1})");

            // Which drivers were picked stays out of the favorite-picks/slot-tendency aggregate
            // until the race is decided — the score above is fine to show (it's just a number),
            // but the driver identity for a still-open race is meant to stay secret until then.
            if (!rps.Race.RaceResults.Any(rr => rr.IsComplete)) continue;

            var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == rps.Race.Id);
            var picks = gameRace?.PicksPerPlayer.FirstOrDefault(pp => pp.PlayerId == player.Id);
            if (picks is null) continue;

            var prevRace = season.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < rps.Race.Round);
            var champPts = prevRace is not null ? data.ChampionshipPoints(season, prevRace) : [];
            var champPos = data.ChampionshipPositions(champPts);
            for (int i = 0; i < picks.DriverId.Count; i++)
            {
                var dId = picks.DriverId[i];
                if (dId.IsEmpty) continue;
                pickCounts[dId] = pickCounts.GetValueOrDefault(dId) + 1;
                if (!slotPositions.TryGetValue(i, out var list)) slotPositions[i] = list = [];
                list.Add(champPos.GetValueOrDefault(dId, 0));
            }
        }
        sb.AppendLine(raceLines.Count > 0 ? string.Join("\n", raceLines) : "(no races this season)");
        sb.AppendLine();

        sb.AppendLine("Favorite picks (most-selected drivers this season):");
        var favorites = pickCounts
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"- {driverMap.GetValueOrDefault(kv.Key)?.Name ?? "unknown driver"}: picked {kv.Value} time(s)")
            .ToList();
        sb.AppendLine(favorites.Count > 0 ? string.Join("\n", favorites) : "(no picks recorded)");

        if (rules.NumPicks > 1 && slotPositions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Pick-slot tendency (average championship position of driver picked, by slot):");
            foreach (var kv in slotPositions.OrderBy(kv => kv.Key))
                sb.AppendLine($"- Slot {kv.Key + 1}: average championship position P{kv.Value.Average():F1}");
        }

        sb.AppendLine();
        sb.AppendLine(best == float.MinValue
            ? "Best single-race score: n/a"
            : $"Best single-race score: {best:F1} pts ({bestRace})");
        sb.AppendLine($"Races with no picks submitted: {noPickRaces}, races scoring zero: {zeroScoreRaces}");

        return sb.ToString();
    }

    public static string BuildSeasonFacts(DataService data, Season season, Race cutoff, League? league, GameSeason? gs)
    {
        var driverMap = season.Drivers.ToDictionary(d => d.Id, d => d);
        var teamMap = season.Teams.ToDictionary(t => t.Id, t => t);
        var completedRaces = season.Races
            .Where(r => r.Round <= cutoff.Round && r.RaceResults.Any(rr => rr.IsComplete))
            .OrderBy(r => r.Round)
            .ToList();

        var points = data.ChampionshipPoints(season, cutoff);
        var positions = data.ChampionshipPositions(points);

        var sb = new StringBuilder();
        sb.AppendLine($"Season: {season.Name} ({season.Year})");
        sb.AppendLine($"Races completed through {cutoff.Name}: {completedRaces.Count} of {season.Races.Count} scheduled");
        sb.AppendLine();
        sb.AppendLine("Top 5 championship standing:");
        sb.AppendLine(string.Join("\n", points.OrderByDescending(kv => kv.Value).Take(5).Select(kv =>
        {
            var d = driverMap.GetValueOrDefault(kv.Key);
            var t = d is not null ? teamMap.GetValueOrDefault(d.CurrentTeamId) : null;
            return $"- P{positions.GetValueOrDefault(kv.Key)}: {d?.Name ?? "unknown"} ({t?.Name ?? "unknown"}), {kv.Value:F1} pts";
        })));
        sb.AppendLine();

        var wins = new Dictionary<ByteString, int>();
        var poles = new Dictionary<ByteString, int>();
        var podiumsCount = new Dictionary<ByteString, int>();
        var dnfs = new Dictionary<ByteString, int>();
        var lastFinish = new Dictionary<ByteString, int>();
        var digest = new List<string>();
        string biggestSwing = "n/a";
        int biggestSwingMagnitude = 0;

        foreach (var race in completedRaces)
        {
            var mainResult = race.RaceResults.Where(rr => rr.IsComplete).OrderByDescending(rr => rr.Results.Count).FirstOrDefault();
            var qualiOrder = QualifyingOrder(race);
            var poleId = qualiOrder.FirstOrDefault();
            if (poleId is not null && !poleId.IsEmpty) poles[poleId] = poles.GetValueOrDefault(poleId) + 1;

            var winner = mainResult?.Results.OrderBy(x => x.Position).FirstOrDefault();
            if (winner is not null)
            {
                wins[winner.DriverId] = wins.GetValueOrDefault(winner.DriverId) + 1;
                var wd = driverMap.GetValueOrDefault(winner.DriverId);
                var wt = wd is not null ? teamMap.GetValueOrDefault(wd.CurrentTeamId) : null;
                var poleDriver = poleId is not null ? driverMap.GetValueOrDefault(poleId) : null;
                digest.Add($"- Round {race.Round} {race.Name}: winner {wd?.Name ?? "unknown"} ({wt?.Name ?? "unknown"}), pole {poleDriver?.Name ?? "unknown"}");
            }

            if (mainResult is not null)
            {
                foreach (var dr in mainResult.Results)
                {
                    if (dr.Position is >= 1 and <= 3) podiumsCount[dr.DriverId] = podiumsCount.GetValueOrDefault(dr.DriverId) + 1;
                    if (dr.Status is FinishStatus.Dnf or FinishStatus.Dsq) dnfs[dr.DriverId] = dnfs.GetValueOrDefault(dr.DriverId) + 1;

                    if (lastFinish.TryGetValue(dr.DriverId, out var prevPos))
                    {
                        var swing = prevPos - dr.Position;
                        if (Math.Abs(swing) > biggestSwingMagnitude)
                        {
                            biggestSwingMagnitude = Math.Abs(swing);
                            var d = driverMap.GetValueOrDefault(dr.DriverId);
                            biggestSwing = $"{d?.Name ?? "unknown"} {(swing > 0 ? "gained" : "lost")} {Math.Abs(swing)} position(s) finishing Round {race.Round} ({race.Name}) vs. the previous round";
                        }
                    }
                    lastFinish[dr.DriverId] = dr.Position;
                }
            }
        }

        sb.AppendLine("Per-race digest:");
        sb.AppendLine(digest.Count > 0 ? string.Join("\n", digest) : "(no completed races)");
        sb.AppendLine();
        sb.AppendLine("Season records:");
        sb.AppendLine($"- {MostOf(wins, "Most wins", driverMap)}");
        sb.AppendLine($"- {MostOf(poles, "Most poles", driverMap)}");
        sb.AppendLine($"- {MostOf(podiumsCount, "Most podiums", driverMap)}");
        sb.AppendLine($"- {MostOf(dnfs, "Most DNF/DSQ", driverMap)}");
        sb.AppendLine($"- Biggest round-over-round position swing: {biggestSwing}");

        if (gs is not null && league is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Confidence-cup league: {league.LeagueName}");
            var scores = data.PlayerScores(season, gs, cutoff);
            var leaderboard = gs.ParticipatingPlayers
                .Select(p => (p.PlayerName, score: scores.GetValueOrDefault(p.Id)))
                .OrderByDescending(x => x.score)
                .Select((x, i) => $"- P{i + 1}: {x.PlayerName}, {x.score:F1} pts")
                .ToList();
            sb.AppendLine("Leaderboard:");
            sb.AppendLine(string.Join("\n", leaderboard));

            var perRace = data.PlayerScoresPerRace(season, gs).Where(x => x.Race.Round <= cutoff.Round).ToList();
            float bestSingle = float.MinValue;
            string bestSingleDesc = "n/a";
            var pickFrequency = new Dictionary<ByteString, int>();
            foreach (var rps in perRace)
            {
                foreach (var kv in rps.ScoreByPlayer)
                {
                    if (kv.Value is not float v || v <= bestSingle) continue;
                    bestSingle = v;
                    var pname = gs.ParticipatingPlayers.FirstOrDefault(p => p.Id == kv.Key)?.PlayerName ?? "unknown";
                    bestSingleDesc = $"{pname}, {v:F1} pts in Round {rps.Race.Round} ({rps.Race.Name})";
                }
                var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == rps.Race.Id);
                if (gameRace is null) continue;
                foreach (var picks in gameRace.PicksPerPlayer)
                    foreach (var dId in picks.DriverId)
                        if (!dId.IsEmpty) pickFrequency[dId] = pickFrequency.GetValueOrDefault(dId) + 1;
            }
            sb.AppendLine($"Biggest single-race confidence-cup score: {bestSingleDesc}");
            sb.AppendLine($"- {MostOf(pickFrequency, "Most-picked driver league-wide", driverMap)}");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Confidence-cup league: none resolved for this request — omit any player/league commentary.");
        }

        return sb.ToString();
    }

    public static string BuildRaceRecapFacts(DataService data, Season season, Race race, League? league, GameSeason? gs)
    {
        var driverMap = season.Drivers.ToDictionary(d => d.Id, d => d);
        var teamMap = season.Teams.ToDictionary(t => t.Id, t => t);

        var sb = new StringBuilder();
        sb.AppendLine($"Race: {race.Name}, Round {race.Round}, {race.Circuit}, {race.Date}");
        sb.AppendLine();

        foreach (var practice in race.Practices.OrderBy(p => p.SessionNumber))
        {
            var fastest = practice.LapData.Where(ld => ld.FastestLapSeconds > 0).OrderBy(ld => ld.FastestLapSeconds).FirstOrDefault();
            if (fastest is null) continue;
            var d = driverMap.GetValueOrDefault(fastest.DriverId);
            sb.AppendLine($"FP{practice.SessionNumber} fastest: {d?.Name ?? "unknown"} ({fastest.FastestLapSeconds:F3}s)");
        }
        sb.AppendLine();

        var qualiOrder = QualifyingOrder(race);
        sb.AppendLine("Qualifying grid order:");
        sb.AppendLine(string.Join("\n", qualiOrder.Select((id, i) =>
        {
            var d = driverMap.GetValueOrDefault(id);
            return $"- P{i + 1}: {d?.Name ?? "unknown"}";
        })));
        sb.AppendLine();

        var mainResult = race.RaceResults.Where(rr => rr.IsComplete).OrderByDescending(rr => rr.Results.Count).FirstOrDefault();
        var prevRace = season.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < race.Round);
        var champPtsPrev = prevRace is not null ? data.ChampionshipPoints(season, prevRace) : [];
        var champPosPrev = data.ChampionshipPositions(champPtsPrev);

        sb.AppendLine("Race classification:");
        if (mainResult is not null)
        {
            foreach (var dr in mainResult.Results.OrderBy(x => x.Position))
            {
                var d = driverMap.GetValueOrDefault(dr.DriverId);
                var t = d is not null ? teamMap.GetValueOrDefault(d.CurrentTeamId) : null;
                sb.AppendLine($"- P{dr.Position}: {d?.Name ?? "unknown"} ({t?.Name ?? "unknown"}), {FormatStatus(dr.Status)}, {dr.Points:F1} pts");
            }
        }
        else
        {
            sb.AppendLine("(no completed race classification)");
        }
        sb.AppendLine();

        sb.AppendLine("Unexpected performances (computed, not inferred):");
        if (mainResult is not null)
        {
            var deltas = mainResult.Results
                .Select(dr =>
                {
                    var gridIdx = qualiOrder.IndexOf(dr.DriverId);
                    if (gridIdx < 0) return ((RaceDriverResult dr, int delta)?)null;
                    return (dr, delta: (gridIdx + 1) - dr.Position); // positive = gained positions in the race
                })
                .Where(x => x is not null)
                .Select(x => x!.Value)
                .OrderByDescending(x => x.delta)
                .ToList();

            foreach (var (dr, delta) in deltas.Take(3).Where(x => x.delta > 0))
                sb.AppendLine($"- Gained {delta} position(s): {driverMap.GetValueOrDefault(dr.DriverId)?.Name ?? "unknown"} (grid P{qualiOrder.IndexOf(dr.DriverId) + 1} -> finish P{dr.Position})");
            foreach (var (dr, delta) in deltas.OrderBy(x => x.delta).Take(3).Where(x => x.delta < 0))
                sb.AppendLine($"- Lost {-delta} position(s): {driverMap.GetValueOrDefault(dr.DriverId)?.Name ?? "unknown"} (grid P{qualiOrder.IndexOf(dr.DriverId) + 1} -> finish P{dr.Position})");

            foreach (var dr in mainResult.Results.Where(dr => dr.Position >= 1))
            {
                var priorPos = champPosPrev.GetValueOrDefault(dr.DriverId, 0);
                if (priorPos == 0) continue;
                var d = driverMap.GetValueOrDefault(dr.DriverId);
                if (dr.Position <= priorPos - 5)
                    sb.AppendLine($"- Overperformed championship form: {d?.Name ?? "unknown"} (championship P{priorPos} entering the race, finished P{dr.Position})");
                else if (dr.Position >= priorPos + 5)
                    sb.AppendLine($"- Underperformed championship form: {d?.Name ?? "unknown"} (championship P{priorPos} entering the race, finished P{dr.Position})");
            }

            var dnfList = mainResult.Results.Where(dr => dr.Status is FinishStatus.Dnf or FinishStatus.Dns or FinishStatus.Dsq)
                .Select(dr => $"{driverMap.GetValueOrDefault(dr.DriverId)?.Name ?? "unknown"} ({FormatStatus(dr.Status)})")
                .ToList();
            sb.AppendLine(dnfList.Count > 0 ? $"- DNF/DNS/DSQ: {string.Join(", ", dnfList)}" : "- No DNF/DNS/DSQ this race");

            var fastestLap = mainResult.Results.Where(dr => dr.FastestLapSeconds > 0).OrderBy(dr => dr.FastestLapSeconds).FirstOrDefault();
            if (fastestLap is not null)
                sb.AppendLine($"- Fastest lap: {driverMap.GetValueOrDefault(fastestLap.DriverId)?.Name ?? "unknown"} ({fastestLap.FastestLapSeconds:F3}s)");
        }

        if (gs is not null && league is not null)
        {
            var perRace = data.PlayerScoresPerRace(season, gs);
            var thisRace = perRace.FirstOrDefault(x => x.Race.Id == race.Id);
            sb.AppendLine();
            sb.AppendLine($"Confidence-cup league: {league.LeagueName}");
            if (thisRace is not null)
            {
                var weekScores = gs.ParticipatingPlayers
                    .Select(p => (p.PlayerName, score: thisRace.ScoreByPlayer.GetValueOrDefault(p.Id)))
                    .OrderByDescending(x => x.score ?? float.MinValue)
                    .Select(x => $"- {x.PlayerName}: {(x.score is float v ? $"{v:F1} pts" : "no picks submitted")}")
                    .ToList();
                sb.AppendLine("This week's scores:");
                sb.AppendLine(string.Join("\n", weekScores));

                if (prevRace is not null)
                {
                    var prevCumulative = data.PlayerScores(season, gs, prevRace);
                    var currCumulative = data.PlayerScores(season, gs, race);
                    var prevRanked = gs.ParticipatingPlayers.Select(p => p.Id).OrderByDescending(id => prevCumulative.GetValueOrDefault(id)).ToList();
                    var currRanked = gs.ParticipatingPlayers.Select(p => p.Id).OrderByDescending(id => currCumulative.GetValueOrDefault(id)).ToList();
                    sb.AppendLine("Standings movement this week:");
                    foreach (var p in gs.ParticipatingPlayers)
                    {
                        var before = prevRanked.IndexOf(p.Id) + 1;
                        var after = currRanked.IndexOf(p.Id) + 1;
                        if (before != after)
                            sb.AppendLine($"- {p.PlayerName}: P{before} -> P{after}");
                    }
                }

                var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == race.Id);
                if (gameRace is not null)
                {
                    var pickFrequency = new Dictionary<ByteString, int>();
                    foreach (var picks in gameRace.PicksPerPlayer)
                        foreach (var dId in picks.DriverId)
                            if (!dId.IsEmpty) pickFrequency[dId] = pickFrequency.GetValueOrDefault(dId) + 1;
                    sb.AppendLine($"- {MostOf(pickFrequency, "Most-picked driver this week", driverMap)}");
                }
            }
            else
            {
                sb.AppendLine("No confidence-cup game data recorded for this race.");
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Confidence-cup league: none resolved for this request — omit any player/league commentary.");
        }

        return sb.ToString();
    }

    // --- shared helpers ---

    // Mirrors the exact ordering ReadCommands.Quali uses, so "grid position" facts here always
    // agree with what /quali displays for the same race.
    private static List<ByteString> QualifyingOrder(Race race) =>
        race.QualifyingSessions
            .SelectMany(qs => qs.LapData.Select(ld => (stage: qs.Stage, ld)))
            .GroupBy(x => x.ld.DriverId)
            .Select(g => g.OrderByDescending(x => x.stage).First())
            .OrderBy(x => x.stage == 0 ? 0 : x.stage == 2 ? 1 : 2)
            .ThenBy(x => x.ld.FastestLapSeconds > 0 ? x.ld.FastestLapSeconds : float.MaxValue)
            .Select(x => x.ld.DriverId)
            .ToList();

    private static int? BestStageReached(Race race, ByteString driverId)
    {
        var stages = race.QualifyingSessions
            .SelectMany(qs => qs.LapData.Select(ld => (stage: qs.Stage, ld.DriverId)))
            .Where(x => x.DriverId == driverId)
            .Select(x => x.stage)
            .ToList();
        return stages.Count > 0 ? stages.Max() : null;
    }

    private static string StageName(int stage) => stage switch { 1 => "Q1", 2 => "Q2", 3 => "Q3", _ => $"stage {stage}" };

    private static string? PracticeSummary(Race race, ByteString driverId)
    {
        var laps = race.Practices
            .Select(p => (session: p.SessionNumber, lap: p.LapData.FirstOrDefault(ld => ld.DriverId == driverId)))
            .Where(x => x.lap is not null && x.lap.FastestLapSeconds > 0)
            .ToList();
        return laps.Count > 0 ? string.Join(", ", laps.Select(x => $"FP{x.session} {x.lap!.FastestLapSeconds:F3}s")) : null;
    }

    private static string FormatStatus(FinishStatus status) => status switch
    {
        FinishStatus.Finished => "Finished",
        FinishStatus.Dnf => "DNF",
        FinishStatus.Dns => "DNS",
        FinishStatus.Dsq => "DSQ",
        _ => "unknown",
    };

    private static string MostOf(Dictionary<ByteString, int> counts, string label, Dictionary<ByteString, Driver> driverMap)
    {
        if (counts.Count == 0) return $"{label}: n/a";
        var top = counts.OrderByDescending(kv => kv.Value).First();
        var d = driverMap.GetValueOrDefault(top.Key);
        return $"{label}: {d?.Name ?? "unknown"} ({top.Value})";
    }
}
