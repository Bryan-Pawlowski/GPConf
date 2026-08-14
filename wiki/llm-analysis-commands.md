# LLM analysis commands (Ollama)

> As of `b7ade99` + uncommitted (2026-08-13).

Four Discord slash commands in `GPConf.DiscordBot` generate narrative text
via a locally-hosted Ollama server, layered on top of the same
`DataService`/`CCUtils` data every other bot command uses. This is a
separate, parallel mechanism from the MCP-skill-driven agent-authored
recaps described in [pickem-bot-plan.md](pickem-bot-plan.md)'s planned
race-week skill sequence (steps 4/6/7) — not a replacement for it. That
plan's recaps are written by a Claude Code skill orchestrating race week
through MCP tool calls; these commands are synchronous, on-demand Discord
slash commands that call a local model directly from the bot process, no
agent orchestration involved.

## Hard rule: the model never computes or invents a fact

Every number, name, or event referenced in generated text is computed in
C# from real protobuf data first (`GPConf.DiscordBot/Services/AnalysisFacts.cs`)
and handed to the model as a finished "facts block." The model's only job
is tone/narrative — never arithmetic, never invention. This extends the
existing "never hardcode eligibility/scoring bot-side" rule from
[pickem-bot-plan.md](pickem-bot-plan.md) to this new surface.

**`RaceDriverResult.laps_completed` is deliberately never surfaced** in any
facts block — its value means "laps behind the race leader," not an
absolute lap count (see [data-model.md](data-model.md) /
[race-data-pipeline.md](race-data-pipeline.md)), and is exactly the kind of
field a model would misread into a false "retired after N laps" claim.

## Commands

| Command | Params | Facts builder |
|---|---|---|
| `/driver-analysis` | `season`, `driver` (exact name), `race` (optional, defaults to latest) | `AnalysisFacts.BuildDriverFacts` |
| `/player-analysis` | `season`, `player` (optional, defaults to caller via Discord username), `league` (optional), `race` (optional) | `AnalysisFacts.BuildPlayerFacts` |
| `/season-overview` | `season`, `league` (optional), `race` (optional, defaults to latest) | `AnalysisFacts.BuildSeasonFacts` |
| `/race-recap` | `season`, `race` (required), `league` (optional) | `AnalysisFacts.BuildRaceRecapFacts` |

All four: `DeferAsync()` → resolve season/race/driver/player via existing
`DataService` lookups (graceful not-found `FollowupAsync`, never throws,
**before** touching Ollama so a bad input never costs a slow model call) →
build the facts block → `OllamaClient.GenerateAsync` → embed. Same house
style as `ReadCommands.cs`.

### League resolution

- `/season-overview` and `/race-recap` reuse the exact league-resolution
  shape `ReadCommands.Results` established: silent if the season has ≤1
  configured league, else try matching the caller's Discord
  username/`GlobalName` against a `Player.PlayerName`, else a
  `SelectMenuBuilder` picker (`analysis_league:{kind}:{seasonIdHex}:{raceIdHex}`,
  `kind` ∈ `season`/`recap`, handled by `AnalysisLeagueSelected`). Unlike
  `/results`, an unresolved league here isn't fatal — the command still runs,
  just without the confidence-cup section.
- `/player-analysis` needs a different resolver (`ResolvePlayerAsync`)
  since the target is a *named player*, not necessarily the caller: explicit
  `player` name is matched case-insensitively against every candidate
  league's `ParticipatingPlayers`; if the name exists in more than one
  league, a picker is shown (`player_analysis_league:{seasonIdHex}:{raceIdHex}`,
  handled by `PlayerAnalysisLeagueSelected`). The player name travels in
  each select option's `Value` (`{leagueIdHex}:{playerNameHexOrSELF}`), not
  the shared `CustomId`, to stay under Discord's 100-char `CustomId` limit
  regardless of name length. Omitting `player` falls back to the caller's
  own Discord username/`GlobalName` match.

## `OllamaClient` (`GPConf.DiscordBot/Services/OllamaClient.cs`)

