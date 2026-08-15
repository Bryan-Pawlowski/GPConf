# Data model

> As of `2bfdba6`. Source:
> `Src/Protobuf/*.proto`. **Never hand-edit the generated C# classes** — they
> regenerate from these `.proto` files at build time into the `GPConf`
> namespace (root `CLAUDE.md`).

All four files: `syntax = "proto3"`, `package gpconf`,
`option csharp_namespace = "GPConf"`. `season.proto` imports the other three.
`confidence_game.proto` deliberately does **not** import `season`/`race`/
`entities` — it references them only via raw `bytes` IDs, resolved by app
code, to avoid a dependency cycle (pick data references races and drivers;
races don't need to know about picks).

## ID convention

Every entity ID (`Driver.id`, `Team.id`, `Manufacturer.id`, `Race.id`,
`Season.id`, `Player.Id`, `League.Id`, …) is a protobuf `bytes` field holding
a raw 16-byte `System.Guid`. Allocate with
`ByteString.CopyFrom(Guid.NewGuid().ToByteArray())` — both the desktop app and
the MCP server use the same helper (`CCUtils.CreateUniqueId()` in
`Src/Utilities/ConfCupUtils.cs`, source-linked into the MCP server project).

## `entities.proto`

| Message | Fields | Notes |
|---|---|---|
| `Driver` | `id`, `name`, `number` (car #), `nationality`, `current_team_id` → `Team.id` | |
| `Manufacturer` | `id`, `name` | |
| `Team` | `id`, `name`, `manufacturer_id` → `Manufacturer.id`, `color` (packed `0x00RRGGBB`) | field 4 (`driver_ids`) is `reserved` — team roster is derived from `Driver.current_team_id`, not stored on the team |

## `race.proto`

| Message | Fields | Notes |
|---|---|---|
| `LapData` | `driver_id`, `fastest_lap_seconds`, `average_lap_seconds`, `quali_session_eliminated` | 1-based stage eliminated in; `0` = not eliminated |
| `PracticeSession` | `session_number` (1=FP1…), `repeated LapData` | |
| `QualifyingSession` | `stage` (1=Q1,2=Q2,3=Q3), `repeated LapData`, `session_name` | |
| `FinishStatus` (enum) | `UNSPECIFIED, FINISHED, DNF, DNS, DSQ` | |
| `RaceDriverResult` | `driver_id`, `position`, `points`, `race_time`, `fastest_lap_seconds`, `laps_completed`, `status`, `team_id` | `team_id` is the team raced for *in this event* — may differ from `Driver.current_team_id` if the driver has since transferred; falls back to the driver's current team when empty (see `Migrate()` in [architecture.md](architecture.md)) |
| `RaceResult` | `race_name`, `repeated RaceDriverResult results`, `PointRulesId` → `PointsScoringRules.Id`, **`is_complete`** (bool, field 4) | One `Race` can have multiple `RaceResult`s (e.g. sprint + main race), each with its own `race_name` and its own `PointRulesId`. Confidence-cup scoring only counts results where `is_complete == true` — see [confidence-cup-scoring.md](confidence-cup-scoring.md) |
| `Race` | `id`, `name`, `circuit`, `round`, `practices`, `qualifying_sessions`, `race_results`, `date` (ISO 8601), `pick_deadline_epoch` (field 11, `int64`), `announcement_message_id` (field 12, `string`) | field 7 is `reserved`; **field 10 is now `reserved` too** — it used to be `bool is_sprint_weekend`, removed because sprint-ness is now inferred from having a second `RaceResult` (or its `race_name` containing "sprint"), not stored as a flag. `pick_deadline_epoch` / `announcement_message_id` are written by `race-weekend-prep` via the bot's `post_pick_announcement` tool (see [mcp-server.md](mcp-server.md)) — they let the bot reject late picks and strip the announce button at the deadline |

**Note:** `race.proto` currently has no trailing newline at EOF — cosmetic,
not a bug, don't be surprised by it in a diff.

## `season.proto`

| Message | Fields | Notes |
|---|---|---|
| `Season` | `year`, `name`, `drivers`, `manufacturers`, `teams`, `races`, `rules` (`PointsScoringRules`), `id` | |
| `MainData` | `repeated Season seasons`, `Season current_season`, `repeated League leagues` | **the root message persisted to `gpconf.data`.** See the `current_season`-vs-`seasons[i]` divergence hazard in [architecture.md](architecture.md) |
| `PointsScoringRules` | `Id`, `name`, `Score` (`repeated int32`, index 0 = P1 points, …), **`conf_cup_multiplier`** (float, field 4) | Multiplier applied when a pick scores via this rule set — e.g. give a sprint's `PointsScoringRules` a lower multiplier than the main race's. Editable in `PointsScoringEditor` |

## `confidence_game.proto`

The pick'em game layer, keyed by raw ID references (no proto imports — see
above).

| Message | Fields | Notes |
|---|---|---|
| `Player` | `Id`, `PlayerName`, `Color` (packed RGB, `0` = unset) | |
| `Picks` | `PlayerId` → `Player.Id`, `DriverId` (`repeated bytes`) → `Driver.id` | ordered pick slots |
| `PickRules` | `NumPicks`, `BasePickScores` (`repeated float`, per pick-slot base score), `StandingsMultipliers` (`map<int32, float>`, keyed by the *lowest* championship position for that multiplier tier), `PositionCutoff` (can't pick drivers at/above this championship position) | |
| `League` | `Id`, `LeagueName`, `Players` (roster), `Seasons` (`repeated GameSeason`) | |
| `GameSeason` | `Id`, `ParticipatingPlayers` (subset of `League.Players` playing this season), `Races` (`repeated GameRace`), `SeasonId` → `Season.id`, `PickRules` | |
| `GameRace` | `RaceId` → `Race.id`, `PicksPerPlayer` (`repeated Picks`) | |

## Cross-reference map

```
Driver.current_team_id      → Team.id
Team.manufacturer_id        → Manufacturer.id
RaceDriverResult.driver_id  → Driver.id
RaceDriverResult.team_id    → Team.id            (per-race override; empty → Driver.current_team_id)
RaceResult.PointRulesId     → PointsScoringRules.Id
GameSeason.SeasonId         → Season.id
GameRace.RaceId             → Race.id
Picks.PlayerId              → Player.Id
Picks.DriverId[]            → Driver.id
League.Seasons[].ParticipatingPlayers ⊆ League.Players   (matched by ID)
```

## Race-result data conventions (see also root `CLAUDE.md`)

- `RaceDriverResult.laps_completed` stores **laps behind the leader**, not
  raw laps completed: `[leader's laps] - [driver's laps]`. `0` = on the lead
  lap. A DNF/DNS with zero laps started stores the leader's total lap count.
- A `Race` can carry more than one `RaceResult` (sprint + main event); they're
  distinguished by `race_name` and each has its own `PointRulesId`.
