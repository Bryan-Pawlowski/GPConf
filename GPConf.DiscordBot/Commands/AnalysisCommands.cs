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
    private static readonly (string label, string emoji)[] TeamSections =
    [
        ("Team Overview", "🏎️"), ("Driver Partnership", "🤝"), ("Strengths & Weaknesses", "⚖️"), ("What's Next", "🔮"),
    ];
    private static readonly (string label, string emoji)[] CompareSections =
    [
        ("Head-to-Head", "⚔️"), ("Key Differences", "🔍"), ("Performance Indicators", "📊"), ("Outlook", "🔮"),
    ];
    private static readonly (string label, string emoji)[] H2HSections =
    [
        ("Head-to-Head", "⚔️"), ("Key Differences", "🔍"), ("Performance Indicators", "📊"), ("Outlook", "🔮"),
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
        AnalysisKind.Team => "Select a season, two teams, and a league, then click Generate:",
        AnalysisKind.SeasonStats => "Select a season, race, and league, then click Generate:",
        AnalysisKind.Leaderboard => "Select a season, race, and league, then click Generate:",
        AnalysisKind.Compare => "Select a season, two drivers, and a race, then click Generate:",
        AnalysisKind.H2H => "Select a season, league, two players, and a race, then click Generate:",
        AnalysisKind.Projected => "Select a season and league, then click Generate:",
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

    [SlashCommand("team-analysis", "AI-generated narrative analysis of a team's season")]
    public async Task TeamAnalysis()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Team, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        if (leagueOptions.Count == 1)
        {
            session = session with { LeagueId = leagueOptions[0].league.Id };
            _analysisSessions.Update(token, session);
        }

        var components = BuildAnalysisComponents(mainData, token, session, season, leagueOptions, disabled: false);
        await FollowupAsync(WizardPrompt(AnalysisKind.Team), components: components, ephemeral: true);
    }

    [SlashCommand("season-stats", "Deterministic season statistics dashboard")]
    public async Task SeasonStats()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.SeasonStats, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        if (leagueOptions.Count == 1)
        {
            session = session with { LeagueId = leagueOptions[0].league.Id };
            _analysisSessions.Update(token, session);
        }

        var components = BuildAnalysisComponents(mainData, token, session, season, leagueOptions, disabled: false);
        await FollowupAsync(WizardPrompt(AnalysisKind.SeasonStats), components: components, ephemeral: true);
    }

    [SlashCommand("leaderboard", "Confidence-cup leaderboard with momentum indicators")]
    public async Task Leaderboard()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Leaderboard, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        if (leagueOptions.Count == 1)
        {
            session = session with { LeagueId = leagueOptions[0].league.Id };
            _analysisSessions.Update(token, session);
        }

        var components = BuildAnalysisComponents(mainData, token, session, season, leagueOptions, disabled: false);
        await FollowupAsync(WizardPrompt(AnalysisKind.Leaderboard), components: components, ephemeral: true);
    }

    [SlashCommand("compare", "Head-to-head statistics between two drivers")]
    public async Task Compare()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Compare, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var leagueOptions = _data.LeaguesForSeason(mainData, season);

        var components = BuildAnalysisComponents(mainData, token, session, season, leagueOptions, disabled: false);
        await FollowupAsync(WizardPrompt(AnalysisKind.Compare), components: components, ephemeral: true);
    }

    [SlashCommand("h2h", "Head-to-head comparison between two confidence-cup players")]
    public async Task H2H()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.H2H, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        if (leagueOptions.Count == 1)
        {
            session = session with { LeagueId = leagueOptions[0].league.Id };
            _analysisSessions.Update(token, session);
        }

        var components = BuildAnalysisComponents(mainData, token, session, season, leagueOptions, disabled: false);
        await FollowupAsync(WizardPrompt(AnalysisKind.H2H), components: components, ephemeral: true);
    }

    [SlashCommand("projected", "Projected confidence-cup standings after the next race")]
    public async Task Projected()
    {
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        if (mainData.Seasons.Count == 0) { await FollowupAsync("No seasons configured.", ephemeral: true); return; }

        var season = mainData.Seasons.OrderByDescending(s => s.Year).First();
        var token = _analysisSessions.Start(AnalysisKind.Projected, season.Id, Context.User.Id);
        var session = _analysisSessions.Get(token)!;
        var leagueOptions = _data.LeaguesForSeason(mainData, season);
        if (leagueOptions.Count == 1)
        {
            session = session with { LeagueId = leagueOptions[0].league.Id };
            _analysisSessions.Update(token, session);
        }

        var components = BuildAnalysisComponents(mainData, token, session, season, leagueOptions, disabled: false);
        await FollowupAsync(WizardPrompt(AnalysisKind.Projected), components: components, ephemeral: true);
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
        var updated = session with { SeasonId = seasonId, DriverId = null, LeagueId = leagueId, PlayerName = null, RaceId = null, TeamId = null, Driver2Id = null, Player2Name = null, Team2Id = null };
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

    [ComponentInteraction("analysis_team:*")]
    public async Task AnalysisTeamSelected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await NotYoursAsync(); return; }

        var mainData = _data.Load();
        var teamId = ByteString.CopyFrom(Convert.FromHexString(selectedValues[0]));
        var updated = session with { TeamId = teamId };
        _analysisSessions.Update(token, updated);
        await RebuildAsync(component, mainData, token, updated);
    }

    [ComponentInteraction("analysis_team2:*")]
    public async Task AnalysisTeam2Selected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await NotYoursAsync(); return; }

        var mainData = _data.Load();
        var teamId = ByteString.CopyFrom(Convert.FromHexString(selectedValues[0]));
        var updated = session with { Team2Id = teamId };
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

    [ComponentInteraction("analysis_driver2:*")]
    public async Task AnalysisDriver2Selected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await NotYoursAsync(); return; }

        var mainData = _data.Load();
        var driverId = ByteString.CopyFrom(Convert.FromHexString(selectedValues[0]));
        var updated = session with { Driver2Id = driverId };
        _analysisSessions.Update(token, updated);
        await RebuildAsync(component, mainData, token, updated);
    }

    [ComponentInteraction("analysis_player2:*")]
    public async Task AnalysisPlayer2Selected(string token, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _analysisSessions.Get(token);
        if (session is null) { await ExpiredAsync(component); return; }
        if (Context.User.Id != session.CallerId) { await NotYoursAsync(); return; }

        var mainData = _data.Load();
        var playerName = Encoding.UTF8.GetString(Convert.FromHexString(selectedValues[0]));
        var updated = session with { Player2Name = playerName };
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

        string title = "";
        string userPrompt = "";
        uint color = 0;
        (string label, string emoji)[] sections = [];
        Embed? embed = null;
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
            case AnalysisKind.Team:
            {
                var t1 = session.TeamId is { } tid ? s.Teams.FirstOrDefault(x => x.Id == tid) : null;
                if (t1 is null) { await component.UpdateAsync(m => m.Content = "⚠️ Select the first team before clicking Generate."); return; }
                var t2 = session.Team2Id is { } t2id ? s.Teams.FirstOrDefault(x => x.Id == t2id) : null;
                if (t2 is null) { await component.UpdateAsync(m => m.Content = "⚠️ Select the second team before clicking Generate."); return; }
                var target = _data.LatestRace(s) ?? s.Races.OrderBy(r => r.Round).FirstOrDefault();
                if (target is null) { await component.UpdateAsync(m => m.Content = "⚠️ No races configured for this season."); return; }
                var (league, gs) = ResolveLeague(session, mainData, s);
                var facts = AnalysisFacts.BuildTeamCompareFacts(_data, s, t1, t2, target, league, gs);
                embed = ReadCommands.BuildTeamCompareEmbed(s, t1, t2, target, facts);
                title = $"Team Comparison — {t1.Name} vs {t2.Name}";
                color = t1.Color;
                sections = TeamSections;
                userPrompt = $"Write a narrative comparison of {t1.Name} and {t2.Name} in {s.Name} through {target.Name}, " +
                    $"highlighting the noteworthy differences between the two teams, their driver partnerships, and key performance indicators. " +
                    $"Include a GPConf confidence-cup outlook grounded in the league's pick-eligibility rules.\n\n{facts}";
                break;
            }
            case AnalysisKind.SeasonStats:
            {
                var target = ResolveRace(s, session);
                if (target is null) { await component.UpdateAsync(m => m.Content = "⚠️ No races configured for this season."); return; }
                var (league, gs) = ResolveLeague(session, mainData, s);
                var leagueObj = gs is not null ? _data.LeaguesForSeason(mainData, s).FirstOrDefault(x => x.gs == gs).league : null;
                var facts = AnalysisFacts.BuildSeasonStatsFacts(_data, s, target, leagueObj, gs);
                embed = ReadCommands.BuildSeasonStatsEmbed(s, target, facts);
                break;
            }
            case AnalysisKind.Leaderboard:
            {
                var (_, gs) = ResolveLeague(session, mainData, s);
                if (gs is null) { await component.UpdateAsync(m => m.Content = "⚠️ Select a league before clicking Generate."); return; }
                var target = ResolveRace(s, session);
                if (target is null) { await component.UpdateAsync(m => m.Content = "⚠️ No races configured for this season."); return; }
                embed = ReadCommands.BuildLeaderboardEmbed(_data, s, gs, target);
                break;
            }
            case AnalysisKind.Compare:
            {
                var d1 = session.DriverId is { } did ? s.Drivers.FirstOrDefault(x => x.Id == did) : null;
                if (d1 is null) { await component.UpdateAsync(m => m.Content = "⚠️ Select the first driver before clicking Generate."); return; }
                var d2 = session.Driver2Id is { } d2id ? s.Drivers.FirstOrDefault(x => x.Id == d2id) : null;
                if (d2 is null) { await component.UpdateAsync(m => m.Content = "⚠️ Select the second driver before clicking Generate."); return; }
                var target = ResolveRace(s, session);
                if (target is null) { await component.UpdateAsync(m => m.Content = "⚠️ No races configured for this season."); return; }
                var facts = AnalysisFacts.BuildCompareFacts(_data, s, d1, d2, target);
                embed = ReadCommands.BuildCompareEmbed(s, d1, d2, target, facts);
                title = $"Driver Comparison — {d1.Name} vs {d2.Name}";
                color = 0;
                sections = CompareSections;
                userPrompt = $"Write a narrative comparison of {d1.Name} and {d2.Name} in {s.Name} through {target.Name}, " +
                    $"highlighting the noteworthy differences between the two drivers and their key performance indicators. " +
                    $"Discuss their practice, qualifying, and race pace — the pace differences between them and how those factor into their results.\n\n{facts}";
                break;
            }
            case AnalysisKind.H2H:
            {
                var (_, gs) = ResolveLeague(session, mainData, s);
                if (gs is null) { await component.UpdateAsync(m => m.Content = "⚠️ Select a league before clicking Generate."); return; }
                if (session.PlayerName is not { } p1name) { await component.UpdateAsync(m => m.Content = "⚠️ Select the first player before clicking Generate."); return; }
                if (session.Player2Name is not { } p2name) { await component.UpdateAsync(m => m.Content = "⚠️ Select the second player before clicking Generate."); return; }
                var p1 = gs.ParticipatingPlayers.FirstOrDefault(p => p.PlayerName.Equals(p1name, StringComparison.OrdinalIgnoreCase));
                if (p1 is null) { await component.UpdateAsync(m => m.Content = "⚠️ That player no longer exists in the selected league."); return; }
                var p2 = gs.ParticipatingPlayers.FirstOrDefault(p => p.PlayerName.Equals(p2name, StringComparison.OrdinalIgnoreCase));
                if (p2 is null) { await component.UpdateAsync(m => m.Content = "⚠️ That player no longer exists in the selected league."); return; }
                var target = ResolveRace(s, session);
                if (target is null) { await component.UpdateAsync(m => m.Content = "⚠️ No races configured for this season."); return; }
                var facts = AnalysisFacts.BuildH2HFacts(_data, s, gs, p1, p2, target);
                embed = ReadCommands.BuildH2HEmbed(s, p1, p2, target, facts);
                title = $"Player Comparison — {p1.PlayerName} vs {p2.PlayerName}";
                color = p1.Color;
                sections = H2HSections;
                userPrompt = $"Write a narrative comparison of {p1.PlayerName} and {p2.PlayerName}'s confidence-cup performance in {s.Name} through {target.Name}, " +
                    $"highlighting the noteworthy differences between the two players and their key performance indicators.\n\n{facts}";
                break;
            }
            case AnalysisKind.Projected:
            {
                var (_, gs) = ResolveLeague(session, mainData, s);
                if (gs is null) { await component.UpdateAsync(m => m.Content = "⚠️ Select a league before clicking Generate."); return; }
                var nextRace = _data.NextPickableRace(s);
                if (nextRace is null) { await component.UpdateAsync(m => m.Content = "⚠️ No upcoming pickable race found."); return; }
                var facts = AnalysisFacts.BuildProjectedFacts(_data, s, gs, nextRace);
                embed = ReadCommands.BuildProjectedEmbed(s, nextRace, facts);
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

        // Comparison kinds (Team/Compare/H2H) build a static embed in the switch AND set an AI
        // prompt — they post both. Pure deterministic kinds (season-stats, leaderboard, projected)
        // build only the static embed. Pure LLM kinds (driver/player/season/recap) build only the
        // AI analysis. `embed` non-null means a static embed is ready; `userPrompt` non-empty means
        // an AI analysis is wanted.
        string? error = null;
        Embed? analysisEmbed = null;
        if (userPrompt.Length > 0)
            (analysisEmbed, error) = await GenerateEmbedAsync(title, userPrompt, color, sections);

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
            {
                if (embed is not null)
                    await restChannel.SendMessageAsync(embed: embed);
                if (analysisEmbed is not null)
                    await restChannel.SendMessageAsync(embed: analysisEmbed);
            }
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

        menus.Add(BuildSeasonMenu("analysis_season", token, mainData, season));

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
                menus.Add(BuildRaceMenu(token, season, session.RaceId, _data.LatestRace(season)?.Id));
                break;
            case AnalysisKind.Season:
                if (leagueOptions.Count > 1) menus.Add(BuildLeagueMenu(token, leagueOptions, resolvedLeagueId));
                menus.Add(BuildRaceMenu(token, season, session.RaceId, _data.LatestRace(season)?.Id));
                break;
            case AnalysisKind.Recap:
                menus.Add(BuildRaceMenu(token, season, session.RaceId, _data.LatestRace(season)?.Id));
                if (leagueOptions.Count > 1) menus.Add(BuildLeagueMenu(token, leagueOptions, resolvedLeagueId));
                break;
            case AnalysisKind.Team:
                menus.Add(BuildTeamMenu(token, season, session.TeamId));
                menus.Add(BuildTeam2Menu(token, season, session.Team2Id));
                if (leagueOptions.Count > 1) menus.Add(BuildLeagueMenu(token, leagueOptions, resolvedLeagueId));
                break;
            case AnalysisKind.SeasonStats:
                if (leagueOptions.Count > 1) menus.Add(BuildLeagueMenu(token, leagueOptions, resolvedLeagueId));
                menus.Add(BuildRaceMenu(token, season, session.RaceId, _data.LatestRace(season)?.Id));
                break;
            case AnalysisKind.Leaderboard:
                if (leagueOptions.Count > 1) menus.Add(BuildLeagueMenu(token, leagueOptions, resolvedLeagueId));
                menus.Add(BuildRaceMenu(token, season, session.RaceId, _data.LatestRace(season)?.Id));
                break;
            case AnalysisKind.Compare:
                menus.Add(BuildDriverMenu(token, season, session.DriverId));
                menus.Add(BuildDriver2Menu(token, season, session.Driver2Id));
                menus.Add(BuildRaceMenu(token, season, session.RaceId, _data.LatestRace(season)?.Id));
                break;
            case AnalysisKind.H2H:
                if (leagueOptions.Count > 1) menus.Add(BuildLeagueMenu(token, leagueOptions, resolvedLeagueId));
                if (resolvedLeagueId is { } h2hLid)
                {
                    var gs = leagueOptions.FirstOrDefault(o => o.league.Id == h2hLid).gs;
                    menus.Add(BuildPlayerMenu(token, gs, session.PlayerName));
                    menus.Add(BuildPlayer2Menu(token, gs, session.Player2Name));
                }
                menus.Add(BuildRaceMenu(token, season, session.RaceId, _data.LatestRace(season)?.Id));
                break;
            case AnalysisKind.Projected:
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

    private static SelectMenuBuilder BuildTeamMenu(string token, Season season, ByteString? teamId)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"analysis_team:{token}")
            .WithPlaceholder("Choose a team")
            .WithMinValues(1)
            .WithMaxValues(1);
        var teams = season.Teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Take(25).ToList();
        if (teams.Count == 0)
        {
            menu.AddOption("No teams registered", "none");
            menu.WithDisabled(true);
        }
        else
        {
            foreach (var t in teams)
            {
                var label = t.Name.Length > 0 ? t.Name : "(unnamed)";
                var driverCount = season.Drivers.Count(d => d.CurrentTeamId == t.Id);
                menu.AddOption(label, Convert.ToHexString(t.Id.ToByteArray()), $"{driverCount} driver(s)", isDefault: t.Id == teamId);
            }
        }
        return menu;
    }

    private static SelectMenuBuilder BuildTeam2Menu(string token, Season season, ByteString? team2Id)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"analysis_team2:{token}")
            .WithPlaceholder("Choose the second team")
            .WithMinValues(1)
            .WithMaxValues(1);
        var teams = season.Teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Take(25).ToList();
        if (teams.Count == 0)
        {
            menu.AddOption("No teams registered", "none");
            menu.WithDisabled(true);
        }
        else
        {
            foreach (var t in teams)
            {
                var label = t.Name.Length > 0 ? t.Name : "(unnamed)";
                var driverCount = season.Drivers.Count(d => d.CurrentTeamId == t.Id);
                menu.AddOption(label, Convert.ToHexString(t.Id.ToByteArray()), $"{driverCount} driver(s)", isDefault: t.Id == team2Id);
            }
        }
        return menu;
    }

    internal static SelectMenuBuilder BuildSeasonMenu(string customIdPrefix, string token, MainData mainData, Season season)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"{customIdPrefix}:{token}")
            .WithPlaceholder("Choose a season")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var candidate in mainData.Seasons.OrderByDescending(x => x.Year).Take(25))
        {
            var label = string.IsNullOrWhiteSpace(candidate.Name) ? $"Season {candidate.Year}" : candidate.Name;
            menu.AddOption(label, Convert.ToHexString(candidate.Id.ToByteArray()), candidate.Year.ToString(),
                isDefault: candidate.Id == season.Id);
        }
        return menu;
    }

    internal static SelectMenuBuilder BuildLeagueMenu(string token, List<(League league, GameSeason gs)> leagueOptions, ByteString? leagueId)
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

    private static SelectMenuBuilder BuildDriver2Menu(string token, Season season, ByteString? driver2Id)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"analysis_driver2:{token}")
            .WithPlaceholder("Choose the second driver")
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
                menu.AddOption(label, Convert.ToHexString(d.Id.ToByteArray()), desc, isDefault: d.Id == driver2Id);
            }
        }
        return menu;
    }

    private static SelectMenuBuilder BuildPlayer2Menu(string token, GameSeason gs, string? player2Name)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"analysis_player2:{token}")
            .WithPlaceholder("Choose the second player")
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
                    isDefault: string.Equals(p.PlayerName, player2Name, StringComparison.OrdinalIgnoreCase));
        }
        return menu;
    }

    internal static SelectMenuBuilder BuildRaceMenu(string token, Season season, ByteString? raceId, ByteString? latestRaceId = null)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"analysis_race:{token}")
            .WithPlaceholder("Choose a race")
            .WithMinValues(1)
            .WithMaxValues(1);
        var races = season.Races.OrderBy(r => r.Round).Take(25).ToList();
        var defaultRaceId = raceId ?? latestRaceId;
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
