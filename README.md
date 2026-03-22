# GPConf

GPConf is a desktop app for configuring Grand Prix fantasy/confidence cup seasons. It manages drivers, teams, manufacturers, and race results, and includes an MCP server so Claude can ingest race data directly into the app.

---

## Installation

Run `GPConf-setup.exe`. The installer will:
- Install **GPConf.exe** (the desktop app)
- Install **GPConf.McpServer.exe** (the Claude MCP server)
- Show you the one-time Claude config step at the end

---

## GPConf — Desktop App

Launch **GPConf** from the Start Menu or desktop shortcut.

### Seasons menu
- **Season Editor** — create and edit seasons. A season holds drivers, teams, manufacturers, and races.
- **Season Updater** — *(coming soon)*

### Conf Cup menu
- **League Editor** — manage leagues, players, pick rules, and game seasons.
- **League Updater** — *(coming soon)*

### Saving
Click **Save** inside the Season Editor, or use the save button in any editor window. Data is stored at:
```
%APPDATA%\GPConf\gpconf.data
```

---

## MCP Server — Claude Integration

The MCP server lets Claude read and write GPConf data directly. When Claude saves data, GPConf detects the change and reloads automatically — no manual refresh needed.

### One-time setup

**Claude Desktop** — add to `%APPDATA%\Claude\claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "gpconf": {
      "command": "C:\\Program Files\\GPConf\\mcp\\GPConf.McpServer.exe"
    }
  }
}
```

**Claude Code (CLI)** — run once in a terminal:
```
claude mcp add gpconf "C:\Program Files\GPConf\mcp\GPConf.McpServer.exe"
```

Restart Claude after making the config change.

### Available tools

| Tool | Description |
|------|-------------|
| `list_seasons` | Lists all seasons with counts of drivers, teams, and races |
| `get_season` | Returns full roster for a season (drivers, teams, manufacturers, races) |
| `upsert_race` | Creates or updates a race entry (name, circuit, round, date) |
| `set_race_results` | Writes finish positions, points, and status for all drivers |
| `set_qualifying_results` | Writes Q1/Q2/Q3 times and grid positions |
| `set_practice_results` | Writes FP1/FP2/FP3 lap times |

### Example prompts

- *"List all my seasons"*
- *"Add the 2025 Australian Grand Prix to my 2025 season"*
- *"Ingest the race results for the 2025 Bahrain GP — Verstappen P1, Norris P2..."*
- *"Set the qualifying results for round 3 of my 2025 season"*

---

## Building from source

Requirements: [.NET 9 SDK](https://dotnet.microsoft.com/download)

```powershell
# Run the app
dotnet run

# Build installer (requires Inno Setup 6)
powershell -ExecutionPolicy Bypass -File Installer\build.ps1
```