# wiki/ — schema

This directory is an **LLM wiki**: a persistent, cross-referenced knowledge base
about the GPConf codebase, maintained by Claude Code instead of being
re-derived from scratch every session. Pattern: <https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f>.

Three layers:

1. **Raw sources** — the actual code (`Src/`, `GPConf.McpServer/`, `RaceData/`,
   git history). Never summarized *into* here permanently without a page;
   never edited by the wiki.
2. **The wiki** — the `*.md` pages in this directory. Each page is a
   synthesized, standing answer to "what is X and how does it work," kept
   current as the code changes.
3. **This file** — the schema: how pages are organized and how to keep them
   honest.

## Pages

| Page | Covers |
|---|---|
| [index.md](index.md) | Catalog / entry point |
| [architecture.md](architecture.md) | Process model, render loop, app↔MCP sync |
| [data-model.md](data-model.md) | Protobuf schema, entity relationships, ID scheme |
| [mcp-server.md](mcp-server.md) | MCP tool inventory, data access, concurrency, packaging |
| [ui-layer.md](ui-layer.md) | ImGui widget map, what's stub vs implemented |
| [confidence-cup-scoring.md](confidence-cup-scoring.md) | `CCUtils` scoring rules (championship + pick scoring) |
| [race-data-pipeline.md](race-data-pipeline.md) | The motorsport.com scraper, `RaceData/` CSVs, ingestion gap |
| [log.md](log.md) | Dated chronological record of what changed and why |

## Ingest — when code changes

After a change lands (commit, or a meaningful working-tree edit you were asked
to make note of):

1. Identify which page(s) own the affected area (see table above; most
   changes touch exactly one).
2. Update the page in place — don't append a "changelog" section inside a
   topic page, just make the page correct *now*. History lives in `log.md`
   and in git, not duplicated on every page.
3. Append one entry to [log.md](log.md): date, one line on what changed, which
   page(s) were touched, and the commit hash (or "uncommitted" if it isn't
   committed yet).
4. If a change is still uncommitted / in-flight, say so explicitly on the
   page (e.g. "uncommitted as of `<date>`") rather than presenting it as
   settled. Update that note to a commit hash once it lands.

## Query — when answering questions about the codebase

1. Check the relevant wiki page(s) first instead of re-reading source from
   scratch.
2. If the page is stale (see Lint below) or doesn't cover the question,
   answer from source directly — then file what you learned back as a page
   update, so the next query doesn't pay the same cost.
3. Cite file paths / line numbers from source when precision matters, even
   if the fact came from the wiki page.

## Lint — periodic health check

Before trusting a page for something consequential, or when asked to review
the wiki itself:

- Every page carries an **"As of"** line near the top naming a commit hash
  (and "+ uncommitted changes" if applicable). If that commit is far behind
  `git log -1`, treat the page as suspect and re-verify against source before
  relying on it.
- Watch for contradictions between pages (e.g. a field described one way in
  `data-model.md` and used differently in `confidence-cup-scoring.md`).
- Watch for orphan pages (not linked from `index.md`) and dangling links.
- `RaceData/` is untracked and large (CSV data dumps) — don't let its
  presence/absence in `git status` block wiki maintenance; it's a data
  directory, not code, and is described qualitatively in
  [race-data-pipeline.md](race-data-pipeline.md) rather than enumerated.

## Conventions

- One page per architectural concern, not per file. Link to specific files
  with relative repo paths (e.g. `Src/Utilities/ConfCupUtils.cs:42`).
- Prefer standard markdown links (`[text](page.md)`) between pages so they
  render on GitHub and in any editor — not wiki-link syntax.
- Keep prose dense; this is reference material for an agent with a context
  budget, not a tutorial.
