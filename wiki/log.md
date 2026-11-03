# Log

Chronological record of what changed and why. One entry per meaningful
ingest. Newest first. Format: `YYYY-MM-DD — <what> — <pages touched> — <commit or "uncommitted">`.

---

**2026-11-02** — Finished the **Round 19 (GP de la Ciudad de Mexico) weekend**.
The weekend's scheduled one-shots all fired and all failed again
(`lastTaskResult 1`): FP3, qualifying and the race were never ingested, so the
whole tail of the weekend was done by hand. Scraped
`motorsport.com/f1/results/2026/mexican-gp-661693/?st={FP3,Q1,Q2,Q3,RACE}` and
ingested FP3 (20 drivers), qualifying (22 drivers, 3 stages) and the race
(22 rows, `isComplete: true`, `pointsRulesName: "Feature"`).

Two entity gaps had to be fixed before the classification was complete:
**`Ryo Hirakawa` did not exist in the season** (he replaced Ocon at Haas for
the weekend), so his P10 was created via `upsert_driver` (50, Japan, Haas)
rather than being dropped — dropping him left a hole at P10 and silently
withheld a point. **`Liam Lawson`'s `CurrentTeamId` was stale** (Red Bull,
showing him in the wrong team on every qualifying read); corrected to VCARB
and qualifying re-ingested so the stored `LapData.TeamId`s refreshed.

The classification is the **post-penalty** one: the stewards added Piastri
+10s (Lindblad collision), Colapinto +30s and Gasly +20s (yellow-flag
infringements) after the flag, which moved Bortoleto, Hadjar and Hirakawa into
the top ten. Verified position-for-position against the Wikipedia final
classification — points, cumulative gaps and the P10 row all agree. Conventions
applied: DNFs at position 0 with `LapsCompleted` = leader's laps − their laps
(Lindblad 13, Bearman 24, Bottas 29, Stroll 66, Alonso 66), and `RaceTime` as
the leader's time plus **cumulative** intervals (the source's `Gap` column is
interval-to-car-ahead, not to the leader).

Posted via the bot MCP: FP3, qualifying, race results, pick reveal, confidence
cup leaderboard, championship standings, a hand-written confidence-cup blurb
and the LLM analysis, then stripped the pick-announcement button. **Antonelli
clinched the title** (380 vs Verstappen 261, 4 rounds left, 100 still
available). `gpconf.data` 250,516 → 256,716 bytes, backed up first. Pages
touched: [log.md](log.md) — data + Discord only, no tracked source changed. —
uncommitted.

---

**2026-10-30** — Kicked off **Round 19 (GP de la Ciudad de Mexico)** by hand,
because the automated chain never did: the `GPConf-NextKickoff-*` job that
should have run on 2026-10-25 returned `lastTaskResult 1`, and worse, four
duplicate `NextKickoff` tasks (one Bahrain + three USGP) were all armed to fire
Sun 2026-11-01 — each one a full kickoff, i.e. four São Paulo kickoffs. Pruned
them down to a single anchor (`GPConf-NextKickoff-GPdelaCiudaddeMexico`).

Posted to Discord #race-week: the AI preview (3 sections), the confidence-cup
picks post (built from the live `get_pick_eligibility` pool — top-5 cutoff,
1.0x/1.5x/2.0x tiers verified per driver), the scoring-rules + deadline
reminder, the `@here` picks-open announcement (deadline **2026-10-31 15:00
Mexico City = 14:00 PDT**, epoch 1793480400), FP2 results, and an FP2 pace
update. FP1 and FP2 were scraped from
`https://www.motorsport.com/f1/results/2026/mexican-gp-661693/` and ingested
via `set_practice_results` (FP1: 13 season drivers — the rest of the field was
rookie stand-ins, dropped rather than stored with empty driver IDs; FP2: all
22).

Scheduler fixes, all in gitignored `.claude/skills/**` scripts:
`schedule-race-week-cron.ps1` `ToEpoch` was applying the venue offset wrong
(`HH:MM` folded into *minutes*-as-seconds and the sign inverted, so a 15:00
local session was registered for 03:00) and used an invalid `-LogonType`
parameter (PS 5.1 wants a `-Principal`); `schedule-pick-reminder.ps1` /
`schedule-pick-lockin.ps1` baked a bare `claude` into the task action (now an
absolute `~/.local/bin/claude.exe`). All three now `-ErrorAction Stop` so a
failed registration can't print "Scheduled …" anyway. S4U principals need
elevation on this host, so every job fell back to InteractiveToken (fires only
with a live desktop session). Registered: `GPConf-FP3-…-20261031` (12:00 PT),
`GPConf-Quali-…-20261031` (15:00 PT), `GPConf-Race-…-20261101` (14:00 PT),
`GPConf-PickReminder-20261031-204500` (13:45 PT),
`GPConf-PickLockIn-20261031-210000` (14:00 PT). Pages touched: [log.md](log.md)
only — no tracked source changed. — uncommitted.

