# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build

# Run
dotnet run

# Build release
dotnet build -c Release

# Publish
dotnet publish -c Release
```

There are no automated tests in this project.

## Architecture

**GPConf** is a desktop GUI app for configuring Formula 1 fantasy/confidence cup seasons. It uses **ImGui** (via `Hexa.NET.ImGui`) rendered over **OpenGL 3.3** with an **SDL3** window backend.

### Render loop

`Program.cs` owns the SDL3 window and GL context, drives the per-frame ImGui lifecycle (`NewFrame` → `app.Update()` → `Render`), and handles `SDL_Quit`/`WindowCloseRequested` events. `GpConfApp` is instantiated once and its `Update()` is called every frame.

### Data model

All persistent state is defined in **Protobuf** schemas (`Src/Protobuf/`):

- `entities.proto` — `Driver`, `Team`, `Manufacturer`
- `race.proto` — `Race`, `PracticeSession`, `QualifyingSession`, `RaceDriverResult`, `LapData`, `FinishStatus`
- `season.proto` — `Season` (owns drivers, manufacturers, teams, races), `MainData` (root: list of seasons + current season pointer)

C# classes are generated automatically from `.proto` files by `Grpc.Tools` at build time into the `GPConf` namespace. **Never hand-edit the generated classes.**

Data is persisted to `%APPDATA%/GPConf/gpconf.data` as a binary protobuf blob via `GpConfApp.Save()` / `Open()`.

### ID allocation

Entity IDs (`Driver.Id`, `Team.Id`, `Manufacturer.Id`, `Race.Id`) are `bytes` fields in protobuf, storing a raw 16-byte `System.Guid`. Allocate with `Google.Protobuf.ByteString.CopyFrom(System.Guid.NewGuid().ToByteArray())`. Cross-references (e.g. `Team.driver_ids`, `Team.manufacturer_id`) use the same `bytes` type.

### UI layer (`Src/UI/`)

All UI is **immediate-mode ImGui**. Widgets are static classes with a `Draw` or `Show*` method — they receive data by reference and mutate it directly.

- `GpConfApp` — top-level menu bar, owns `MainData`, dispatches to editor windows
- `StaticWidgets/SeasonEditor` — main season editor window (name, year, teams combo, drivers collapsing header, save/clear buttons)
- `StaticWidgets/SeasonPicker` — combo box for switching/creating seasons
- `StaticWidgets/TeamEditor` — inline team list editor used inside SeasonEditor
- `StaticWidgets/DriverEditor` — driver list editor (add/edit name, number, nationality) inside SeasonEditor
- `UI/SeasonUpdater`, `StaticWidgets/RaceConfig`, `StaticWidgets/RaceUpdater` — stubs not yet implemented

### ImGui ID hygiene

Widget labels use the `##SufixName` convention to avoid ID collisions (e.g. `"Name##SeasonEditor"`). Always include a unique `##` suffix scoped to the widget class.
