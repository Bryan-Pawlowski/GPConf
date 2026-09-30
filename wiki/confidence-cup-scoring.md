# Confidence cup scoring (`CCUtils`)

> As of `1f3cf1b`. Source: `Src/Utilities/ConfCupUtils.cs`, class `CCUtils`.
> Source-linked (not project-referenced) into `GPConf.McpServer` — see
> [mcp-server.md](mcp-server.md) — so this is genuinely one implementation
> shared by the desktop app and the MCP server, not two implementations kept
> in sync by hand.

`CCUtils` also owns `CreateUniqueId()`, the GUID-based ID allocator used
everywhere (see [data-model.md](data-model.md)).

## Two distinct scoring systems live here

1. **F1-style championship points** — points a driver actually scores in a
   race, per the season's `PointsScoringRules`.
2. **Confidence-cup pick scoring** — points a *player* scores for picking a
   driver, derived from (1) plus the pick's position in their pick list and
   the driver's pre-race championship standing.

### 1. Championship points

- `GetPointsForDriverInRace(season, race, driver)` — sums points across
  *all* `RaceResult` sessions within one `Race` (handles sprint weekends,
  which have more than one `RaceResult`), each scored against its own
  `PointsScoringRules` via `RaceResult.PointRulesId`.
- `CalculatePointsForResult(season, raceResult, driverResult)` — same idea,
  given a specific `RaceResult` directly.
- `GetPointsForPosition(rules, position)` — 1-based `position` →
  `rules.Score[position-1]`, `0` if out of range (i.e. finishing outside the
  scoring positions).
- `GenerateRaceOrder(raceResult)` — orders by `Position` (the authoritative
  classification written at ingestion), but forces non-finishers
  (`Status != Finished`) and rows with no valid `Position` to the tail
  regardless of their stored position. This guarantees a DNF/DNS can never
  land in a points slot even if ingestion writes it a duplicate or unset
  `Position` (see the Round-14 incident in [log.md](log.md)). Drives the
  `DrawResultsTable` ordering in [ui-layer.md](ui-layer.md) and the
  championship-point snapshot (and therefore pick eligibility/multipliers).
- `GetDriverChampionshipPointSnapshotForRace(season, race, driver)` —
  cumulative points for a driver through a given race (inclusive), iterating
  `season.Races` in list order and stopping once the target race is reached.
  Powers the standings tooltip in `SeasonUpdater` and the `QueryTools`
  `GetChampionshipStandings` MCP tool.

### 2. Confidence-cup pick scoring

`CalculatePickScore(rules, pickIndex, champPos)`:

```
score = rules.BasePickScores[pickIndex] × standingsMultiplier(champPos)
```

- `champPos == 0` (round 1, no prior standings) always uses multiplier
  `1.0`.
- Otherwise: find the smallest `StandingsMultipliers` key `>= champPos`
  (keys are ordered ascending, each key is the *lowest* position for that
  tier); if `champPos` exceeds every tier, fall back to the highest tier's
  multiplier.

`GetPickScoreFromResults(season, rules, pickIndex, driverId, race, champPos)`
— the canonical entry point used by both the app and the MCP server:

1. Iterate only `race.RaceResults.Where(rr => rr.IsComplete)` — **this is the
   `RaceResult.is_complete` field** (see
   [data-model.md](data-model.md)). Incomplete/preview results don't count.
2. For each complete session, find the driver's result and compute
   `pts = CalculatePointsForResult(...)`. Skip the session if `pts <= 0` —
   the driver has to have actually scored points in that session for the
   pick to earn anything from it.
3. Multiply `CalculatePickScore(...)` by that session's
   `PointsScoringRules.ConfCupMultiplier` (**field**, falls back to `1.0`
   if unset/`≤0`).
4. Sum across sessions — so a driver who scores in both a sprint and the
   main race (each with a different multiplier on its own ruleset) gets
   both contributions added.

## Pick eligibility and guest/junior drivers

`CCUtils.GetEligibleDriversWithPos(season, prevRace, positionCutoff)` builds the pool of drivers a
player can pick, ranked by championship points snapshot through `prevRace` and filtered to positions
past `positionCutoff`. It's the shared entry point used by the desktop `LeagueUpdater`/`SeasonUpdater`
and the Discord bot's pick commands (`DataService`, `PickCommands`) — see
[pickem-bot-plan.md](pickem-bot-plan.md).

Guest/junior drivers who only run practice laps in a senior driver's car (registered via
`upsert_driver` so `set_practice_results` can resolve their name, but never entered into a qualifying
session or race result) are excluded from this pool via `CCUtils.HasQualifiedAndStarted(season,
driver)` — true only if the driver has a qualifying-session `LapData` entry *and* a `RaceDriverResult`
with `status != DNS` somewhere in the season. This filter only applies from round 2 onward
(`prevRace != null`); round 1 stays unfiltered because at season start no driver has any participation
history yet, so gating there would empty the whole pick pool instead of just excluding guests. From
round 2 on, though, the filter is `HasQualifiedAndStarted(season, driver) || IsSeasonEntrant(season,
driver)`: `IsSeasonEntrant` is true when the driver is registered to one of the season's teams
(`Driver.current_team_id` matches a `season.Teams` id), so a legitimate mid-season debut (a genuine
entrant with no qualifying/start record yet) stays pickable on their first race of the year, while a
practice-only guest who carries no team still drops out once the round-2 filter kicks in.

Championship standings themselves never needed this treatment — they're built from
`RaceDriverResult.Points` directly (`QueryTools.GetChampionshipStandings`,
`SeasonUpdater.DrawStandingsTooltip`), so a practice-only driver with no race result already never
accumulates points and never appears.

## What changed and why

Before this rework, `LeagueUpdater.cs` and `QueryTools.cs` each had their own
copy of this logic, and both used a **hardcoded heuristic**: a race counted
as a sprint (and got a fixed 0.5× multiplier) if `race_name` contained the
substring `"sprint"`. That's now gone. In its place:

- **`RaceResult.is_complete`** makes "does this session count yet" an
  explicit flag set by a human (the new checkbox in `RaceUpdater`, see
  [ui-layer.md](ui-layer.md)) or an agent (`RaceTools.SetRaceResults`'s new
  `isComplete` param, see [mcp-server.md](mcp-server.md)), instead of
  "any `RaceResult` exists for this race."
- **`PointsScoringRules.conf_cup_multiplier`** makes the confidence-cup
  weight of a session a property of *which ruleset scored it*, editable in
  `PointsScoringEditor`, instead of a string match on the race name. This
  also means a non-sprint session could in principle carry a custom
  multiplier too, and sprint detection via name-matching is no longer load-
  bearing for scoring (only cosmetically, if at all).
- The duplicate logic in `LeagueUpdater.cs` and `QueryTools.cs` was deleted
  and both now call this shared implementation — see
  [ui-layer.md](ui-layer.md) and [mcp-server.md](mcp-server.md) for the
  specific diffs.

If you're chasing a confidence-cup scoring discrepancy: check first whether
the relevant `RaceResult.IsComplete` is actually set, and whether the
`PointsScoringRules` it points at (`PointRulesId`) has the
`ConfCupMultiplier` you expect.