---

**2026-10-26** — Finished the **Round 18 (COTA / MSC Cruises United States GP)
weekend** and posted everything to Discord #race-week. The weekend's five
scheduled session jobs all fired and all failed (`lastTaskResult 1`): their
`claude -p` prompts had the results URL mangled to `"=RACE"` (the `$BaseUrl`
interpolation was lost in the prompt-building step — the same bash
`$`-interpolation pitfall the cron skill warns about), so nothing was scraped.
FP1/FP2 were already in the DB from an earlier pass; FP3, Q1/Q2/Q3 and the race
were scraped live from `https://www.motorsport.com/f1/results/2026/united-states-gp-661690/?st=…`
and ingested via `set_practice_results` / `set_qualifying_results` (one call per
stage) / `set_race_results` (`isComplete: true`, `pointsRulesName: "Feature"`).
All 22 drivers resolved; the 3 DNFs (Bearman lap 24, Colapinto lap 35, Bottas
lap 48) store at position 0 with `Dnf` status per the standing convention. Then
posted FP3, qualifying, race results, pick reveal, leaderboard, driver
standings, the LLM post-race analysis, a hand-written Confidence Cup blurb, and
stripped the pick-announcement button (`close_pick_announcement`).

Two code fixes landed in the same cycle (both uncommitted):

- **Qualifying classification order was inverted.** `ReadCommands.BuildQualiEmbed`
  (×2), `AnalysisFacts.QualifyingOrder` and `QueryTools.GetQualifyingResults`
  bucketed drivers with `x.stage == 0 ? 0 : x.stage == 2 ? 1 : 2` — a leftover
  from a 0-indexed ingest path. Per-stage ingestion writes the **1-based** stage
  actually reached (`3` = took part in Q3), so Q3 was landing in the same bucket
  as Q1 casualties and the embed listed Q2 casualties as P1–P6. Now
  `OrderByDescending(x => x.stage == 0 ? int.MaxValue : x.stage)` in all four
  places (and `StageName(0)` renders "Q3"), which also fixes the already-stored
  Round 16 / Round 17 classifications. Pages touched:
  [data-model.md](data-model.md).
- **`OllamaClient` failed every LLM post** because the splash endpoint no longer
  serves `incoai/Qwen3.8-27B-Splash` (it 404s `model_not_found`; the resident
  model is now `mlx-community/Qwen3.8-27B-4bit`). Default updated, and
  `GenerateAsync` now self-heals: on `model_not_found` it reads `/v1/models`,
  adopts the advertised id and retries once. Pages touched:
  [llm-analysis-commands.md](llm-analysis-commands.md).

