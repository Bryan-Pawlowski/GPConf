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
    // Discord embed description cap is 4096; leave margin.
    private const int MaxDescriptionLength = 3900;
    // Discord embed field-value cap is 1024; leave margin.
    private const int MaxFieldLength = 1000;

    private readonly DataService _data;
    private readonly AnalysisSessionStore _analysisSessions;

    public ReadCommands(DataService data, AnalysisSessionStore analysisSessions)
    {
        _data = data;
        _analysisSessions = analysisSessions;
    }

    [SlashCommand("standings", "Current championship standings")]
    public async Task Standings()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Standings, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var components = BuildReadComponents(mainData, token, session, season, disabled: false);
        await FollowupAsync("Select a season, race, and league, then click Generate:", components: components, ephemeral: true);
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
    public async Task Results()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Results, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var components = BuildReadComponents(mainData, token, session, season, disabled: false);
        await FollowupAsync("Select a season, race, and league, then click Generate:", components: components, ephemeral: true);
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
    public async Task Quali()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Quali, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var components = BuildReadComponents(mainData, token, session, season, disabled: false);
        await FollowupAsync("Select a season and race, then click Generate:", components: components, ephemeral: true);
    }

    internal static Embed BuildQualiEmbed(DataService data, Season s, Race r)
    {
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

        return new EmbedBuilder()
            .WithTitle($"Qualifying — {r.Name}")
            .WithDescription($"```\n{string.Join("\n", ordered)}\n```")
            .WithColor(ToDiscordColor(poleColor))
            .Build();
    }

    [SlashCommand("practice", "Practice session results for a race")]
    public async Task Practice()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Practice, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var components = BuildReadComponents(mainData, token, session, season, disabled: false);
        await FollowupAsync("Select a season, race, and session, then click Generate:", components: components, ephemeral: true);
    }

    internal static Embed BuildPracticeEmbed(DataService data, Season s, Race r, int session)
    {
        var practice = r.Practices.FirstOrDefault(p => p.SessionNumber == session);
        if (practice is null) return new EmbedBuilder().WithDescription($"No FP{session} data stored for '{r.Name}'.").Build();

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

        return new EmbedBuilder()
            .WithTitle($"FP{session} — {r.Name}")
            .WithDescription($"```\n{string.Join("\n", rows)}\n```")
            .WithColor(ToDiscordColor(fastestColor))
            .Build();
    }

    [SlashCommand("pick", "A player's picks and scores for a race")]
    public async Task Pick()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Pick, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var components = BuildReadComponents(mainData, token, session, season, disabled: false);
        await FollowupAsync("Select a season, race, and league, then click Generate:", components: components, ephemeral: true);
    }

    // Applies the pick-secrecy gate: other players' picks stay secret until the race has results —
    // otherwise this command could be used to scout an opponent's picks for a race that hasn't
    // happened yet. Once there's a result, picks are fair game. This gate is deliberately only
    // applied here, not inside BuildPickEmbed — BotMcpTools.post_pick_reveal calls BuildPickEmbed
    // directly with every participating player, for the one intentional moment (the pick deadline,
    // via the Pick Lock-In skill) that calls for an unconditional reveal.
    private IEnumerable<Player> VisiblePlayers(Season s, Race r, GameSeason gs)
    {
        bool raceDecided = r.RaceResults.Any(rr => rr.IsComplete);
        if (raceDecided) return gs.ParticipatingPlayers;

        var usernames = new[] { Context.User.Username, Context.User.GlobalName }
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return gs.ParticipatingPlayers
            .Where(p => usernames.Any(u => string.Equals(u, p.PlayerName, StringComparison.OrdinalIgnoreCase)));
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
        for (int rank = 0; rank < standings.Count; rank++)
        {
            var (playerName, _, total, rows) = standings[rank];

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
    public async Task Rules()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Rules, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var components = BuildReadComponents(mainData, token, session, season, disabled: false);
        await FollowupAsync("Select a season, then click Generate:", components: components, ephemeral: true);
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

    internal static Embed BuildSeasonStatsEmbed(Season s, Race target, string facts)
    {
        var description = facts.Length > MaxDescriptionLength ? facts[..MaxDescriptionLength] + "…" : facts;
        return new EmbedBuilder()
            .WithTitle($"Season Stats — {s.Name}")
            .WithDescription($"through {target.Name}\n```\n{description}\n```")
            .Build();
    }

    // Shared with BotMcpTools.post_leaderboard. Renders the top 3 as
    // medal-marked prose (markdown bold, outside the code fence) and the rest as a ranked code
    // fence, with a momentum column showing rank change vs. the previous race.
    internal static Embed BuildLeaderboardEmbed(DataService data, Season s, GameSeason gs, Race target)
    {
        var scores = data.PlayerScores(s, gs, target);
        var ranked = gs.ParticipatingPlayers
            .Select(p => new { p.PlayerName, p.Color, score = scores.GetValueOrDefault(p.Id) })
            .OrderByDescending(x => x.score)
            .ToList();

        // Momentum: rank change vs. the previous race's cumulative standings.
        var prevRace = s.Races.OrderBy(r => r.Round).LastOrDefault(r => r.Round < target.Round);
        var prevScores = prevRace is not null ? data.PlayerScores(s, gs, prevRace) : null;
        var prevRanked = prevScores is not null
            ? gs.ParticipatingPlayers.Select(p => p.Id).OrderByDescending(id => prevScores.GetValueOrDefault(id)).ToList()
            : null;

        var sb = new StringBuilder();
        var medals = new[] { "🥇", "🥈", "🥉" };
        for (int i = 0; i < ranked.Count; i++)
        {
            var p = ranked[i];
            var momentum = MomentumLabel(prevRanked, gs, p.PlayerName, i + 1);
            if (i < 3)
            {
                sb.AppendLine($"{medals[i]} **{p.PlayerName}** — {p.score:F1} pts {momentum}");
            }
            else
            {
                sb.AppendLine($"{i + 1,2}. {p.PlayerName} — {p.score:F1} pts {momentum}");
            }
        }

        return new EmbedBuilder()
            .WithTitle($"🏆 Confidence Cup Leaderboard — {s.Name}")
            .WithDescription($"through {target.Name}\n{sb}")
            .WithColor(ToDiscordColor(ranked.FirstOrDefault()?.Color ?? 0))
            .Build();
    }

    // Returns a compact momentum marker: ↑N / ↓N for rank movement, → for no change, or empty
    // when there's no prior race to compare against.
    private static string MomentumLabel(List<ByteString>? prevRanked, GameSeason gs, string playerName, int currentRank)
    {
        if (prevRanked is null) return "";
        var player = gs.ParticipatingPlayers.FirstOrDefault(p => p.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase));
        if (player is null) return "";
        var prevIdx = prevRanked.IndexOf(player.Id);
        if (prevIdx < 0) return "";
        var prevRank = prevIdx + 1;
        if (prevRank == currentRank) return "→";
        return prevRank > currentRank ? $"↑{prevRank - currentRank}" : $"↓{currentRank - prevRank}";
    }

    internal static Embed BuildCompareEmbed(Season s, Driver d1, Driver d2, Race target, string facts)
    {
        return new EmbedBuilder()
            .WithTitle($"⚔️ {d1.Name} vs {d2.Name}")
            .WithDescription($"through {target.Name}")
            .WithFields(ParseFactsFields(facts))
            .Build();
    }

    internal static Embed BuildH2HEmbed(Season s, Player p1, Player p2, Race target, string facts)
    {
        return new EmbedBuilder()
            .WithTitle($"⚔️ {p1.PlayerName} vs {p2.PlayerName}")
            .WithDescription($"through {target.Name}")
            .WithFields(ParseFactsFields(facts))
            .Build();
    }

    internal static Embed BuildTeamCompareEmbed(Season s, Team t1, Team t2, Race target, string facts)
    {
        return new EmbedBuilder()
            .WithTitle($"🏎️ {t1.Name} vs {t2.Name}")
            .WithDescription($"through {target.Name}")
            .WithFields(ParseFactsFields(facts))
            .Build();
    }

    // Splits a markdown facts block on "**Header**" lines into embed fields, so the static info
    // renders as clean titled sections rather than one wall-of-text code fence.
    private static List<EmbedFieldBuilder> ParseFactsFields(string facts)
    {
        var fields = new List<EmbedFieldBuilder>();
        string? currentTitle = null;
        var currentBody = new StringBuilder();

        void Flush()
        {
            if (currentTitle is not null)
            {
                var body = currentBody.ToString().Trim();
                if (body.Length > 0)
                {
                    if (body.Length > MaxFieldLength) body = body[..MaxFieldLength] + "…";
                    fields.Add(new EmbedFieldBuilder().WithName(currentTitle).WithValue(body).WithIsInline(false));
                }
            }
            currentBody.Clear();
        }

        foreach (var line in facts.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("**") && trimmed.EndsWith("**") && trimmed.Length > 4)
            {
                Flush();
                currentTitle = trimmed[2..^2];
            }
            else if (currentTitle is not null)
            {
                currentBody.AppendLine(line);
            }
        }
        Flush();
        return fields;
    }

    internal static Embed BuildProjectedEmbed(Season s, Race nextRace, string facts)
    {
        var description = facts.Length > MaxDescriptionLength ? facts[..MaxDescriptionLength] + "…" : facts;
        return new EmbedBuilder()
            .WithTitle($"🔮 Projected Standings — {s.Name}")
            .WithDescription($"```\n{description}\n```")
            .Build();
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

    // ── Read-command dropdown wizard ─────────────────────────────────────────
    // The six read commands (/standings, /results, /quali, /practice, /pick, /rules) are
    // parameterless and open an ephemeral dropdown wizard, per the "any command needing structured
    // input uses a wizard" convention. Unlike the analysis wizard, the result stays ephemeral in
    // the DM (no public post, no regeneration-keep) — it's a one-shot lookup. Reuses
    // AnalysisSessionStore for the token/TTL/CallerId machinery and the shared dropdown builders
    // from AnalysisCommands.

    [ComponentInteraction("read_season:*")]
    public async Task ReadSeasonSelected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ReadExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await ReadNotYoursAsync(); return; }

        var mainData = _data.Load();
        var seasonId = ByteString.CopyFrom(Convert.FromHexString(selectedValues[0]));
        var season = _data.FindSeasonById(mainData, seasonId);
        if (season is null) { await ReadGoneAsync(component); return; }

        var updated = session with { SeasonId = seasonId, RaceId = null, LeagueId = null, SessionNumber = null };
        _analysisSessions.Update(token, updated);
        await ReadRebuildAsync(component, mainData, token, updated);
    }

    [ComponentInteraction("read_race:*")]
    public async Task ReadRaceSelected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ReadExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await ReadNotYoursAsync(); return; }

        var mainData = _data.Load();
        var raceId = ByteString.CopyFrom(Convert.FromHexString(selectedValues[0]));
        var updated = session with { RaceId = raceId };
        _analysisSessions.Update(token, updated);
        await ReadRebuildAsync(component, mainData, token, updated);
    }

    [ComponentInteraction("read_league:*")]
    public async Task ReadLeagueSelected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ReadExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await ReadNotYoursAsync(); return; }

        var mainData = _data.Load();
        var leagueId = ByteString.CopyFrom(Convert.FromHexString(selectedValues[0]));
        var updated = session with { LeagueId = leagueId };
        _analysisSessions.Update(token, updated);
        await ReadRebuildAsync(component, mainData, token, updated);
    }

    [ComponentInteraction("read_session:*")]
    public async Task ReadSessionSelected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ReadExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await ReadNotYoursAsync(); return; }

        var mainData = _data.Load();
        var updated = session with { SessionNumber = int.Parse(selectedValues[0]) };
        _analysisSessions.Update(token, updated);
        await ReadRebuildAsync(component, mainData, token, updated);
    }

    [ComponentInteraction("read_generate:*")]
    public async Task ReadGenerate(string token)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ReadExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await ReadNotYoursAsync(); return; }

        var mainData = _data.Load();
        var s = _data.FindSeasonById(mainData, session.SeasonId);
        if (s is null) { await ReadGoneAsync(component); return; }

        Embed? embed = null;
        string? error = null;
        switch (session.Kind)
        {
            case AnalysisKind.Standings:
            {
                var target = ResolveReadRace(s, session);
                if (target is null) { error = "⚠️ No races configured for this season."; break; }
                var leagueName = ResolveReadLeagueName(mainData, s, session);
                embed = BuildStandingsEmbed(_data, mainData, s, target, leagueName);
                break;
            }
            case AnalysisKind.Results:
            {
                var target = ResolveReadRace(s, session);
                if (target is null) { error = "⚠️ No races configured for this season."; break; }
                if (target.RaceResults.Count == 0) { error = $"No race results stored for '{target.Name}'."; break; }
                var gs = ResolveReadGameSeason(mainData, s, session);
                embed = BuildResultsEmbed(_data, s, target, gs);
                break;
            }
            case AnalysisKind.Quali:
            {
                var target = ResolveReadRace(s, session);
                if (target is null) { error = "⚠️ No races configured for this season."; break; }
                if (target.QualifyingSessions.Count == 0) { error = $"No qualifying data stored for '{target.Name}'."; break; }
                embed = BuildQualiEmbed(_data, s, target);
                break;
            }
            case AnalysisKind.Practice:
            {
                var target = ResolveReadRace(s, session);
                if (target is null) { error = "⚠️ No races configured for this season."; break; }
                if (session.SessionNumber is not { } sn) { error = "⚠️ Select a practice session before clicking Generate."; break; }
                embed = BuildPracticeEmbed(_data, s, target, sn);
                break;
            }
            case AnalysisKind.Pick:
            {
                var target = ResolveReadRace(s, session);
                if (target is null) { error = "⚠️ No races configured for this season."; break; }
                var gs = ResolveReadGameSeason(mainData, s, session);
                if (gs is null) { error = $"No game season found for '{s.Name}'."; break; }
                var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == target.Id);
                if (gameRace is null) { error = $"No picks recorded for '{target.Name}'."; break; }
                embed = BuildPickEmbed(_data, s, gs, target, gameRace, VisiblePlayers(s, target, gs));
                break;
            }
            case AnalysisKind.Rules:
                embed = BuildRulesEmbed(s);
                break;
            default:
                return;
        }

        if (error is not null)
        {
            await component.UpdateAsync(m => { m.Content = error; m.Components = new ComponentBuilder().Build(); });
            return;
        }

        // Ephemeral one-shot result: replace the wizard with the embed, no public post.
        await component.UpdateAsync(m => { m.Content = null; m.Embed = embed; m.Components = new ComponentBuilder().Build(); });
        _analysisSessions.Remove(token);
    }

    // Renders the dropdowns + Generate button for the read wizard. The sequence depends on the
    // kind; the league dropdown only appears when the season has more than one configured league
    // (0 or 1 auto-resolve silently, same convention as the analysis wizard).
    private MessageComponent BuildReadComponents(
        MainData mainData, string token, AnalysisSession session, Season season, bool disabled)
    {
        var menus = new List<SelectMenuBuilder>();
        menus.Add(AnalysisCommands.BuildSeasonMenu("read_season", token, mainData, season));

        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        ByteString? resolvedLeagueId = leagueOptions.Count == 1 ? leagueOptions[0].league.Id : session.LeagueId;

        switch (session.Kind)
        {
            case AnalysisKind.Rules:
                break;
            case AnalysisKind.Quali:
                menus.Add(AnalysisCommands.BuildRaceMenu(token, season, session.RaceId, _data.LatestRace(season)?.Id));
                break;
            case AnalysisKind.Practice:
                menus.Add(AnalysisCommands.BuildRaceMenu(token, season, session.RaceId, _data.LatestRace(season)?.Id));
                menus.Add(BuildSessionMenu(token, session.SessionNumber));
                break;
            case AnalysisKind.Standings:
            case AnalysisKind.Results:
            case AnalysisKind.Pick:
                menus.Add(AnalysisCommands.BuildRaceMenu(token, season, session.RaceId, _data.LatestRace(season)?.Id));
                if (leagueOptions.Count > 1) menus.Add(AnalysisCommands.BuildLeagueMenu(token, leagueOptions, resolvedLeagueId));
                break;
        }

        var builder = new ComponentBuilder();
        for (int i = 0; i < menus.Count; i++)
            builder.WithSelectMenu(menus[i], row: i);
        builder.WithButton("📊 Generate", $"read_generate:{token}", ButtonStyle.Primary, row: menus.Count, disabled: disabled);
        return builder.Build();
    }

    private static SelectMenuBuilder BuildSessionMenu(string token, int? sessionNumber)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"read_session:{token}")
            .WithPlaceholder("Choose a practice session")
            .WithMinValues(1)
            .WithMaxValues(1);
        for (int i = 1; i <= 3; i++)
            menu.AddOption($"FP{i}", i.ToString(), isDefault: sessionNumber == i);
        return menu;
    }

    private Race? ResolveReadRace(Season s, AnalysisSession session) =>
        session.RaceId is { } rid ? s.Races.FirstOrDefault(r => r.Id == rid)
            : _data.LatestRace(s) ?? s.Races.OrderBy(r => r.Round).FirstOrDefault();

    private GameSeason? ResolveReadGameSeason(MainData mainData, Season s, AnalysisSession session)
    {
        if (session.LeagueId is { } lid)
        {
            var league = _data.FindLeagueById(mainData, lid);
            return league?.Seasons.FirstOrDefault(x => x.SeasonId == s.Id);
        }
        var options = _data.LeaguesForSeason(mainData, s);
        return options.Count == 1 ? options[0].gs : null;
    }

    private string? ResolveReadLeagueName(MainData mainData, Season s, AnalysisSession session)
    {
        if (session.LeagueId is { } lid)
            return _data.FindLeagueById(mainData, lid)?.LeagueName;
        var options = _data.LeaguesForSeason(mainData, s);
        return options.Count == 1 ? options[0].league.LeagueName : null;
    }

    private async Task ReadRebuildAsync(SocketMessageComponent component, MainData mainData, string token, AnalysisSession session)
    {
        var season = _data.FindSeasonById(mainData, session.SeasonId);
        if (season is null) { await ReadGoneAsync(component); return; }
        var components = BuildReadComponents(mainData, token, session, season, disabled: false);
        await component.UpdateAsync(m => { m.Components = components; });
    }

    private static async Task ReadExpiredAsync(SocketMessageComponent component) =>
        await component.UpdateAsync(m => { m.Content = "This session expired — run the command again."; m.Components = new ComponentBuilder().Build(); });

    private static async Task ReadGoneAsync(SocketMessageComponent component) =>
        await component.UpdateAsync(m => { m.Content = "That season no longer exists."; m.Components = new ComponentBuilder().Build(); });

    private async Task ReadNotYoursAsync() =>
        await RespondAsync("This isn't your session — run the command yourself.", ephemeral: true);
}
