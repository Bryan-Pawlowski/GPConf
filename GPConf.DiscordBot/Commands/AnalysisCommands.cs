using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Google.Protobuf;
using GPConf.DiscordBot.Services;

namespace GPConf.DiscordBot.Commands;

/// <summary>
/// LLM-generated analysis commands, backed by a local Ollama server (see OllamaClient). Every
/// fact referenced in generated text is computed in C# by AnalysisFacts first — the model's job
/// is narrative/tone only, never arithmetic or invention. Kept separate from the purely
/// synchronous ReadCommands module since these depend on a slow external service.
/// </summary>
public class AnalysisCommands : InteractionModuleBase<SocketInteractionContext>
{
    // internal so BotMcpTools.generate_and_post can reuse the same Discord embed-size margins.
    internal const int MaxDescriptionLength = 3900; // Discord embed description cap is 4096; leave margin.
    internal const int MaxFieldLength = 1000;        // Discord embed field-value cap is 1024; leave margin.

    private static class AnalysisPrompts
    {
        public const string SystemPrompt =
            "You are an enthusiastic Formula 1 broadcast color commentator writing for a small " +
            "friend-group fantasy pick'em league. Your tone is warm, energetic, and lightly, " +
            "affectionately funny — never corporate, never a press release.\n\n" +
            "Only use the facts given to you below. Never invent a name, number, position, or " +
            "event that isn't listed. If something interesting isn't in the facts, leave it out " +
            "rather than guessing. The numbers you're given are already final — do not recompute, " +
            "double-check, or contradict them. Do not invent quotes from drivers or players. Some " +
            "facts are fixed rules or context, not events worth commentary — e.g. how many picks " +
            "are made per race, or how many players are in the league. Everyone follows the same " +
            "rules every week, so don't call these out as if they were notable; only comment on " +
            "what a specific driver or player actually did.\n\n" +
            "Structure your response into the exact sections named in the request, each starting " +
            "on its own line written exactly as '## <Section Name>'. Within a section, write 2-4 " +
            "short sentences, or a handful of short bullet points starting with '- '. Use " +
            "**bold** around driver/player names and key numbers to make them stand out. Do not " +
            "use markdown tables, headers other than the '## ' section markers, or code fences. " +
            "Keep the whole response under 350 words total.";
    }

    // Section labels the model is asked to emit (via "## <label>" markers) for each command,
    // each rendered as its own bold embed field instead of one wall-of-text description.
    private static readonly (string label, string emoji)[] DriverSections =
    [
        ("Overview", "🏎️"), ("Highlights", "⭐"), ("Rough Patches", "⚠️"), ("Outlook", "🔮"),
    ];
    private static readonly (string label, string emoji)[] PlayerSections =
    [
        ("Overview", "🎯"), ("Favorite Picks", "❤️"), ("Trends", "📈"), ("Outlook", "🔮"),
    ];
    private static readonly (string label, string emoji)[] SeasonSections =
    [
        ("Championship Picture", "🏆"), ("Storylines", "📰"), ("Confidence Cup", "🎯"),
    ];
    private static readonly (string label, string emoji)[] RaceRecapSections =
    [
        ("Race Summary", "🏁"), ("Unexpected Performances", "😲"), ("Confidence Cup This Week", "🎯"),
    ];

    private readonly DataService _data;
    private readonly OllamaClient _ollama;

    public AnalysisCommands(DataService data, OllamaClient ollama)
    {
        _data = data;
        _ollama = ollama;
    }

