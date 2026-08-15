# LLM analysis commands (Ollama)

> As of `56009f3` + uncommitted (2026-08-14).

The Discord slash commands in `GPConf.DiscordBot` that take structured input
(season/driver/player/team/race/league) generate narrative text via a
locally-hosted Ollama server, layered on top of the same
`DataService`/`CCUtils` data every other bot command uses. This is a
separate, parallel mechanism from the MCP-skill-driven agent-authored
recaps described in [pickem-bot-plan.md](pickem-bot-plan.md)'s planned
race-week skill sequence (steps 4/6/7) — not a replacement for it. That
plan's recaps are written by a Claude Code skill orchestrating race week
through MCP tool calls; these commands are synchronous, on-demand Discord
slash commands that call a local model directly from the bot process, no
agent orchestration involved.

## Convention: commands that need input use a wizard, not slash args

**Any bot command that needs structured input (season, driver, player, team,
race, league) must be a parameterless slash command that opens an ephemeral
dropdown wizard — never a command that takes those values as slash-command
arguments.** This is a hard convention for the GPConf bot. Reasons:

- The caller never has to know exact season/driver/player/team/race/league
  spelling — they pick from real, populated dropdowns.
- The wizard auto-resolves the league when the season has exactly one, so
  the common case needs no league input at all.
- It keeps the command surface uniform and avoids the "not found" error
  path that typed-arg commands hit on every typo.

This applies to **every** command that takes structured input, including the
DM-only read commands (`/standings`, `/results`, `/quali`, `/practice`,
`/pick`, `/rules`) — they are parameterless wizards too, not typed-arg
commands. The read-command wizard is a **separate, simpler module** from the
analysis wizard below: it reuses `AnalysisSessionStore` for the token/TTL/
CallerId machinery and the shared dropdown builders, but its result renders
**ephemeral in the DM** (a one-shot lookup — no public post, no
regeneration-keep). The analysis wizard posts public persistent messages and
keeps the session for regeneration.

The wizard infrastructure lives in `AnalysisCommands.cs` and is shared by
both the LLM commands and the deterministic stat commands below. The
deterministic commands (`/season-stats`, `/leaderboard`, `/projected`) build
their embed directly in the Generate handler (no Ollama call); the LLM
commands go through `GenerateEmbedAsync`. The **comparison** commands
(`/compare`, `/h2h`, `/team-analysis`) are hybrid: they post a prettier
static embed of the raw stats **and** an Ollama-generated narrative analysis
highlighting noteworthy differences and key performance indicators.

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

### LLM (Ollama-backed)

| Command | Params | Facts builder |
|---|---|---|
| `/driver-analysis` | none — dropdown wizard | `AnalysisFacts.BuildDriverFacts` |
| `/player-analysis` | none — dropdown wizard | `AnalysisFacts.BuildPlayerFacts` |
| `/season-overview` | none — dropdown wizard | `AnalysisFacts.BuildSeasonFacts` |
| `/race-recap` | none — dropdown wizard | `AnalysisFacts.BuildRaceRecapFacts` |

### Comparison (static embed + Ollama analysis)

| Command | Params | Static embed | AI analysis |
|---|---|---|---|
| `/team-analysis` | none — dropdown wizard | `ReadCommands.BuildTeamCompareEmbed` | `AnalysisFacts.BuildTeamCompareFacts` |
| `/compare` | none — dropdown wizard | `ReadCommands.BuildCompareEmbed` | `AnalysisFacts.BuildCompareFacts` |
| `/h2h` | none — dropdown wizard | `ReadCommands.BuildH2HEmbed` | `AnalysisFacts.BuildH2HFacts` |

### Deterministic (no Ollama)

| Command | Params | Facts builder / embed |
|---|---|---|
| `/season-stats` | none — dropdown wizard | `AnalysisFacts.BuildSeasonStatsFacts` → `ReadCommands.BuildSeasonStatsEmbed` |
| `/leaderboard` | none — dropdown wizard | `ReadCommands.BuildLeaderboardEmbed` |
| `/projected` | none — dropdown wizard | `AnalysisFacts.BuildProjectedFacts` → `ReadCommands.BuildProjectedEmbed` |

