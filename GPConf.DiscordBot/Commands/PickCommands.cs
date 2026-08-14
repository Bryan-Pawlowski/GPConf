using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Google.Protobuf;
using GPConf.DiscordBot.Services;
using GPConf.Utilities;

namespace GPConf.DiscordBot.Commands;

/// <summary>
/// The bot's first write path: an interactive 5-select-menu pick-submission widget. Kept in its
/// own module (like AnalysisCommands) since it depends on PickSessionStore and writes to
/// gpconf.data, unlike the purely read-only ReadCommands.
/// </summary>
public class PickCommands : InteractionModuleBase<SocketInteractionContext>
{
    // Discord's own named embed-color presets — "a random, Discord-supported color" for an
    // auto-registered player, rather than an arbitrary (possibly ugly) random RGB value.
    private static readonly Color[] PlayerColorPalette =
    [
        Color.Red, Color.Orange, Color.Gold, Color.Green, Color.Teal,
        Color.Blue, Color.Purple, Color.Magenta, Color.LightGrey, Color.DarkGrey,
    ];

    // Ramp used for both the per-driver option description and the scoring-rules legend, ordered
    // by increasing risk/reward (lowest standings-multiplier tier first) — safest pick first.
    private static readonly string[] TierEmojiRamp = ["🟢", "🟡", "🟠", "🔴", "🔥", "💎"];

    private readonly DataService _data;
    private readonly PickSessionStore _sessions;
    private readonly OllamaClient _ollama;

    public PickCommands(DataService data, PickSessionStore sessions, OllamaClient ollama)
    {
        _data = data;
        _sessions = sessions;
        _ollama = ollama;
    }

    // Dev-only: this bypasses the announce-button flow entirely, so it's restricted to server
    // admins by default. Discord permission gating is guild-scoped and does nothing in DMs —
    // if this is tested via DM, it stays visible there to anyone who can message the bot; the
    // restriction only takes effect inside a guild channel.
    [SlashCommand("pick-submit", "Submit your confidence-cup picks for the next upcoming race")]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    public async Task PickSubmit(
        [Summary("season", "Season name or year")] string season,
        [Summary("league", "League name (optional, disambiguates if needed)")] string? league = null)
    {
        // Ephemeral throughout: picks, and the scoring-rules context around them, are only ever
        // meant for the caller — this also means nobody else can even see the picker to interact
        // with it, closing off the tampering risk the CallerId check below defends against too.
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }

        var target = _data.NextPickableRace(s);
        if (target is null) { await FollowupAsync($"No upcoming race to pick for in {s.Name} — every race already has results.", ephemeral: true); return; }

        var resolved = await ResolveLeagueOrPromptAsync(mainData, s, target, league);
        if (resolved is null) return; // a league picker (or an error) was already sent

        await RunPickerFlowAsync(s, target, resolved.Value.league, resolved.Value.gs);
    }

    // Also admin-only — a regular player posting their own "picks are open" announcement doesn't
    // make sense. Unlike PickSubmit, this one genuinely has no purpose in a DM (there's no shared
    // channel to announce to), so DM usage is disabled outright rather than just discouraged.
    // EnabledInDm is marked obsolete in this Discord.Net version in favor of a CommandContextTypes
    // attribute that doesn't actually exist in the installed package (3.20.1) — using the
    // deprecated-but-functional attribute rather than a nonexistent one.
#pragma warning disable CS0618
    [SlashCommand("pick-announce", "Post a public button anyone in this channel can click to submit their picks privately")]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    [EnabledInDm(false)]