Singleton `HttpClient`, POSTs to Ollama's `/api/chat` (non-streaming,
`"think": false`). Env vars, read once in the constructor:

| Var | Default |
|---|---|
| `OLLAMA_HOST` | `scout:11434` (`http://` prepended if missing) |
| `OLLAMA_MODEL` | `muse-glimmer:30b-mlx` |
| `OLLAMA_TIMEOUT_SECONDS` | `180` |

`muse-glimmer:30b-mlx` is a reasoning-capable model that produces lengthy
chain-of-thought if left unconstrained (~30s and 663 eval tokens for a
3-word test prompt with thinking on, ~17s with `think:false`) — real
analysis prompts (facts block + 350-word output budget) should be expected
to take well under the 180s timeout but noticeably longer than a trivial
prompt; there is no caching/pre-generation, this is by design (the user
explicitly accepted the on-demand wait over adding a cache layer).

All Ollama failure modes (connection refused, DNS failure, timeout,
malformed JSON) normalize into one `OllamaException`, caught once per
command as a plain `⚠️` `FollowupAsync` — never an unhandled exception.

## Facts-block field lists

See `AnalysisFacts.cs` for the authoritative implementation; summary of
what each builder includes:

- **Driver** (`BuildDriverFacts`): identity (name, number, nationality,
  team), championship position/points/gaps at the cutoff race, a per-race
  table (qualifying grid position + stage reached, practice fastest laps,
  race finish/status/points per session), and season aggregates (best/worst
  finish, podiums, DNF/DNS/DSQ counts, average qualifying/race position).
- **Player** (`BuildPlayerFacts`): identity + league, cumulative score/rank
  at cutoff, per-race score history with running total (from the new
  `DataService.PlayerScoresPerRace`, which distinguishes "no picks
  submitted" — `null` — from "submitted and scored exactly 0" — `0f`),
  favorite picks (frequency count, top 5), pick-slot tendency (average
  championship position picked, per slot, only if `NumPicks > 1`), best
  single-race score, no-pick/zero-score race counts.
- **Season** (`BuildSeasonFacts`): framing, top-5 standings, per-race digest
  (winner + pole), season records (most wins/poles/podiums/DNFs), biggest
  round-over-round position swing for any driver; if a league resolved,
  adds the confidence-cup leaderboard, biggest single-race player score all
  season, and the most-picked driver league-wide.
- **Race recap** (`BuildRaceRecapFacts`): race identity, practice/qualifying
  digest, full race classification; "unexpected performances" section —
  grid-vs-finish position deltas (top gainers/losers), finish position vs.
  championship position entering the race (over/underperformance, ±5
  position threshold), DNF/DNS/DSQ list, fastest lap; if a league resolved,
  adds this week's per-player scores, cumulative standings movement vs. the
  previous race, and the most-picked driver that week.

Qualifying "grid position" is derived identically everywhere (a shared
`QualifyingOrder` helper in `AnalysisFacts.cs`) using the same ordering
`ReadCommands.Quali` already displays, so grid-position facts here never
disagree with what `/quali` shows for the same race.

## `DataService` additions

- `FindDriver(Season, string name)` — thin delegate to
  `GpConfDataAccess.FindDriver`, same convention as `FindSeason`/`FindRace`.
- `PlayerScoresPerRace(Season, GameSeason)` → `List<RacePlayerScores>`
  (`RacePlayerScores(Race, Dictionary<ByteString, float?> ScoreByPlayer)`).
  Computed independently per race via `CCUtils.ScorePickSlot` rather than
  diffing `PlayerScores`' cumulative totals, to avoid float-subtraction
  drift and to distinguish "no `Picks` entry that race" (missing key /
  `null`) from "submitted and scored exactly 0" (`0f`).

## See also

- [pickem-bot-plan.md](pickem-bot-plan.md) — the MCP-skill-driven recap
  mechanism this feature runs alongside, not replaces.
- [confidence-cup-scoring.md](confidence-cup-scoring.md) — the `CCUtils`
  scoring rules every facts builder calls into.
- [data-model.md](data-model.md) — protobuf field reference, including the
  `laps_completed` convention this feature deliberately avoids surfacing.
