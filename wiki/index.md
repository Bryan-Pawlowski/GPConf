# GPConf wiki

> As of `2bfdba6` + uncommitted changes (see [log.md](log.md)).

GPConf is a Windows desktop app (ImGui + OpenGL 3.3 + SDL3, .NET) for building
and running Formula 1 "confidence cup" fantasy seasons: season/driver/team
data entry, race-result entry, and a pick-scoring game layered on top. A
companion process, `GPConf.McpServer`, exposes the same data store as MCP
tools so an AI agent can read/write season data directly — e.g. to ingest
scraped race results.

## Pages

- **[architecture.md](architecture.md)** — the two-process model (desktop app
  + MCP server) sharing one file, the render loop, how they stay in sync.
- **[data-model.md](data-model.md)** — the protobuf schema (`entities`,
  `race`, `season`, `confidence_game`), what references what, ID conventions.
- **[mcp-server.md](mcp-server.md)** — every MCP tool, what it does, the
  data-access layer, concurrency caveats, how it's packaged/installed.
- **[ui-layer.md](ui-layer.md)** — map of the ImGui widgets, which windows
  exist, what's a stub.
- **[confidence-cup-scoring.md](confidence-cup-scoring.md)** — how
  championship points and pick-scoring are computed (`CCUtils`), including the
  in-flight "result completeness" + per-ruleset multiplier rework.
- **[race-data-pipeline.md](race-data-pipeline.md)** — the standalone
  motorsport.com scraper and the `RaceData/` CSV dumps it produces, and the
  (currently manual) gap between "CSV on disk" and "data in `gpconf.data`".
- **[pickem-bot-plan.md](pickem-bot-plan.md)** — design & tech plan for a
  Discord pick'em bot extending the confidence-cup model. The `GPConf.DiscordBot/`
  scaffold, DM-only read commands (now wizards), the pick-submission flow
  (`/pick-submit`, `/pick-announce`), LLM analysis commands, the bot-as-MCP-server
  surface, and the seven race-week orchestration skills are all built.
- **[llm-analysis-commands.md](llm-analysis-commands.md)** — four Discord
  slash commands (`/driver-analysis`, `/player-analysis`, `/season-overview`,
  `/race-recap`) that generate narrative text via a local Ollama server,
  layered on the same data — a separate mechanism from the MCP-skill-driven
  recaps `pickem-bot-plan.md` envisions. Also documents the hard convention
  that any command needing structured input (including the DM-only read
  commands `/standings`, `/results`, `/quali`, `/practice`, `/pick`,
  `/rules`) must use a parameterless dropdown wizard.
- **[log.md](log.md)** — dated log of what changed in the codebase and in
  this wiki.

## Orientation for a new session

1. Read [architecture.md](architecture.md) first — it explains why there are
   two processes touching one file and where the sync hazard lives.
2. [data-model.md](data-model.md) is the fastest way to answer "what fields
   does X have" without opening `.proto` files.
3. If the task involves scoring (points, picks, standings), start at
   [confidence-cup-scoring.md](confidence-cup-scoring.md) — the logic lives in
   one place (`CCUtils`) and is shared by the app and the MCP server.
4. If the task involves getting real race data into the app, read
   [race-data-pipeline.md](race-data-pipeline.md) — there's a known manual
   step in that pipeline.
5. If the task involves the Discord bot, read
   [pickem-bot-plan.md](pickem-bot-plan.md) first — it records decisions
   (architecture, eligibility semantics, a blocking bug, the deadline/lock
   design) that would otherwise get re-derived from scratch.

See [wiki/CLAUDE.md](CLAUDE.md) for how this wiki is maintained (ingest /
query / lint) and root [CLAUDE.md](../CLAUDE.md) for build/run commands and
project-wide conventions (ID allocation, ImGui `##` suffix hygiene, the
`LapsCompleted`-is-a-gap convention).
