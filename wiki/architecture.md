# Architecture

> As of `421462d` + uncommitted changes (see [log.md](log.md)).

## Two processes, one data file

GPConf is really **two independent .NET processes sharing one protobuf file**:

- The **desktop app** (`Program.cs` → `Src/GpConfApp.cs`) — SDL3 window + GL
  3.3 context + ImGui, for humans.
- **`GPConf.McpServer`** (`GPConf.McpServer/Program.cs`) — a stdio MCP server,
  for AI agents. See [mcp-server.md](mcp-server.md).

Both read/write the exact same file: `%APPDATA%/GPConf/gpconf.data`, a raw
`MainData` protobuf blob (`Src/GpConfApp.cs` `AppPath`,
`GPConf.McpServer/DataAccess/GpConfDataAccess.cs` `DataPath`). Neither process
knows the other is running. There is **no locking, no mutex, no
optimistic-concurrency check** — `Save()` in both places just truncates and
rewrites the whole file (`FileMode.Create` + `WriteTo`). Simultaneous writes
from both processes race; last write wins and can lose data. In practice this
is fine because usage is bursty (a human edits, or an agent edits, not both at
once), but it's a real hazard worth knowing before parallelizing anything.

The one thing that *does* keep them loosely in sync: `GpConfApp` starts a
`FileSystemWatcher` on `gpconf.data` (`StartWatcher`). When the MCP server
writes the file, the watcher sets `_pendingReload = true`; the next
`Update()` frame re-opens and re-migrates the file. This is push-based and
eventual, not transactional — if the user is mid-edit in the desktop app when
an agent writes, the reload can clobber unsaved UI state.

`GpConfDataAccess.Save()` also has to defend against a data-modeling wrinkle:
see "`CurrentSeason` vs the season list" below.

## Render loop (desktop app)

`Program.cs` owns the SDL3 window and GL context and drives ImGui's per-frame
lifecycle: `NewFrame()` → `app.Update()` → `Render()`, handling
`SDL_Quit`/`WindowCloseRequested`. `GpConfApp` is instantiated once; its
`Update()` runs every frame and:

1. If `_pendingReload` (set by the file watcher), re-`Open()`s the file and
   re-runs `Migrate()`.
2. Draws the dockspace and top menu bar (`DoMenuBar`), which toggles the
   editor windows — see [ui-layer.md](ui-layer.md) for the widget tree.

## `CurrentSeason` vs the season list — a load-bearing quirk

`MainData` stores both `repeated Season seasons` and a standalone
`Season current_season`. Historically some editors wrote to `current_season`
directly rather than the matching entry in `seasons`, so the two can diverge.
`GpConfApp.Migrate()` (run on every load) and `SeasonEditor.Draw` both
resolve this the same way: if `current_season` has data and can be matched to
a `seasons[i]` (by ID, or by name with an empty-races fallback), the list
entry is replaced with `current_season`. `GpConfDataAccess.Save()` on the MCP
side does the mirror-image fix before writing — it looks up the season
matching `current_season` in the `seasons` list and reassigns
`current_season` to that match, specifically so that `Migrate()` won't
clobber MCP-side edits to `seasons[i]` on the app's next load. **If you touch
season-shaped data anywhere, check whether this dance still applies** — it's
the kind of thing that's easy to silently break.

`Migrate()` also does two other one-time cleanups on every load: backfills
missing `Season.Id` on old data, and defaults any `RaceDriverResult` with no
pinned `TeamId` to the driver's *current* registered team (so historic
results don't retroactively show a driver's later team once results carry an
explicit per-result `team_id` — see [data-model.md](data-model.md)).

## Save path (desktop app)

`GpConfApp.Save()` disables the watcher's `EnableRaisingEvents` before
writing (so its own write doesn't trigger a self-reload loop), truncates via
`File.Create`, writes, then re-enables the watcher.

## See also

- [data-model.md](data-model.md) for what's actually inside `MainData`.
- [mcp-server.md](mcp-server.md) for the MCP-side data access and packaging.
