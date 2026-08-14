# Log

Chronological record of what changed and why. One entry per meaningful
ingest. Newest first. Format: `YYYY-MM-DD — <what> — <pages touched> — <commit or "uncommitted">`.

---

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