    [SlashCommand("driver-analysis", "AI-generated narrative analysis of a driver's season")]
    public async Task DriverAnalysis(
        [Summary("season", "Season name or year")] string season,
        [Summary("driver", "Driver name (exact)")] string driver,
        [Summary("race", "Analyze through this race (defaults to latest)")] string? race = null)
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }
        var target = race is null ? _data.LatestRace(s) : _data.FindRace(s, race);
        if (target is null) { await FollowupAsync($"Race '{race}' not found.", ephemeral: true); return; }
        var d = _data.FindDriver(s, driver);
        if (d is null) { await FollowupAsync($"Driver '{driver}' not found in {s.Name}.", ephemeral: true); return; }

        var team = s.Teams.FirstOrDefault(t => t.Id == d.CurrentTeamId);
        var facts = AnalysisFacts.BuildDriverFacts(_data, s, d, target);
        var userPrompt = $"Write a narrative analysis of {d.Name}'s {s.Name} season through {target.Name}.\n\n{facts}";

        var (embed, error) = await GenerateEmbedAsync($"Driver Analysis — {d.Name}", userPrompt, team?.Color ?? 0, DriverSections);
        if (error is not null) { await FollowupAsync(error, ephemeral: true); return; }
        await FollowupAsync(embed: embed, ephemeral: true);
    }

    [SlashCommand("player-analysis", "AI-generated narrative analysis of a player's confidence-cup picks")]
    public async Task PlayerAnalysis(
        [Summary("season", "Season name or year")] string season,
        [Summary("player", "Player name (defaults to you, matched via Discord username)")] string? player = null,
        [Summary("league", "League name (optional, disambiguates if the player name is ambiguous)")] string? league = null,
        [Summary("race", "Analyze through this race (defaults to latest)")] string? race = null)
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }
        var target = race is null ? _data.LatestRace(s) : _data.FindRace(s, race);
        if (target is null) { await FollowupAsync($"Race '{race}' not found.", ephemeral: true); return; }

        var resolved = await ResolvePlayerAsync(mainData, s, target, player, league);
        if (resolved is null) return; // ResolvePlayerAsync already sent an error or a league picker

        var (resolvedLeague, gs, resolvedPlayer) = resolved.Value;
        var facts = AnalysisFacts.BuildPlayerFacts(_data, s, resolvedLeague, gs, resolvedPlayer, target);
        var userPrompt = $"Write a narrative analysis of {resolvedPlayer.PlayerName}'s confidence-cup picks in {s.Name} through {target.Name}.\n\n{facts}";

        var (embed, error) = await GenerateEmbedAsync($"Player Analysis — {resolvedPlayer.PlayerName}", userPrompt, resolvedPlayer.Color, PlayerSections);
        if (error is not null) { await FollowupAsync(error, ephemeral: true); return; }
        await FollowupAsync(embed: embed, ephemeral: true);
    }

    [SlashCommand("season-overview", "AI-generated narrative overview of a season")]
    public async Task SeasonOverview(
        [Summary("season", "Season name or year")] string season,
        [Summary("league", "League name (optional, adds confidence-cup winners/losers)")] string? league = null,
        [Summary("race", "Analyze through this race (defaults to latest)")] string? race = null)
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }
        var target = race is null ? _data.LatestRace(s) : _data.FindRace(s, race);
        if (target is null) { await FollowupAsync($"Race '{race}' not found.", ephemeral: true); return; }

        var resolved = await ResolveLeagueOrPromptAsync(mainData, s, target, league, "season");
        if (resolved is null) return; // a league picker was sent instead

        var facts = AnalysisFacts.BuildSeasonFacts(_data, s, target, resolved.Value.league, resolved.Value.gs);
        var userPrompt = $"Write a season overview of {s.Name} through {target.Name}, covering winners, losers, and noteworthy performances.\n\n{facts}";

        var (embed, error) = await GenerateEmbedAsync($"Season Overview — {s.Name}", userPrompt, 0, SeasonSections);
        if (error is not null) { await FollowupAsync(error, ephemeral: true); return; }
        await FollowupAsync(embed: embed, ephemeral: true);
    }

    [SlashCommand("race-recap", "AI-generated recap of a race weekend")]
    public async Task RaceRecap(
        [Summary("season", "Season name or year")] string season,
        [Summary("race", "Race name or round")] string race,
        [Summary("league", "League name (optional, adds confidence-cup winners/losers)")] string? league = null)
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }
        var target = _data.FindRace(s, race);
        if (target is null) { await FollowupAsync($"Race '{race}' not found.", ephemeral: true); return; }

        var resolved = await ResolveLeagueOrPromptAsync(mainData, s, target, league, "recap");
        if (resolved is null) return; // a league picker was sent instead

        var facts = AnalysisFacts.BuildRaceRecapFacts(_data, s, target, resolved.Value.league, resolved.Value.gs);
        var userPrompt = $"Write a recap of the {target.Name} race weekend, highlighting unexpected driver performances and confidence-cup winners/losers for the week.\n\n{facts}";

        var (embed, error) = await GenerateEmbedAsync($"Race Recap — {target.Name}", userPrompt, 0, RaceRecapSections);
        if (error is not null) { await FollowupAsync(error, ephemeral: true); return; }
        await FollowupAsync(embed: embed, ephemeral: true);
    }

    [ComponentInteraction("player_analysis_league:*:*")]
    public async Task PlayerAnalysisLeagueSelected(string seasonIdHex, string raceIdHex, string[] selectedValues)
    {
        var mainData = _data.Load();
        var s = _data.FindSeasonById(mainData, ByteString.CopyFrom(Convert.FromHexString(seasonIdHex)));
        var target = s is not null ? _data.FindRaceById(s, ByteString.CopyFrom(Convert.FromHexString(raceIdHex))) : null;
        var component = (SocketMessageComponent)Context.Interaction;

        if (s is null || target is null)
        {
            await component.UpdateAsync(m => { m.Content = "That season/race no longer exists."; m.Components = new ComponentBuilder().Build(); });
            return;
        }

        var parts = selectedValues[0].Split(':', 2);
        var league = _data.FindLeagueById(mainData, ByteString.CopyFrom(Convert.FromHexString(parts[0])));
        var gs = league?.Seasons.FirstOrDefault(x => x.SeasonId == s.Id);
        if (league is null || gs is null)
        {
            await component.UpdateAsync(m => { m.Content = "That league no longer exists."; m.Components = new ComponentBuilder().Build(); });
            return;
        }

        Player? player;
        if (parts[1] == "SELF")
        {
            var usernames = new[] { Context.User.Username, Context.User.GlobalName }
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            player = gs.ParticipatingPlayers.FirstOrDefault(p => usernames.Any(u => string.Equals(u, p.PlayerName, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            var playerName = Encoding.UTF8.GetString(Convert.FromHexString(parts[1]));
            player = gs.ParticipatingPlayers.FirstOrDefault(p => p.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase));
        }

        if (player is null)
        {
            await component.UpdateAsync(m => { m.Content = "Couldn't resolve that player in the selected league."; m.Components = new ComponentBuilder().Build(); });
            return;
        }

        var facts = AnalysisFacts.BuildPlayerFacts(_data, s, league, gs, player, target);
        var userPrompt = $"Write a narrative analysis of {player.PlayerName}'s confidence-cup picks in {s.Name} through {target.Name}.\n\n{facts}";
        var (embed, error) = await GenerateEmbedAsync($"Player Analysis — {player.PlayerName}", userPrompt, player.Color, PlayerSections);

        await component.UpdateAsync(m =>
        {
            m.Content = error;
            m.Embed = embed;
            m.Components = new ComponentBuilder().Build();
        });
    }

    [ComponentInteraction("analysis_league:*:*:*")]
    public async Task AnalysisLeagueSelected(string kind, string seasonIdHex, string raceIdHex, string[] selectedLeagueIds)
    {
        var mainData = _data.Load();
        var s = _data.FindSeasonById(mainData, ByteString.CopyFrom(Convert.FromHexString(seasonIdHex)));
        var target = s is not null ? _data.FindRaceById(s, ByteString.CopyFrom(Convert.FromHexString(raceIdHex))) : null;
        var component = (SocketMessageComponent)Context.Interaction;

        if (s is null || target is null)
        {
            await component.UpdateAsync(m => { m.Content = "That season/race no longer exists."; m.Components = new ComponentBuilder().Build(); });
            return;
        }

        var league = _data.FindLeagueById(mainData, ByteString.CopyFrom(Convert.FromHexString(selectedLeagueIds[0])));
        var gs = league?.Seasons.FirstOrDefault(x => x.SeasonId == s.Id);

        string title;
        string userPrompt;
        (string label, string emoji)[] sections;
        if (kind == "recap")
        {
            var facts = AnalysisFacts.BuildRaceRecapFacts(_data, s, target, league, gs);
            title = $"Race Recap — {target.Name}";
            userPrompt = $"Write a recap of the {target.Name} race weekend, highlighting unexpected driver performances and confidence-cup winners/losers for the week.\n\n{facts}";
            sections = RaceRecapSections;
        }
        else
        {
            var facts = AnalysisFacts.BuildSeasonFacts(_data, s, target, league, gs);
            title = $"Season Overview — {s.Name}";
            userPrompt = $"Write a season overview of {s.Name} through {target.Name}, covering winners, losers, and noteworthy performances.\n\n{facts}";
            sections = SeasonSections;
        }

        var (embed, error) = await GenerateEmbedAsync(title, userPrompt, 0, sections);
        await component.UpdateAsync(m =>
        {
            m.Content = error;
            m.Embed = embed;
            m.Components = new ComponentBuilder().Build();
        });
    }

    // Resolves a named player to a specific (League, GameSeason, Player), or sends an error/league
    // picker itself and returns null. Distinct from ResolveLeagueOrPromptAsync because the target
    // here is a specific player who isn't necessarily the caller.
    private async Task<(League league, GameSeason gs, Player player)?> ResolvePlayerAsync(
        MainData mainData, Season s, Race target, string? playerName, string? leagueName)
    {
        List<(League league, GameSeason gs)> candidates;
        if (leagueName is not null)
        {
            var (l, gs) = _data.FindGameSeason(mainData, s, leagueName);
            if (l is null || gs is null) { await FollowupAsync($"League '{leagueName}' not found for {s.Name}.", ephemeral: true); return null; }
            candidates = [(l, gs)];
        }
        else
        {
            candidates = _data.LeaguesForSeason(mainData, s);
            if (candidates.Count == 0) { await FollowupAsync($"No league configured for {s.Name}.", ephemeral: true); return null; }
        }

        if (playerName is not null)
        {
            var matches = candidates
                .Select(c => (c.league, c.gs, player: c.gs.ParticipatingPlayers.FirstOrDefault(p => p.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.player is not null)
                .ToList();
            if (matches.Count == 1) return (matches[0].league, matches[0].gs, matches[0].player!);
            if (matches.Count == 0) { await FollowupAsync($"No player named '{playerName}' found for {s.Name}.", ephemeral: true); return null; }

            await SendPlayerLeaguePickerAsync(s, target, matches.Select(m => m.league).ToList(), playerName);
            return null;
        }

        // No player given — default to the caller via Discord username/global name.
        var usernames = new[] { Context.User.Username, Context.User.GlobalName }
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selfMatches = candidates
            .Select(c => (c.league, c.gs, player: c.gs.ParticipatingPlayers.FirstOrDefault(p => usernames.Any(u => string.Equals(u, p.PlayerName, StringComparison.OrdinalIgnoreCase)))))
            .Where(x => x.player is not null)
            .ToList();
        if (selfMatches.Count == 1) return (selfMatches[0].league, selfMatches[0].gs, selfMatches[0].player!);
        if (selfMatches.Count == 0)
        {
            await FollowupAsync("Couldn't match your Discord username to a player — pass `player` explicitly.", ephemeral: true);
            return null;
        }

        await SendPlayerLeaguePickerAsync(s, target, selfMatches.Select(m => m.league).ToList(), null);
        return null;
    }

    private async Task SendPlayerLeaguePickerAsync(Season s, Race target, List<League> leagues, string? playerName)
    {
        // Player name travels in each option's Value (not the shared CustomId) to stay well under
        // Discord's 100-char CustomId limit regardless of name length.
        var playerToken = playerName is not null ? Convert.ToHexString(Encoding.UTF8.GetBytes(playerName)) : "SELF";
        var menu = new SelectMenuBuilder()
            .WithCustomId($"player_analysis_league:{Convert.ToHexString(s.Id.ToByteArray())}:{Convert.ToHexString(target.Id.ToByteArray())}")
            .WithPlaceholder("Choose a league")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var league in leagues)
            menu.AddOption(league.LeagueName, $"{Convert.ToHexString(league.Id.ToByteArray())}:{playerToken}");
        await FollowupAsync(
            "That player name exists in more than one league — pick one:",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(),
            ephemeral: true);
    }

    // Same league-resolution shape as /results in ReadCommands.cs: silent if the season has ≤1
    // configured league, else try the caller's Discord username, else prompt with a select menu
    // (sent directly, caller returns null). Unlike ResolvePlayerAsync, an unresolved league here
    // isn't fatal — season-overview/race-recap just render without the confidence-cup section.
    private async Task<(League? league, GameSeason? gs)?> ResolveLeagueOrPromptAsync(
        MainData mainData, Season s, Race target, string? leagueName, string kind)
    {
        if (leagueName is not null)
        {
            var (l, gs) = _data.FindGameSeason(mainData, s, leagueName);
            return (l, gs);
        }

        var candidates = _data.LeaguesForSeason(mainData, s);
        if (candidates.Count <= 1)
            return candidates.Count == 1 ? (candidates[0].league, candidates[0].gs) : (null, null);

        var usernames = new[] { Context.User.Username, Context.User.GlobalName }
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var matches = candidates
            .Where(c => c.gs.ParticipatingPlayers.Any(p => usernames.Any(u => string.Equals(u, p.PlayerName, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (matches.Count == 1) return (matches[0].league, matches[0].gs);

        var menu = new SelectMenuBuilder()
            .WithCustomId($"analysis_league:{kind}:{Convert.ToHexString(s.Id.ToByteArray())}:{Convert.ToHexString(target.Id.ToByteArray())}")
            .WithPlaceholder("Choose a league")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var (leagueOption, _) in candidates)
            menu.AddOption(leagueOption.LeagueName, Convert.ToHexString(leagueOption.Id.ToByteArray()));
        await FollowupAsync(
            "Couldn't tell which league you're in — pick one:",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(),
            ephemeral: true);
        return null;
    }

    private async Task<(Embed? embed, string? error)> GenerateEmbedAsync(
        string title, string userPrompt, uint accentColor, (string label, string emoji)[] sections)
    {
        try
        {
            var sectionList = string.Join(", ", sections.Select(x => x.label));
            var fullPrompt = $"{userPrompt}\n\nStructure your response into these sections, in this order: {sectionList}.";
            var text = await _ollama.GenerateAsync(AnalysisPrompts.SystemPrompt, fullPrompt);

            var builder = new EmbedBuilder().WithTitle(title).WithColor(ToDiscordColor(accentColor));
            var parsed = ParseSections(text);
            if (parsed.Count > 0)
            {
                foreach (var (label, emoji) in sections)
                {
                    if (!parsed.TryGetValue(label, out var body) || body.Length == 0) continue;
                    if (body.Length > MaxFieldLength) body = body[..MaxFieldLength] + "…";
                    builder.AddField($"{emoji} {label}", body);
                }
            }
            else
            {
                // Model ignored the section format — fall back to a plain description so nothing breaks.
                builder.WithDescription(text.Length > MaxDescriptionLength ? text[..MaxDescriptionLength] + "…" : text);
            }
            return (builder.Build(), null);
        }
        catch (OllamaException ex)
        {
            return (null, $"⚠️ Couldn't generate analysis: {ex.Message}");
        }
    }

    // Splits model output on "## <title>" marker lines into a title -> body map, trimmed of
    // surrounding whitespace. Returns empty if the model didn't use any section markers at all.
    // internal so BotMcpTools.generate_and_post can reuse the same "## Section" parsing.
    internal static Dictionary<string, string> ParseSections(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentTitle = null;
        var currentBody = new StringBuilder();

        void Flush()
        {
            if (currentTitle is not null)
            {
                var body = currentBody.ToString().Trim();
                if (body.Length > 0) result[currentTitle] = body;
            }
            currentBody.Clear();
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("## "))
            {
                Flush();
                currentTitle = trimmed[3..].Trim();
            }
            else if (currentTitle is not null)
            {
                currentBody.AppendLine(line);
            }
        }
        Flush();
        return result;
    }

    private static Color ToDiscordColor(uint packedRgb) =>
        packedRgb == 0 ? Color.Default : new Color((byte)((packedRgb >> 16) & 0xFF), (byte)((packedRgb >> 8) & 0xFF), (byte)(packedRgb & 0xFF));
}
