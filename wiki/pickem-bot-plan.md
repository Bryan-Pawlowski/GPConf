# Discord pick'em bot — design & tech plan

> As of `b7ade99` + uncommitted changes (see [log.md](log.md)). DM-only read
> commands, the pick-submission widget (`/pick-submit`, `/pick-announce`),
> LLM analysis commands, and — as of this update — the full bot-as-MCP-server
> foundation plus all six race-week automation skill files are built and
> working. Discord permission gating, ephemeral-everything, and pick-secrecy
> are all live. What's left is genuinely optional polish (see "Remaining
> build order"), not core functionality.

## Concept

A Discord bot that runs the confidence-cup pick'em game (see
[confidence-cup-scoring.md](confidence-cup-scoring.md)) as a weekly Discord
flow instead of the desktop `LeagueUpdater` UI: players pick drivers via a
Discord select menu, and an AI agent (via Claude Code or OpenCode skills —
both read the same `.claude/skills/*/SKILL.md` files, confirmed via
OpenCode's own docs) drives the race-week sequence — posting the weekend
intro, the deadline/announcement, ingesting results, locking in the reveal,
and writing recap/preview blurbs.

Origin: a draft plan was written by a different assistant with no visibility
into this codebase (`pickem_bot.md`, not in this repo). It assumed a
from-scratch system with its own JSON/SQLite storage. That draft was
ground-truthed against the actual codebase and substantially revised — see
below for what changed and why.

## Corrections to the original draft

- **No separate storage.** The draft's `data/league.json`,
  `data/picks/*.json`, `data/standings.json` would have forked a second copy
  of data GPConf already owns. `League` → `GameSeason` → `GameRace` →
  `Picks`, with `PickRules`, already exist in `confidence_game.proto` and are
  already live for the current season via the desktop `LeagueEditor`/
  `LeagueUpdater`. The bot is a new client of this existing store, not a new
  store.
- **Scoring is not "1 point per hit."** The draft assumed naive per-driver
  scoring with an optional order bonus. Actual scoring
  (`CCUtils.GetPickScoreFromResults` /  `CalculatePickScore`) is a per-pick-slot
  base score × a championship-standings-tier multiplier, summed across
  complete race results where the picked driver scored points — see
  [confidence-cup-scoring.md](confidence-cup-scoring.md). The bot must use
  this, not reimplement the draft's simpler rule.
- **Eligibility ("P6 and lower") is championship standings, not last-race
  classification.** Confirmed by reading `LeagueUpdater.GetEligibleDriversWithPos`
  / `CCUtils.GetDriverChampionshipPointSnapshotForRace`
  (`Src/UI/LeagueUpdater.cs:1048`): eligibility = cumulative championship
  points through the previous race by schedule order, ranked, filtered to
  `position > PickRules.PositionCutoff`. A driver who won last race but sits
  P8 in the championship is eligible; a driver who finished P4 last race but
  leads the championship is not. **Round 1 (no previous race) has no cutoff
  at all** — every driver is eligible. This is locked as the bot's rule
  (matches what's already live this season — the alternative, redefining it
  around last-race classification, would retroactively contradict weeks
  already scored). "Previous race" means the immediately preceding `Race` by
  round order, not "most recent race with results entered" — the pick
  window must not open until the prior race's results are actually in
  GPConf, or the standings snapshot used for eligibility/multipliers will be
  stale.

## Architecture decisions

- **Bot is a sibling project `GPConf.DiscordBot` that reads data access
  directly**, not a separate data-access library or a new HTTP/gRPC service
  layer. It source-links the same `GpConfDataAccess` and `CCUtils` the MCP
  server uses (see [mcp-server.md](mcp-server.md)), so it shares the exact
  same scoring/eligibility code without reimplementing it. It does **not**
  spawn the MCP server per command — that would be slow and heavy for a
  read-only slash command. The MCP server remains the AI-agent interface; the
  bot is a parallel consumer of the same shared code.
- **Agent drives the bot; the bot doesn't drive itself.** The Discord bot
  process stays a thin Discord-interaction layer for its slash commands, but
  **also hosts its own MCP server** (`GPConf.DiscordBot/Tools/BotMcpTools.cs`,
  registered via `.AddMcpServer().WithHttpTransport()` in `Program.cs`,
  listening on `127.0.0.1:<CONF_MCP_HTTP_PORT, default 5177>`) in the same
  process as the Discord gateway connection — built and working, not just
  planned. This is deliberately **HTTP transport, not stdio**: stdio (what
  `GPConf.McpServer` uses) spawns a new server process per client and can't
  reach an already-running bot, which is the whole point here — skills need
  to make the *live*, already-connected bot post. Tools: `post_message`,
  `post_pick_deadline_reminder`, `post_pick_announcement`, `post_pick_reveal`,
  `post_race_results`, `post_standings`, `generate_and_post`. Every tool
  reuses the exact embed-builder methods the slash commands use
  (`ReadCommands`/`PickCommands` expose them as `internal static` — see e.g.
  `BuildResultsEmbed`, `BuildPickEmbed`, `BuildPickAnnouncement`) — nothing is
  reimplemented for the automation path. All timing, orchestration, and
  narrative content (the analysis/recap blurbs) live in the skills
  (`.claude/skills/*/SKILL.md`), not in bot-side scheduling logic — the bot
  itself has no concept of a race week, a deadline, or a skill.
  One design rule worth calling out: `generate_and_post` should fire **once
  per genuinely new piece of narrative content**. A step that needs to
  *display* content already generated (by an earlier `generate_and_post`
  call, or composed by the skill/agent itself) should format it as markdown
  and post it with the plain `post_message` tool instead — never feed
  already-generated text back through the LLM for a second pass. See
  `race-end/SKILL.md` step 3 for the concrete case this came from.
- **Never hardcode eligibility or scoring bot-side.** Any bot feature that
  depends on a GPConf-computed value (eligibility, pick score, standings,
  lock state) must call an MCP tool and use its output directly. If a needed
  computation only exists as a private/UI-only helper today (e.g.
  `GetEligibleDriversWithPos`), the fix is exposing it via a new MCP tool
  backed by the same server-side logic — not porting the algorithm into the
  bot. This keeps the bot resilient to future `PickRules`/scoring changes
  without a redeploy.
- **Persistence hardening comes before the bot becomes a new writer.**
  [architecture.md](architecture.md) already documents "no locking, no
  mutex, no optimistic-concurrency check" as tolerable today because usage
  is bursty (one human or one agent editing, not both at once). A Discord
  bot collecting picks from many users near a deadline changes that
  assumption. It's worse than "bot vs. desktop app," too: MCP's stdio
  transport means **each MCP client spawns its own server process**, so an
  arbitrary number of independent `GPConf.McpServer` processes could be
  writing `gpconf.data` with zero coordination between them. Plan: add
  rotating backups before every overwrite, plus an optimistic version/hash
  check, in both `GpConfApp.Save` and `GpConfDataAccess.Save`, before wiring
  up bot-driven writes.
- **Discord-ID-to-Player linking uses an external file, not a proto field.**
  Player IDs are already known to match Discord handles (a known mapping to
  enter directly, not something to infer/fuzzy-match) — so the only question
  was where to store the link. Decided **against** adding a field to
  `Player` in `entities.proto`/`confidence_game.proto` to avoid a mid-season
  schema change: `GPConf.csproj` and `GPConf.McpServer.csproj` each compile
  their own independent copy of the generated protobuf code (separate
  binaries, not shared — see [mcp-server.md](mcp-server.md) packaging
  section), and `GpConfApp.Open()` deletes `gpconf.data` outright on any
  parse failure (`Src/GpConfApp.cs:127`). Schema drift between the two
  binaries is therefore a real destructive risk, not just abstract caution.
  Instead: a small external file (e.g. `%APPDATA%/GPConf/discord_links.json`),
  keyed by `Player.Id` (stable, never changes) → Discord user ID, read by the
  bot/MCP server alongside `gpconf.data`. Revisit folding it into the schema
  properly only after the season ends.
- **Results ingestion reuses the existing scraper**, doesn't reinvent it. The
  practice/qualifying/race-results skills shell out to
  `Python/f1_results_to_csv.py` (see
  [race-data-pipeline.md](race-data-pipeline.md)) for the scrape, then bridge
  the CSV into the JSON shape `RaceTools` expects — formalizing the "gap"
  that page already documents as currently manual/ad hoc. Two known hazards
  to carry into that bridge: driver/team name resolution is exact
  case-insensitive string match and **fails silently** to an empty ID on a
  mismatch (verify/create entities first); the scraper's raw lap count needs
  converting to GPConf's "laps behind the leader" `LapsCompleted` convention,
  not a straight field copy.

## Known blocking bug

`RaceTools.SetQualifyingResults` (`RaceTools.cs:153`) unconditionally calls
`r.QualifyingSessions.Clear()` before adding the new sessions on **every**
call — so calling it once for Sprint Qualifying and again for regular
Qualifying wipes the first one out. `SetRaceResults` doesn't have this
problem (it finds-or-replaces by session name); `SetQualifyingResults` needs
the same treatment before a race weekend can have both session types stored
at once. **Fixed as of `1106481`** — it now find-or-replaces each
`QualifyingSession` by `(SessionName, Stage)` instead of clearing all
sessions.

## DM-only read commands

The bot responds **only to slash commands in DMs** — never in channels, never to
plain text. It is not a chat bot. Each command maps to an existing MCP tool
(or will once implemented) and returns a formatted embed.

| Command | Maps to MCP tool | Output |
|---|---|---|
| `/standings` | `GetChampionshipStandings` | Championship points table (driver, team, nationality, points) |
| `/results <race>` | `GetRaceResults` | Race results table for the given race (name or round) |
| `/quali <race>` | `GetQualifyingResults` | Qualifying grid results |
| `/practice <race>` | `GetPracticeResults` | Practice session results |
| `/scores` | `GetPlayerScores` | Cumulative player confidence-cup scores |
| `/pick <player>` | `GetPlayerPicks` | A specific player's picks + computed scores per race |
| `/rules` | `GetSeason` | Current season's points rules and confidence-cup multiplier table |

These are static, synchronous replies (command → MCP tool → embed). No state,
no ephemeral messages, no select menus, no button interactions. The existing
`QueryTools` tools already cover 5 of 7 commands; the remaining two
(`/pick` needs the already-planned player MCP tools + pick lookup) come from
existing `QueryTools.GetPlayerPicks` once the player-pick data pipeline is wired
in.

**No new MCP tools required for the common commands** — the existing read-only
`QueryTools` inventory maps directly. If a future command needs a computation
that `QueryTools` doesn't cover, the rule from the main plan applies: expose it
via the MCP server, don't reimplement bot-side.

## Discord timestamp convention

Use Discord's native timestamp markup, not custom-formatted strings:
`<t:EPOCH:F>` (full date+time) followed by `<t:EPOCH:R>` (relative, e.g. "in
3 hours") in parentheses. Both render client-side and auto-localize to each
viewer's own timezone — the skill only needs to compute one Unix epoch from
whatever timezone the admin gives the Qualifying/Sprint Qualifying start
time in; no per-viewer timezone handling needed.

> **Note (2026-08-13):** the narrative-content steps below (4, 6, 7) describe
> an MCP-skill-driven agent authoring the text itself while orchestrating
> race week. A separate, parallel mechanism now exists —
> [llm-analysis-commands.md](llm-analysis-commands.md)'s four Discord slash
> commands (`/driver-analysis`, `/player-analysis`, `/season-overview`,
> `/race-recap`), which call a local Ollama server directly and
> synchronously from the bot process, no agent orchestration involved. This
> plan's skill-driven recaps are not superseded by that — they're two
> different mechanisms that happen to produce conceptually similar content.

## Race-week skill set (built)

Six `SKILL.md` files under `.claude/skills/`, discoverable by both Claude
Code and OpenCode (OpenCode reads `.claude/skills/*/SKILL.md` natively as a
compatibility path — confirmed via its own docs, no duplication needed).
Supersedes the original 7-step sketch below; kept for historical context on
what changed and why.

1. **`practice-data-entry`**, **`qualifying-data-entry`**,
   **`race-data-entry`** — each takes a motorsport.com session URL, shells
   out to `Python/f1_results_to_csv.py` (never reimplements the scrape),
   upserts any missing manufacturer/team/driver first (in that dependency
   order — `RaceTools`' name resolution fails *silently* to an empty ID on a
   mismatch), converts the scraper's raw lap count to GPConf's "laps behind
   the leader" `LapsCompleted` convention, and calls the matching
   `RaceTools` MCP tool. `qualifying-data-entry` also computes the combined
   starting grid order across Q1/Q2/Q3 eliminations.
2. **`race-weekend-prep`** — web-searches the real Qualifying/Sprint
   Qualifying start time for the deadline epoch, researches track
   history/recent news for an AI-written intro (one `generate_and_post`
   call, framed explicitly as speculation where it speculates), posts the
   deadline+scoring-rules reminder and the `@here` picks-open announcement,
   then registers a one-shot Windows Task Scheduler job
   (`pick-lockin/schedule-pick-lockin.ps1`) to fire `pick-lockin`
   automatically at the deadline — neither Claude Code's nor OpenCode's
   skill system has any scheduling primitive of its own, confirmed via
   research, so this has to live outside both.
3. **`pick-lockin`** — fires at the deadline (scheduled, or run manually);
   posts the unconditional, ranked-by-potential-score picks reveal via
   `post_pick_reveal`. No call-to-action here — that already happened in
   `race-weekend-prep`.
4. **`race-end`** — posts results/pick-reveal/driver standings/confidence-cup
   standings (four thin tool calls, no generation), one `generate_and_post`
   call for a researched post-race analysis, then an **agent-composed**
   confidence-cup blurb + next-race preview posted via plain `post_message`
   (deliberately *not* a second `generate_and_post` call — see the
   "one design rule" note above). On the season finale (this race's round
   equals the season's highest scheduled round) it also runs a richer
   `generate_and_post` covering the champion, their season arc, challengers,
   and ~5 tongue-in-cheek/data-nerdy awards computed from real per-player
   season stats.

Every skill posts non-ephemeral — they're announcements/calls-to-action for
the whole league, not private replies.

## Discord bot setup

Before any bot code can run, one-time setup:

1. **Create Discord application** — go to Discord Developer Portal (https://discord.com/developers/applications), create a new application, give it a name and icon.
2. **Create bot** — under the "Bot" tab, click "Add Bot", generate a token, copy it. This token is the only credential needed.
3. **Generate invite URL** — under "OAuth2 → URL Generator", select scopes `bot` and `applications.commands`, select the guild to invite it to. This is the URL to paste into your browser to add the bot to your Discord server.
4. **Store the token** — `.NET` will need it, via environment variable `DISCORD_BOT_TOKEN` or a local config file (not committed). The token belongs in the same `%APPDATA%/GPConf/` folder as `discord_links.json` — e.g. `%APPDATA%/GPConf/discord_credentials.json` with `"token": "..."` — or as a system/user environment variable. **Never commit the token to git.**

Then the bot appears in the server and slash commands become functional.

**For skills to reach the bot's MCP server** (see the bot-as-MCP-server bullet
above), the running Claude Code / OpenCode client also needs an MCP client
entry pointing at `http://127.0.0.1:<CONF_MCP_HTTP_PORT, default 5177>/`,
alongside the existing `gpconf` (desktop-data) server. On this machine:
Claude Code's project entry lives in `~/.claude.json` under
`projects["<repo path>"].mcpServers.gpconf-bot` (`{"type": "http", "url":
"http://127.0.0.1:5177/"}`); OpenCode reads a project-local `opencode.json`
at the repo root (`{"mcp": {"gpconf-bot": {"type": "remote", "url": "..."}}}`
— OpenCode's schema uses `"remote"`/`"local"` instead of Claude Code's
`"http"`/`"stdio"`). Both are already set up in this repo/machine. The bot
process has to actually be running for either to connect — it's not
autostarted by either client.

## Remaining build order

1. ~~Discord bot setup~~ **done** — bot is live in the target guild, token
   via `CONF_BOT_TOKEN`.
2. ~~Persistence hardening~~ **done** — rotating backups + optimistic
   version check in both `GpConfApp.Save` and `GpConfDataAccess.Save`.
3. **Not done, and no longer blocking**: the external `discord_links.json`
   Player↔Discord-user mapping was superseded by a simpler design —
   `DataService.SubmitPicks` matches/auto-registers players by Discord
   display name directly (case-insensitive), no separate link file. Revisit
   only if display-name collisions become a real problem.
4. **Not done**: League/Player MCP *write* tools (create league, add/remove
   player) — today, league creation/roster edits still go through the
   desktop UI; the bot only auto-registers new players via pick submission.
5. ~~Discord bot project scaffold with DM-only slash commands~~ **done** —
   `/standings`, `/results`, `/quali`, `/practice`, `/scores`, `/pick`,
   `/rules`, all ephemeral, `/pick` gated to keep other players' picks
   secret for the currently-open race only.
6. ~~Fix `SetQualifyingResults` session clobbering~~ **done** (`1106481`).
7. ~~CSV-to-MCP results bridge~~ **done** — as three skills
   (`practice-data-entry`, `qualifying-data-entry`, `race-data-entry`)
   rather than a standalone script, since the bridging logic needs
   judgment calls (grid-order computation, DNF/DNS/DSQ text classification)
   an agent handles better than a rigid parser.
8. ~~Pick-submission~~ **done**, but via a different mechanism than
   originally sketched — not an MCP tool, but the bot-native
   `/pick-submit`/`/pick-announce` slash-command flow (5 select-menu
   dropdowns + explicit "Lock In Picks" button), with
   `CCUtils.GetEligibleDriversWithPos` as the shared, never-duplicated
   eligibility source both the desktop app and bot call into.
9. **Design changed, not "locked" the way originally planned**: there's no
   explicit deadline-enforced lock state in the data model. The pick window
   closes implicitly — `DataService.NextPickableRace` only ever targets the
   earliest race with no recorded results, so picking after a race is
   decided is structurally impossible. The *announced* deadline
   (`race-weekend-prep`'s epoch) is a social/scheduling deadline enforced by
   the `pick-lockin` skill firing the reveal, not a hard cutoff the bot
   rejects late submissions against.
10. ~~Leaderboard/round-summary formatting~~ **done** — `/scores`, `/pick`,
    and the `post_pick_reveal`/`post_standings` MCP tools all render ranked
    tables.
11. ~~The race-week orchestration skills~~ **done** — six skills under
    `.claude/skills/`, see "Race-week skill set (built)" above. Superseded
    the original 7-step sketch (merged pre-race-analysis into `race-end`'s
    post-race analysis rather than a separate pre-race skill, and added a
    season-finale branch instead of a placeholder).

## See also

- [confidence-cup-scoring.md](confidence-cup-scoring.md) — the scoring rules
  this bot must call into, not reimplement.
- [mcp-server.md](mcp-server.md) — existing tool inventory the bot's new
  tools will sit alongside.
- [architecture.md](architecture.md) — the concurrency hazard this plan's
  persistence-hardening step addresses.
- [race-data-pipeline.md](race-data-pipeline.md) — the scraper and CSV gap
  the results-ingestion skills formalize.
