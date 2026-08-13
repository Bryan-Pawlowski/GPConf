# Log

Chronological record of what changed and why. One entry per meaningful
ingest. Newest first. Format: `YYYY-MM-DD — <what> — <pages touched> — <commit or "uncommitted">`.

---

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

- [data-model.md](data-model.md) and [confidence-cup-scoring.md](confidence-cup-scoring.md)
  both describe `is_complete` and `conf_cup_multiplier` as **uncommitted**.
  Once that working-tree diff is committed, update both pages' "As of" lines
  to the new commit hash and drop the "uncommitted" qualifiers — the
  behavior described won't change, just its status.
- [race-data-pipeline.md](race-data-pipeline.md) describes `RaceData/` as
  untracked. If it gets committed (or `.gitignore`d, or replaced by an
  automated ingestion script), that page's "gap" framing needs revisiting.
