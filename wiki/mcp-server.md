# MCP server

> As of `4f40de8`. Source: `GPConf.McpServer/`.

`GPConf.McpServer` is a separate .NET process (`Program.cs`, a minimal
generic host) that exposes `gpconf.data` to AI agents over stdio
(`AddMcpServer().WithStdioServerTransport()`). It reads/writes the exact same
file the desktop app uses — see the concurrency caveats in
[architecture.md](architecture.md) before assuming writes from here and from
the app can safely interleave.

## Data access — `DataAccess/GpConfDataAccess.cs`

- `DataPath` = `%APPDATA%/GPConf/gpconf.data`.
- `Load()` — returns `new MainData()` if the file doesn't exist; otherwise
  `MainData.Parser.ParseFrom(fs)`.
- `Save(MainData data)` — before writing, re-syncs `data.CurrentSeason` to
  point at the matching `Seasons[i]` entry (by ID, falling back to
  case-insensitive name). This exists specifically so the desktop app's
  `Migrate()` doesn't clobber MCP-side edits on its next load — see the
  `CurrentSeason`-vs-`seasons[i]` note in [architecture.md](architecture.md).
  Then it's a full overwrite: `FileMode.Create` + `WriteTo(fs)`. No locking,
  no atomic rename, no version check.
- Lookup helpers, all case-insensitive by name (with int-parse-first
  shortcuts for year/round): `FindSeason(nameOrYear)`, `FindDriver`,
  `FindTeam`, `FindManufacturer`, `FindRace(nameOrRound)`.
- `NewId()` — `ByteString.CopyFrom(Guid.NewGuid().ToByteArray())`, same
  scheme as the desktop app's `CCUtils.CreateUniqueId()`.

## Tool inventory

Every tool: `Load()` → find/mutate or read → (`Save()` if mutating). All
season-scoped tools take the season by name-or-year.

### `Tools/SeasonTools.cs`

| Tool | Params | Does |
|---|---|---|
| `CreateSeason` | `name, year` | Creates a season; errors if the name already exists (supports multiple series sharing a year, e.g. "F1 2025" vs "WEC 2025") |
| `ListSeasons` | — | JSON: id (hex), name, year, entity counts |
| `GetSeason` | `season` | JSON: full driver/team/manufacturer/race lists |

### `Tools/EntityTools.cs`

Manufacturer/Team/Driver CRUD, all season-scoped: `UpsertManufacturer`,
`RemoveManufacturer`, `UpsertTeam(season, name, manufacturerName?)`,
`RemoveTeam`, `UpsertDriver(season, name, number, nationality, teamName?)`,
`RemoveDriver`. Team/driver upserts require their dependency
(manufacturer/team) to already exist — they return an error string rather
than auto-creating it.

### `Tools/RaceTools.cs` (**modified in working tree**)

| Tool | Params | Does |
|---|---|---|
| `UpsertRace` | `season, raceName, circuit, round, date` | Creates/updates race metadata (matched by name or round) |
| `SetRaceResults` | `season, race, resultsJson, sessionName?, isComplete=false, pointsRulesName?` | Deserializes a JSON array (`DriverName, TeamName, Position, Points, Status, RaceTime, FastestLap, LapsCompleted`), resolves names → IDs, replaces the `RaceResult` keyed by `sessionName` (defaults to the race name; use a distinct name for a sprint result stored alongside the main race). **New:** `isComplete` sets `RaceResult.IsComplete`; `pointsRulesName` resolves a `PointsScoringRules.Name` match and sets `PointRulesId` |
| `SetQualifyingResults` | `season, race, resultsJson` | Deserializes (`DriverName, Q1Time, Q2Time, Q3Time, GridPosition, QualifyingStage, SessionName`), groups by stage into `QualifyingSession`s, sets `FastestLapSeconds` from the latest stage reached |
| `SetPracticeResults` | `season, race, sessionNumber, resultsJson` | Deserializes (`DriverName, FastestLap, AverageLap`), replaces the `PracticeSession` for that number |

`ParseStatus` maps `"DNF"/"DNS"/"DSQ"` (case-insensitive) → `FinishStatus`,
defaulting to `Finished`.

**Silent-failure trap:** name→ID resolution falls back to `ByteString.Empty`
if a `DriverName`/`TeamName` doesn't match an existing entity exactly
(case-insensitive). A typo or an un-normalized name from scraped data (see
[race-data-pipeline.md](race-data-pipeline.md)) won't error — it'll write an
empty ID silently. Create/verify drivers and teams via `EntityTools` first,
using names that exactly match what you're about to feed to `RaceTools`.

### `Tools/QueryTools.cs` (**modified in working tree** — refactored)

Read-only, all return JSON:

| Tool | Does |
|---|---|
| `GetRaceResults` | Per-session results (handles sprint), resolved to names, ordered by position |
| `GetPracticeResults` | FP results sorted by fastest lap |
| `GetQualifyingResults` | Grid order: Q3 finishers first, then Q2 eliminees, then Q1 eliminees |
| `GetChampionshipStandings` | Cumulative driver points through the given race (inclusive) |
| `GetPlayerPicks` | Each player's picks + computed confidence-cup score for a race; "preview" (pre-race) scoring if the race has no complete result yet, real scoring once it does |
| `GetPlayerScores` | Cumulative player confidence-cup scores, round 1 → given race |
| `GetPlayerRankings` | Same as above, ranked |

**This file used to have its own copy of the pick-scoring logic** (with a
hardcoded "sprint = 0.5×, detected by `race_name.Contains("sprint")`"
heuristic). That copy was deleted; `QueryTools` now calls the shared
`GPConf.Utilities.CCUtils` implementation — see
[confidence-cup-scoring.md](confidence-cup-scoring.md) for why and what
changed.

## Packaging (**modified in working tree**, `GPConf.McpServer.csproj`)

- `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` +
  `<SelfContained>true</SelfContained>` — publishes as a self-contained win-x64
  exe, no separate .NET runtime dependency for the agent host.
- `<Compile Include="..\Src\Utilities\ConfCupUtils.cs" Link="Utilities\ConfCupUtils.cs" />`
  — source-links the desktop app's `CCUtils` straight into this project
  (not a project reference), which is what let `QueryTools` de-duplicate its
  scoring logic.
- An MSBuild `Target Name="CopyToInstallDir" AfterTargets="Publish"` copies
  published output to `C:\Program Files (x86)\GPConf\mcp\` — `dotnet publish`
  doubles as the install step for a fixed local install location.
- Deps: `Google.Protobuf 3.34.0`, `Grpc.Tools 2.78.0` (codegen only),
  `ModelContextProtocol 1.1.0`, `Microsoft.Extensions.Hosting 9.0.0`.

> **Deploy note:** wait for the user to disable the MCP server connection
> before deploying/overwriting the installed binary — it'll be locked/in-use
> otherwise.