All commands are parameterless and open an ephemeral dropdown wizard (see
below). The generated analysis is posted as a **separate, public, persistent
channel message** — only the wizard itself stays ephemeral, so the result
survives bot restarts and isn't replaced by the widget. The deterministic
commands follow the same wizard + public-persistent-post pattern; only the
embed construction differs (direct, no Ollama call). The comparison commands
post **two** messages: the static stats embed, then the AI analysis embed.

### The dropdown wizard

Every command opens an ephemeral select-menu-plus-button widget, state-tracked
by `AnalysisSessionStore` (`GPConf.DiscordBot/Services/DriverAnalysisSessionStore.cs`,
mirrors `PickSessionStore`'s shape/TTL: an opaque 8-byte hex token embedded
in every component's `CustomId`, since three raw GUIDs plus a command prefix
would blow past Discord's 100-char `CustomId` limit). A session records its
`Kind` (`Driver`/`Player`/`Season`/`Recap`/`Team`/`SeasonStats`/`Leaderboard`/
`Compare`/`H2H`/`Projected`) plus the optional `DriverId`, `Driver2Id`,
`LeagueId`, `PlayerName`, `Player2Name`, `RaceId`, `TeamId`, and `Team2Id`
selections, and `CallerId` binds it to whoever ran the command — every
handler rejects interactions from any other user id.

The dropdown sequence depends on the kind:

- **Driver:** season → driver → league
- **Player:** season → league → player → race (players are league-scoped,
  from `GameSeason.ParticipatingPlayers`)
- **Season:** season → league → race
- **Recap:** season → race → league
- **Team:** season → team → team (second) → league
- **SeasonStats:** season → league → race
- **Leaderboard:** season → league → race
- **Compare:** season → driver → driver (second) → race
- **H2H:** season → league → player → player (second) → race
- **Projected:** season → league

The **read-command wizard** (`ReadCommands.cs`, `read_*` component ids) uses
the same season/race/league dropdown builders but a simpler sequence:

- **Rules:** season
- **Quali:** season → race
- **Practice:** season → race → session (FP1/FP2/FP3)
- **Standings / Results / Pick:** season → race → league

The league dropdown only appears when the season has more than one configured
league (0 or 1 auto-resolve silently), the same convention as the analysis
wizard. `/pick` preserves its secrecy gate (other players' picks hidden until
the race has results).

Flow:

1. Command invocation defaults the season dropdown to the newest season
   (`mainData.Seasons.OrderByDescending(s => s.Year).First()`) so the
   subsequent dropdowns are populated immediately rather than starting empty.
2. Changing the **season** dropdown (`analysis_season:{token}`) re-populates
   the rest from that season's roster and resets any previously chosen
   driver/league/player/race (a pick from a different season's data would be
   meaningless).
3. The **league** dropdown (`analysis_league:{token}`) only appears when the
   season has more than one configured league — 0 or 1 leagues auto-resolve
   silently, the same ≤1-league convention `ReadCommands.Results` uses.
4. The **race** dropdown (`analysis_race:{token}`) defaults to the latest
   race (`DataService.LatestRace`, falling back to round 1) but is
   user-selectable.
5. **Generate** (`analysis_generate:{token}`) validates the kind's required
   selections, then `component.UpdateAsync` both acks the interaction AND
   disables the Generate button (content set to "Generating…") so it can't be
   re-clicked while the slow Ollama call is in flight. On success the embed
   is sent to the channel via `Context.Channel.SendMessageAsync` (public,
   persistent), then `ModifyOriginalResponseAsync` re-enables the button and
   restores the prompt — the session is intentionally **kept** so the caller
   can tweak the dropdowns and regenerate. On an Ollama error the button is
   re-enabled and the error shown in the wizard. The deterministic kinds
   (`SeasonStats`/`Leaderboard`/`Compare`/`H2H`/`Projected`) set `embed`
   directly in the switch and skip the Ollama call entirely — the same
   ack/disable/post/re-enable flow still runs.

Every re-render (`BuildAnalysisComponents`) rebuilds the menus from the
session's current state and marks the already-chosen option as `isDefault`
on each, so switching one dropdown doesn't visually lose the others.

## `OllamaClient` (`GPConf.DiscordBot/Services/OllamaClient.cs`)

Singleton `HttpClient`, POSTs to Ollama's `/api/chat` (non-streaming,
`"think": false`). Env vars, read once in the constructor:

