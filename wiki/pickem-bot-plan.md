# Discord pick'em bot — design & tech plan

> As of `421462d` + uncommitted changes (see [log.md](log.md)). **Design-only
> — no code for this exists in the repo yet.** This page documents decisions
> made during planning so a future session doesn't re-derive them.

## Concept

A Discord bot that runs the confidence-cup pick'em game (see
[confidence-cup-scoring.md](confidence-cup-scoring.md)) as a weekly Discord
flow instead of the desktop `LeagueUpdater` UI: players pick drivers via a
Discord select menu, the bot enforces the deadline, and an AI agent (via
Claude Code skills) drives the race-week sequence — posting the picker,
DMing confirmations, ingesting results, and writing recap/preview blurbs.

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

- **Bot extends `GPConf.McpServer`**, not a separate data-access library or a
  new HTTP/gRPC service layer. Same process-and-tool model as the existing
  tool inventory (see [mcp-server.md](mcp-server.md)).
- **Agent drives the bot; the bot doesn't drive itself.** The Discord bot
  process stays a thin Discord-interaction layer: posting messages/embeds,
  handling select-menu and button interactions, enforcing the pick deadline,
  queuing submitted picks. It exposes its own MCP tools (e.g. `post_picker`,
  `lock_race`, `post_recap`, `dm_pick_confirmation`) that Claude Code skills
  call to actually sequence a race week. All timing, orchestration, and
  narrative content (the analysis/recap blurbs) live in the skills, not in
  bot-side scheduling logic.
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
at once. Not yet fixed as of this writing.

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

## Planned race-week skill set

In sequence for a normal race week:

1. **Prep Next Race Week** — admin gives the Qualifying/Sprint Qualifying
   start time; skill posts the `<t:EPOCH:F> (<t:EPOCH:R>)` announcement,
   calls `post_picker` (driver options show the live per-slot multiplier,
   colorized/bold, sourced from MCP — never hardcoded), and once a player
   confirms via button the pick is queued, processed, and the bot DMs a
   formatted widget (picks, multipliers, potential score, total). The
   deadline for `lock_race` is the same epoch as the announcement.
2. **Input Practice Results** — admin gives a URL; bridges through the
   scraper into `SetPracticeResults`.
3. **Input Qualifying Results** — same, into `SetQualifyingResults`,
   distinguishing Sprint Qualifying from regular Qualifying (blocked on the
   bug above).
4. **Pre-race analysis blurb** — once picks are locked, agent writes a short
   piece on noteworthy eligible-driver storylines (past qualifying form,
   practice→qualifying→race trends, DNF history, current F1 news via web
   search). Must **not** expose player picks — analysis only.
5. **Input Race Results** — sprint (if applicable) and feature race, into
   `SetRaceResults`, distinct session names/`isComplete`/`pointsRulesName`
   per session.
6. **Post-race recap** — once results are in and scores tally, bot posts
   race results, confidence-cup results, championship standings, and next
   race's driver eligibilities (all via MCP, per the never-hardcode rule
   above). Agent writes a kind-but-tongue-in-cheek recap of noteworthy
   league performances, closing with a short preview of the next race.
7. **End-of-season recap** — special case of (6) when the completed race is
   the season finale. Content/format not yet designed.

## Remaining build order (nothing below is implemented yet)

1. Persistence hardening (backups + concurrency guard).
2. External `discord_links.json` mapping + a read-only "list league players"
   MCP tool to seed/review it.
3. League/Player MCP write tools (create league, add/remove player).
4. Discord bot project scaffold with DM-only slash commands (standings, results,
   quali, practice, scores, pick, rules) — uses existing `QueryTools` MCP tools,
   zero state, no chat.
5. Fix `SetQualifyingResults` session clobbering.
6. CSV-to-MCP results bridge (shared by the three ingestion skills).
7. Pick-submission MCP tool, with `GetEligibleDriversWithPos` ported to a
   shared, MCP-exposed implementation (not duplicated bot-side).
8. Pick-window locking (deadline-driven, from the Prep skill's epoch).
9. Leaderboard/round-summary formatting.
10. The seven race-week orchestration skills (prep, practice input, qualifying
    input, pre-race analysis, race results input, post-race recap, end-of-season).

## See also

- [confidence-cup-scoring.md](confidence-cup-scoring.md) — the scoring rules
  this bot must call into, not reimplement.
- [mcp-server.md](mcp-server.md) — existing tool inventory the bot's new
  tools will sit alongside.
- [architecture.md](architecture.md) — the concurrency hazard this plan's
  persistence-hardening step addresses.
- [race-data-pipeline.md](race-data-pipeline.md) — the scraper and CSV gap
  the results-ingestion skills formalize.
