using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Google.Protobuf;
using GPConf.DiscordBot.Services;
using GPConf.Utilities;
using System.Text;

namespace GPConf.DiscordBot.Commands;

/// <summary>
/// DM-only read commands. Each maps to a shared DataService/CCUtils query and
/// returns a formatted embed. The bot never reimplements scoring/eligibility.
/// </summary>
public class ReadCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly DataService _data;

    public ReadCommands(DataService data)
    {
        _data = data;
    }

    [SlashCommand("standings", "Current championship standings")]
    public async Task Standings(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round (defaults to latest)")] string? race = null,
        [Summary("league", "League name (optional, enables eligibility/multiplier columns)")] string? league = null)
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }

        var target = race is null ? _data.LatestRace(s) : _data.FindRace(s, race);
        if (target is null) { await FollowupAsync($"Race '{race}' not found.", ephemeral: true); return; }

        await FollowupAsync(embed: BuildStandingsEmbed(_data, mainData, s, target, league), ephemeral: true);
    }

    // Shared with BotMcpTools.post_standings — the automation path posts the exact same embed
    // the slash command renders, no reimplementation.
    internal static Embed BuildStandingsEmbed(DataService data, MainData mainData, Season s, Race target, string? league)
    {
        var points = data.ChampionshipPoints(s, target);
        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d);
        var teamMap = s.Teams.ToDictionary(t => t.Id, t => t);

        // Eligibility/standings-multiplier columns need PickRules from a game season; without
        // one (no league configured, or none matches), standings still render without them.
        var (_, gs) = data.FindGameSeason(mainData, s, league);
        var rules = gs?.PickRules;
        // Mirrors the desktop app's "prevRace == null" special case (LeagueUpdater.DrawDriverStandings):
        // if target is the season's first scheduled race, there's no prior standings to gate
        // eligibility on, so every driver counts as eligible with a ×1 multiplier.
        var noPriorRace = !s.Races.Any(x => x.Round < target.Round);

        var ordered = points
            .Select(kv =>
            {
                var driver = driverMap.GetValueOrDefault(kv.Key);
                var team = driver is not null ? teamMap.GetValueOrDefault(driver.CurrentTeamId) : null;
                return (driver: DriverLabel(driver), team: TeamLabel(team), color: team?.Color ?? 0, points: kv.Value);
            })
            .OrderByDescending(x => x.points)
            .ToList();

        var rows = ordered
            .Select((x, i) =>
            {
                var pos = i + 1;
                var trailing = $"{x.points,5:F1}";
                if (rules is not null)
                    trailing += "  " + MultiplierLabel(rules, pos, noPriorRace);
                return FormatRow(pos, x.driver, x.team, trailing);
            })
            .ToList();

        var legend = rules is not null ? "×N = eligible (multiplier shown) · ❌ = ineligible\n" : "";
        return new EmbedBuilder()
            .WithTitle($"Championship Standings — {s.Name}")
            .WithDescription($"{legend}through {target.Name}\n```\n{string.Join("\n", rows)}\n```")
            .WithColor(ToDiscordColor(ordered.FirstOrDefault().color))
            .Build();
    }

    [SlashCommand("results", "Race results for a race")]
    public async Task Results(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round")] string race,
        [Summary("league", "League name (optional, enables confidence-cup pick attribution)")] string? league = null)
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }
        var r = _data.FindRace(s, race);
        if (r is null) { await FollowupAsync($"Race '{race}' not found.", ephemeral: true); return; }
        if (r.RaceResults.Count == 0) { await FollowupAsync($"No race results stored for '{r.Name}'.", ephemeral: true); return; }

        if (league is not null)
        {
            var (_, gs) = _data.FindGameSeason(mainData, s, league);
            await FollowupAsync(embed: BuildResultsEmbed(_data, s, r, gs), ephemeral: true);
            return;
        }

        var candidates = _data.LeaguesForSeason(mainData, s);
        if (candidates.Count <= 1)
        {
            await FollowupAsync(embed: BuildResultsEmbed(_data, s, r, candidates.Count == 1 ? candidates[0].gs : null), ephemeral: true);
            return;
        }

        // No league specified and more than one is configured for this season — try to guess
        // which one the caller belongs to from their Discord username before asking.
        var usernames = new[] { Context.User.Username, Context.User.GlobalName }
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var matches = candidates
            .Where(c => c.gs.ParticipatingPlayers.Any(p => usernames.Any(u => string.Equals(u, p.PlayerName, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (matches.Count == 1)
        {
            await FollowupAsync(embed: BuildResultsEmbed(_data, s, r, matches[0].gs), ephemeral: true);
            return;
        }

        // Ambiguous (matched none, or matched more than one league) — let the caller pick.
        var menu = new SelectMenuBuilder()
            .WithCustomId($"results_league:{Convert.ToHexString(s.Id.ToByteArray())}:{Convert.ToHexString(r.Id.ToByteArray())}")
            .WithPlaceholder("Choose a league")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var (leagueOption, _) in candidates)
            menu.AddOption(leagueOption.LeagueName, Convert.ToHexString(leagueOption.Id.ToByteArray()));
        await FollowupAsync(
            "Couldn't tell which league you're in — pick one to see confidence-cup pick attribution:",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(),
            ephemeral: true);
    }

    [ComponentInteraction("results_league:*:*")]
    public async Task ResultsLeagueSelected(string seasonIdHex, string raceIdHex, string[] selectedLeagueIds)
    {
        var mainData = _data.Load();
        var s = _data.FindSeasonById(mainData, ByteString.CopyFrom(Convert.FromHexString(seasonIdHex)));
        var r = s is not null ? _data.FindRaceById(s, ByteString.CopyFrom(Convert.FromHexString(raceIdHex))) : null;
        var league = _data.FindLeagueById(mainData, ByteString.CopyFrom(Convert.FromHexString(selectedLeagueIds[0])));
        var component = (SocketMessageComponent)Context.Interaction;

        if (s is null || r is null)
        {
            await component.UpdateAsync(m => { m.Content = "That season/race no longer exists."; m.Components = new ComponentBuilder().Build(); });
            return;
        }

        var gs = league?.Seasons.FirstOrDefault(gs => gs.SeasonId == s.Id);
        await component.UpdateAsync(m =>
        {
            m.Content = null;
            m.Embed = BuildResultsEmbed(_data, s, r, gs);
            m.Components = new ComponentBuilder().Build();
        });
    }

    // Flags each picked driver with a standings multiplier and an eligibility/scoring emoji —
    // no points, no player attribution. Eligibility and the multiplier are driver-only (same
    // standings position regardless of who picked them), so this needs only the set of picked
    // driver ids, not each player's individual pick score.
    // gs is null when no league's scoring rules could be resolved; results still render without it.
    // Shared with BotMcpTools.post_race_results — same reasoning as BuildStandingsEmbed above.
    internal static Embed BuildResultsEmbed(DataService data, Season s, Race r, GameSeason? gs)
    {
        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d);
        var teamMap = s.Teams.ToDictionary(t => t.Id, t => t);

        var pickedDriverIds = new HashSet<ByteString>();
        PickRules? rules = null;
        var champPos = new Dictionary<ByteString, int>();
        var noPriorRace = true;
        if (gs is not null)
        {
            rules = gs.PickRules ?? new PickRules();
            var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == r.Id);
            var prevRace = s.Races.OrderBy(x => x.Round).LastOrDefault(x => x.Round < r.Round);
            var champPts = prevRace is not null ? data.ChampionshipPoints(s, prevRace) : [];
            champPos = data.ChampionshipPositions(champPts);
            noPriorRace = prevRace is null;

            if (gameRace is not null)
                foreach (var picks in gameRace.PicksPerPlayer)
                    foreach (var dId in picks.DriverId)
                        if (!dId.IsEmpty) pickedDriverIds.Add(dId);
        }

        var sb = new StringBuilder();
        if (rules is not null)
            sb.AppendLine("✅×N = picked, eligible, scored · ➖×N = picked, eligible, no points · ❌ = picked, ineligible · ❗×N = eligible, unpicked, scored\n");
        uint winnerColor = 0;
        var first = true;
        foreach (var rr in r.RaceResults)
        {
            if (!first) sb.AppendLine();
            first = false;

            // Session name as real markdown bold — renders on every client, unlike ANSI bold
            // which only desktop understands. Markdown doesn't process inside code fences, so
            // the header has to live outside the ``` block.
            sb.AppendLine($"**{rr.RaceName}**");
            sb.AppendLine("```");
            foreach (var dr in rr.Results.OrderBy(x => x.Position))
            {
                var team = teamMap.GetValueOrDefault(dr.TeamId);
                if (dr.Position == 1) winnerColor = team?.Color ?? winnerColor;

                string pickStatus = "—";
                if (rules is not null)
                {
                    var picked = pickedDriverIds.Contains(dr.DriverId);
                    var mult = MultiplierLabel(rules, champPos.GetValueOrDefault(dr.DriverId, 0), noPriorRace);
                    var eligible = mult != "❌";
                    var scored = dr.Points > 0;
                    if (picked)
                        pickStatus = !eligible ? "❌" : (scored ? "✅" : "➖") + mult;
                    else if (eligible && scored)
                        // Nobody picked them, but they were eligible and scored — a missed opportunity worth flagging.
                        pickStatus = "❗" + mult;
                }

                var trailing = $"{FormatStatus(dr.Status),-8} {pickStatus}";
                sb.AppendLine(FormatRow(dr.Position, DriverLabel(driverMap.GetValueOrDefault(dr.DriverId)), TeamLabel(team), trailing));
            }
            sb.AppendLine("```");
        }

        return new EmbedBuilder()
            .WithTitle($"Race Results — {r.Name}")
            .WithDescription(sb.ToString())
            .WithColor(ToDiscordColor(winnerColor))
            .Build();
    }

    [SlashCommand("quali", "Qualifying results for a race")]
    public async Task Quali(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round")] string race)
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }
        var r = _data.FindRace(s, race);
        if (r is null) { await FollowupAsync($"Race '{race}' not found.", ephemeral: true); return; }
        if (r.QualifyingSessions.Count == 0) { await FollowupAsync($"No qualifying data stored for '{r.Name}'.", ephemeral: true); return; }

        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d);
        var teamMap = s.Teams.ToDictionary(t => t.Id, t => t);
        var allEntries = r.QualifyingSessions
            .SelectMany(qs => qs.LapData.Select(ld => (stage: qs.Stage, ld)))
            .GroupBy(x => x.ld.DriverId)
            .Select(g => g.OrderByDescending(x => x.stage).First())
            .ToList();

        var poleColor = allEntries
            .OrderBy(x => x.stage == 0 ? 0 : x.stage == 2 ? 1 : 2)
            .ThenBy(x => x.ld.FastestLapSeconds > 0 ? x.ld.FastestLapSeconds : float.MaxValue)
            .Select(x =>
            {
                var driver = driverMap.GetValueOrDefault(x.ld.DriverId);
                return driver is not null ? teamMap.GetValueOrDefault(driver.CurrentTeamId)?.Color ?? 0 : 0;
            })
            .FirstOrDefault();

        var ordered = allEntries
            .OrderBy(x => x.stage == 0 ? 0 : x.stage == 2 ? 1 : 2)
            .ThenBy(x => x.ld.FastestLapSeconds > 0 ? x.ld.FastestLapSeconds : float.MaxValue)
            .Select((x, i) =>
            {
                var driver = driverMap.GetValueOrDefault(x.ld.DriverId);
                var team = driver is not null ? teamMap.GetValueOrDefault(driver.CurrentTeamId) : null;
                return FormatRow(i + 1, DriverLabel(driver), TeamLabel(team), FormatLap(x.ld.FastestLapSeconds).PadLeft(9));
            })
            .ToList();

        var embed = new EmbedBuilder()
            .WithTitle($"Qualifying — {r.Name}")
            .WithDescription($"```\n{string.Join("\n", ordered)}\n```")
            .WithColor(ToDiscordColor(poleColor));
        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    [SlashCommand("practice", "Practice session results for a race")]
    public async Task Practice(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round")] string race,
        [Summary("session", "Practice session number (1, 2, or 3)")] int session)
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }
        var r = _data.FindRace(s, race);
        if (r is null) { await FollowupAsync($"Race '{race}' not found.", ephemeral: true); return; }

        var practice = r.Practices.FirstOrDefault(p => p.SessionNumber == session);
        if (practice is null) { await FollowupAsync($"No FP{session} data stored for '{r.Name}'.", ephemeral: true); return; }

        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d);
        var teamMap = s.Teams.ToDictionary(t => t.Id, t => t);
        var fastestEntries = practice.LapData
            .Where(ld => ld.FastestLapSeconds > 0)
            .OrderBy(ld => ld.FastestLapSeconds)
            .ToList();

        var rows = fastestEntries
            .Select((ld, i) =>
            {
                var driver = driverMap.GetValueOrDefault(ld.DriverId);
                var team = driver is not null ? teamMap.GetValueOrDefault(driver.CurrentTeamId) : null;
                return FormatRow(i + 1, DriverLabel(driver), TeamLabel(team), FormatLap(ld.FastestLapSeconds).PadLeft(9));
            })
            .ToList();

        var fastestColor = fastestEntries.Count > 0
            ? (driverMap.GetValueOrDefault(fastestEntries[0].DriverId) is { } fd ? teamMap.GetValueOrDefault(fd.CurrentTeamId)?.Color ?? 0 : 0)
            : 0;

        var embed = new EmbedBuilder()
            .WithTitle($"FP{session} — {r.Name}")
            .WithDescription($"```\n{string.Join("\n", rows)}\n```")
            .WithColor(ToDiscordColor(fastestColor));
        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    [SlashCommand("scores", "Cumulative player confidence-cup scores")]
    public async Task Scores(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round (defaults to latest)")] string? race = null,
        [Summary("league", "League name (optional)")] string? league = null)
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }
        var target = race is null ? _data.LatestRace(s) : _data.FindRace(s, race);
        if (target is null) { await FollowupAsync($"Race '{race}' not found.", ephemeral: true); return; }

        var (_, gs) = _data.FindGameSeason(mainData, s, league);
        if (gs is null) { await FollowupAsync($"No game season found for '{s.Name}'.", ephemeral: true); return; }

        await FollowupAsync(embed: BuildScoresEmbed(_data, s, gs, target), ephemeral: true);
    }

    // Shared with BotMcpTools.post_standings (confidence-cup variant) — same reasoning as
    // BuildStandingsEmbed above.
    internal static Embed BuildScoresEmbed(DataService data, Season s, GameSeason gs, Race target)
    {
        var scores = data.PlayerScores(s, gs, target);
        var rankedPlayers = gs.ParticipatingPlayers
            .Select(p => new { p.PlayerName, p.Color, score = scores.GetValueOrDefault(p.Id) })
            .OrderByDescending(x => x.score)
            .ToList();
        var rows = rankedPlayers
            .Select((x, i) => FormatScoreRow(i + 1, x.PlayerName, x.score))
            .ToList();

        return new EmbedBuilder()
            .WithTitle($"Confidence Cup Scores — {s.Name}")
            .WithDescription($"through {target.Name}\n```\n{string.Join("\n", rows)}\n```")
            .WithColor(ToDiscordColor(rankedPlayers.FirstOrDefault()?.Color ?? 0))
            .Build();
    }

    [SlashCommand("pick", "A player's picks and scores for a race")]
    public async Task Pick(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round")] string race,
        [Summary("league", "League name (optional)")] string? league = null)
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }
        var r = _data.FindRace(s, race);
        if (r is null) { await FollowupAsync($"Race '{race}' not found.", ephemeral: true); return; }

        var (_, gs) = _data.FindGameSeason(mainData, s, league);
        if (gs is null) { await FollowupAsync($"No game season found for '{s.Name}'.", ephemeral: true); return; }

        var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == r.Id);
        if (gameRace is null) { await FollowupAsync($"No picks recorded for '{r.Name}'.", ephemeral: true); return; }

        // Other players' picks stay secret until the race has results — otherwise this command
        // could be used to scout an opponent's picks for a race that hasn't happened yet. Once
        // there's a result, picks are fair game (the whole point of a post-race recap). This gate
        // is deliberately only applied here, not inside BuildPickEmbed — BotMcpTools.post_pick_reveal
        // calls BuildPickEmbed directly with every participating player, for the one intentional
        // moment (the pick deadline, via the Pick Lock-In skill) that calls for an unconditional reveal.
        bool raceDecided = r.RaceResults.Any(rr => rr.IsComplete);
        var visiblePlayers = gs.ParticipatingPlayers.AsEnumerable();
        if (!raceDecided)
        {
            var usernames = new[] { Context.User.Username, Context.User.GlobalName }
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            visiblePlayers = gs.ParticipatingPlayers
                .Where(p => usernames.Any(u => string.Equals(u, p.PlayerName, StringComparison.OrdinalIgnoreCase)));
        }

        await FollowupAsync(embed: BuildPickEmbed(_data, s, gs, r, gameRace, visiblePlayers), ephemeral: true);
    }

    // Shared with BotMcpTools.post_pick_reveal — see the secrecy-gate comment above for why the
    // gate itself lives in the Pick command, not here.
    internal static Embed BuildPickEmbed(DataService data, Season s, GameSeason gs, Race r, GameRace gameRace, IEnumerable<Player> visiblePlayers)
    {
        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d);
        var teamMap = s.Teams.ToDictionary(t => t.Id, t => t);
        var rules = gs.PickRules ?? new PickRules();
        var prevRace = s.Races.OrderBy(x => x.Round).LastOrDefault(x => x.Round < r.Round);
        var champPts = prevRace is not null ? data.ChampionshipPoints(s, prevRace) : [];
        var champPos = data.ChampionshipPositions(champPts);

        var standings = visiblePlayers
            .Select(player =>
            {
                var picks = gameRace.PicksPerPlayer.FirstOrDefault(pp => pp.PlayerId == player.Id);
                float total = 0f;
                var rows = new List<(string driver, string team, float score)>();
                if (picks is not null)
                {
                    bool hasResults = r.RaceResults.Any(rr => rr.IsComplete);
                    for (int i = 0; i < picks.DriverId.Count; i++)
                    {
                        var dId = picks.DriverId[i];
                        int pos = champPos.GetValueOrDefault(dId, 0);
                        float score = CCUtils.ScorePickSlot(s, rules, i, dId, r, pos, hasResults);
                        total += score;

                        var driver = driverMap.GetValueOrDefault(dId);
                        var team = driver is not null ? teamMap.GetValueOrDefault(driver.CurrentTeamId) : null;
                        var driverLabel = dId.IsEmpty ? "—" : DriverLabel(driver);
                        rows.Add((driverLabel, dId.IsEmpty ? "—" : TeamLabel(team), score));
                    }
                }
                return (player.PlayerName, player.Color, total, rows);
            })
            // Ranked best to worst for the race weekend, as requested — ties keep participant order.
            .OrderByDescending(x => x.total)
            .ToList();

        var sb = new StringBuilder();
        var first = true;
        for (int rank = 0; rank < standings.Count; rank++)
        {
            var (playerName, _, total, rows) = standings[rank];
            if (!first) sb.AppendLine();
            first = false;

            var medal = rank switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"{rank + 1,2}." };
            // Player name as real markdown bold, outside the code fence (markdown doesn't render
            // inside ``` blocks), so each player's pick table reads as its own labeled section.
            sb.AppendLine($"{medal} **{playerName}** — {total:F1} pts");
            sb.AppendLine("```");
            var bestScore = rows.Count > 0 ? rows.Max(x => x.score) : 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                // 🔥 flags the standout pick of the weekend, ➖ flags a pick that scored nothing.
                var marker = rows[i].score <= 0 ? "➖" : rows[i].score == bestScore && bestScore > 0 ? "🔥" : "";
                sb.AppendLine($"{FormatPickLine(i + 1, rows[i].driver, rows[i].team, rows[i].score)} {marker}");
            }
            sb.AppendLine("```");
        }

        return new EmbedBuilder()
            .WithTitle($"🏁 Picks — {r.Name}")
            .WithDescription(sb.ToString())
            .WithColor(ToDiscordColor(standings.Count > 0 ? standings[0].Color : 0))
            .Build();
    }

    [SlashCommand("rules", "Current season points rules and multipliers")]
    public async Task Rules(
        [Summary("season", "Season name or year")] string season)
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }

        await FollowupAsync(embed: BuildRulesEmbed(s), ephemeral: true);
    }

    // Shared with BotMcpTools — no automation tool wraps this directly yet, but it follows the
    // same reuse pattern as every other builder here.
    internal static Embed BuildRulesEmbed(Season s)
    {
        var rules = s.Rules;
        var sb = new StringBuilder();
        if (rules.Count == 0)
        {
            sb.AppendLine("No points rules configured.");
        }
        else
        {
            foreach (var r in rules)
            {
                sb.AppendLine($"**{r.Name}**");
                sb.AppendLine($"Multiplier: {r.ConfCupMultiplier:F2}");
                sb.AppendLine("Points: " + string.Join(", ", r.Score));
                sb.AppendLine();
            }
        }

        return new EmbedBuilder().WithTitle($"Points Rules — {s.Name}").WithDescription(sb.ToString()).Build();
    }

    private static string FormatLap(float? seconds) =>
        seconds is null ? "—" : $"{seconds:F3}s";

    // Presence of a multiplier already implies eligible, so a separate checkmark is redundant —
    // this keeps the marker short enough to fit on one line in Discord's narrow mobile embed
    // width instead of wrapping onto its own line. Shared by /standings and /results.
    private static string MultiplierLabel(PickRules rules, int champPos, bool noPriorRace)
    {
        if (noPriorRace) return "×1";
        var eligible = champPos > rules.PositionCutoff;
        return eligible ? $"×{CCUtils.GetStandingsMultiplier(rules, champPos):G}" : "❌";
    }

    private static string FormatStatus(FinishStatus status) => status switch
    {
        FinishStatus.Finished => "Finished",
        FinishStatus.Dnf => "DNF",
        FinishStatus.Dns => "DNS",
        FinishStatus.Dsq => "DSQ",
        _ => "—",
    };

    // Driver/team columns hold abbreviations now (e.g. "LN4", "MCL"), so they stay narrow.
    // Player names (scores/pick headers) aren't abbreviated and keep their own, wider column.
    private const int DriverWidth = 5;
    private const int TeamWidth = 3;
    private const int PlayerNameWidth = 17;

    private static string FormatPickLine(int rank, string driverName, string teamName, float score) =>
        FormatRow(rank, driverName, teamName, $"{score,6:F1} pts");

    // Lays out rank/name/team/trailing in fixed-width columns inside a plain (non-ansi) code
    // fence. Team/player color no longer gets a per-row indicator (dropped in favor of the
    // single embed accent stripe below) — just the aligned columns.
    private static string FormatRow(int rank, string driverName, string teamName, string trailing, int nameWidth = DriverWidth, int teamWidth = TeamWidth)
    {
        var name = Truncate(driverName, nameWidth).PadRight(nameWidth);
        var team = Truncate(teamName, teamWidth).PadRight(teamWidth);
        return $"{rank,2}. {name} {team} {trailing}";
    }

    // Ranked player-score row (no team column); rank 1 gets a trailing crown since bold styling
    // isn't available inside a code fence.
    private static string FormatScoreRow(int rank, string playerName, float score)
    {
        var name = Truncate(playerName, PlayerNameWidth).PadRight(PlayerNameWidth);
        var crown = rank == 1 ? " 👑" : "";
        return $"{rank,2}. {name} {score,6:F1} pts{crown}";
    }

    private static string Truncate(string s, int width) => s.Length <= width ? s : s[..width];

    // "Lando Norris" (#4) -> "LN4". Suffixes like "Jr."/"III" are dropped so the last initial
    // comes from the actual surname (e.g. "Carlos Sainz Jr." -> "CS55", not "CJ55").
    private static readonly HashSet<string> NameSuffixes = new(StringComparer.OrdinalIgnoreCase)
        { "Jr.", "Jr", "Sr.", "Sr", "II", "III", "IV" };

    private static string DriverLabel(Driver? driver)
    {
        if (driver is null) return "??";
        var parts = driver.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !NameSuffixes.Contains(p))
            .ToArray();
        var initials = parts.Length switch
        {
            0 => "??",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}",
        };
        return $"{initials}{driver.Number}";
    }

    // "Red Bull" -> "RB", "Mercedes" -> "MER": word initials for multi-word names,
    // first three letters for single-word names.
    private static string TeamLabel(Team? team)
    {
        if (team is null) return "—";
        var words = team.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1)
            return words[0][..Math.Min(3, words[0].Length)].ToUpperInvariant();
        var initials = string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
        return initials.Length > 3 ? initials[..3] : initials;
    }

    // Packed 0x00RRGGBB -> embed accent color, used for the single per-embed accent stripe,
    // which (unlike ANSI text color) Discord renders identically on every client.
    private static Color ToDiscordColor(uint packedRgb) =>
        packedRgb == 0 ? Color.Default : new Color((byte)((packedRgb >> 16) & 0xFF), (byte)((packedRgb >> 8) & 0xFF), (byte)(packedRgb & 0xFF));
}