| Var | Default |
|---|---|
| `OLLAMA_HOST` | `scout:11434` (`http://` prepended if missing) |
| `OLLAMA_MODEL` | `qwen3.8:27b-mlx` (was `muse-glimmer:30b-mlx` until 2026-08-14) |
| `OLLAMA_TIMEOUT_SECONDS` | `180` |

There is no caching/pre-generation — this is by design (the user explicitly
accepted the on-demand wait over adding a cache layer). `"think": false` is
sent regardless of model to suppress chain-of-thought output where the model
supports it.

All Ollama failure modes (connection refused, DNS failure, timeout,
malformed JSON) normalize into one `OllamaException`, caught once per
command as a plain `⚠️` `FollowupAsync` — never an unhandled exception.

## Facts-block field lists

See `AnalysisFacts.cs` for the authoritative implementation; summary of
what each builder includes:

- **Driver** (`BuildDriverFacts`): identity (name, number, nationality,
  team), championship position/points/gaps at the cutoff race, a per-race
  table (qualifying grid position + stage reached, practice fastest laps,
  race finish/status/points per session), season aggregates (best/worst
  finish, podiums, DNF/DNS/DSQ counts, average qualifying/race position), a
  recent finishing-trend line (last up-to-3 race finishes, improving/
  declining/steady), and — when a league resolved (optional `league`/`gs`
  params, `null` from the three typed-param commands' call sites but always
  populated by the `/driver-analysis` wizard) — a "GPConf confidence-cup
  outlook" block: pick eligibility for the next race
  (`CCUtils.GetEligibleDriversWithPos`, using the race immediately before
  `DataService.NextPickableRace` as `prevRace` — never `cutoff` itself, so
  this never disagrees with what the real picker would offer), the driver's
  standings multiplier if eligible (`CCUtils.GetStandingsMultiplier`), and
  the league's position-cutoff rule. A driver who hasn't yet satisfied
  `CCUtils.HasQualifiedAndStarted` (see [confidence-cup-scoring.md](confidence-cup-scoring.md))
  gets an explicit note explaining why they're ineligible.
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
- **Team compare** (`BuildTeamCompareFacts`, comparison): two teams'
  constructor points + best driver position, season aggregates (wins/podiums/
  DNFs, both-scored count, avg finish), per-driver championship positions,
  and — when a league resolved — each driver's pick eligibility + standings
  multiplier for the next race.
- **Season stats** (`BuildSeasonStatsFacts`, deterministic): driver
  reliability (fewest DNFs/DNS), lowest qualifying SD (most consistent),
  longest winless streak, average quali→race position change; if a league
  resolved, adds pick-perfect races, missed opportunities (scoreable driver
  nobody picked), zero-score weeks, and the most-picked driver per race.
- **Compare** (`BuildCompareFacts`, comparison): two drivers' championship
  positions, head-to-head win count across shared races, reliability
  (DNF/DSQ + average finish), average qualifying position **plus** a quali
  head-to-head count, and pace analysis — average fastest-lap and race-time
  gaps (race pace) and average practice fastest-lap gap (practice pace). The
  AI analysis is prompted to discuss practice, qualifying, and race pace and
  how the pace differences factor into their results.
- **H2H** (`BuildH2HFacts`, comparison): two players' cumulative scores,
  head-to-head outscore count, no-pick counts, scoring consistency (avg/best
  per race), shared picks, and each player's unique picks.
- **Projected** (`BuildProjectedFacts`, deterministic): the next pickable
  race's best-case picks (highest-multiplier eligible drivers) and the
  projected standings if every player picks optimally.

The comparison facts builders emit markdown with `**Header**` section lines;
`ReadCommands.ParseFactsFields` splits those into titled embed fields so the
static embed renders as clean sections rather than a code fence. The AI
analysis for these commands is prompted to highlight noteworthy differences
and key performance indicators between the two subjects.

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