- **`dotnet build` was broken repo-wide** by untracked scratch tooling: `Python/_picklog/PickLog.cs` (an ad-hoc C# project left in the tree, gitignored) got globbed into `GPConf.csproj`, yielding CS8802 (duplicate top-level statements) plus duplicate-assembly-attribute errors from its `obj/`. `GPConf.csproj` now excludes `Python\**` alongside the existing MCP-server / Discord-bot removals; the solution builds clean again.

Also verified: the confidence-cup scores flow through (top score for the round:
WildGingerBear 13; cumulative leader AWK0TAC0 216, chopper34 212, WildGingerBear
204). The `GPConf-NextKickoff-*` task that should have kicked off **Round 19
(Mexico City)** on 2026-10-25 18:00 also returned `lastTaskResult 1`, so Mexico
has no pick announcement yet and its session jobs are not registered.

---

**2026-10-05** — Finished the **Round 16 (Sepang) weekend** and posted everything
to Discord #race-week: FP1/FP2/FP3, qualifying (Q1/Q2/Q3), the race result,
the Confidence Cup pick reveal, and the updated league leaderboard. Data was
re-ingested from the motorsport.com results CSVs via
`Python/_bahrain_reingest.py` (one script, all sessions; `IsComplete` set on
the race and `pointsRulesName="feature"` passed to `set_race_results`, which
resolves the season's **Feature** preset — 25/18/15/12/10/8/6/4/2/1 — by
case-insensitive name). All 22 season drivers resolved to the season's driver
DB (no unknowns). The 3 DNFs (Russell, Albon, Bottas — all off the lead lap
after ~1–3 laps) are stored at position 0 with their on-track lap counts, per
the standing data convention; the league reveal scores them 0 points.

The scoring fix that made the above work landed uncommitted this cycle
(13 files, +346/−61): `CCUtils` (in `Src/Utilities/ConfCupUtils.cs`) drops the
`IsComplete` gate in `GetPickScoreFromResults` (counts any session with
recorded results) and adds a case-insensitive rules-name fallback in
`GetPointsScoringRulesByName` for when `PointRulesId` is empty. The Discord
bot now flows `race.PointRulesId` → `GetScoreMultiplier` for pick-reveal and
uses `GenerateRaceOrder()` (see [confidence-cup-scoring.md](confidence-cup-scoring.md))
as its single source of truth for result ordering. Pages touched:
[confidence-cup-scoring.md](confidence-cup-scoring.md) (2.0/0.5 history +
single-math-source rule), [ui-layer.md](ui-layer.md)
(RaceWeekendDashboard/PointsPresets/LeagueUpdater status),
[race-data-pipeline.md](race-data-pipeline.md) (re-ingest script + cron gap).
Data: `gpconf.data` re-saved with Bahrain sessions. — uncommitted
(.gitignore, GPConf.DiscordBot/{Commands,Services,Tools}/*.cs,
GPConf.McpServer/Tools/QueryTools.cs, Src/UI/LeagueUpdater.cs,
Src/UI/StaticWidgets/{PointsPresets,RaceWeekendDashboard}.cs,
Src/Utilities/ConfCupUtils.cs, wiki/confidence-cup-scoring.md, wiki/log.md).

**2026-09-30** — Added a **cron self-recovery** one-shot for the Round 16 Sepang
weekend: `GPConf-CronFix-BaseUrl-Sepang-2026` (fires 2026-10-01 21:00 PT =
04:00 UTC, just before FP1). The 5 data-ingest one-shots were registered with a
best-guess motorsport.com base URL (`2026/bahrain-gp-in-malaysia`) because the
*actual* results-page slug is unresolvable before the weekend exists (motorsport.com
pages are JS-gated; Brave hasn't indexed the new event page). The fix job runs
`cronfix/fix_baseurl.ps1`, which drives the web-research bot's `web_research`
plugin (scratch `resolve_slug.py`, 3× retry) to find the real slug, then
re-registers the per-session jobs via `schedule-race-week-cron.ps1` with the
correct base URL (idempotent). If the slug can't be found it leaves the jobs in
place (their retry logic copes). Data/automation only; no source changed. —
uncommitted.

**2026-09-30** — Kicked off the race weekend for **Round 16: Formula 1 Gulf Air
Bahrain Grand Prix in Malaysia** (Sepang, UTC+8) via the `race-weekend-prep`
skill. Posted to Discord (#race-week): (1) an AI-written weekend preview
(Track & History / Storylines Since Baku / What To Watch) and (2) a dedicated
Confidence Cup Picks post (strong picks in the 1.0× tier — Verstappen, Piastri,
Hadjar; value picks in the 1.5×/2.0× tiers — Albon, Bottas, Alonso, Hulkenberg,
Stroll, Sainz) — the 5 top-ranked drivers (Antonelli, Russell, Hamilton, Norris,
Leclerc) are locked and un-pickable. Then the deterministic steps: pick-deadline
reminder (Sat 16:00 local = **epoch 1791039600** = 08:00 UTC), the picks-open
@here announcement (deadline recorded on the race), and the two pick one-shots
(`GPConf-PickReminder-20261003-144500` @ 15 min before, `GPConf-PickLockIn-
20261003-150000` @ the deadline). Also registered the race-week-cron automation:
FP1/FP2 (Oct 2), FP3 + Qualifying (Oct 3), Race + race-end (Oct 4) plus the
self-perpetuating `GPConf-NextKickoff-…` (Sunday 18:00, will auto-derive
Singapore R17). All one-shots `Ready`. The single 4-section `generate_and_post`
call timed out on the splash model's ~11k `reasoning_content` budget, so the
preview and picks were split into two smaller LLM calls — documented in
`.claude/skills/race-weekend-prep/SKILL.md`. Data + automation only; no source
changed. — uncommitted.

**2026-09-30** — Corrected the app's 2026 calendar in `gpconf.data` (F1 Official
Season '26) after a web-research pass (via the `research` bot's `web_research`
tool) confirmed F1's **official** `formula1.com/racing/2026` schedule. The season
was seeded with 22 rounds (Bahrain + Saudi Arabia originally dropped for the
Middle East) but F1 has since **added back a Bahrain GP, rescheduled to the
Sepang International Circuit in Malaysia for 2–4 October**, making it Round 16.
The live store still carried the old 22-round calendar (Singapore = R16), so the
upcoming races were stale. Inserted the Bahrain-in-Malaysia GP as R16 and
renumbered Singapore→R17, US→R18, Mexico→R19, Brazil→R20, Las Vegas→R21, Qatar→R22,
Abu Dhabi→R23 (23 rounds, all with official dates); verified via a fresh
`get_season` read. No results/standings affected (renumbered rounds 16–22 had no
dates/deadlines/announcement IDs and results link by name/id, not round). Data
store only — no wiki code page covers the calendar. — uncommitted (data change).

**2026-09-30** — `4ae3b3f` switches `OllamaClient` from Ollama's native
`/api/chat` to the OpenAI-compatible `/v1/chat/completions` endpoint: the
in-use "splash" vLLM endpoint (the 48GB MacBook's resident Qwen3.8) is
OpenAI-only, so the Ollama wrapper could never reach it (`/api/chat` → 404)
and every `generate_and_post` / LLM analysis command failed. The request
shape is now standard OpenAI (`model`, `messages`, `stream:false`,
`max_tokens:16384`, `temperature`, `top_p`) — still OpenAI-compatible
Ollama servers work via their `/v1` proxy, hence the unchanged class name
and `OLLAMA_HOST` / `OLLAMA_MODEL` env vars, whose defaults now point at
splash (`100.123.43.8:11436` / `incoai/Qwen3.8-27B-Splash`). The generous
`max_tokens` is load-bearing: Qwen3.8 spends several thousand reasoning
tokens before emitting content, so a tight cap truncates every reply to
empty. Non-2xx responses now include the server error body in the
`OllamaException` message. Pages touched:
[llm-analysis-commands.md](llm-analysis-commands.md), [log.md](log.md). — `4ae3b3f`.

**2026-09-29** — Committed the Round-14 confidence-cup fix plus a batch of
working-tree changes as seven tightly-scoped commits:
`14336bc` pins `LapData.team_id` at ingestion (practice/qualifying laps
resolve to the team driven *that session*; `Migrate()` backfills old data;
MCP query tools, Discord FP/quali embeds, and the desktop tables now
fallback to the driver's current team, so a transferred driver's history
keeps the original team color instead of zeroing);
`4b26bbb` adds the `SetRaceComplete` MCP tool (flips
`RaceResult.IsComplete` on stored results without re-passing them —
the retroactive fix path used for the Round-14 incident);
`2fb5f93` hardens `GenerateRaceOrder` against the Round-14 failure mode
(duplicate/unset `Position` can no longer promote a DNF into a points
slot) and adds `IsSeasonEntrant` so a genuine mid-season debut is pickable
on its first race; `c8f6196` null-guards combo labels (TeamEditor,
DriverEditor, RaceUpdater), fixes the `RaceConfig` drag-downward off-by-one,
resolves `SeasonUpdater`'s `currentSeason` from the canonical `Seasons`
list, and makes `Clear()` also wipe `Leagues` with a real `CurrentSeason.Id`;
`3658dc7` adds points-scoring presets (F1 2025 GP/Sprint + three historical
grids) with a Load Preset combo in `PointsScoringEditor`; `5459ff0` adds the
`RaceWeekendDashboard` window (per-race data-entry status, Seasons menu);
`1f3cf1b` raises the gpconf MCP timeout in `opencode.json`. Pages touched:
[data-model.md](data-model.md), [mcp-server.md](mcp-server.md),
[architecture.md](architecture.md), [ui-layer.md](ui-layer.md),
[confidence-cup-scoring.md](confidence-cup-scoring.md),
[race-data-pipeline.md](race-data-pipeline.md),
[pickem-bot-plan.md](pickem-bot-plan.md),
[llm-analysis-commands.md](llm-analysis-commands.md),
[index.md](index.md), [log.md](log.md). — `1f3cf1b`.

**2026-09-15** — Fixed Round 14 ("Tag Heuer Gran Premio De Espana", 2026)
confidence-cup scoring corruption. The result set had been ingested with four
DNFs (Sainz, Perez, Stroll, Hamilton) all stored at `Position = 1` alongside
the winner; `GenerateRaceOrder`'s plain `OrderBy(Position)` then promoted those
DNFs into P2–P5 and pushed the real P7–P10 points finishers (Colapinto,
Piastri, Lindblad, Hulkenberg) out of the scoring positions, zeroing valid
picks. Corrected the stored classification (DNFs reclassified 19–22 by laps
completed) and hardened `GenerateRaceOrder` (`Src/Utilities/ConfCupUtils.cs`)
to force non-finishers/unset positions behind classified finishers regardless
of stored `Position` — a no-op for every clean round, but no future duplicate
or missing position can promote a DNF into the points again. Also set the
race's `circuit` to `MADRING` (was `"???"`). Corrected R14 pick scores:
AWK0TAC0 23 (+21), Sp00nAhh 15.5, Sceve 12.5, WildGingerBear 13, chopper34 9,
Skweepz 7.5; through R14 AWK0TAC0 moves to P1 (178.75) ahead of chopper34
(170.5). Pages touched: [confidence-cup-scoring.md](confidence-cup-scoring.md),
[log.md](log.md), [mcp-server.md](mcp-server.md), [data-model.md](data-model.md),
[architecture.md](architecture.md), [ui-layer.md](ui-layer.md), [pickem-bot-plan.md](pickem-bot-plan.md),
[index.md](index.md). — `2fb5f93` (plus the team_id, `SetRaceComplete`, UI, and
opencode commits of 2026-09-29).

**2026-08-15** — Disambiguated the `gpconf-bot` MCP tool surface. Removed
`post_standings`'s `confidenceCup: true` mode (which posted the plain
confidence-cup scores embed) — it was redundant with the newer
`post_leaderboard` tool (ranked player scores with momentum), and no skill
called it. `post_standings` is now driver-championship only; its description
points callers to `post_leaderboard` for confidence-cup standings. Pages
touched: [pickem-bot-plan.md](pickem-bot-plan.md), [log.md](log.md). —
uncommitted.

**2026-08-15** — Converted the six DM-only read commands (`/standings`,
`/results`, `/quali`, `/practice`, `/pick`, `/rules`) from typed-arg commands
to parameterless ephemeral dropdown wizards, making the "any command needing
structured input uses a wizard" convention in
[llm-analysis-commands.md](llm-analysis-commands.md) universally true. The
read wizard is a separate, simpler module in `ReadCommands.cs` (`read_*`
component ids) that reuses `AnalysisSessionStore` (new `AnalysisKind` values
`Standings`/`Results`/`Quali`/`Practice`/`Pick`/`Rules` + a `SessionNumber`
field) and the shared dropdown builders from `AnalysisCommands` (widened to
`internal static`); results render ephemeral in the DM, one-shot. Removed
`/scores` + `BuildScoresEmbed` (superseded by `/leaderboard`) and the old
`results_league` disambiguation picker (the wizard's auto-resolving league
dropdown replaces it). `/pick`'s secrecy gate and `/results`' league
attribution are preserved. Pages touched: [pickem-bot-plan.md](pickem-bot-plan.md),
[llm-analysis-commands.md](llm-analysis-commands.md), [log.md](log.md). —
`3d9462c`.

**2026-08-15** — Added `GetPickEligibility` MCP tool to `GPConf.McpServer`
(`QueryTools`). It returns the active league `PickRules` (incl.
`PositionCutoff`, base scores, standings multipliers), the upcoming
(undecided) race, the ineligible drivers (ranked `P1…PositionCutoff`), and
the eligible pool with per-driver standings multipliers. This gives
`race-weekend-prep`'s intro a single authoritative "who can be picked" source
so its Confidence Cup Picks section never recommends an ineligible driver
(fixed a real bug where it recommended Norris/Leclerc, both P1–P5 = cutoff,
as strong picks). `race-weekend-prep` now calls `get_pick_eligibility`
instead of deriving eligibility from raw standings. Pages touched:
[mcp-server.md](mcp-server.md). — uncommitted.

**2026-08-15** — `race-weekend-prep` intro now includes a confidence-cup
picks analysis. Its research step pulls the weekend's practice/qualifying
data and the previous race's results + championship standings from the
`gpconf` MCP server, web-searches for similar tracks and driver pace at them,
and weighs the confidence-cup scoring mechanics to flag strong/weak and value
picks for the track. Surfaced as a dedicated `🎯 Confidence Cup Picks` section
in the `generate_and_post` intro (sections now
`Track & History, Storylines Since Last Race, Confidence Cup Picks, What To
Watch`). Pages touched:
[pickem-bot-plan.md](pickem-bot-plan.md). — uncommitted.

**2026-08-15** — Enhanced `/compare` (driver comparison) with pace analysis.
`BuildCompareFacts` now surfaces practice, qualifying, and race pace: average
fastest-lap and race-time gaps (race pace), average practice fastest-lap gap
(practice pace), and a quali head-to-head count alongside the existing grid
positions. The AI analysis prompt now explicitly asks the model to discuss
pace differences and how they factor into results. Pages touched:
[llm-analysis-commands.md](llm-analysis-commands.md). — uncommitted.

**2026-08-15** — Made the three comparison commands hybrid: they now post a
prettier static stats embed **and** an Ollama-generated narrative analysis
highlighting noteworthy differences and key performance indicators.
`/team-analysis` became a two-team comparison (new `Team2Id` session field,
`analysis_team2` handler, `BuildTeam2Menu`, `BuildTeamCompareFacts`).
`/compare` and `/h2h` now also emit an AI analysis alongside their static
embed. The comparison facts builders emit `**Header**` markdown sections that
`ReadCommands.ParseFactsFields` splits into titled embed fields. Pages
touched: [llm-analysis-commands.md](llm-analysis-commands.md). — uncommitted.

**2026-08-15** — Converted the five deterministic stat commands
(`/season-stats`, `/leaderboard`, `/compare`, `/h2h`, `/projected`) from
typed slash-arg commands to the parameterless dropdown-wizard pattern, and
added `/team-analysis` (Ollama-backed). Established the hard convention that
**any bot command needing structured input (season/driver/player/team/race/
league) must use a wizard, never slash args.** `AnalysisSessionStore` gained
`Team`/`SeasonStats`/`Leaderboard`/`Compare`/`H2H`/`Projected` kinds and
`Driver2Id`/`Player2Name` fields; `AnalysisCommands` gained the five wizard
commands, `analysis_driver2`/`analysis_player2` handlers, and a deterministic
generate path (embed built directly, no Ollama). The `Build*Embed` builders
stayed in `ReadCommands.cs` (shared with `BotMcpTools`). Pages touched:
[llm-analysis-commands.md](llm-analysis-commands.md), [index.md](index.md).
— uncommitted.

**2026-08-14** — Added the `pick-reminder` race-week skill: fires 15
minutes before the pick deadline and posts an `@here` nudge listing any
players who still haven't submitted picks (compares `get_player_picks`
against the full roster from `get_player_scores`). New
`.claude/skills/pick-reminder/SKILL.md` + `schedule-pick-reminder.ps1`
(mirrors `schedule-pick-lockin.ps1`, fires at deadline − 15 min);
`race-weekend-prep` now schedules it alongside `pick-lockin`. Because the
bot stores players by Discord display name (not user ID) and runs with
`GatewayIntents.None`, per-player @mentions aren't reliable, so the reminder
uses an `@here` ping plus a named list. Pages touched:
[pickem-bot-plan.md](pickem-bot-plan.md). — uncommitted.

**2026-08-14** — Enforced the pick deadline as a hard cutoff. Added
`Race.pick_deadline_epoch` + `Race.announcement_message_id` (race.proto
fields 11/12). `post_pick_announcement` now takes a `deadlineEpoch` and
records both on the race. `DataService.SubmitPicks` rejects late submissions
(new `SubmitPicksResult.DeadlinePassed`), and the shared
`RunPickerFlowAsync` guard (covers `/pick-submit`, the announce button, and
resumed sessions) refuses to open a picker once the deadline has passed.
Added a `close_pick_announcement` bot MCP tool that strips the announce
button from the recorded message; `pick-lockin` now calls it before the
reveal. Fixed `schedule-pick-lockin.ps1` (dropped `-DeleteExpiredTaskAfter`,
which needs an `EndBoundary` PS 5.1 can't set — was failing 0x80041319).
Pages touched: [data-model.md](data-model.md), [pickem-bot-plan.md](pickem-bot-plan.md).
— uncommitted.

**2026-08-14** — Generalized the analysis wizard to all four commands and
made results persistent. `DriverAnalysisSessionStore` became
`AnalysisSessionStore` with a `Kind` (`Driver`/`Player`/`Season`/`Recap`) and
optional `PlayerName`/`RaceId`; `/player-analysis`, `/season-overview`, and
`/race-recap` are now parameterless wizard commands (replacing their
typed-param versions and the old `player_analysis_league`/`analysis_league`
picker handlers). Generate now acks by disabling the button ("Generating…"),
posts the analysis as a separate public persistent channel message, then
re-enables the button and keeps the session for regeneration — `llm-analysis-commands.md` — uncommitted.

**2026-08-14** — Switched the default `OLLAMA_MODEL` in `OllamaClient.cs`
from `muse-glimmer:30b-mlx` to `qwen3.8:27b-mlx` — `llm-analysis-commands.md`
— uncommitted.

**2026-08-14** — Turned `/driver-analysis` from a typed-param command into a
parameterless 3-select-menu-plus-button wizard (season -> driver -> league ->
Generate), state-tracked by the new `DriverAnalysisSessionStore`
(`GPConf.DiscordBot/Services/DriverAnalysisSessionStore.cs`, mirrors
`PickSessionStore`). Added a "GPConf confidence-cup outlook" section to
`AnalysisFacts.BuildDriverFacts` (new optional `league`/`gs` params): pick
eligibility for the next race, standings multiplier if eligible, the
league's position-cutoff rule, and a recent finishing-trend line — all
computed via the same `CCUtils` entry points the real picker uses, never
reimplemented — `llm-analysis-commands.md` — uncommitted.

**2026-08-14** — Added `CCUtils.HasQualifiedAndStarted` and used it to filter
`GetEligibleDriversWithPos`'s pick pool (from round 2 onward) so guest/junior
drivers who only ran practice laps in a senior driver's car don't show up as
legal confidence-cup picks — `confidence-cup-scoring.md` — uncommitted.

**2026-08-14** — Built the full race-week automation skills system planned
in `pickem-bot-plan.md`: (1) `GPConf.DiscordBot` now hosts its own MCP
server over HTTP (`Tools/BotMcpTools.cs`, `Program.cs` switched to
`Microsoft.NET.Sdk.Web`/`WebApplication` running alongside the existing
Discord gateway connection) with 7 tools (`post_message`,
`post_pick_deadline_reminder`, `post_pick_announcement`, `post_pick_reveal`,
`post_race_results`, `post_standings`, `generate_and_post`), all reusing
existing embed builders from `ReadCommands`/`PickCommands` (widened from
`private` to `internal static`) rather than reimplementing them; (2) six
`SKILL.md` files under `.claude/skills/` (`practice-data-entry`,
`qualifying-data-entry`, `race-data-entry`, `race-weekend-prep`,
`pick-lockin`, `race-end`), readable by both Claude Code and OpenCode;
(3) `pick-lockin/schedule-pick-lockin.ps1` registers a one-shot Windows
Task Scheduler job (neither Claude Code nor OpenCode has a scheduling
primitive); (4) registered the new `gpconf-bot` MCP server for both clients
— Claude Code via `~/.claude.json`, OpenCode via a new project-local
`opencode.json`. Verified end-to-end over raw MCP JSON-RPC (initialize →
tools/list → tools/call `post_message`) — confirmed posting to the
configured Discord channel after fixing a bot-role permission gap on
Discord's side. Design note carried into `generate_and_post`'s docs and the
`race-end` skill: LLM generation should fire once per new piece of
narrative content — a step that only needs to *display* already-generated
content should format it and use the plain `post_message` tool, not
re-generate. Pages touched: [pickem-bot-plan.md](pickem-bot-plan.md),
[mcp-server.md](mcp-server.md). — uncommitted.

**2026-08-13** — Added four LLM-generated analysis Discord slash commands
(`/driver-analysis`, `/player-analysis`, `/season-overview`, `/race-recap`)
backed by a locally-hosted Ollama server (`OllamaClient.cs`, first outbound
HTTP integration in the .NET codebase). All facts referenced in generated
text are computed in C# (`AnalysisFacts.cs`) from existing `DataService`/
`CCUtils` output — the model narrates, never computes. New
`DataService.PlayerScoresPerRace` aggregate added for per-race (not
cumulative) confidence-cup scores. A separate, parallel mechanism from the
MCP-skill-driven recaps `pickem-bot-plan.md` already envisioned, not a
replacement. Pages touched: [llm-analysis-commands.md](llm-analysis-commands.md)
(new), [index.md](index.md), [CLAUDE.md](CLAUDE.md), [pickem-bot-plan.md](pickem-bot-plan.md).
— uncommitted.

**2026-08-13** — Fixed `RaceTools.SetQualifyingResults` session clobbering
(`RaceTools.cs:153`): it unconditionally cleared `r.QualifyingSessions` on
every call, so storing Sprint Qualifying then regular Qualifying on the same
race wiped the first out. Now find-or-replaces each `QualifyingSession` by
`(SessionName, Stage)`, mirroring `SetRaceResults`. Pages touched:
[pickem-bot-plan.md](pickem-bot-plan.md). — uncommitted.

**2026-08-13** — Scaffolded `GPConf.DiscordBot/`: a sibling .NET project
(Discord.Net 3.20.1) with DM-only slash commands (`/standings`, `/results`,
`/quali`, `/practice`, `/scores`, `/pick`, `/rules`). It source-links the same
`GpConfDataAccess` + `CCUtils` the MCP server uses, so it reads `gpconf.data`
directly and shares scoring/eligibility code without reimplementing it — a
deliberate deviation from the plan's earlier "bot extends the MCP server"
wording (spawning an MCP subprocess per command would be slow/heavy). Added
`GPConf.DiscordBot\**` to `GPConf.csproj`'s compile-exclusion list and to the
solution. Builds clean. Needs a real `DISCORD_BOT_TOKEN` to run. Pages touched:
[pickem-bot-plan.md](pickem-bot-plan.md), [race-data-pipeline.md](race-data-pipeline.md).
— uncommitted.

**2026-08-13** — Committed `4f40de8` ("feat: race data pipeline, scoring
multiplier rework, DM bot design"): the previously-uncommitted working-tree
diff (`is_complete`, `conf_cup_multiplier`, `RaceUpdater` starting grid,
MCP query de-dup, self-contained packaging) plus the `RaceData/` CSVs and the
wiki. Refreshed all wiki "As of" lines to `4f40de8` and dropped the
"uncommitted"/"untracked" qualifiers that no longer apply. Pages touched:
[index.md](index.md), [architecture.md](architecture.md), [data-model.md](data-model.md),
[mcp-server.md](mcp-server.md), [ui-layer.md](ui-layer.md),
[confidence-cup-scoring.md](confidence-cup-scoring.md),
[race-data-pipeline.md](race-data-pipeline.md). — `4f40de8`.

**2026-08-13** — Added [pickem-bot-plan.md](pickem-bot-plan.md): design & tech
plan for a Discord pick'em bot extending the confidence-cup model, produced
by ground-truthing an externally-drafted plan (`pickem_bot.md`, not in this
repo) against the actual data model, MCP tools, and UI. Records: corrections
to the original draft (no separate storage, real weighted scoring not "1pt
per hit", eligibility = championship standings not last-race classification),
architecture decisions (bot extends the MCP server; agent-drives-bot split
via new bot-side MCP tools; persistence hardening before the bot becomes a
new concurrent writer; Discord-ID-to-Player linking via an external file
instead of a proto field, specifically to avoid a mid-season schema change),
a newly-found blocking bug (`SetQualifyingResults` clears all sessions on
every call), the Discord `<t:EPOCH:F>`/`<t:EPOCH:R>` timestamp convention,
and the planned race-week skill sequence. Pages touched:
[index.md](index.md), [pickem-bot-plan.md](pickem-bot-plan.md). — uncommitted
(no bot code exists yet; this page is planning only).

**2026-08-12** — Wiki created from scratch, covering the codebase as it
stood at commit `421462d` plus the in-flight uncommitted working-tree
changes (confidence-cup "result completeness" + per-ruleset multiplier
rework, MCP query-logic de-duplication, starting-grid UI, MCP self-contained
packaging). Pages created: [index.md](index.md), [architecture.md](architecture.md),
[data-model.md](data-model.md), [mcp-server.md](mcp-server.md),
[ui-layer.md](ui-layer.md), [confidence-cup-scoring.md](confidence-cup-scoring.md),
[race-data-pipeline.md](race-data-pipeline.md), [CLAUDE.md](CLAUDE.md) (schema).
— uncommitted (wiki files themselves not yet committed to git).

**As of `421462d`** (`fixed UI drift in race data`, tip commit) — summary of
repo state the wiki was seeded from:

- Uncommitted working-tree diff (10 files, +162/−107) implements a single
  coherent feature: `RaceResult.is_complete` (new proto field) gates
  confidence-cup scoring on a per-session basis; `PointsScoringRules.conf_cup_multiplier`
  (new proto field) replaces a hardcoded "sprint = 0.5×, detected by
  string-matching the race name" heuristic that used to live duplicated in
  `LeagueUpdater.cs` and `QueryTools.cs` — both copies were deleted in favor
  of one shared implementation in `Src/Utilities/ConfCupUtils.cs`, now
  source-linked into the MCP server project so it's genuinely one
  implementation, not two kept in sync by hand. `Race.is_sprint_weekend`
  (old field 10) was removed/reserved as part of this, since sprint-ness no
  longer needs to be a stored flag. A read-only "Starting Grid" table (derived
  from qualifying) was added to `RaceUpdater`. See
  [confidence-cup-scoring.md](confidence-cup-scoring.md) and
  [data-model.md](data-model.md) for details.
- `3a01b82` ("adding more mcp tool functionality and a non-AI scraper...")
  added the `Python/f1_results_to_csv.py` motorsport.com scraper and (per
  `git diff --stat`) the `RaceTools`/`QueryTools` groundwork this later diff
  builds on. The untracked `RaceData/` directory (66 CSVs across 11
  race-locations, 2025 + partial 2026) is scraper output, not yet consumed
  by any automated ingestion path — see
  [race-data-pipeline.md](race-data-pipeline.md) for the manual gap.
- `1362dd8` — CLAUDE.md updated (root project instructions).
- `f215725`/`3b3bafe`/`698b944` — original MCP server + installer stood up.

---

## Known stale-page risks to watch

- [race-data-pipeline.md](race-data-pipeline.md) describes the CSV→JSON
  ingestion gap as manual/agent-driven. If an automated bridge script lands
  (planned as part of the Discord bot's results-ingestion skills), that page's
  "gap" framing needs revisiting.
