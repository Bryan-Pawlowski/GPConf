# UI layer

> As of `1f3cf1b`. Source: `Src/UI/`. All UI is
> immediate-mode ImGui — widgets are static classes with a `Draw`/`Show*`
> method that receive data by reference and mutate it directly (root
> `CLAUDE.md`). Labels use `"Name##SuffixScopedToWidget"` to avoid ID
> collisions.

## Window/widget tree

```
GpConfApp (Src/GpConfApp.cs)
 └─ DoMenuBar
     ├─ Seasons
     │   ├─ SeasonEditor.Draw
     │   │   ├─ SeasonPicker         (season select / "New Season")
     │   │   ├─ ManufacturerEditor   (name-only table)
     │   │   ├─ TeamEditor           (colored per-team collapsible: name, color, manufacturer, roster)
     │   │   ├─ DriverEditor         (table: name/number/nationality/team)
     │   │   ├─ RaceConfig           (schedule: drag-reorderable race list, auto-renumbers Round)
     │   │   └─ PointsScoringEditor  (per-ruleset: name, position→points table, Conf Cup Multiplier)
     │   ├─ SeasonUpdater.Draw
     │       └─ RaceUpdater.Draw (per race, expanded from a color-coded race list)
     │           ├─ DrawPractice        (per-FP-session lap tables)
     │           ├─ DrawQualifying      (per-stage lap tables, elimination tracking)
     │           └─ DrawRaceResults
     │               ├─ DrawStartingGrid  (read-only, derived from qualifying)
     │               └─ DrawResultsTable  (finishing order, points, per-result "Complete" checkbox)
     │   └─ RaceWeekendDashboard.Draw  (per-race table: round/name/circuit/date + data-entry status, color-coded)
     └─ Conf Cup
         ├─ LeagueEditor.Draw
         │   └─ per league: DrawPlayers, DrawGameSeasons → DrawParticipatingPlayers, DrawPickRules, DrawGameRacePreview
         └─ LeagueUpdater.Draw
             ├─ "League Updater" window   (DrawSelectors, DrawRaces → DrawRacePicks: per-player pick combos, live scores)
             └─ "Current Race Info" window (DrawDriverStandings, DrawPlayerStandings, DrawPracticeInfo,
                                             DrawPointsChart [ImPlot], DrawQualifyingInfo, DrawFastestLaps, DrawTeamStandings)
```

## Per-file notes

- **`GpConfApp.cs`** — not in `Src/UI/`, but the orchestrator: owns
  `MainData`, dockspace, menu bar, file-watch reload. See
  [architecture.md](architecture.md).
- **`SeasonEditor.cs`** — top-level season CRUD; resolves `CurrentSeason` vs
  the canonical list entry (same pattern as `GpConfApp.Migrate`, see
  [architecture.md](architecture.md)) before drawing children. Save/Clear
  buttons.
- **`SeasonPicker.cs`** — reusable combo: existing seasons + "New Season"
  (allocates via `CCUtils.CreateUniqueId()`). Used by `SeasonEditor` and
  `SeasonUpdater`.
- **`ManufacturerEditor.cs`** — simplest editor: name-only add/remove table.
- **`TeamEditor.cs`** — colored collapsible header per team; manufacturer
  combo; read-only roster (`Drivers.Where(d => d.CurrentTeamId == team.Id)`).
- **`DriverEditor.cs`** — table editor with a team combo colored to match the
  team.
- **`RaceConfig.cs`** — schedule editor with custom drag-and-drop reordering
  (`ImGuiDragDropFlags`, payload type `"RACE_ROW"`); `Round` auto-renumbers to
  list position every frame.