#pragma warning restore CS0618
    public async Task PickAnnounce(
        [Summary("season", "Season name or year")] string season,
        [Summary("league", "League name (optional; if omitted, each clicker's league is auto-detected)")] string? league = null)
    {
        await DeferAsync();
        var mainData = _data.Load();
        var s = _data.FindSeason(mainData, season);
        if (s is null) { await FollowupAsync($"Season '{season}' not found.", ephemeral: true); return; }

        var target = _data.NextPickableRace(s);
        if (target is null) { await FollowupAsync($"No upcoming race to pick for in {s.Name} — every race already has results.", ephemeral: true); return; }

        ByteString? leagueId = null;
        if (league is not null)
        {
            var (l, gs) = _data.FindGameSeason(mainData, s, league);
            if (l is null || gs is null) { await FollowupAsync($"League '{league}' not found for {s.Name}.", ephemeral: true); return; }
            leagueId = l.Id;
        }

        var (embed, button) = BuildPickAnnouncement(s, target, leagueId);
        // Deliberately public/non-ephemeral: this is the shared announcement. Each click below
        // opens a fresh, ephemeral picker scoped to whoever clicked it, not to this message.
        await FollowupAsync(embed: embed, components: button);
    }

    // Shared with BotMcpTools.post_pick_announcement — the automation path (Race Weekend Prep
    // skill) posts the exact same embed+button this slash command does, just proactively rather
    // than in response to an admin invoking /pick-announce.
    internal static (Embed embed, MessageComponent button) BuildPickAnnouncement(Season s, Race target, ByteString? leagueId)
    {
        var leagueToken = leagueId is { } id ? Convert.ToHexString(id.ToByteArray()) : "NONE";
        var customId = $"pick_announce:{Convert.ToHexString(s.Id.ToByteArray())}:{leagueToken}";
        var button = new ComponentBuilder().WithButton("🏁 Make Your Picks", customId, ButtonStyle.Primary).Build();
        var embed = new EmbedBuilder()
            .WithTitle($"🏁 Picks Are Open — {target.Name}")
            .WithDescription("Click below to submit your confidence-cup picks. Your picks — and everyone else's — stay private until race results are in.")
            .WithColor(Color.Blue)
            .Build();
        return (embed, button);
    }

    [ComponentInteraction("pick_announce:*:*")]
    public async Task PickAnnounceClicked(string seasonIdHex, string leagueIdHexOrNone)
    {
        // Ephemeral, and NOT an UpdateAsync on the announcement message — that message is shared
        // and must stay clickable for everyone else; this opens a private followup just for the
        // clicker instead, the same way the slash-command entry point does.
        await DeferAsync(ephemeral: true);
        var mainData = _data.Load();
        var s = _data.FindSeasonById(mainData, ByteString.CopyFrom(Convert.FromHexString(seasonIdHex)));
        if (s is null) { await FollowupAsync("That season no longer exists.", ephemeral: true); return; }

        var target = _data.NextPickableRace(s);
        if (target is null) { await FollowupAsync($"No upcoming race to pick for in {s.Name} — every race already has results.", ephemeral: true); return; }

        (League league, GameSeason gs)? resolved;
        if (leagueIdHexOrNone != "NONE")
        {
            var league = _data.FindLeagueById(mainData, ByteString.CopyFrom(Convert.FromHexString(leagueIdHexOrNone)));
            var gs = league?.Seasons.FirstOrDefault(x => x.SeasonId == s.Id);
            if (league is null || gs is null) { await FollowupAsync("That league no longer exists.", ephemeral: true); return; }
            resolved = (league, gs);
        }
        else
        {
            resolved = await ResolveLeagueOrPromptAsync(mainData, s, target, null);
            if (resolved is null) return; // a league picker (or an error) was already sent
        }

        // Discord has no concept of "disable this shared button for just one viewer" — a button's
        // enabled state is a property of the message, shared by everyone who can see it. Every
        // click just opens a fresh picker (SubmitPicks already finds-or-replaces, so re-clicking
        // after already submitting is how a player revises their picks, not an error case).
        await RunPickerFlowAsync(s, target, resolved.Value.league, resolved.Value.gs);
    }

    // Shared by /pick-submit, the pick-announce button click, and the league-disambiguation
    // picker — all three have resolved season/race/league by this point and just need the
    // session started and the picker sent as a fresh followup message. Always a *followup*
    // (never UpdateAsync on some pre-existing message) specifically so the returned IUserMessage
    // can be stored and independently edited later from a different interaction (see PickLockIn
    // and the IUserMessage doc-comment on PickSession.DropdownMessage).
    private async Task RunPickerFlowAsync(Season s, Race target, League league, GameSeason gs)
    {
        var outcome = StartSession(s, target, league, gs);
        if (!outcome.Ok) { await FollowupAsync(outcome.Message, ephemeral: true); return; }
        var dropdownMsg = await FollowupAsync(outcome.Message, embed: outcome.RulesEmbed, components: outcome.Components, ephemeral: true);
        if (!outcome.ButtonIncluded)
        {
            RememberDropdownMessage(outcome.Token!, dropdownMsg);
            await FollowupAsync(components: BuildLockInComponent(outcome.Token!), ephemeral: true);
        }
    }

    [ComponentInteraction("pick_league:*:*")]
    public async Task PickLeagueSelected(string seasonIdHex, string raceIdHex, string[] selectedLeagueIds)
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
        if (league is null || gs is null)
        {
            await component.UpdateAsync(m => { m.Content = "That league no longer exists."; m.Components = new ComponentBuilder().Build(); });
            return;
        }

        // Close out the league picker itself; the dropdowns/button are sent as fresh followups
        // via RunPickerFlowAsync (same as every other entry point) so the dropdown message is
        // independently addressable later, not tied to this now-closed interaction response.
        await component.UpdateAsync(m => { m.Content = "League selected."; m.Embed = null; m.Components = new ComponentBuilder().Build(); });
        await RunPickerFlowAsync(s, target, league, gs);
    }

    [ComponentInteraction("pick_slot:*:*")]
    public async Task PickSlotSelected(string token, string slotIndexStr, string[] selectedValues)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _sessions.Get(token);
        if (session is null)
        {
            await component.UpdateAsync(m => { m.Content = "This pick session expired — click the picks button again to start over."; m.Components = new ComponentBuilder().Build(); });
            return;
        }
        if (Context.User.Id != session.CallerId)
        {
            await RespondAsync("This isn't your pick session — click the picks button yourself to make your own picks.", ephemeral: true);
            return;
        }

        int slotIndex = int.Parse(slotIndexStr);
        var chosenDriverId = ByteString.CopyFrom(Convert.FromHexString(selectedValues[0]));

        var selections = (ByteString?[])session.Selections.Clone();
        // "Steal" rule: if this driver was already picked in another slot, clear it there —
        // BuildPickComponents already excludes taken drivers from other menus going forward, so
        // this only matters for the rare case of two menus mid-flight before a re-render lands.
        for (int i = 0; i < selections.Length; i++)
            if (i != slotIndex && selections[i] == chosenDriverId) selections[i] = null;
        selections[slotIndex] = chosenDriverId;
        _sessions.Update(token, session with { Selections = selections });

        // Re-render the dropdowns only — locking in picks is a separate, explicit step now (a
        // "Lock In Picks" button, on this same message when there's a free row, else alongside it).
        var eligibility = LoadEligibility(_data.Load(), session.SeasonId, session.RaceId, session.LeagueId);
        var driverMap = eligibility?.eligible.ToDictionary(d => d.Id, d => d) ?? new Dictionary<ByteString, Driver>();
        var components = BuildPickComponents(token, eligibility?.eligible ?? [], selections, driverMap,
            eligibility?.tierInfo, includeLockInButton: eligibility?.canFitButton ?? false);
        await component.UpdateAsync(m => { m.Components = components; });
    }

    [ComponentInteraction("pick_lock:*")]
    public async Task PickLockIn(string token)
    {
        var component = (SocketMessageComponent)Context.Interaction;
        var session = _sessions.Get(token);
        if (session is null)
        {
            await component.UpdateAsync(m => { m.Content = "This pick session expired — click the picks button again to start over."; m.Components = new ComponentBuilder().Build(); });
            return;
        }
        if (Context.User.Id != session.CallerId)
        {
            await RespondAsync("This isn't your pick session — click the picks button yourself to make your own picks.", ephemeral: true);
            return;
        }

        if (session.Selections.Any(x => x is null))
        {
            await component.UpdateAsync(m => { m.Content = "⚠️ Fill in every pick slot above before locking in."; });
            return;
        }

        var driverIds = session.Selections.Select(x => x!).ToArray();
        var (result, player, isNewPlayer) = _data.SubmitPicks(session.SeasonId, session.RaceId, session.LeagueId,
            session.CallerDisplayName, RandomPlayerColor(), driverIds);
        _sessions.Remove(token);

        if (result != DataService.SubmitPicksResult.Ok || player is null)
        {
            var msg = result switch
            {
                DataService.SubmitPicksResult.StaleData => "⚠️ The season data changed while you were picking — click the picks button again to retry.",
                DataService.SubmitPicksResult.IneligibleDriver => "⚠️ One of your picks is no longer eligible — click the picks button again to retry.",
                _ => "⚠️ Couldn't save your picks — please try again.",
            };
            await component.UpdateAsync(m => { m.Content = msg; m.Embed = null; m.Components = new ComponentBuilder().Build(); });
            return;
        }

        // Only a brand-new player waits on an LLM call, so only that path needs to defer first —
        // Discord's 3-second initial-ack window doesn't allow calling Ollama before acknowledging.
        // component.UpdateAsync() *is* the ack for the fast path; DeferAsync() is the ack for the
        // slow one, finalized afterward via ModifyOriginalResponseAsync (UpdateAsync can't be
        // called a second time once already acknowledged either way).
        string? welcomeQuip = null;
        if (isNewPlayer)
        {
            await component.DeferAsync();
            welcomeQuip = await TryGenerateWelcomeQuip(player.PlayerName);
        }

        async Task FinalizeAsync(Action<MessageProperties> mutator)
        {
            if (isNewPlayer) await component.ModifyOriginalResponseAsync(mutator);
            else await component.UpdateAsync(mutator);
        }

        var embed = BuildConfirmationEmbed(session, driverIds, player, isNewPlayer, welcomeQuip);
        var lockedComponents = BuildLockedComponents(_data.Load(), session, driverIds);

        if (session.DropdownMessage is { } dropdownMsg)
        {
            // Button lives on its own message — clear it here, and separately grey out the
            // dropdown message so it doesn't look like it's still accepting changes. dropdownMsg
            // edits itself via the interaction token that originally sent it (see PickSession's
            // doc-comment), which is what makes this work for an ephemeral message despite
            // PickLockIn being a completely different interaction than the one that sent it.
            await FinalizeAsync(m => { m.Content = null; m.Embed = embed; m.Components = new ComponentBuilder().Build(); });
            try
            {
                await dropdownMsg.ModifyAsync(m => m.Components = lockedComponents);
            }
            catch
            {
                // Best-effort — the dropdown message may have been deleted or its interaction
                // token may have expired (>15 min). The picks are already saved by this point, so
                // a stale-looking dropdown message is a cosmetic gap, not a failure worth surfacing.
            }
        }
        else
        {
            // Button shared the dropdown message — one edit covers both: show the confirmation and
            // leave the (now disabled) dropdowns visible underneath as a record of the final picks.
            await FinalizeAsync(m => { m.Content = null; m.Embed = embed; m.Components = lockedComponents; });
        }
    }

    // Best-effort: falls back to null (BuildConfirmationEmbed uses a static line instead) if the
    // local Ollama server is unreachable or times out — a flaky LLM must never block a pick
    // submission that's already been saved successfully.
    private async Task<string?> TryGenerateWelcomeQuip(string playerName)
    {
        try
        {
            const string systemPrompt =
                "You write a single short welcome sentence for a new member of a Formula 1 fantasy " +
                "pick'em league. Output ONLY the sentence itself — no preamble, no quotation marks, " +
                "no extra commentary. Under 20 words. Warm, playful, and racing-themed (lights out, " +
                "pole position, chequered flag, podium, etc.) — a good-luck send-off, not a summary.";
            var quip = await _ollama.GenerateAsync(systemPrompt, $"Write the welcome sentence for {playerName}.");
            return quip.Trim();
        }
        catch (OllamaException)
        {
            return null;
        }
    }

    private void RememberDropdownMessage(string token, IUserMessage message)
    {
        var session = _sessions.Get(token);
        if (session is not null) _sessions.Update(token, session with { DropdownMessage = message });
    }

    // Rebuilds the picker in its disabled (locked-in) state, reflecting the final submitted picks.
    private MessageComponent BuildLockedComponents(MainData mainData, PickSession session, ByteString[] driverIds)
    {
        var eligibility = LoadEligibility(mainData, session.SeasonId, session.RaceId, session.LeagueId);
        var driverMap = eligibility?.eligible.ToDictionary(d => d.Id, d => d) ?? new Dictionary<ByteString, Driver>();
        var selections = driverIds.Select(id => (ByteString?)id).ToArray();
        return BuildPickComponents(string.Empty, eligibility?.eligible ?? [], selections, driverMap,
            eligibility?.tierInfo, disabled: true);
    }

    private sealed record SessionStartOutcome(bool Ok, string Message, Embed? RulesEmbed, MessageComponent? Components, string? Token, bool ButtonIncluded);

    private SessionStartOutcome StartSession(Season s, Race target, League league, GameSeason gs)
    {
        var rules = gs.PickRules ?? new PickRules();
        if (rules.NumPicks is < 1 or > 5)
            return new SessionStartOutcome(false, "This league's pick count isn't supported by the picker (must be 1-5 picks per race).", null, null, null, false);

        var prevRace = s.Races.OrderBy(x => x.Round).LastOrDefault(x => x.Round < target.Round);
        var noPriorRace = prevRace is null;
        var eligiblePairs = CCUtils.GetEligibleDriversWithPos(s, prevRace, rules.PositionCutoff);
        if (eligiblePairs.Count == 0)
            return new SessionStartOutcome(false, "No eligible drivers found for this race.", null, null, null, false);

        var (tierInfo, legend) = BuildTierInfo(rules, noPriorRace, eligiblePairs);
        var eligible = eligiblePairs.Select(x => x.driver).ToList();

        var callerName = Context.User.GlobalName ?? Context.User.Username;
        var token = _sessions.Start(s.Id, target.Id, league.Id, callerName, Context.User.Id,
            eligible.Select(d => d.Id).ToArray(), rules.NumPicks);
        var driverMap = eligible.ToDictionary(d => d.Id, d => d);
        // Discord caps a message at 5 action rows and a select menu always fills its whole row —
        // the lock-in button only fits alongside the dropdowns when there's a spare row (≤4 picks).
        var canFitButton = CanFitButton(rules.NumPicks);
        var components = BuildPickComponents(token, eligible, new ByteString?[rules.NumPicks], driverMap, tierInfo, canFitButton);
        var rulesEmbed = BuildScoringRulesEmbed(target, rules, noPriorRace, legend);
        return new SessionStartOutcome(true, $"Pick your {rules.NumPicks} drivers for {target.Name}:", rulesEmbed, components, token, canFitButton);
    }

    private static bool CanFitButton(int numPicks) => numPicks <= 4;

    // Re-derives the eligible-driver list and per-driver tier info fresh from a reload, for re-rendering
    // the dropdowns after a slot changes — the session only carries IDs, never loaded objects (see
    // PickSessionStore), so there's nothing else to go stale here.
    private (List<Driver> eligible, Dictionary<ByteString, (float mult, string emoji)> tierInfo, bool canFitButton)? LoadEligibility(
        MainData mainData, ByteString seasonId, ByteString raceId, ByteString leagueId)
    {
        var s = _data.FindSeasonById(mainData, seasonId);
        var target = s is not null ? _data.FindRaceById(s, raceId) : null;
        var league = _data.FindLeagueById(mainData, leagueId);
        var gs = league?.Seasons.FirstOrDefault(x => x.SeasonId == seasonId);
        if (s is null || target is null || gs is null) return null;

        var rules = gs.PickRules ?? new PickRules();
        var prevRace = s.Races.OrderBy(x => x.Round).LastOrDefault(x => x.Round < target.Round);
        var eligiblePairs = CCUtils.GetEligibleDriversWithPos(s, prevRace, rules.PositionCutoff);
        var (tierInfo, _) = BuildTierInfo(rules, prevRace is null, eligiblePairs);
        return (eligiblePairs.Select(x => x.driver).ToList(), tierInfo, CanFitButton(rules.NumPicks));
    }

    // Maps each eligible driver to their standings multiplier (via the real CCUtils.GetStandingsMultiplier
    // — never reimplemented) and a tier emoji, plus a legend string describing each configured tier's
    // multiplier and championship-position range. Emoji index is the tier's rank by position (lowest
    // positions = safest pick = 🟢), not by multiplier value, so it stays meaningful even if a league
    // configures a non-monotonic multiplier scheme.
    // internal (not private) so BotMcpTools.post_pick_deadline_reminder can derive the same
    // rules/noPriorRace/legend inputs BuildScoringRulesEmbed needs.
    internal static (Dictionary<ByteString, (float mult, string emoji)> perDriver, string legend) BuildTierInfo(
        PickRules rules, bool noPriorRace, List<(Driver driver, int champPos)> eligible)
    {
        var perDriver = new Dictionary<ByteString, (float, string)>();

        if (noPriorRace || rules.StandingsMultipliers.Count == 0)
        {
            foreach (var (driver, _) in eligible) perDriver[driver.Id] = (1f, TierEmojiRamp[0]);
            var note = noPriorRace
                ? "First race of the season — no prior standings, so every pick scores at ×1.0 this week."
                : "No standings-tier multipliers configured — every pick scores at ×1.0.";
            return (perDriver, note);
        }

        var orderedTiers = rules.StandingsMultipliers.OrderBy(kv => kv.Key).ToList();
        string EmojiForRank(int rank) => TierEmojiRamp[Math.Clamp(rank, 0, TierEmojiRamp.Length - 1)];

        foreach (var (driver, champPos) in eligible)
        {
            var mult = CCUtils.GetStandingsMultiplier(rules, champPos);
            var rank = orderedTiers.FindIndex(kv => champPos <= kv.Key);
            if (rank < 0) rank = orderedTiers.Count - 1; // beyond every configured key -> last (highest) tier
            perDriver[driver.Id] = (mult, EmojiForRank(rank));
        }

        var legendLines = new List<string>();
        int lower = rules.PositionCutoff + 1;
        for (int i = 0; i < orderedTiers.Count; i++)
        {
            var (key, value) = (orderedTiers[i].Key, orderedTiers[i].Value);
            var isLast = i == orderedTiers.Count - 1;
            var range = isLast ? $"P{lower}+" : $"P{lower}–{key}";
            legendLines.Add($"{EmojiForRank(i)} ×{value:G} — {range}");
            lower = key + 1;
        }
        return (perDriver, string.Join("\n", legendLines));
    }

    // internal (not private) so BotMcpTools.post_pick_deadline_reminder can wrap the exact same
    // embed — no reimplementation for the automation path.
    internal static Embed BuildScoringRulesEmbed(Race target, PickRules rules, bool noPriorRace, string legend)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Each pick slot has a base score; your score for that slot is the base score × your picked driver's standings multiplier — only if they actually score points that race.");
        sb.AppendLine();
        sb.AppendLine("**Base score per slot:**");
        sb.AppendLine(string.Join("  ·  ", rules.BasePickScores.Select((score, i) => $"#{i + 1}: {score:G}")));
        sb.AppendLine();
        sb.AppendLine("**Standings multiplier tiers:**");
        sb.AppendLine(legend);
        if (!noPriorRace)
        {
            sb.AppendLine();
            sb.AppendLine($"Drivers ranked P{rules.PositionCutoff} or better in the championship aren't eligible this week.");
        }

        return new EmbedBuilder()
            .WithTitle($"📋 Confidence Cup Scoring — {target.Name}")
            .WithDescription(sb.ToString())
            .WithColor(Color.Blue)
            .Build();
    }

    // Lays out one select menu per pick slot (one per action row — Discord allows at most 5 rows
    // per message, and a select menu occupies its whole row). The lock-in button joins this same
    // message on the next free row when there's room (≤4 picks); a 5-pick league fills every row
    // with dropdowns, so the button has to go in a second, separate message instead. Drivers
    // already chosen in another slot are excluded from every other menu's option list — this, not
    // just the "steal" fallback in PickSlotSelected, is what actually prevents picking the same
    // driver twice. Each option's description shows the driver's standings multiplier and tier emoji.
    private static MessageComponent BuildPickComponents(
        string token, List<Driver> eligible, ByteString?[] selections, Dictionary<ByteString, Driver> driverMap,
        Dictionary<ByteString, (float mult, string emoji)>? tierInfo = null, bool includeLockInButton = false,
        bool disabled = false)
    {
        var builder = new ComponentBuilder();
        var usedElsewhere = selections.Where(x => x is not null).Select(x => x!).ToHashSet();
        for (int i = 0; i < selections.Length; i++)
        {
            var mine = selections[i];
            var options = eligible.Where(d => d.Id == mine || !usedElsewhere.Contains(d.Id)).Take(25).ToList();
            var placeholder = mine is { } sel && driverMap.TryGetValue(sel, out var d) ? d.Name : $"Pick #{i + 1}";
            var menu = new SelectMenuBuilder()
                .WithCustomId($"pick_slot:{token}:{i}")
                .WithPlaceholder(placeholder)
                .WithMinValues(1)
                .WithMaxValues(1)
                .WithDisabled(disabled);
            foreach (var opt in options)
            {
                var description = tierInfo is not null && tierInfo.TryGetValue(opt.Id, out var t)
                    ? $"{t.emoji} ×{t.mult:G} multiplier"
                    : null;
                menu.AddOption(opt.Name, Convert.ToHexString(opt.Id.ToByteArray()), description, isDefault: opt.Id == mine);
            }
            builder.WithSelectMenu(menu, row: i);
        }
        // Locked-in dropdowns are shown disabled (greyed, reflecting the final picks) rather than
        // removed, so the record of what was picked stays visible — the button is dropped either way,
        // it's served its purpose once picks are locked.
        if (includeLockInButton && !disabled)
            builder.WithButton("🔒 Lock In Picks", $"pick_lock:{token}", ButtonStyle.Success, row: selections.Length);
        return builder.Build();
    }

    private static MessageComponent BuildLockInComponent(string token) =>
        new ComponentBuilder().WithButton("🔒 Lock In Picks", $"pick_lock:{token}", ButtonStyle.Success).Build();

    private Embed BuildConfirmationEmbed(PickSession session, ByteString[] driverIds, Player player, bool isNewPlayer, string? welcomeQuip)
    {
        var mainData = _data.Load();
        var s = _data.FindSeasonById(mainData, session.SeasonId)!;
        var race = _data.FindRaceById(s, session.RaceId)!;
        var league = _data.FindLeagueById(mainData, session.LeagueId)!;
        var gs = league.Seasons.First(x => x.SeasonId == s.Id);
        var rules = gs.PickRules ?? new PickRules();

        var driverMap = s.Drivers.ToDictionary(d => d.Id, d => d);
        var teamMap = s.Teams.ToDictionary(t => t.Id, t => t);
        var prevRace = s.Races.OrderBy(x => x.Round).LastOrDefault(x => x.Round < race.Round);
        var champPts = prevRace is not null ? _data.ChampionshipPoints(s, prevRace) : [];
        var champPos = _data.ChampionshipPositions(champPts);
        bool hasResults = race.RaceResults.Any(rr => rr.IsComplete);

        var sb = new StringBuilder();
        float total = 0f;
        for (int i = 0; i < driverIds.Length; i++)
        {
            var driver = driverMap.GetValueOrDefault(driverIds[i]);
            var team = driver is not null ? teamMap.GetValueOrDefault(driver.CurrentTeamId) : null;
            var pos = champPos.GetValueOrDefault(driverIds[i], 0);
            var score = CCUtils.ScorePickSlot(s, rules, i, driverIds[i], race, pos, hasResults);
            total += score;
            sb.AppendLine($"{i + 1}. {driver?.Name ?? "unknown"} ({team?.Name ?? "unknown"}) — {score:F1} pts potential");
        }
        sb.AppendLine();
        sb.AppendLine($"**Total potential: {total:F1} pts**");

        var embed = new EmbedBuilder()
            .WithTitle($"✅ Picks Locked In — {race.Name}")
            .WithDescription(sb.ToString())
            .WithColor(ToDiscordColor(player.Color));

        if (isNewPlayer)
        {
            var welcomeLine = !string.IsNullOrWhiteSpace(welcomeQuip)
                ? $"Welcome, {player.PlayerName}! {welcomeQuip}"
                : $"Welcome, {player.PlayerName} — you've been added to {league.LeagueName}!";
            embed.WithFooter(welcomeLine);
        }

        return embed.Build();
    }

    private async Task<(League league, GameSeason gs)?> ResolveLeagueOrPromptAsync(
        MainData mainData, Season s, Race target, string? leagueName)
    {
        if (leagueName is not null)
        {
            var (l, gs) = _data.FindGameSeason(mainData, s, leagueName);
            if (l is null || gs is null) { await FollowupAsync($"League '{leagueName}' not found for {s.Name}.", ephemeral: true); return null; }
            return (l, gs);
        }

        var candidates = _data.LeaguesForSeason(mainData, s);
        if (candidates.Count == 0) { await FollowupAsync($"No league configured for {s.Name}.", ephemeral: true); return null; }
        if (candidates.Count == 1) return (candidates[0].league, candidates[0].gs);

        var usernames = new[] { Context.User.Username, Context.User.GlobalName }
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var matches = candidates
            .Where(c => c.gs.ParticipatingPlayers.Any(p => usernames.Any(u => string.Equals(u, p.PlayerName, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (matches.Count == 1) return (matches[0].league, matches[0].gs);

        var menu = new SelectMenuBuilder()
            .WithCustomId($"pick_league:{Convert.ToHexString(s.Id.ToByteArray())}:{Convert.ToHexString(target.Id.ToByteArray())}")
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

    private static uint RandomPlayerColor()
    {
        var c = PlayerColorPalette[Random.Shared.Next(PlayerColorPalette.Length)];
        return ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    }

    private static Color ToDiscordColor(uint packedRgb) =>
        packedRgb == 0 ? Color.Default : new Color((byte)((packedRgb >> 16) & 0xFF), (byte)((packedRgb >> 8) & 0xFF), (byte)(packedRgb & 0xFF));
}
