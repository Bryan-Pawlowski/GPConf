using Discord;
using Discord.Interactions;
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
        [Summary("race", "Race name or round (defaults to latest)")] string? race = null)
    {
        await DeferAsync();
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found."); return; }

        var target = race is null ? _data.LatestRace(s) : _data.FindRace(s, race);
        if (target is null) { await FollowupAsync($"Race '{race}' not found."); return; }

        var points = _data.ChampionshipPoints(s, target);
        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d.Name);
        var rows = points
            .Select(kv => new { driver = driverMap.GetValueOrDefault(kv.Key, "(unknown)"), points = kv.Value })
            .OrderByDescending(x => x.points)
            .Select((x, i) => $"{i + 1,2}. {x.driver,-20} {x.points,5} pts")
            .ToList();

        var embed = new EmbedBuilder()
            .WithTitle($"Championship Standings — {s.Name}")
            .WithDescription($"through {target.Name}\n```\n{string.Join("\n", rows)}\n```");
        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("results", "Race results for a race")]
    public async Task Results(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round")] string race)
    {
        await DeferAsync();
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found."); return; }
        var r = _data.FindRace(s, race);
        if (r is null) { await FollowupAsync($"Race '{race}' not found."); return; }
        if (r.RaceResults.Count == 0) { await FollowupAsync($"No race results stored for '{r.Name}'."); return; }

        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d.Name);
        var teamMap = s.Teams.ToDictionary(t => t.Id, t => t.Name);
        var sb = new StringBuilder();
        foreach (var rr in r.RaceResults)
        {
            sb.AppendLine($"**{rr.RaceName}**");
            foreach (var dr in rr.Results.OrderBy(x => x.Position))
                sb.AppendLine($"{dr.Position,2}. {driverMap.GetValueOrDefault(dr.DriverId, "(unknown)"),-20} {dr.Points,3} pts  {dr.Status}");
        }

        var embed = new EmbedBuilder().WithTitle($"Race Results — {r.Name}").WithDescription(sb.ToString());
        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("quali", "Qualifying results for a race")]
    public async Task Quali(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round")] string race)
    {
        await DeferAsync();
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found."); return; }
        var r = _data.FindRace(s, race);
        if (r is null) { await FollowupAsync($"Race '{race}' not found."); return; }
        if (r.QualifyingSessions.Count == 0) { await FollowupAsync($"No qualifying data stored for '{r.Name}'."); return; }

        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d.Name);
        var allEntries = r.QualifyingSessions
            .SelectMany(qs => qs.LapData.Select(ld => (stage: qs.Stage, ld)))
            .GroupBy(x => x.ld.DriverId)
            .Select(g => g.OrderByDescending(x => x.stage).First())
            .ToList();

        var ordered = allEntries
            .OrderBy(x => x.stage == 0 ? 0 : x.stage == 2 ? 1 : 2)
            .ThenBy(x => x.ld.FastestLapSeconds > 0 ? x.ld.FastestLapSeconds : float.MaxValue)
            .Select((x, i) => $"{i + 1,2}. {driverMap.GetValueOrDefault(x.ld.DriverId, "(unknown)"),-20} {FormatLap(x.ld.FastestLapSeconds)}")
            .ToList();

        var embed = new EmbedBuilder()
            .WithTitle($"Qualifying — {r.Name}")
            .WithDescription($"```\n{string.Join("\n", ordered)}\n```");
        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("practice", "Practice session results for a race")]
    public async Task Practice(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round")] string race,
        [Summary("session", "Practice session number (1, 2, or 3)")] int session)
    {
        await DeferAsync();
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found."); return; }
        var r = _data.FindRace(s, race);
        if (r is null) { await FollowupAsync($"Race '{race}' not found."); return; }

        var practice = r.Practices.FirstOrDefault(p => p.SessionNumber == session);
        if (practice is null) { await FollowupAsync($"No FP{session} data stored for '{r.Name}'."); return; }

        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d.Name);
        var rows = practice.LapData
            .Where(ld => ld.FastestLapSeconds > 0)
            .OrderBy(ld => ld.FastestLapSeconds)
            .Select((ld, i) => $"{i + 1,2}. {driverMap.GetValueOrDefault(ld.DriverId, "(unknown)"),-20} {FormatLap(ld.FastestLapSeconds)}")
            .ToList();

        var embed = new EmbedBuilder()
            .WithTitle($"FP{session} — {r.Name}")
            .WithDescription($"```\n{string.Join("\n", rows)}\n```");
        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("scores", "Cumulative player confidence-cup scores")]
    public async Task Scores(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round (defaults to latest)")] string? race = null,
        [Summary("league", "League name (optional)")] string? league = null)
    {
        await DeferAsync();
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found."); return; }
        var target = race is null ? _data.LatestRace(s) : _data.FindRace(s, race);
        if (target is null) { await FollowupAsync($"Race '{race}' not found."); return; }

        var (_, gs) = _data.FindGameSeason(mainData, s, league);
        if (gs is null) { await FollowupAsync($"No game season found for '{s.Name}'."); return; }

        var scores = _data.PlayerScores(s, gs, target);
        var rows = gs.ParticipatingPlayers
            .Select(p => new { p.PlayerName, score = scores.GetValueOrDefault(p.Id) })
            .OrderByDescending(x => x.score)
            .Select(x => $"{x.PlayerName,-20} {x.score,6:F1} pts")
            .ToList();

        var embed = new EmbedBuilder()
            .WithTitle($"Confidence Cup Scores — {s.Name}")
            .WithDescription($"through {target.Name}\n```\n{string.Join("\n", rows)}\n```");
        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("pick", "A player's picks and scores for a race")]
    public async Task Pick(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round")] string race,
        [Summary("league", "League name (optional)")] string? league = null)
    {
        await DeferAsync();
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found."); return; }
        var r = _data.FindRace(s, race);
        if (r is null) { await FollowupAsync($"Race '{race}' not found."); return; }

        var (_, gs) = _data.FindGameSeason(mainData, s, league);
        if (gs is null) { await FollowupAsync($"No game season found for '{s.Name}'."); return; }

        var gameRace = gs.Races.FirstOrDefault(gr => gr.RaceId == r.Id);
        if (gameRace is null) { await FollowupAsync($"No picks recorded for '{r.Name}'."); return; }

        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d.Name);
        var rules = gs.PickRules ?? new PickRules();
        var prevRace = s.Races.OrderBy(x => x.Round).LastOrDefault(x => x.Round < r.Round);
        var champPts = prevRace is not null ? _data.ChampionshipPoints(s, prevRace) : [];
        var champPos = _data.ChampionshipPositions(champPts);

        var sb = new StringBuilder();
        foreach (var player in gs.ParticipatingPlayers)
        {
            var picks = gameRace.PicksPerPlayer.FirstOrDefault(pp => pp.PlayerId == player.Id);
            float total = 0f;
            var lines = new List<string>();
            if (picks is not null)
            {
                for (int i = 0; i < picks.DriverId.Count; i++)
                {
                    var dId = picks.DriverId[i];
                    int pos = champPos.GetValueOrDefault(dId, 0);
                    float score = r.RaceResults.Any(rr => rr.IsComplete)
                        ? CCUtils.GetPickScoreFromResults(s, rules, i, dId, r, pos)
                        : CCUtils.CalculatePickScore(rules, i, pos);
                    total += score;
                    lines.Add($"  {i + 1}. {driverMap.GetValueOrDefault(dId, "(unknown)"),-20} ({score:F1} pts)");
                }
            }
            sb.AppendLine($"**{player.PlayerName}** — {total:F1} pts");
            sb.AppendLine(string.Join("\n", lines));
        }

        var embed = new EmbedBuilder().WithTitle($"Picks — {r.Name}").WithDescription(sb.ToString());
        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("rules", "Current season points rules and multipliers")]
    public async Task Rules(
        [Summary("season", "Season name or year")] string season)
    {
        await DeferAsync();
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found."); return; }

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

        var embed = new EmbedBuilder().WithTitle($"Points Rules — {s.Name}").WithDescription(sb.ToString());
        await FollowupAsync(embed: embed.Build());
    }

    private static string FormatLap(float? seconds) =>
        seconds is null ? "—" : $"{seconds:F3}s";
}
