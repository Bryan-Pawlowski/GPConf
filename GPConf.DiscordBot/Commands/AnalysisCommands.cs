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
///
/// All four commands are parameterless and open an ephemeral dropdown wizard (tracked by
/// AnalysisSessionStore) so the caller never has to know exact season/driver/player/league
/// spelling. The generated analysis is posted as a separate, public, persistent channel message —
/// only the wizard itself stays ephemeral.
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
            "on its own line written exactly as '## <Section Name>'. Favor structured, scannable " +
            "formatting over walls of text: mix short prose with lists and fact bullets. Within a " +
            "section, prefer a handful of short bullet points starting with '- ' (each a single " +
            "fact or observation), and use short prose only to tie them together or set up a point. " +
            "Use **bold** around driver/player names and key numbers to make them stand out. Do not " +
            "use markdown tables, headers other than the '## ' section markers, or code fences. " +
            "Keep sections short and visually distinct — a reader should be able to scan the " +
            "bullets and get the gist without reading every word. Keep the whole response under " +
            "350 words total.";
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
    private readonly AnalysisSessionStore _analysisSessions;

    public AnalysisCommands(DataService data, OllamaClient ollama, AnalysisSessionStore analysisSessions)
    {
        _data = data;
        _ollama = ollama;
        _analysisSessions = analysisSessions;
    }

    private static string WizardPrompt(AnalysisKind kind) => kind switch
    {
        AnalysisKind.Driver => "Select a season, driver, and league, then click Generate:",
        AnalysisKind.Player => "Select a season, league, player, and race, then click Generate:",
        AnalysisKind.Season => "Select a season, league, and race, then click Generate:",
        AnalysisKind.Recap => "Select a season, race, and league, then click Generate:",
        _ => "Select the options, then click Generate:",
    };

    // Parameterless: opens a dropdown wizard (season -> driver -> league) instead of typed params,
    // so the caller never has to know exact season/driver spelling. Season defaults to the newest
    // one so the driver/league dropdowns are populated immediately rather than starting empty.
    [SlashCommand("driver-analysis", "AI-generated narrative analysis of a driver's season")]
    public async Task DriverAnalysis()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Driver, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        // Silent auto-resolve when there's only one league — only ambiguity needs a dropdown.
        if (leagueOptions.Count == 1)
        {
            session = session with { LeagueId = leagueOptions[0].league.Id };
            _analysisSessions.Update(token, session);
        }

        var components = BuildAnalysisComponents(mainData, token, session, season, leagueOptions, disabled: false);
        await FollowupAsync(WizardPrompt(AnalysisKind.Driver), components: components, ephemeral: true);
    }

    [SlashCommand("player-analysis", "AI-generated narrative analysis of a player's confidence-cup picks")]
    public async Task PlayerAnalysis()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Player, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        if (leagueOptions.Count == 1)
        {
            session = session with { LeagueId = leagueOptions[0].league.Id };
            _analysisSessions.Update(token, session);
        }

        var components = BuildAnalysisComponents(mainData, token, session, season, leagueOptions, disabled: false);
        await FollowupAsync(WizardPrompt(AnalysisKind.Player), components: components, ephemeral: true);
    }

    [SlashCommand("season-overview", "AI-generated narrative overview of a season")]
    public async Task SeasonOverview()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Season, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        if (leagueOptions.Count == 1)
        {
            session = session with { LeagueId = leagueOptions[0].league.Id };
            _analysisSessions.Update(token, session);
        }

        var components = BuildAnalysisComponents(mainData, token, session, season, leagueOptions, disabled: false);
        await FollowupAsync(WizardPrompt(AnalysisKind.Season), components: components, ephemeral: true);
    }

    [SlashCommand("race-recap", "AI-generated recap of a race weekend")]
    public async Task RaceRecap()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Recap, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        if (leagueOptions.Count == 1)
        {
            session = session with { LeagueId = leagueOptions[0].league.Id };
            _analysisSessions.Update(token, session);
        }

        var components = BuildAnalysisComponents(mainData, token, session, season, leagueOptions, disabled: false);
        await FollowupAsync(WizardPrompt(AnalysisKind.Recap), components: components, ephemeral: true);
    }

    [ComponentInteraction("analysis_season:*")]
    public async Task AnalysisSeasonSelected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await NotYoursAsync(); return; }

        var mainData = _data.Load();
        var seasonId = ByteString.CopyFrom(Convert.FromHexString(selectedValues[0]));
        var season = _data.FindSeasonById(mainData, seasonId);
        if (season is null) { await GoneAsync(component); return; }

        // Changing season invalidates any previously chosen driver/league/player/race — re-derive
        // fresh rather than carrying stale IDs from a different season's roster.
        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        ByteString? leagueId = leagueOptions.Count == 1 ? leagueOptions[0].league.Id : null;
        var updated = session with { SeasonId = seasonId, DriverId = null, LeagueId = leagueId, PlayerName = null, RaceId = null };
        _analysisSessions.Update(token, updated);
        await RebuildAsync(component, mainData, token, updated);
    }

    [ComponentInteraction("analysis_driver:*")]
    public async Task AnalysisDriverSelected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await NotYoursAsync(); return; }

        var mainData = _data.Load();
        var driverId = ByteString.CopyFrom(Convert.FromHexString(selectedValues[0]));
        var updated = session with { DriverId = driverId };
        _analysisSessions.Update(token, updated);
        await RebuildAsync(component, mainData, token, updated);
    }

    [ComponentInteraction("analysis_league:*")]
    public async Task AnalysisLeagueSelected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await NotYoursAsync(); return; }

        var mainData = _data.Load();
        var leagueId = ByteString.CopyFrom(Convert.FromHexString(selectedValues[0]));
        // Changing league invalidates any previously chosen player (players are league-scoped).
        var updated = session with { LeagueId = leagueId, PlayerName = null };
        _analysisSessions.Update(token, updated);
        await RebuildAsync(component, mainData, token, updated);
    }

    [ComponentInteraction("analysis_player:*")]
    public async Task AnalysisPlayerSelected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await NotYoursAsync(); return; }

        var mainData = _data.Load();
        var playerName = Encoding.UTF8.GetString(Convert.FromHexString(selectedValues[0]));
        var updated = session with { PlayerName = playerName };
        _analysisSessions.Update(token, updated);
        await RebuildAsync(component, mainData, token, updated);
    }

    [ComponentInteraction("analysis_race:*")]
    public async Task AnalysisRaceSelected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await NotYoursAsync(); return; }

        var mainData = _data.Load();
        var raceId = ByteString.CopyFrom(Convert.FromHexString(selectedValues[0]));
        var updated = session with { RaceId = raceId };
        _analysisSessions.Update(token, updated);
        await RebuildAsync(component, mainData, token, updated);
    }

    [ComponentInteraction("analysis_generate:*")]
    public async Task AnalysisGenerate(string token)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await NotYoursAsync(); return; }

        var mainData = _data.Load();
        var s = _data.FindSeasonById(mainData, session.SeasonId);
        if (s is null) { await GoneAsync(component); return; }

        string title;
        string userPrompt;
        uint color;
        (string label, string emoji)[] sections;
        switch (session.Kind)
        {
            case AnalysisKind.Driver:
            {
                var d = session.DriverId is { } did ? s.Drivers.FirstOrDefault(x => x.Id == did) : null;
                if (d is null) { await component.UpdateAsync(m => m.Content = "⚠️ Select a driver before clicking Generate."); return; }
                var target = _data.LatestRace(s) ?? s.Races.OrderBy(r => r.Round).FirstOrDefault();
                if (target is null) { await component.UpdateAsync(m => m.Content = "⚠️ No races configured for this season."); return; }
                var (league, gs) = ResolveLeague(session, mainData, s);
                var team = s.Teams.FirstOrDefault(t => t.Id == d.CurrentTeamId);
                var facts = AnalysisFacts.BuildDriverFacts(_data, s, d, target, league, gs);
                title = $"Driver Analysis — {d.Name}";
                color = team?.Color ?? 0;
                sections = DriverSections;
                userPrompt = $"Write a narrative analysis of {d.Name}'s {s.Name} season through {target.Name}, " +
                    $"including a GPConf confidence-cup outlook grounded in the league's pick-eligibility rules and the driver's recent finishing trend.\n\n{facts}";
                break;
            }
            case AnalysisKind.Player:
            {
                var (league, gs) = ResolveLeague(session, mainData, s);
                if (league is null || gs is null) { await component.UpdateAsync(m => m.Content = "⚠️ Select a league before clicking Generate."); return; }
                if (session.PlayerName is not { } pname) { await component.UpdateAsync(m => m.Content = "⚠️ Select a player before clicking Generate."); return; }
                var player = gs.ParticipatingPlayers.FirstOrDefault(p => p.PlayerName.Equals(pname, StringComparison.OrdinalIgnoreCase));
                if (player is null) { await component.UpdateAsync(m => m.Content = "⚠️ That player no longer exists in the selected league."); return; }
                var target = ResolveRace(s, session);
                if (target is null) { await component.UpdateAsync(m => m.Content = "⚠️ No races configured for this season."); return; }
                var facts = AnalysisFacts.BuildPlayerFacts(_data, s, league, gs, player, target);
                title = $"Player Analysis — {player.PlayerName}";
                color = player.Color;
                sections = PlayerSections;
                userPrompt = $"Write a narrative analysis of {player.PlayerName}'s confidence-cup picks in {s.Name} through {target.Name}.\n\n{facts}";
                break;
            }
            case AnalysisKind.Season:
            {
                var (league, gs) = ResolveLeague(session, mainData, s);
                var target = ResolveRace(s, session);
                if (target is null) { await component.UpdateAsync(m => m.Content = "⚠️ No races configured for this season."); return; }
                var facts = AnalysisFacts.BuildSeasonFacts(_data, s, target, league, gs);
                title = $"Season Overview — {s.Name}";
                color = 0;
                sections = SeasonSections;
                userPrompt = $"Write a season overview of {s.Name} through {target.Name}, covering winners, losers, and noteworthy performances.\n\n{facts}";
                break;
            }
            case AnalysisKind.Recap:
            {
                var (league, gs) = ResolveLeague(session, mainData, s);
                var target = ResolveRace(s, session);
                if (target is null) { await component.UpdateAsync(m => m.Content = "⚠️ No races configured for this season."); return; }
                var facts = AnalysisFacts.BuildRaceRecapFacts(_data, s, target, league, gs);
                title = $"Race Recap — {target.Name}";
                color = 0;
                sections = RaceRecapSections;
                userPrompt = $"Write a recap of the {target.Name} race weekend, highlighting unexpected driver performances and confidence-cup winners/losers for the week.\n\n{facts}";
                break;
            }
            default:
                return;
        }

        // The Ollama call routinely takes longer than Discord's 3-second interaction-ack window, so
        // this must ack immediately. UpdateAsync both acks the interaction AND disables the Generate
        // button so it can't be re-clicked while a generation is in flight.
        var leagueOptions = _data.LeaguesForSeason(mainData, s);
        var disabledComponents = BuildAnalysisComponents(mainData, token, session, s, leagueOptions, disabled: true);
        await component.UpdateAsync(m => { m.Content = "Generating…"; m.Components = disabledComponents; });

        var (embed, error) = await GenerateEmbedAsync(title, userPrompt, color, sections);

        if (error is not null)
        {
            var reenabled = BuildAnalysisComponents(mainData, token, session, s, leagueOptions, disabled: false);
            await component.ModifyOriginalResponseAsync(m => { m.Content = error; m.Components = reenabled; });
            return;
        }

        // Post the analysis as a separate, public, persistent channel message — the wizard stays
        // ephemeral but the result survives. Then re-enable the button so the caller can tweak the
        // dropdowns and regenerate (the session is intentionally kept, not removed).
        //
        // Send via REST, not the socket channel: the bot runs with GatewayIntents.None, so the
        // socket cache never holds guild/channel data and Context.Channel is null for guild text
        // channels (it only resolves for DMs, which are created on demand). Use the channel id
        // from the interaction payload itself, which is always present regardless of cache state,
        // then resolve it over REST — same approach BotMcpTools.SendAsync uses.
        if (Context.Interaction.ChannelId is { } channelId)
        {
            var restChannel = await Context.Client.Rest.GetChannelAsync(channelId) as IMessageChannel;
            if (restChannel is not null)
                await restChannel.SendMessageAsync(embed: embed);
        }
        var reenabled2 = BuildAnalysisComponents(mainData, token, session, s, leagueOptions, disabled: false);
        await component.ModifyOriginalResponseAsync(m => { m.Content = WizardPrompt(session.Kind); m.Components = reenabled2; });
    }

    // Re-renders the wizard dropdowns for the current session state after a selection changes.
    private async Task RebuildAsync(SocketMessageComponent component, MainData mainData, string token, AnalysisSession session)
    {
        var season = _data.FindSeasonById(mainData, session.SeasonId);
        if (season is null) { await GoneAsync(component); return; }
        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        var components = BuildAnalysisComponents(mainData, token, session, season, leagueOptions, disabled: false);
        await component.UpdateAsync(m => { m.Components = components; });
    }

    private async Task ExpiredAsync(SocketMessageComponent component) =>
        await component.UpdateAsync(m => { m.Content = "This session expired — run the command again."; m.Components = new ComponentBuilder().Build(); });

    private async Task GoneAsync(SocketMessageComponent component) =>
        await component.UpdateAsync(m => { m.Content = "That season no longer exists."; m.Components = new ComponentBuilder().Build(); });

    private async Task NotYoursAsync() =>
        await RespondAsync("This isn't your session — run the command yourself.", ephemeral: true);

    // Resolves the league for a session, auto-resolving when the season has exactly one league.
    private (League? league, GameSeason? gs) ResolveLeague(AnalysisSession session, MainData mainData, Season s)
    {
        if (session.LeagueId is { } lid)
        {
            var league = _data.FindLeagueById(mainData, lid);
            return (league, league?.Seasons.FirstOrDefault(x => x.SeasonId == s.Id));
        }
        var options = _data.LeaguesForSeason(mainData, s);
        return options.Count == 1 ? (options[0].league, options[0].gs) : (null, null);
    }

    // Resolves the target race, defaulting to the latest race when the user hasn't picked one.
    private Race? ResolveRace(Season s, AnalysisSession session) =>
        session.RaceId is { } rid ? s.Races.FirstOrDefault(r => r.Id == rid)
            : _data.LatestRace(s) ?? s.Races.OrderBy(r => r.Round).FirstOrDefault();

    // Renders the dropdowns + Generate button for the current wizard state. The dropdown sequence
    // and order depend on the session kind; the league dropdown is only included when the season has
    // more than one configured league (0 or 1 auto-resolve silently). Each select marks its
    // currently-chosen option as the default so re-renders still show prior choices.
    private MessageComponent BuildAnalysisComponents(
        MainData mainData, string token, AnalysisSession session, Season season,
        List<(League league, GameSeason gs)> leagueOptions, bool disabled)
    {
        var menus = new List<SelectMenuBuilder>();

        var seasonMenu = new SelectMenuBuilder()
            .WithCustomId($"analysis_season:{token}")
            .WithPlaceholder("Choose a season")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var candidate in mainData.Seasons.OrderByDescending(x => x.Year).Take(25))
        {
            var label = string.IsNullOrWhiteSpace(candidate.Name) ? $"Season {candidate.Year}" : candidate.Name;
            seasonMenu.AddOption(label, Convert.ToHexString(candidate.Id.ToByteArray()), candidate.Year.ToString(),
                isDefault: candidate.Id == season.Id);
        }
        menus.Add(seasonMenu);

        ByteString? resolvedLeagueId = leagueOptions.Count == 1 ? leagueOptions[0].league.Id : session.LeagueId;

        switch (session.Kind)
        {
            case AnalysisKind.Driver:
                menus.Add(BuildDriverMenu(token, season, session.DriverId));
                if (leagueOptions.Count > 1) menus.Add(BuildLeagueMenu(token, leagueOptions, resolvedLeagueId));
                break;
            case AnalysisKind.Player:
                if (leagueOptions.Count > 1) menus.Add(BuildLeagueMenu(token, leagueOptions, resolvedLeagueId));
                if (resolvedLeagueId is { } lid)
                {
                    var gs = leagueOptions.FirstOrDefault(o => o.league.Id == lid).gs;
                    menus.Add(BuildPlayerMenu(token, gs, session.PlayerName));
                }
                menus.Add(BuildRaceMenu(token, season, session.RaceId));
                break;
            case AnalysisKind.Season:
                if (leagueOptions.Count > 1) menus.Add(BuildLeagueMenu(token, leagueOptions, resolvedLeagueId));
                menus.Add(BuildRaceMenu(token, season, session.RaceId));
                break;
            case AnalysisKind.Recap:
                menus.Add(BuildRaceMenu(token, season, session.RaceId));
                if (leagueOptions.Count > 1) menus.Add(BuildLeagueMenu(token, leagueOptions, resolvedLeagueId));
                break;
        }

        var builder = new ComponentBuilder();
        for (int i = 0; i < menus.Count; i++)
            builder.WithSelectMenu(menus[i], row: i);
        builder.WithButton("📊 Generate", $"analysis_generate:{token}", ButtonStyle.Primary, row: menus.Count, disabled: disabled);
        return builder.Build();
    }

    private static SelectMenuBuilder BuildDriverMenu(string token, Season season, ByteString? driverId)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"analysis_driver:{token}")
            .WithPlaceholder("Choose a driver")
            .WithMinValues(1)
            .WithMaxValues(1);
        var drivers = season.Drivers.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Take(25).ToList();
        if (drivers.Count == 0)
        {
            menu.AddOption("No drivers registered", "none");
            menu.WithDisabled(true);
        }
        else
        {
            foreach (var d in drivers)
            {
                var team = season.Teams.FirstOrDefault(t => t.Id == d.CurrentTeamId);
                var label = d.Name.Length > 0 ? d.Name : "(unnamed)";
                var desc = team is not null ? $"#{d.Number} — {team.Name}" : $"#{d.Number}";
                menu.AddOption(label, Convert.ToHexString(d.Id.ToByteArray()), desc, isDefault: d.Id == driverId);
            }
        }
        return menu;
    }

    private static SelectMenuBuilder BuildLeagueMenu(string token, List<(League league, GameSeason gs)> leagueOptions, ByteString? leagueId)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"analysis_league:{token}")
            .WithPlaceholder("Choose a league")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var (leagueOption, _) in leagueOptions.Take(25))
            menu.AddOption(leagueOption.LeagueName, Convert.ToHexString(leagueOption.Id.ToByteArray()),
                isDefault: leagueOption.Id == leagueId);
        return menu;
    }

    private static SelectMenuBuilder BuildPlayerMenu(string token, GameSeason gs, string? playerName)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"analysis_player:{token}")
            .WithPlaceholder("Choose a player")
            .WithMinValues(1)
            .WithMaxValues(1);
        var players = gs.ParticipatingPlayers.OrderBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase).Take(25).ToList();
        if (players.Count == 0)
        {
            menu.AddOption("No players registered", "none");
            menu.WithDisabled(true);
        }
        else
        {
            foreach (var p in players)
                menu.AddOption(p.PlayerName, Convert.ToHexString(Encoding.UTF8.GetBytes(p.PlayerName)),
                    isDefault: string.Equals(p.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
        }
        return menu;
    }

    private SelectMenuBuilder BuildRaceMenu(string token, Season season, ByteString? raceId)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"analysis_race:{token}")
            .WithPlaceholder("Choose a race")
            .WithMinValues(1)
            .WithMaxValues(1);
        var races = season.Races.OrderBy(r => r.Round).Take(25).ToList();
        var defaultRaceId = raceId ?? _data.LatestRace(season)?.Id;
        if (races.Count == 0)
        {
            menu.AddOption("No races configured", "none");
            menu.WithDisabled(true);
        }
        else
        {
            foreach (var r in races)
                menu.AddOption(r.Name, Convert.ToHexString(r.Id.ToByteArray()), isDefault: r.Id == defaultRaceId);
        }
        return menu;
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
