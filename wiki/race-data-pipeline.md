# Race data pipeline: scraper → CSV → (manual) → `gpconf.data`

> As of `421462d`. Scraper source: `Python/f1_results_to_csv.py` (introduced
> in commit `3a01b82`). Data: `RaceData/` — **untracked**, not part of any
> commit as of this writing.

## The scraper — `Python/f1_results_to_csv.py`

Standalone Python 3 CLI, not a dependency of the C# app and not invoked by
it. Scrapes result tables from motorsport.com:

```
https://www.motorsport.com/f1/results/{year}/{race-slug}/?st={SESSION}
```

`SESSION ∈ {FP1, FP2, FP3, Q1, Q2, Q3, RACE, SS, SPR}`.

- Uses **Playwright** (real headless Chromium, not a plain HTTP request) +
  BeautifulSoup — needed because results are JS-rendered.
  `pip install playwright beautifulsoup4 && python -m playwright install chromium`.
- `fetch_page` sets a realistic desktop UA and spoofs `navigator.webdriver`
  (anti-bot-detection evasion), waits `4000ms` after `domcontentloaded` for
  JS content, then parses with BeautifulSoup. This is the "non-AI scraper"
  referenced in the commit message — deterministic DOM scraping, not an LLM
  reading the page.
- `derive_filename(url)` turns the URL into the exact filename convention
  seen under `RaceData/`: `{year}_{race_slug}_{session}.csv` (e.g.
  `2025_abu_dhabi_gp_fp1.csv`), stripping the trailing numeric event ID from
  the slug.
- `parse_table` extracts rows from `<tr class="ms-table_row">` by CSS class
  per cell (`ms-table_field--pos`, `--result_driver_id`, `--number`, `--laps`,
  `--time`, `--interval`, `--best_tyres`, `--avg_speed`, `--points`,
  `--pits`, `--retirement`). Driver name comes from the row's **href**
  (`/driver/{slug}/{id}/`, title-cased), not the abbreviated visible text
  (e.g. "L. Norris").
- CLI: one or more URLs positionally, or `--file/-f urls.txt`
  (`#`-comments skipped), `--output/-o DIR`. Per-URL try/except — one bad URL
  doesn't abort a batch.

## CSV schema

Two header shapes, by session type:

- **Race / Sprint** (`race`, `spr`):
  `Position,Driver,Team,Car No.,Laps,Time,Gap,km/h,Pits,Points,Retirement`
- **Practice / Qualifying** (`fp*`, `q*`, `sq*`):
  `Position,Driver,Team,Car No.,Laps,Time,Gap,Tyres,km/h`

Leader's `Time` is absolute (`1:26'07.469`, using `'` as the minute/second
separator); everyone else's is a gap to the leader. **This `'` convention is
not incidental** — it's the same separator format
`RaceUpdater.ParseLapTime`/`ParseRaceTime` explicitly parses (see
[ui-layer.md](ui-layer.md)), i.e. the scraper output was designed to be
pasteable straight into the desktop UI's time fields.

Known noise: some rows for reserve/junior drivers have malformed multi-line
cell text (e.g. `"P. Aron \n \n Alpine"` with an empty `Team` column) — the
site's markup for those rows doesn't match the expected `<a class="ms-link">`
structure the scraper assumes.

## What's on disk (`RaceData/`, untracked)

```
RaceData/{Series}{Year}/{RaceLocation}/{year}_{race_slug}_{session}.csv
```

- `F12025/` — 7 completed rounds (Abu Dhabi, Australia, Bahrain, China [+
  sprint], Japan, Miami [+ sprint quali + sprint], Saudi Arabia).
- `F12026/` — partial early-season data (Japan, Miami [+ sprint], Monaco
  [race only], Barcelona [race only]).

## The gap: nothing currently turns a CSV into an MCP call

`RaceTools.SetRaceResults` / `SetQualifyingResults` / `SetPracticeResults`
(see [mcp-server.md](mcp-server.md)) take a **JSON array**, not a CSV, and
use different field names than the scraper's CSV headers. There is no
CSV→JSON adapter script in the repo. The apparent intended workflow is:

1. Run the scraper to produce CSVs under `RaceData/`.
2. An agent (or a human) reads a CSV and constructs the JSON payload the
   relevant `RaceTools` method expects, then calls it.

This bridging step is currently manual/agent-driven, not automated. Two
practical hazards to know about when doing this by hand:

- **Team-name matching is exact (case-insensitive) string matching** against
  `Team.Name` in the season (`GpConfDataAccess.FindTeam`). The scraper's team
  names (`"Red Bull Racing"`, `"Haas F1 Team"`, etc.) must match names
  already created via `EntityTools.UpsertTeam` for driver/team resolution to
  succeed. A mismatch **doesn't error** — `RaceTools` falls back to
  `ByteString.Empty` silently. Create/verify teams and drivers first.
- The `laps_completed` convention on the C# side is "laps behind the
  leader," not the CSV's raw `Laps` column — see
  [data-model.md](data-model.md) and root `CLAUDE.md`. Converting requires
  the leader's lap count, not a 1:1 field copy.

## Related

A separate, later project idea — a Discord pick'em bot extending this same
confidence-cup model — is tracked in project memory, not in this repo; it
would need mid-season player-to-Discord-account reconciliation and is not
implemented here.
