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

    /// <summary>
    /// Computes deterministic season statistics for the `/season-stats` slash command.
    /// All numbers come from real data via DataService/CCUtils — no LLM inference.
    /// </summary>
    public static string BuildSeasonStatsFacts(DataService data, Season season, Race cutoff, League? league = null, GameSeason? gs = null)
    {
        var driverMap = season.Drivers.ToDictionary(d => d.Id, d => d);
        var completedRaces = season.Races
            .Where(r => r.Round <= cutoff.Round && r.RaceResults.Any(rr => rr.IsComplete))
            .OrderBy(r => r.Round)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Season: {season.Name} ({season.Year})");
        sb.AppendLine($"Races completed: {completedRaces.Count} of {season.Races.Count} scheduled");
        sb.AppendLine();

        var driverStats = new Dictionary<ByteString, (List<int> finishes, List<int> quals, int dnfs, int dnss, int dsqs, int scoreRaces)>();
        foreach (var d in season.Drivers)
            driverStats[d.Id] = ([], [], 0, 0, 0, 0);

        foreach (var race in completedRaces)
        {
            var mainResult = race.RaceResults.Where(rr => rr.IsComplete).OrderByDescending(rr => rr.Results.Count).FirstOrDefault();
            if (mainResult is null) continue;
            var qualiOrder = QualifyingOrder(race);

            foreach (var dr in mainResult.Results)
            {
                var existing = driverStats[dr.DriverId];
                existing.finishes.Add(dr.Position);
                if (dr.Points > 0) existing.scoreRaces++;
                if (dr.Status is FinishStatus.Dnf) existing.dnfs++;
                if (dr.Status is FinishStatus.Dns) existing.dnss++;
                if (dr.Status is FinishStatus.Dsq) existing.dsqs++;
                driverStats[dr.DriverId] = existing;

                var qualiIdx = qualiOrder.IndexOf(dr.DriverId);
                if (qualiIdx >= 0)
                {
                    var stats = driverStats[dr.DriverId];
                    stats.quals.Add(qualiIdx + 1);
                    driverStats[dr.DriverId] = stats;
                }
            }
        }

        sb.AppendLine("Driver reliability (fewest DNFs/DNS):");
        var reliability = driverStats
            .Select(kv => (Driver: driverMap.GetValueOrDefault(kv.Key), Starts: kv.Value.finishes.Count, DNFs: kv.Value.dnfs, DNS: kv.Value.dnss))
            .Where(x => x.Starts > 0)
            .OrderBy(x => x.DNFs + x.DNS)
            .ThenBy(x => x.Starts)
            .Take(5)
            .Select(x => $"{x.Driver?.Name ?? "unknown"}: {x.DNFs + x.DNS}/{x.Starts} races")
            .ToList();
        sb.AppendLine(reliability.Count > 0 ? string.Join("\n", reliability) : "(no data)");
        sb.AppendLine();

        sb.AppendLine("Lowest qualifying SD (most consistent, min 2 races):");
        var qualiConsistency = driverStats
            .Select(kv => (Driver: driverMap.GetValueOrDefault(kv.Key), Count: kv.Value.quals.Count, StdDev: GetStdDev(kv.Value.quals)))
            .Where(x => x.Count >= 2)
            .OrderBy(x => x.StdDev)
            .Take(5)
            .Select(x => $"{x.Driver?.Name ?? "unknown"}: SD {x.StdDev:F2} ({x.Count} races)")
            .ToList();
        sb.AppendLine(qualiConsistency.Count > 0 ? string.Join("\n", qualiConsistency) : "(no qualifying data with 2+ races)");
        sb.AppendLine();

        sb.AppendLine("Longest winless streak:");
        var wins = new List<(string driver, int round)>();
        foreach (var race in completedRaces)
        {
            var mainResult = race.RaceResults.Where(rr => rr.IsComplete).OrderByDescending(rr => rr.Results.Count).FirstOrDefault();
            if (mainResult is null) continue;
            foreach (var dr in mainResult.Results.Where(x => x.Position == 1))
                wins.Add((driverMap.GetValueOrDefault(dr.DriverId)?.Name ?? "unknown", race.Round));
        }
        var winlessStreak = GetLongestGap(wins, completedRaces.LastOrDefault()?.Round ?? 0);
        sb.AppendLine(winlessStreak ?? "n/a (no wins yet this season)");
        sb.AppendLine();

        sb.AppendLine("Average qualifying-to-race position change:");
        var qualiRaceDiff = driverStats
            .Select(kv =>
            {
                var driver = driverMap.GetValueOrDefault(kv.Key);
                var finishes = kv.Value.finishes;
                var quals = kv.Value.quals;
                int count = Math.Min(finishes.Count, quals.Count);
                if (count == 0) return (Driver: driver, AvgDiff: (double?)null);
                double avg = Enumerable.Range(0, count)
                    .Select(i => finishes[i] - quals[i])
                    .Average();
                return (Driver: driver, AvgDiff: avg);
            })
            .Where(x => x.AvgDiff.HasValue)
            .OrderBy(x => x.AvgDiff!.Value)
            .Take(5)
            .Select(x => $"{x.Driver?.Name ?? "unknown"}: avg {x.AvgDiff:+0;-0;=0} position(s) (quali -> race)")
            .ToList();
        sb.AppendLine(qualiRaceDiff.Count > 0 ? string.Join("\n", qualiRaceDiff) : "(no data)");
        sb.AppendLine();

        if (gs is not null && league is not null)
        {
            sb.AppendLine($"Confidence-cup league: {league.LeagueName}");
            sb.AppendLine();

            var perRace = data.PlayerScoresPerRace(season, gs).Where(x => x.Race.Round <= cutoff.Round).ToList();
            var playerIds = gs.ParticipatingPlayers.ToDictionary(p => p.Id);

            sb.AppendLine("Most pick-perfect races (all picks scored):");
            var playerPerfect = new Dictionary<ByteString, List<int>>();
            foreach (var p in gs.ParticipatingPlayers)
                playerPerfect[p.Id] = new List<int>();

            foreach (var rps in perRace)
            {
                if (!rps.Race.RaceResults.Any(rr => rr.IsComplete)) continue;
                var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == rps.Race.Id);
                if (gameRace is null) continue;
                foreach (var picks in gameRace.PicksPerPlayer)
                {
                    if (!playerIds.ContainsKey(picks.PlayerId)) continue;
                    var playerScore = rps.ScoreByPlayer.GetValueOrDefault(picks.PlayerId) ?? 0f;
                    if (playerScore > 0)
                        playerPerfect[picks.PlayerId].Add(rps.Race.Round);
                }
            }

            var perfectRows = playerPerfect
                .OrderByDescending(x => x.Value.Count)
                .Select(x => $"{playerIds[x.Key]?.PlayerName ?? "unknown"}: {x.Value.Count} race(s)")
                .ToList();
            sb.AppendLine(perfectRows.Count > 0 ? string.Join("\n", perfectRows.Take(5)) : "(none)");
            sb.AppendLine();

            sb.AppendLine("Missed opportunities (scoreable driver not picked by anyone):");
            var missedCount = 0;
            foreach (var race in completedRaces)
            {
                if (!race.RaceResults.Any(rr => rr.IsComplete)) continue;
                var mainResult = race.RaceResults.Where(rr => rr.IsComplete).OrderByDescending(rr => rr.Results.Count).FirstOrDefault();
                if (mainResult is null) continue;
                var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == race.Id);
                if (gameRace is null) continue;

                var prevRace = season.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < race.Round);
                var champPts = prevRace is not null ? data.ChampionshipPoints(season, prevRace) : [];
                var eligible = CCUtils.GetEligibleDriversWithPos(season, prevRace, (gs.PickRules ?? new PickRules()).PositionCutoff);

                var pickedDriverIds = new HashSet<ByteString>();
                foreach (var picks in gameRace.PicksPerPlayer)
                    foreach (var dId in picks.DriverId)
                        if (!dId.IsEmpty) pickedDriverIds.Add(dId);

                foreach (var e in eligible)
                {
                    var dr = mainResult.Results.FirstOrDefault(x => x.DriverId == e.driver.Id);
                    if (dr is not null && dr.Points > 0 && !pickedDriverIds.Contains(e.driver.Id))
                    {
                        missedCount++;
                        sb.AppendLine($"- Round {race.Round} {race.Name}: {driverMap.GetValueOrDefault(e.driver.Id)?.Name ?? "unknown"} (champ P{e.champPos}) scored but was not picked by anyone");
                    }
                }
            }
            sb.AppendLine(missedCount == 0 ? "(no missed opportunities this season)" : "");
            sb.AppendLine();

            sb.AppendLine("Zero-score weeks:");
            var zeroScoreWeeks = new Dictionary<ByteString, int>();
            foreach (var p in gs.ParticipatingPlayers)
                zeroScoreWeeks[p.Id] = 0;
            foreach (var rps in perRace)
                if (rps.Race.RaceResults.Any(rr => rr.IsComplete))
                    foreach (var kv in rps.ScoreByPlayer)
                        if ((kv.Value ?? 0f) <= 0f)
                            zeroScoreWeeks[kv.Key] = zeroScoreWeeks.GetValueOrDefault(kv.Key) + 1;

            var zeroRows = zeroScoreWeeks
                .OrderByDescending(x => x.Value)
                .Select(x => $"{playerIds[x.Key]?.PlayerName ?? "unknown"}: {x.Value} week(s)")
                .ToList();
            sb.AppendLine(zeroRows.Count > 0 ? string.Join("\n", zeroRows) : "(none)");

            sb.AppendLine();
            sb.AppendLine("League-wide most-picked driver per race:");
            foreach (var rps in perRace)
            {
                var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == rps.Race.Id);
                if (gameRace is null) continue;
                var pickFreq = new Dictionary<ByteString, int>();
                foreach (var picks in gameRace.PicksPerPlayer)
                    foreach (var dId in picks.DriverId)
                        if (!dId.IsEmpty) pickFreq[dId] = pickFreq.GetValueOrDefault(dId) + 1;
                if (pickFreq.Count > 0)
                {
                    var mostFreq = pickFreq.OrderByDescending(kv => kv.Value).First();
                    var d = driverMap.GetValueOrDefault(mostFreq.Key);
                    sb.AppendLine($"- Round {rps.Race.Round} {rps.Race.Name}: {d?.Name ?? "unknown"} (picked {mostFreq.Value} time(s))");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Computes deterministic head-to-head statistics between two drivers for the `/compare`
    /// slash command. All numbers come from real data — no LLM inference.
    /// </summary>
    public static string BuildCompareFacts(DataService data, Season season, Driver driverA, Driver driverB, Race cutoff)
    {
        var teamMap = season.Teams.ToDictionary(t => t.Id, t => t);
        var points = data.ChampionshipPoints(season, cutoff);
        var positions = data.ChampionshipPositions(points);

        var sb = new StringBuilder();
        sb.AppendLine($"**Championship**");
        sb.AppendLine($"- {driverA.Name}: P{positions.GetValueOrDefault(driverA.Id, 0)} — {points.GetValueOrDefault(driverA.Id, 0f):F1} pts");
        sb.AppendLine($"- {driverB.Name}: P{positions.GetValueOrDefault(driverB.Id, 0)} — {points.GetValueOrDefault(driverB.Id, 0f):F1} pts");
        sb.AppendLine();

        int aBeatsB = 0, bBeatsA = 0, bothDnf = 0, aDnf = 0, bDnf = 0, sharedRaces = 0;
        var aFinishes = new List<int>();
        var bFinishes = new List<int>();
        var aQuali = new List<int>();
        var bQuali = new List<int>();
        var aQualiBeatsB = 0;
        var bQualiBeatsA = 0;
        var aFastestLaps = new List<float>();
        var bFastestLaps = new List<float>();
        var aRaceTimes = new List<float>();
        var bRaceTimes = new List<float>();
        var aPracticeLaps = new List<float>();
        var bPracticeLaps = new List<float>();

        foreach (var race in season.Races.OrderBy(r => r.Round))
        {
            if (race.Round > cutoff.Round) break;
            var mainResult = race.RaceResults.Where(rr => rr.IsComplete).OrderByDescending(rr => rr.Results.Count).FirstOrDefault();
            if (mainResult is null) continue;

            var drA = mainResult.Results.FirstOrDefault(x => x.DriverId == driverA.Id);
            var drB = mainResult.Results.FirstOrDefault(x => x.DriverId == driverB.Id);
            if (drA is null || drB is null) continue;

            sharedRaces++;
            aFinishes.Add(drA.Position);
            bFinishes.Add(drB.Position);
            if (drA.Position < drB.Position) aBeatsB++;
            else if (drB.Position < drA.Position) bBeatsA++;
            else bothDnf++;

            if (drA.Status is FinishStatus.Dnf or FinishStatus.Dsq) aDnf++;
            if (drB.Status is FinishStatus.Dnf or FinishStatus.Dsq) bDnf++;

            // Race pace: fastest lap and race time (only meaningful when both finished).
            if (drA.FastestLapSeconds > 0) aFastestLaps.Add(drA.FastestLapSeconds);
            if (drB.FastestLapSeconds > 0) bFastestLaps.Add(drB.FastestLapSeconds);
            if (drA.RaceTime > 0 && drB.RaceTime > 0)
            {
                aRaceTimes.Add(drA.RaceTime);
                bRaceTimes.Add(drB.RaceTime);
            }

            // Qualifying: grid position + head-to-head quali battle.
            var qualiOrder = QualifyingOrder(race);
            var aIdx = qualiOrder.IndexOf(driverA.Id);
            var bIdx = qualiOrder.IndexOf(driverB.Id);
            if (aIdx >= 0) aQuali.Add(aIdx + 1);
            if (bIdx >= 0) bQuali.Add(bIdx + 1);
            if (aIdx >= 0 && bIdx >= 0)
            {
                if (aIdx < bIdx) aQualiBeatsB++;
                else if (bIdx < aIdx) bQualiBeatsA++;
            }

            // Practice pace: fastest lap across all practice sessions.
            foreach (var p in race.Practices)
            {
                var la = p.LapData.FirstOrDefault(ld => ld.DriverId == driverA.Id);
                var lb = p.LapData.FirstOrDefault(ld => ld.DriverId == driverB.Id);
                if (la is not null && la.FastestLapSeconds > 0) aPracticeLaps.Add(la.FastestLapSeconds);
                if (lb is not null && lb.FastestLapSeconds > 0) bPracticeLaps.Add(lb.FastestLapSeconds);
            }
        }

        sb.AppendLine($"**Head-to-head** ({sharedRaces} shared races)");
        sb.AppendLine($"- {driverA.Name} beat {driverB.Name}: {aBeatsB} time(s)");
        sb.AppendLine($"- {driverB.Name} beat {driverA.Name}: {bBeatsA} time(s)");
        sb.AppendLine($"- Both DNF/DSQ: {bothDnf} time(s)");
        sb.AppendLine();

        sb.AppendLine($"**Reliability**");
        sb.AppendLine($"- {driverA.Name}: {aDnf} DNF/DSQ, avg finish P{(aFinishes.Count > 0 ? aFinishes.Average() : 0):F1}");
        sb.AppendLine($"- {driverB.Name}: {bDnf} DNF/DSQ, avg finish P{(bFinishes.Count > 0 ? bFinishes.Average() : 0):F1}");
        sb.AppendLine();

        sb.AppendLine($"**Qualifying** (avg grid position)");
        sb.AppendLine($"- {driverA.Name}: P{(aQuali.Count > 0 ? aQuali.Average() : 0):F1} ({aQuali.Count} races)");
        sb.AppendLine($"- {driverB.Name}: P{(bQuali.Count > 0 ? bQuali.Average() : 0):F1} ({bQuali.Count} races)");
        sb.AppendLine($"- Quali head-to-head: {driverA.Name} ahead {aQualiBeatsB}×, {driverB.Name} ahead {bQualiBeatsA}×");
        sb.AppendLine();

        sb.AppendLine($"**Race pace** (fastest lap, avg across shared races)");
        sb.AppendLine($"- {driverA.Name}: {FormatLapTime(aFastestLaps.Count > 0 ? aFastestLaps.Average() : 0)} ({aFastestLaps.Count} races)");
        sb.AppendLine($"- {driverB.Name}: {FormatLapTime(bFastestLaps.Count > 0 ? bFastestLaps.Average() : 0)} ({bFastestLaps.Count} races)");
        if (aFastestLaps.Count > 0 && bFastestLaps.Count > 0)
        {
            var diff = aFastestLaps.Average() - bFastestLaps.Average();
            sb.AppendLine($"- Avg fastest-lap gap: {(diff < 0 ? driverA.Name : driverB.Name)} {(Math.Abs(diff) < 0.001 ? "level" : $"{Math.Abs(diff):F3}s faster")}");
        }
        if (aRaceTimes.Count > 0 && bRaceTimes.Count > 0)
        {
            var timeDiff = aRaceTimes.Average() - bRaceTimes.Average();
            sb.AppendLine($"- Avg race time: {driverA.Name} {FormatRaceTime(aRaceTimes.Average())} vs {driverB.Name} {FormatRaceTime(bRaceTimes.Average())}");
            sb.AppendLine($"- Avg race-time gap (both finished): {(timeDiff < 0 ? driverA.Name : driverB.Name)} {(Math.Abs(timeDiff) < 0.001 ? "level" : $"{Math.Abs(timeDiff):F1}s faster")}");
        }
        sb.AppendLine();

        sb.AppendLine($"**Practice pace** (fastest lap, all sessions)");
        sb.AppendLine($"- {driverA.Name}: {FormatLapTime(aPracticeLaps.Count > 0 ? aPracticeLaps.Average() : 0)} ({aPracticeLaps.Count} laps)");
        sb.AppendLine($"- {driverB.Name}: {FormatLapTime(bPracticeLaps.Count > 0 ? bPracticeLaps.Average() : 0)} ({bPracticeLaps.Count} laps)");
        if (aPracticeLaps.Count > 0 && bPracticeLaps.Count > 0)
        {
            var diff = aPracticeLaps.Average() - bPracticeLaps.Average();
            sb.AppendLine($"- Avg practice gap: {(diff < 0 ? driverA.Name : driverB.Name)} {(Math.Abs(diff) < 0.001 ? "level" : $"{Math.Abs(diff):F3}s faster")}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Computes deterministic head-to-head statistics between two confidence-cup players for the
    /// `/h2h` slash command. All numbers come from real data — no LLM inference.
    /// </summary>
    public static string BuildH2HFacts(DataService data, Season season, GameSeason gs, Player playerA, Player playerB, Race cutoff)
    {
        var rules = gs.PickRules ?? new PickRules();
        var driverMap = season.Drivers.ToDictionary(d => d.Id, d => d);
        var perRace = data.PlayerScoresPerRace(season, gs).Where(x => x.Race.Round <= cutoff.Round).ToList();
        var cumulative = data.PlayerScores(season, gs, cutoff);

        var sb = new StringBuilder();
        sb.AppendLine($"**Cumulative**");
        sb.AppendLine($"- {playerA.PlayerName}: {cumulative.GetValueOrDefault(playerA.Id):F1} pts");
        sb.AppendLine($"- {playerB.PlayerName}: {cumulative.GetValueOrDefault(playerB.Id):F1} pts");
        sb.AppendLine();

        int aWins = 0, bWins = 0, ties = 0, aNoPick = 0, bNoPick = 0;
        var aScores = new List<float>();
        var bScores = new List<float>();
        var sharedPicks = new Dictionary<ByteString, int>();
        var aOnlyPicks = new Dictionary<ByteString, int>();
        var bOnlyPicks = new Dictionary<ByteString, int>();

        foreach (var rps in perRace)
        {
            if (!rps.Race.RaceResults.Any(rr => rr.IsComplete)) continue;
            var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == rps.Race.Id);
            if (gameRace is null) continue;

            var aScore = rps.ScoreByPlayer.GetValueOrDefault(playerA.Id);
            var bScore = rps.ScoreByPlayer.GetValueOrDefault(playerB.Id);
            if (aScore is null) aNoPick++;
            if (bScore is null) bNoPick++;
            if (aScore is float av) aScores.Add(av);
            if (bScore is float bv) bScores.Add(bv);

            if (aScore is float av2 && bScore is float bv2)
            {
                if (av2 > bv2) aWins++;
                else if (bv2 > av2) bWins++;
                else ties++;
            }

            var picksA = gameRace.PicksPerPlayer.FirstOrDefault(pp => pp.PlayerId == playerA.Id);
            var picksB = gameRace.PicksPerPlayer.FirstOrDefault(pp => pp.PlayerId == playerB.Id);
            if (picksA is null || picksB is null) continue;

            var aSet = picksA.DriverId.Where(id => !id.IsEmpty).ToHashSet();
            var bSet = picksB.DriverId.Where(id => !id.IsEmpty).ToHashSet();
            foreach (var id in aSet)
            {
                if (bSet.Contains(id)) sharedPicks[id] = sharedPicks.GetValueOrDefault(id) + 1;
                else aOnlyPicks[id] = aOnlyPicks.GetValueOrDefault(id) + 1;
            }
            foreach (var id in bSet)
                if (!aSet.Contains(id)) bOnlyPicks[id] = bOnlyPicks.GetValueOrDefault(id) + 1;
        }

        sb.AppendLine($"**Head-to-head** (decided races, both submitted)");
        sb.AppendLine($"- {playerA.PlayerName} outscored {playerB.PlayerName}: {aWins} time(s)");
        sb.AppendLine($"- {playerB.PlayerName} outscored {playerA.PlayerName}: {bWins} time(s)");
        sb.AppendLine($"- Tied: {ties} time(s)");
        sb.AppendLine($"- {playerA.PlayerName} no-pick races: {aNoPick}, {playerB.PlayerName} no-pick races: {bNoPick}");
        sb.AppendLine();

        sb.AppendLine($"**Scoring consistency**");
        sb.AppendLine($"- {playerA.PlayerName}: avg {(aScores.Count > 0 ? aScores.Average() : 0):F1} pts/race, best {(aScores.Count > 0 ? aScores.Max() : 0):F1}");
        sb.AppendLine($"- {playerB.PlayerName}: avg {(bScores.Count > 0 ? bScores.Average() : 0):F1} pts/race, best {(bScores.Count > 0 ? bScores.Max() : 0):F1}");
        sb.AppendLine();

        sb.AppendLine($"**Shared picks** (both picked the same driver)");
        var sharedRows = sharedPicks.OrderByDescending(kv => kv.Value).Take(5)
            .Select(kv => $"- {driverMap.GetValueOrDefault(kv.Key)?.Name ?? "unknown"}: {kv.Value} time(s)")
            .ToList();
        sb.AppendLine(sharedRows.Count > 0 ? string.Join("\n", sharedRows) : "- (none)");
        sb.AppendLine();

        sb.AppendLine($"**{playerA.PlayerName}'s unique picks** (not picked by {playerB.PlayerName})");
        var aOnlyRows = aOnlyPicks.OrderByDescending(kv => kv.Value).Take(5)
            .Select(kv => $"- {driverMap.GetValueOrDefault(kv.Key)?.Name ?? "unknown"}: {kv.Value} time(s)")
            .ToList();
        sb.AppendLine(aOnlyRows.Count > 0 ? string.Join("\n", aOnlyRows) : "- (none)");
        sb.AppendLine();

        sb.AppendLine($"**{playerB.PlayerName}'s unique picks** (not picked by {playerA.PlayerName})");
        var bOnlyRows = bOnlyPicks.OrderByDescending(kv => kv.Value).Take(5)
            .Select(kv => $"- {driverMap.GetValueOrDefault(kv.Key)?.Name ?? "unknown"}: {kv.Value} time(s)")
            .ToList();
        sb.AppendLine(bOnlyRows.Count > 0 ? string.Join("\n", bOnlyRows) : "- (none)");

        return sb.ToString();
    }

    /// <summary>
    /// Computes a rolling confidence-cup projection for the `/projected` slash command: what the
    /// standings could look like after the next pickable race if each player picks the highest-
    /// multiplier eligible drivers. Deterministic — no LLM inference.
    /// </summary>
    public static string BuildProjectedFacts(DataService data, Season season, GameSeason gs, Race nextRace)
    {
        var rules = gs.PickRules ?? new PickRules();
        var driverMap = season.Drivers.ToDictionary(d => d.Id, d => d);
        var current = data.PlayerScores(season, gs, nextRace);

        // Eligibility for the next race is gated on the race immediately before it.
        var prevRace = season.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < nextRace.Round);
        var eligible = CCUtils.GetEligibleDriversWithPos(season, prevRace, rules.PositionCutoff)
            .OrderByDescending(e => CCUtils.GetStandingsMultiplier(rules, e.champPos))
            .ThenByDescending(e => e.champPos)
            .ToList();

        // Best-case: each player picks the top-N highest-multiplier eligible drivers.
        var bestPicks = eligible.Take(rules.NumPicks).Select(e => e.driver.Id).ToArray();
        float bestCaseGain = 0f;
        for (int i = 0; i < bestPicks.Length; i++)
            bestCaseGain += CCUtils.ScorePickSlot(season, rules, i, bestPicks[i], nextRace, eligible[i].champPos, hasResults: false);

        var sb = new StringBuilder();
        sb.AppendLine($"Projection for {nextRace.Name} (Round {nextRace.Round})");
        sb.AppendLine($"Best-case picks: {string.Join(", ", bestPicks.Select(id => driverMap.GetValueOrDefault(id)?.Name ?? "unknown"))}");
        sb.AppendLine($"Best-case gain per player: +{bestCaseGain:F1} pts");
        sb.AppendLine();

        var projected = gs.ParticipatingPlayers
            .Select(p => (p.PlayerName, p.Color, current: current.GetValueOrDefault(p.Id), projected: current.GetValueOrDefault(p.Id) + bestCaseGain))
            .OrderByDescending(x => x.projected)
            .ToList();

        sb.AppendLine("Projected standings (if everyone picks optimally):");
        for (int i = 0; i < projected.Count; i++)
        {
            var p = projected[i];
            var delta = p.projected - p.current;
            sb.AppendLine($"- P{i + 1}: {p.PlayerName} — {p.projected:F1} pts (current {p.current:F1}, +{delta:F1})");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a plain-text facts block for the `/team-analysis` LLM command. Every number, name,
    /// and event is computed from real protobuf data — the model only narrates, never computes.
    /// </summary>
    public static string BuildTeamFacts(DataService data, Season season, Team team, Race cutoff, League? league = null, GameSeason? gs = null)
    {
        var driverMap = season.Drivers.ToDictionary(d => d.Id, d => d);
        var teamDrivers = season.Drivers.Where(d => d.CurrentTeamId == team.Id).ToList();
        var manufacturer = season.Manufacturers.FirstOrDefault(m => m.Id == team.ManufacturerId);

        var sb = new StringBuilder();
        sb.AppendLine($"Team: {team.Name}, manufacturer: {manufacturer?.Name ?? "unknown"}");
        sb.AppendLine($"Drivers: {string.Join(", ", teamDrivers.Select(d => $"{d.Name} (#{d.Number})"))}");
        sb.AppendLine();

        // Constructor points: sum of both drivers' points across completed races.
        var points = data.ChampionshipPoints(season, cutoff);
        var teamPoints = teamDrivers.Sum(d => points.GetValueOrDefault(d.Id, 0f));
        var positions = data.ChampionshipPositions(points);
        var teamPos = teamDrivers.Count > 0 ? teamDrivers.Min(d => positions.GetValueOrDefault(d.Id, int.MaxValue)) : 0;
        sb.AppendLine($"Constructor points through {cutoff.Name}: {teamPoints:F1} (best driver championship position P{teamPos})");
        sb.AppendLine();

        sb.AppendLine("Race-by-race team results:");
        var raceLines = new List<string>();
        int podiums = 0, wins = 0, dnfs = 0, bothScored = 0, racesRun = 0;
        var driverFinishes = teamDrivers.ToDictionary(d => d.Id, _ => new List<int>());

        foreach (var race in season.Races.OrderBy(r => r.Round))
        {
            if (race.Round > cutoff.Round) break;
            var mainResult = race.RaceResults.Where(rr => rr.IsComplete).OrderByDescending(rr => rr.Results.Count).FirstOrDefault();
            if (mainResult is null) continue;

            var teamResults = mainResult.Results.Where(dr => teamDrivers.Any(d => d.Id == dr.DriverId)).OrderBy(dr => dr.Position).ToList();
            if (teamResults.Count == 0) continue;

            racesRun++;
            var resultText = string.Join(", ", teamResults.Select(dr =>
            {
                var d = driverMap.GetValueOrDefault(dr.DriverId);
                return $"P{dr.Position} {d?.Name ?? "unknown"} ({FormatStatus(dr.Status)})";
            }));
            raceLines.Add($"- Round {race.Round} {race.Name}: {resultText}");

            foreach (var dr in teamResults)
            {
                if (dr.Position == 1) wins++;
                if (dr.Position is >= 1 and <= 3) podiums++;
                if (dr.Status is FinishStatus.Dnf or FinishStatus.Dsq) dnfs++;
                if (dr.Points > 0 && driverFinishes.ContainsKey(dr.DriverId)) driverFinishes[dr.DriverId].Add(dr.Position);
            }
            if (teamResults.Count >= 2 && teamResults.All(dr => dr.Points > 0)) bothScored++;
        }
        sb.AppendLine(raceLines.Count > 0 ? string.Join("\n", raceLines) : "(no completed races)");
        sb.AppendLine();

        sb.AppendLine("Season aggregates:");
        sb.AppendLine($"- Wins: {wins}, podiums: {podiums}, DNF/DSQ: {dnfs}, races run: {racesRun}");
        sb.AppendLine($"- Both drivers scored in the same race: {bothScored} time(s)");
        foreach (var d in teamDrivers)
        {
            var finishes = driverFinishes.GetValueOrDefault(d.Id) ?? [];
            sb.AppendLine($"- {d.Name}: avg finish P{(finishes.Count > 0 ? finishes.Average() : 0):F1} ({finishes.Count} races)");
        }

        if (gs is not null && league is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Confidence-cup league: {league.LeagueName}");
            var rules = gs.PickRules ?? new PickRules();
            var nextRace = data.NextPickableRace(season);
            var prevRaceForEligibility = nextRace is not null
                ? season.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < nextRace.Round)
                : cutoff;
            var eligiblePairs = CCUtils.GetEligibleDriversWithPos(season, prevRaceForEligibility, rules.PositionCutoff);
            foreach (var d in teamDrivers)
            {
                var eligibleEntry = eligiblePairs.FirstOrDefault(x => x.driver.Id == d.Id);
                bool isEligible = eligibleEntry.driver is not null;
                sb.AppendLine($"- {d.Name} pick eligibility for {nextRace?.Name ?? "the next race"}: {(isEligible ? "eligible" : "not eligible")}" +
                    (isEligible && eligibleEntry.champPos > 0
                        ? $" (multiplier x{CCUtils.GetStandingsMultiplier(rules, eligibleEntry.champPos):G}, entering at P{eligibleEntry.champPos})"
                        : ""));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Computes deterministic head-to-head statistics between two teams for the `/team-analysis`
    /// slash command. All numbers come from real data — no LLM inference.
    /// </summary>
    public static string BuildTeamCompareFacts(DataService data, Season season, Team teamA, Team teamB, Race cutoff, League? league = null, GameSeason? gs = null)
    {
        var driverMap = season.Drivers.ToDictionary(d => d.Id, d => d);
        var teamADrivers = season.Drivers.Where(d => d.CurrentTeamId == teamA.Id).ToList();
        var teamBDrivers = season.Drivers.Where(d => d.CurrentTeamId == teamB.Id).ToList();
        var manA = season.Manufacturers.FirstOrDefault(m => m.Id == teamA.ManufacturerId);
        var manB = season.Manufacturers.FirstOrDefault(m => m.Id == teamB.ManufacturerId);

        var points = data.ChampionshipPoints(season, cutoff);
        var positions = data.ChampionshipPositions(points);
        var teamAPts = teamADrivers.Sum(d => points.GetValueOrDefault(d.Id, 0f));
        var teamBPts = teamBDrivers.Sum(d => points.GetValueOrDefault(d.Id, 0f));
        var teamAPos = teamADrivers.Count > 0 ? teamADrivers.Min(d => positions.GetValueOrDefault(d.Id, int.MaxValue)) : 0;
        var teamBPos = teamBDrivers.Count > 0 ? teamBDrivers.Min(d => positions.GetValueOrDefault(d.Id, int.MaxValue)) : 0;

        var sb = new StringBuilder();
        sb.AppendLine($"**Constructor points** (through {cutoff.Name})");
        sb.AppendLine($"- {teamA.Name}: {teamAPts:F1} pts (best driver P{teamAPos})");
        sb.AppendLine($"- {teamB.Name}: {teamBPts:F1} pts (best driver P{teamBPos})");
        sb.AppendLine();

        // Per-team aggregates across completed races.
        (int wins, int podiums, int dnfs, int bothScored, int racesRun, List<int> finishes) TeamAgg(Team team, List<Driver> drivers)
        {
            int wins = 0, podiums = 0, dnfs = 0, bothScored = 0, racesRun = 0;
            var finishes = new List<int>();
            foreach (var race in season.Races.OrderBy(r => r.Round))
            {
                if (race.Round > cutoff.Round) break;
                var mainResult = race.RaceResults.Where(rr => rr.IsComplete).OrderByDescending(rr => rr.Results.Count).FirstOrDefault();
                if (mainResult is null) continue;
                var teamResults = mainResult.Results.Where(dr => drivers.Any(d => d.Id == dr.DriverId)).OrderBy(dr => dr.Position).ToList();
                if (teamResults.Count == 0) continue;
                racesRun++;
                foreach (var dr in teamResults)
                {
                    if (dr.Position == 1) wins++;
                    if (dr.Position is >= 1 and <= 3) podiums++;
                    if (dr.Status is FinishStatus.Dnf or FinishStatus.Dsq) dnfs++;
                    if (dr.Points > 0) finishes.Add(dr.Position);
                }
                if (teamResults.Count >= 2 && teamResults.All(dr => dr.Points > 0)) bothScored++;
            }
            return (wins, podiums, dnfs, bothScored, racesRun, finishes);
        }

        var aggA = TeamAgg(teamA, teamADrivers);
        var aggB = TeamAgg(teamB, teamBDrivers);

        sb.AppendLine($"**Season aggregates**");
        sb.AppendLine($"- {teamA.Name}: {aggA.wins} wins, {aggA.podiums} podiums, {aggA.dnfs} DNF/DSQ, both scored {aggA.bothScored}×, avg finish P{(aggA.finishes.Count > 0 ? aggA.finishes.Average() : 0):F1}");
        sb.AppendLine($"- {teamB.Name}: {aggB.wins} wins, {aggB.podiums} podiums, {aggB.dnfs} DNF/DSQ, both scored {aggB.bothScored}×, avg finish P{(aggB.finishes.Count > 0 ? aggB.finishes.Average() : 0):F1}");
        sb.AppendLine();

        sb.AppendLine($"**Drivers**");
        foreach (var d in teamADrivers)
            sb.AppendLine($"- {teamA.Name}: {d.Name} (#{d.Number}) — P{positions.GetValueOrDefault(d.Id, 0)}, {points.GetValueOrDefault(d.Id, 0f):F1} pts");
        foreach (var d in teamBDrivers)
            sb.AppendLine($"- {teamB.Name}: {d.Name} (#{d.Number}) — P{positions.GetValueOrDefault(d.Id, 0)}, {points.GetValueOrDefault(d.Id, 0f):F1} pts");

        if (gs is not null && league is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"**Confidence-cup league**: {league.LeagueName}");
            var rules = gs.PickRules ?? new PickRules();
            var nextRace = data.NextPickableRace(season);
            var prevRaceForEligibility = nextRace is not null
                ? season.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < nextRace.Round)
                : cutoff;
            var eligiblePairs = CCUtils.GetEligibleDriversWithPos(season, prevRaceForEligibility, rules.PositionCutoff);
            foreach (var d in teamADrivers.Concat(teamBDrivers))
            {
                var eligibleEntry = eligiblePairs.FirstOrDefault(x => x.driver.Id == d.Id);
                bool isEligible = eligibleEntry.driver is not null;
                sb.AppendLine($"- {d.Name} pick eligibility for {nextRace?.Name ?? "the next race"}: {(isEligible ? "eligible" : "not eligible")}" +
                    (isEligible && eligibleEntry.champPos > 0
                        ? $" (multiplier x{CCUtils.GetStandingsMultiplier(rules, eligibleEntry.champPos):G}, entering at P{eligibleEntry.champPos})"
                        : ""));
            }
        }

        return sb.ToString();
    }

    private static string? GetLongestGap(List<(string driver, int round)> events, int totalRounds)
    {
        if (events.Count == 0) return null;
        var perDriver = events.GroupBy(e => e.driver)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.round).Select(e => e.round).ToList());

        string? result = null;
        int maxGap = 0;

        foreach (var kv in perDriver)
        {
            var rounds = kv.Value;
            int gapAfter = totalRounds - rounds[^1];
            if (gapAfter > maxGap)
            {
                maxGap = gapAfter;
                result = $"{kv.Key}: {maxGap} race(s) without a win (last win round {rounds[^1]})";
            }
        }

        var allGaps = new List<(string driver, int gap, int lastWin)>();
        foreach (var kv in perDriver)
        {
            var rounds = kv.Value;
            for (int i = 0; i < rounds.Count; i++)
            {
                var gapBefore = i == 0 ? rounds[0] - 1 : rounds[i] - rounds[i - 1] - 1;
                allGaps.Add((kv.Key, gapBefore, rounds[i]));
            }
            var gapAfter = totalRounds - rounds[^1];
            allGaps.Add((kv.Key, gapAfter, rounds[^1]));
        }

        var longest = allGaps.OrderByDescending(x => x.gap).FirstOrDefault();
        if (longest.gap > 0)
            return $"{longest.driver}: {longest.gap} race(s) without a win (last win round {longest.lastWin})";
        return result;
    }

    private static double GetStdDev(List<int> values)
    {
        if (values.Count <= 1) return 0;
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return Math.Sqrt(variance);
    }

    // Formats a lap time in seconds as m:ss.fff (e.g. 83.456 -> "1:23.456").
    private static string FormatLapTime(float seconds)
    {
        if (seconds <= 0) return "—";
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    // Formats a race time in seconds as h:mm:ss.fff (e.g. 5400.5 -> "1:30:00.500").
    private static string FormatRaceTime(float seconds)
    {
        if (seconds <= 0) return "—";
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }
}