- **`PointsScoringEditor.cs`** — position→points table, plus a **Conf Cup
  Multiplier** float field bound to `PointsScoringRules.ConfCupMultiplier`
  (step 0.1, `%.2f`) — see [confidence-cup-scoring.md](confidence-cup-scoring.md).
  A **Load Preset** combo (backed by `PointsPresets.cs`) inserts a ready-made
  `PointsScoringRules` (F1 2025 Grand Prix, F1 2025 Sprint, and the
  2003-2009 / 1991-2002 / 1961-1990 historical grids), so a new season doesn't
  start from a blank scoring table.
- **`SeasonUpdater.cs`** — season picker + race list (orange/green by
  whether `RaceResults.Count > 0`), each expanding to `RaceUpdater.Draw`.
  Resolves `currentSeason` from the canonical `Seasons` list by Id (not the
  detached `CurrentSeason` copy, matching `SeasonEditor`) so edits land in
  `Seasons[i]`. Hover tooltip shows a standings snapshot via
  `CCUtils.GetDriverChampionshipPointSnapshotForRace`. Own local Save button.
- **`RaceUpdater.cs`** (**modified**, ~700 lines, the densest widget) — three
  collapsible sections per race:
  - `DrawPractice` / `DrawQualifying` — lap tables via shared `DrawLapTable`
    (qualifying also exposes `QualiSessionEliminated`).
  - `DrawRaceResults` — **new:** `DrawStartingGrid`, a read-only grid table
    derived from qualifying `LapData` (same sort as the quali table:
    not-eliminated first, then descending elimination stage, then time
    ascending). Then `DrawResultsTable`: finishing order via
    `CCUtils.GenerateRaceOrder`, P1 row highlighted purple, other
    points-scoring positions green (bound to
    `PointsScoringRules.Score.Count`), per-row team-override combo (falls
    back to the driver's registered team, marked `*` if not explicitly
    pinned), read-only computed points
    (`CCUtils.CalculatePointsForResult`), editable race-time/fastest-lap via
    custom time-input widgets supporting `M:SS.sss`, `M'SS.sss`, and
    `H:MM:SS.sss`. **New:** a per-`RaceResult` **Complete** checkbox bound to
    `IsComplete`, gating confidence-cup scoring — see
    [confidence-cup-scoring.md](confidence-cup-scoring.md).
- **`LeagueEditor.cs`** — per-league: `DrawPlayers` (name + color),
  `DrawGameSeasons` (linking a season auto-populates one `GameRace` per
  `Race`), `DrawParticipatingPlayers`, `DrawPickRules` (NumPicks,
  PositionCutoff, BasePickScores list, StandingsMultipliers map editor),
  `DrawGameRacePreview` (read-only, syncs missing `GameRace` entries).
- **`LeagueUpdater.cs`** (~1050 lines) — the live dashboard.
  **Had its own private copies of `GetPickScoreFromResults`/
  `CalculatePickScore`, deleted in favor of the shared `CCUtils`
  implementation** — see [confidence-cup-scoring.md](confidence-cup-scoring.md)
  for the behavioral change that came with the move (the old hardcoded
  "sprint = 0.5×" heuristic is gone). Also: `hasResults` checks are
  "any `RaceResult` with `IsComplete == true`"; the fastest-lap section
  resolves each lap's team from the pinned `LapData.team_id` (falling back
  to the driver's current team) so a transferred driver's history keeps its
  original team color.
- **`RaceWeekendDashboard.cs`** — standalone per-race overview table: for each
  race in the current season, round/name/circuit/date plus one status column
  per data section (Practice, Qualifying, Race Results), color-coded
  orange=missing / green=entered. Resolves `currentSeason` from the canonical
  `Seasons` list by Id (same pattern as `SeasonEditor`/`SeasonUpdater`), so it
  sees the same data the editors mutate. Opens from a Seasons menu-bar item.

## Stub

- **`SeasonData.cs`** — `SeasonConfig` is an empty placeholder class (doc
  comment only: "Holds info on Drivers, Teams, and Race Data"). Not
  referenced elsewhere as of this writing.

Everything else listed above is fully implemented.
