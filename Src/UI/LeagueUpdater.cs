using System.Numerics;
using Google.Protobuf;
using GPConf.Utilities;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;

namespace GPConf.UI;

public class LeagueUpdater
{
    private static ByteString _leagueId       = ByteString.Empty;
    private static ByteString _gameSeasonId   = ByteString.Empty;
    private static ByteString _selectedRaceId = ByteString.Empty;

    public static void Draw(GpConfApp app)
    {
        MainData data = app.GetMainData();

        ImGui.Begin("League Updater");
        DrawSelectors(data);

        League?     league = data.Leagues.FirstOrDefault(l => l.Id == _leagueId);
        GameSeason? gs     = league?.Seasons.FirstOrDefault(s => s.Id == _gameSeasonId);
        Season?     season = gs != null ? data.Seasons.FirstOrDefault(s => s.Id == gs.SeasonId) : null;

        if (league == null || gs == null || season == null)
        {
            ImGui.TextDisabled("Select a league and linked game season to begin.");
        }
        else
        {
            gs.PickRules ??= new PickRules();
            DrawRaces(season, gs);
            ImGui.Separator();
            if (ImGui.Button("Save##LeagueUpdater")) app.Save();
        }

        ImGui.End();

        DrawCurrentRaceInfoWindow(season, gs);
    }

    // ── Selectors ─────────────────────────────────────────────────────────────

    private static void DrawSelectors(MainData data)
    {
        League? selLeague   = data.Leagues.FirstOrDefault(l => l.Id == _leagueId);
        string  leagueLabel = selLeague != null
            ? (selLeague.LeagueName.Length > 0 ? selLeague.LeagueName : "(unnamed)")
            : "(select league)";

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("League##lu", leagueLabel))
        {
            foreach (League l in data.Leagues)
            {
                string ln = l.LeagueName.Length > 0 ? l.LeagueName : "(unnamed)";
                if (ImGui.Selectable(ln, l.Id == _leagueId))
                {
                    _leagueId     = l.Id;
                    _gameSeasonId = ByteString.Empty;
                }
            }
            ImGui.EndCombo();
        }

        League? league = data.Leagues.FirstOrDefault(l => l.Id == _leagueId);
        if (league == null) return;

        ImGui.SameLine();

        GameSeason? selGs   = league.Seasons.FirstOrDefault(s => s.Id == _gameSeasonId);
        string      gsLabel = "(select game season)";
        if (selGs != null)
        {
            Season? linked = data.Seasons.FirstOrDefault(s => s.Id == selGs.SeasonId);
            gsLabel = linked != null && linked.Name.Length > 0 ? linked.Name : $"{linked?.Year ?? 0}";
        }

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Game Season##lu", gsLabel))
        {
            foreach (GameSeason gs in league.Seasons)
            {
                Season? linked = data.Seasons.FirstOrDefault(s => s.Id == gs.SeasonId);
                string  gsl    = linked != null && linked.Name.Length > 0 ? linked.Name : $"{linked?.Year ?? 0}";
                if (ImGui.Selectable(gsl, gs.Id == _gameSeasonId))
                    _gameSeasonId = gs.Id;
            }
            ImGui.EndCombo();
        }
    }

    // ── Race List ─────────────────────────────────────────────────────────────

    private static void DrawRaces(Season season, GameSeason gs)
    {
        var orderedRaces = season.Races.OrderBy(r => r.Round).ToList();

        if (orderedRaces.Count == 0)
        {
            ImGui.TextDisabled("No races in the linked season.");
            return;
        }

        if (gs.ParticipatingPlayers.Count == 0)
        {
            ImGui.TextDisabled("No participating players. Add players in the League Editor.");
            return;
        }

        for (int raceIdx = 0; raceIdx < orderedRaces.Count; raceIdx++)
        {
            Race      race = orderedRaces[raceIdx];
            GameRace? gr   = gs.Races.FirstOrDefault(g => g.RaceId == race.Id);
            if (gr == null) continue;

            ImGui.PushID(race.Id.ToBase64());

            string name  = race.Name.Length > 0 ? race.Name : "(unnamed)";
            string label = $"R{race.Round}: {name}";

            if (ImGui.CollapsingHeader(label))
            {
                _selectedRaceId = race.Id;
                Race? prevRace = raceIdx > 0 ? orderedRaces[raceIdx - 1] : null;
                DrawRacePicks(season, gs, gr, race, prevRace);
            }

            ImGui.PopID();
        }
    }

    // ── Picks Table ───────────────────────────────────────────────────────────

    private static void DrawRacePicks(Season season, GameSeason gs, GameRace gr, Race race, Race? prevRace)
    {
        PickRules rules      = gs.PickRules!;
        int       numPicks   = Math.Max(0, rules.NumPicks);
        bool      hasResults = race.RaceResults.Count > 0;

        if (numPicks == 0)
        {
            ImGui.Indent();
            ImGui.TextDisabled("NumPicks is 0 - configure Pick Rules in the League Editor.");
            ImGui.Unindent();
            return;
        }

        var eligible = GetEligibleDriversWithPos(season, prevRace, rules.PositionCutoff);

        int colCount = 1 + numPicks + 1;

        ImGui.Indent();
        if (!ImGui.BeginTable("##picktbl", colCount,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.Unindent();
            return;
        }

        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 160);
        for (int i = 0; i < numPicks; i++)
            ImGui.TableSetupColumn($"Pick {i + 1}", ImGuiTableColumnFlags.None, 220);
        ImGui.NewLine();
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableHeadersRow();

        foreach (Player player in gs.ParticipatingPlayers)
        {
            ImGui.PushID(player.Id.ToBase64());

            Picks? picks = gr.PicksPerPlayer.FirstOrDefault(p => p.PlayerId == player.Id);
            if (picks == null)
            {
                picks = new Picks { PlayerId = player.Id };
                gr.PicksPerPlayer.Add(picks);
            }

            while (picks.DriverId.Count < numPicks)
                picks.DriverId.Add(ByteString.Empty);

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            if (player.Color != 0)
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg,
                    ImGui.ColorConvertFloat4ToU32(ColorUtils.ToVec4(player.Color)));
                ImGui.PushStyleColor(ImGuiCol.Text, ColorUtils.ContrastingText(player.Color));
            }
            ImGui.AlignTextToFramePadding();
            ImGui.Text(player.PlayerName.Length > 0 ? player.PlayerName : "(unnamed)");
            if (player.Color != 0) ImGui.PopStyleColor();

            float total = 0f;
            for (int i = 0; i < numPicks; i++)
            {
                ImGui.TableSetColumnIndex(1 + i);
                ImGui.PushID(i);

                ByteString pickedId   = picks.DriverId[i];
                bool       isEligible = eligible.Any(e => e.driver.Id == pickedId);
                Driver?    picked     = season.Drivers.FirstOrDefault(d => d.Id == pickedId);

                string comboLabel = pickedId.IsEmpty                   ? "(none)"
                    : picked != null && !isEligible                    ? $"[!] {DriverLabel(picked)}"
                    : picked != null                                   ? DriverLabel(picked)
                    : "(none)";

                // Drivers already used in other slots for this player this race.
                var usedElsewhere = picks.DriverId
                    .Where((id, idx) => idx != i && !id.IsEmpty)
                    .ToHashSet();

                ImGui.SetNextItemWidth(-1);
                if (ImGui.BeginCombo("##drv", comboLabel))
                {
                    if (ImGui.Selectable("(none)", pickedId.IsEmpty))
                        picks.DriverId[i] = ByteString.Empty;

                    foreach (var (driver, champPos) in eligible.OrderBy(e => e.champPos))
                    {
                        if (usedElsewhere.Contains(driver.Id)) continue;
                        string dlabel = champPos > 0
                            ? $"{DriverLabel(driver)}  (P{champPos})"
                            : DriverLabel(driver);
                        if (ImGui.Selectable(dlabel, driver.Id == pickedId))
                            picks.DriverId[i] = driver.Id;
                    }
                    ImGui.EndCombo();
                }

                float score = 0f;
                if (!pickedId.IsEmpty && isEligible)
                {
                    int pos = eligible.First(e => e.driver.Id == pickedId).champPos;
                    score = hasResults
                        ? GetPickScoreFromResults(season, rules, i, pickedId, race, pos)
                        : CalculatePickScore(rules, i, pos);
                }
                total += score;

                if (!pickedId.IsEmpty && !isEligible)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.35f, 0.35f, 1f));
                    ImGui.Text("ineligible pick");
                    ImGui.PopStyleColor();
                }
                else if (hasResults && !pickedId.IsEmpty)
                {
                    // Result confirmed: green if scored, red zero if missed
                    ImGui.PushStyleColor(ImGuiCol.Text,
                        score > 0 ? new Vector4(0.45f, 0.9f, 0.45f, 1f)
                                  : new Vector4(1f,    0.35f, 0.35f, 1f));
                    ImGui.Text(score > 0 ? $"{score:F1} pts" : "0 pts");
                    ImGui.PopStyleColor();
                }
                else
                {
                    // Preview (no results yet or no pick made)
                    ImGui.PushStyleColor(ImGuiCol.Text,
                        score > 0 ? new Vector4(0.45f, 0.9f, 0.45f, 1f)
                                  : new Vector4(0.5f,  0.5f,  0.5f,  1f));
                    ImGui.Text(score > 0 ? $"{score:F1} pts" : "-");
                    ImGui.PopStyleColor();
                }

                ImGui.PopID();
            }

            ImGui.TableSetColumnIndex(1 + numPicks);
            ImGui.Dummy(new Vector2(0, ImGui.GetFrameHeight()));
            ImGui.Text(total > 0 ? $"{total:F1}" : "-");

            ImGui.PopID();
        }

        ImGui.EndTable();
        ImGui.Unindent();
    }

    // ── Current Race Info Window ───────────────────────────────────────────────

    private static void DrawCurrentRaceInfoWindow(Season? season, GameSeason? gs)
    {
        ImGui.Begin("Current Race Info");

        if (season == null || gs == null)
        {
            ImGui.TextDisabled("No game season selected.");
            ImGui.End();
            return;
        }

        gs.PickRules ??= new PickRules();
        Race? selectedRace = season.Races.FirstOrDefault(r => r.Id == _selectedRaceId);
        if (selectedRace == null)
        {
            ImGui.TextDisabled("Expand a race in the League Updater to view info.");
            ImGui.End();
            return;
        }

        var   orderedRaces = season.Races.OrderBy(r => r.Round).ToList();
        int   selIdx       = orderedRaces.FindIndex(r => r.Id == selectedRace.Id);
        Race? prevRace     = selIdx > 0 ? orderedRaces[selIdx - 1] : null;

        string raceName = selectedRace.Name.Length > 0 ? selectedRace.Name : "(unnamed)";
        ImGui.Text($"R{selectedRace.Round}: {raceName}");
        ImGui.Separator();

        if (ImGui.CollapsingHeader("Player Standings##cri"))
            DrawPlayerStandings(season, gs, orderedRaces, selIdx);
        
        if (ImGui.CollapsingHeader("Driver Championship Standings##cri"))
            DrawDriverStandings(season, gs, prevRace);
        
        if (ImGui.CollapsingHeader("Team Standings##cri"))
            DrawTeamStandings(season, prevRace);
        
        if (ImGui.CollapsingHeader("Practice Session Info##cri"))
            DrawPracticeInfo(season, selectedRace);

        if (ImGui.CollapsingHeader("Driver Qualifying Info##cri"))
            DrawQualifyingInfo(season, orderedRaces, selIdx);

        if (ImGui.CollapsingHeader("Fastest Laps##cri"))
            DrawFastestLaps(season, orderedRaces, selIdx);

        if (ImGui.CollapsingHeader("Championship Position Chart##cri"))
            DrawPointsChart(season, orderedRaces, selIdx);


        ImGui.End();
    }

    // ── Driver Championship Standings ─────────────────────────────────────────

    private static void DrawDriverStandings(Season season, GameSeason gs, Race? prevRace)
    {
        PickRules rules = gs.PickRules!;

        List<(Driver driver, int pts)> standings = prevRace == null
            ? season.Drivers.Select(d => (d, 0)).ToList()
            : season.Drivers
                .Select(d => (driver: d,
                    pts: CCUtils.GetDriverChampionshipPointSnapshotForRace(season, prevRace, d)))
                .OrderByDescending(x => x.pts)
                .ToList();

        ImGui.Indent();
        if (ImGui.BeginTable("##drvstand", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Pos",    ImGuiTableColumnFlags.WidthFixed,  35);
            ImGui.TableSetupColumn("Driver", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pts",    ImGuiTableColumnFlags.WidthFixed,  40);
            ImGui.TableSetupColumn("Mult",   ImGuiTableColumnFlags.WidthFixed,  50);
            ImGui.TableSetupColumn("Elig",   ImGuiTableColumnFlags.WidthFixed,  35);
            ImGui.TableHeadersRow();

            for (int i = 0; i < standings.Count; i++)
            {
                var (driver, pts) = standings[i];
                int  pos      = i + 1;
                bool eligible = prevRace == null || pos > rules.PositionCutoff;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); CenterText($"P{pos}");

                ImGui.TableSetColumnIndex(1);
                Team? team = season.Teams.FirstOrDefault(t => t.Id == driver.CurrentTeamId);
                if (team != null && team.Color != 0)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg,
                        ImGui.ColorConvertFloat4ToU32(ColorUtils.ToVec4(team.Color)));
                    ImGui.PushStyleColor(ImGuiCol.Text, ColorUtils.ContrastingText(team.Color));
                }
                string num = driver.Number > 0 ? $"#{driver.Number} " : "";
                CenterText($"{num}{(driver.Name.Length > 0 ? driver.Name : "(unnamed)")}");
                if (team != null && team.Color != 0) ImGui.PopStyleColor();

                ImGui.TableSetColumnIndex(2); CenterText($"{pts}");

                ImGui.TableSetColumnIndex(3);
                {
                    string multText = prevRace == null
                        ? "×1"
                        : (eligible && rules.StandingsMultipliers.Count > 0
                            ? $"×{GetStandingsMultiplier(rules, pos):G}" : "-");
                    float avail = ImGui.GetContentRegionAvail().X;
                    float tw    = ImGui.CalcTextSize(multText).X;
                    if (avail > tw) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - tw) * 0.5f);
                    if (multText == "—") ImGui.TextDisabled(multText);
                    else                 ImGui.Text(multText);
                }

                ImGui.TableSetColumnIndex(4);
                {
                    string eligText = eligible ? "V" : "X";
                    float  avail    = ImGui.GetContentRegionAvail().X;
                    float  tw       = ImGui.CalcTextSize(eligText).X;
                    if (avail > tw) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - tw) * 0.5f);
                    ImGui.PushStyleColor(ImGuiCol.Text, eligible
                        ? new Vector4(0.3f, 0.9f, 0.3f, 1f)
                        : new Vector4(0.9f, 0.3f, 0.3f, 1f));
                    ImGui.Text(eligText);
                    ImGui.PopStyleColor();
                }
            }

            ImGui.EndTable();
        }
        ImGui.Unindent();
    }

    // ── Player Standings ──────────────────────────────────────────────────────

    private static void DrawPlayerStandings(Season season, GameSeason gs,
        List<Race> orderedRaces, int selectedIdx)
    {
        if (selectedIdx == 0)
        {
            ImGui.Indent();
            ImGui.TextDisabled("No prior race data.");
            ImGui.Unindent();
            return;
        }

        PickRules rules       = gs.PickRules!;
        var       playerScores = gs.ParticipatingPlayers.ToDictionary(p => p.Id, _ => 0f);

        for (int rIdx = 0; rIdx < selectedIdx; rIdx++)
        {
            Race      race       = orderedRaces[rIdx];
            Race?     prev       = rIdx > 0 ? orderedRaces[rIdx - 1] : null;
            GameRace? gr         = gs.Races.FirstOrDefault(g => g.RaceId == race.Id);
            bool      hasResults = race.RaceResults.Count > 0;
            if (gr == null) continue;

            var eligible = GetEligibleDriversWithPos(season, prev, rules.PositionCutoff);

            foreach (Player player in gs.ParticipatingPlayers)
            {
                Picks? picks = gr.PicksPerPlayer.FirstOrDefault(p => p.PlayerId == player.Id);
                if (picks == null) continue;

                for (int i = 0; i < Math.Min(picks.DriverId.Count, rules.NumPicks); i++)
                {
                    ByteString dId  = picks.DriverId[i];
                    if (dId.IsEmpty) continue;

                    int eIdx = eligible.FindIndex(e => e.driver.Id == dId);
                    if (eIdx < 0) continue;

                    int   champPos = eligible[eIdx].champPos;
                    float score    = hasResults
                        ? GetPickScoreFromResults(season, rules, i, dId, race, champPos)
                        : CalculatePickScore(rules, i, champPos);

                    playerScores[player.Id] += score;
                }
            }
        }

        var sorted = gs.ParticipatingPlayers
            .OrderByDescending(p => playerScores.GetValueOrDefault(p.Id))
            .ToList();

        ImGui.Indent();
        if (ImGui.BeginTable("##plyrstand", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Pos",    ImGuiTableColumnFlags.WidthFixed,  35);
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pts",    ImGuiTableColumnFlags.WidthFixed,  50);
            ImGui.TableHeadersRow();

            for (int i = 0; i < sorted.Count; i++)
            {
                Player p     = sorted[i];
                float  score = playerScores.GetValueOrDefault(p.Id);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); CenterText($"P{i + 1}");

                ImGui.TableSetColumnIndex(1);
                if (p.Color != 0)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg,
                        ImGui.ColorConvertFloat4ToU32(ColorUtils.ToVec4(p.Color)));
                    ImGui.PushStyleColor(ImGuiCol.Text, ColorUtils.ContrastingText(p.Color));
                }
                CenterText(p.PlayerName.Length > 0 ? p.PlayerName : "(unnamed)");
                if (p.Color != 0) ImGui.PopStyleColor();

                ImGui.TableSetColumnIndex(2); CenterText(score > 0 ? $"{score:F1}" : "-");
            }

            ImGui.EndTable();
        }
        ImGui.Unindent();
    }

    // ── Practice Session Info ─────────────────────────────────────────────────

    private static void DrawPracticeInfo(Season season, Race selectedRace)
    {
        if (selectedRace.Practices.Count == 0)
        {
            ImGui.Indent();
            ImGui.TextDisabled("No practice session data for this race.");
            ImGui.Unindent();
            return;
        }

        var bestLaps = new Dictionary<ByteString, (float time, int session)>();
        foreach (PracticeSession ps in selectedRace.Practices)
        {
            foreach (LapData ld in ps.LapData)
            {
                if (ld.FastestLapSeconds <= 0) continue;
                if (!bestLaps.TryGetValue(ld.DriverId, out var existing) ||
                    ld.FastestLapSeconds < existing.time)
                    bestLaps[ld.DriverId] = (ld.FastestLapSeconds, ps.SessionNumber);
            }
        }

        var sorted = bestLaps
            .Select(kv => (
                driver:  season.Drivers.FirstOrDefault(d => d.Id == kv.Key),
                time:    kv.Value.time,
                session: kv.Value.session))
            .Where(x => x.driver != null)
            .OrderBy(x => x.time)
            .ToList();

        if (sorted.Count == 0)
        {
            ImGui.Indent();
            ImGui.TextDisabled("No lap time data recorded.");
            ImGui.Unindent();
            return;
        }

        float best = sorted[0].time;

        ImGui.Indent();
        if (ImGui.BeginTable("##practinfo", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Driver",   ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Best Lap", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Gap",      ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("Session",  ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            foreach (var (driver, time, session) in sorted)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                Team? team = season.Teams.FirstOrDefault(t => t.Id == driver!.CurrentTeamId);
                if (team != null && team.Color != 0)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg,
                        ImGui.ColorConvertFloat4ToU32(ColorUtils.ToVec4(team.Color)));
                    ImGui.PushStyleColor(ImGuiCol.Text, ColorUtils.ContrastingText(team.Color));
                }
                CenterText(DriverLabel(driver!));
                if (team != null && team.Color != 0) ImGui.PopStyleColor();

                ImGui.TableSetColumnIndex(1); CenterText(FormatLapTime(time));
                ImGui.TableSetColumnIndex(2);
                float gap = time - best;
                CenterText(gap < 0.001f ? "—" : $"+{gap:F3}s");
                ImGui.TableSetColumnIndex(3); CenterText($"FP{session}");
            }

            ImGui.EndTable();
        }
        ImGui.Unindent();
    }

    // ── Championship Position Chart ───────────────────────────────────────────

    private static void DrawPointsChart(Season season, List<Race> orderedRaces, int selectedIdx)
    {
        if (selectedIdx == 0)
        {
            ImGui.Indent();
            ImGui.TextDisabled("No prior race data to chart.");
            ImGui.Unindent();
            return;
        }

        var pastRaces = orderedRaces.Take(selectedIdx).ToList();

        // For each race, compute cumulative points → sort → assign position.
        // positionsByRace[raceIdx][driverId] = championship position after that race.
        var positionsByRace = pastRaces
            .Select(r =>
            {
                var ranked = season.Drivers
                    .Select(d => (id: d.Id, pts: CCUtils.GetDriverChampionshipPointSnapshotForRace(season, r, d)))
                    .Where(x => x.pts > 0)
                    .OrderByDescending(x => x.pts)
                    .Select((x, i) => (x.id, pos: i + 1))
                    .ToDictionary(x => x.id, x => x.pos);
                return ranked;
            })
            .ToList();

        // Build per-driver series with per-driver xs/ys containing only races where the
        // driver has a valid position (avoids NaN-only or single-point series that PlotLine
        // cannot render as a visible line).
        var lastSnapshot = positionsByRace[selectedIdx - 1];
        var driverSeries = season.Drivers
            .Where(d => lastSnapshot.ContainsKey(d.Id))
            .OrderBy(d => lastSnapshot[d.Id])
            .Select(d =>
            {
                var pts = pastRaces
                    .Select((r, ri) => (x: (float)r.Round,
                                        y: positionsByRace[ri].TryGetValue(d.Id, out int p) ? (float)p : float.NaN))
                    .Where(pt => !float.IsNaN(pt.y))
                    .ToList();
                return (driver: d,
                        xs: pts.Select(pt => pt.x).ToArray(),
                        ys: pts.Select(pt => pt.y).ToArray());
            })
            .ToList();

        ImGui.Indent();
        if (ImPlot.BeginPlot("##poschart", new Vector2(-1, 260), ImPlotFlags.NoLegend))
        {
            int driverCount = lastSnapshot.Count;
            ImPlot.SetupAxes("Round", "Pos", ImPlotAxisFlags.None, ImPlotAxisFlags.Invert);
            ImPlot.SetupAxisLimits(ImAxis.Y1, 0.5, driverCount + 0.5, ImPlotCond.Always);

            var endpoints = new List<(Vector2 pixel, Vector4 color, string label)>();

            unsafe
            {
                for (int i = 0; i < driverSeries.Count; i++)
                {
                    var (driver, dxs, dys) = driverSeries[i];
                    if (dxs.Length == 0) continue;
                    Team? team = season.Teams.FirstOrDefault(t => t.Id == driver.CurrentTeamId);
                    Vector4 lineColor = team != null && team.Color != 0
                        ? BrightenForDarkBg(ColorUtils.ToVec4(team.Color))
                        : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                    ImPlot.SetNextLineStyle(lineColor, 1.5f);
                    fixed (float* xp = dxs, yp = dys)
                        ImPlot.PlotLine($"##d{i}", xp, yp, dxs.Length);

                    Vector2 ep = ImPlot.PlotToPixels(dxs[dxs.Length - 1], dys[dys.Length - 1]);
                    endpoints.Add((ep, lineColor, DriverShortLabel(driver)));
                }

                // Draw endpoint pips and labels using the plot draw list.
                var dl = ImPlot.GetPlotDrawList();
                float textH  = ImGui.GetTextLineHeight();
                float pipW   = 6f;
                float pipH   = textH - 2f;
                foreach (var (pixel, color, label) in endpoints)
                {
                    uint col = ImGui.ColorConvertFloat4ToU32(color);
                    float x0 = pixel.X + 4f;
                    float y0 = pixel.Y - pipH * 0.5f;
                    ImPlot.PushPlotClipRect(0f);
                    dl.AddRectFilled(new Vector2(x0, y0), new Vector2(x0 + pipW, y0 + pipH), col);
                    dl.AddText(new Vector2(x0 + pipW + 2f, pixel.Y - textH * 0.5f), 0xFFFFFFFF, label);
                    ImPlot.PopPlotClipRect();
                }
            }
            ImPlot.EndPlot();
        }
        ImGui.Unindent();
    }

    // Boosts Value (HSV) so dark team colors are legible on a dark plot background.
    private static Vector4 BrightenForDarkBg(Vector4 c, float minValue = 0.72f, float minSaturation = 0.45f)
    {
        float h = 0, s = 0, v = 0;
        ImGui.ColorConvertRGBtoHSV(c.X, c.Y, c.Z, ref h, ref s, ref v);
        v = Math.Max(v, minValue);
        s = Math.Max(s, minSaturation);
        float r = 0, g = 0, b = 0;
        ImGui.ColorConvertHSVtoRGB(h, s, v, ref r, ref g, ref b);
        return new Vector4(r, g, b, c.W);
    }

    private static string DriverShortLabel(Driver d)
    {
        string[] parts = d.Name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        char fi = parts.Length > 0 && parts[0].Length > 0 ? char.ToUpper(parts[0][0]) : '?';
        char li = parts.Length > 1 && parts[^1].Length > 0 ? char.ToUpper(parts[^1][0]) : fi;
        string num = d.Number > 0 ? $"{d.Number}" : "";
        return $"{fi}{li}{num}";
    }

    // ── Driver Qualifying Info ────────────────────────────────────────────────

    private static void DrawQualifyingInfo(Season season, List<Race> orderedRaces, int selectedIdx)
    {
        if (selectedIdx == 0)
        {
            ImGui.Indent();
            ImGui.TextDisabled("No prior qualifying data.");
            ImGui.Unindent();
            return;
        }

        // Per-driver accumulated grid results across past races.
        var driverData = new Dictionary<ByteString, List<(int pos, string raceName, int? elimSession)>>();
        // Per-driver per-session poles: (raceName, sessionName).
        var driverPoles = new Dictionary<ByteString, List<(string raceName, string sessionName)>>();

        for (int ri = 0; ri < selectedIdx; ri++)
        {
            Race race = orderedRaces[ri];
            if (race.QualifyingSessions.Count == 0) continue;

            int maxStage = race.QualifyingSessions.Max(qs => qs.Stage);
            string rn    = race.Name.Length > 0 ? race.Name : $"R{race.Round}";

            // Track pole per qualifying session (fastest driver in each session).
            foreach (QualifyingSession qs in race.QualifyingSessions)
            {
                string sname = qs.SessionName.Length > 0 ? qs.SessionName : $"Q{qs.Stage}";
                LapData? poleLap = qs.LapData
                    .Where(ld => ld.FastestLapSeconds > 0)
                    .OrderBy(ld => ld.FastestLapSeconds)
                    .FirstOrDefault();
                if (poleLap != null)
                {
                    if (!driverPoles.TryGetValue(poleLap.DriverId, out var pl))
                    {
                        pl = new List<(string, string)>();
                        driverPoles[poleLap.DriverId] = pl;
                    }
                    pl.Add((rn, sname));
                }
            }

            // Build per-driver: explicit QualiSessionEliminated (max non-zero across all sessions),
            // participation-based highest stage, and best times per stage.
            var explicitElim    = new Dictionary<ByteString, int>();
            var highestStage    = new Dictionary<ByteString, int>();
            var bestTimeByStage = new Dictionary<ByteString, Dictionary<int, float>>();

            foreach (QualifyingSession qs in race.QualifyingSessions)
            {
                foreach (LapData ld in qs.LapData)
                {
                    // Explicit elimination: take the max non-zero value seen.
                    if (ld.QualiSessionEliminated > 0)
                    {
                        if (!explicitElim.TryGetValue(ld.DriverId, out int prev)
                            || ld.QualiSessionEliminated > prev)
                            explicitElim[ld.DriverId] = ld.QualiSessionEliminated;
                    }

                    // Participation.
                    if (!highestStage.TryGetValue(ld.DriverId, out int cur) || qs.Stage > cur)
                        highestStage[ld.DriverId] = qs.Stage;

                    if (ld.FastestLapSeconds > 0)
                    {
                        if (!bestTimeByStage.TryGetValue(ld.DriverId, out var sm))
                        {
                            sm = new Dictionary<int, float>();
                            bestTimeByStage[ld.DriverId] = sm;
                        }
                        if (!sm.TryGetValue(qs.Stage, out float prev) || ld.FastestLapSeconds < prev)
                            sm[qs.Stage] = ld.FastestLapSeconds;
                    }
                }
            }
            if (highestStage.Count == 0) continue;

            // Derive grid positions.
            // Order: not-eliminated first, then by descending elim session; within group by time asc.
            var ranked = highestStage
                .Select(kv =>
                {
                    int? elimSess = explicitElim.TryGetValue(kv.Key, out int ex)
                        ? ex                                              // explicit
                        : (kv.Value == maxStage ? null : (int?)kv.Value); // derived
                    // int.MaxValue keeps not-eliminated drivers above any elim value regardless of maxStage.
                    int sortStage = elimSess.HasValue ? elimSess.Value : int.MaxValue;
                    // Always look up time from the driver's actual highest participating stage,
                    // not the virtual elim number (which may not correspond to a real session).
                    float time = bestTimeByStage.TryGetValue(kv.Key, out var sm)
                                 && sm.TryGetValue(kv.Value, out float t) ? t : float.MaxValue;
                    return (id: kv.Key, sortStage, elimSess, time);
                })
                .OrderByDescending(x => x.sortStage)
                .ThenBy(x => x.time)
                .Select((x, i) => (x.id, pos: i + 1, x.elimSess))
                .ToList();

            foreach (var (id, pos, elimSess) in ranked)
            {
                if (!driverData.TryGetValue(id, out var list))
                {
                    list = new List<(int, string, int?)>();
                    driverData[id] = list;
                }
                list.Add((pos, rn, elimSess));
            }
        }

        if (driverData.Count == 0)
        {
            ImGui.Indent();
            ImGui.TextDisabled("No qualifying data in past races.");
            ImGui.Unindent();
            return;
        }

        var rows = driverData
            .Select(kv =>
            {
                Driver? d         = season.Drivers.FirstOrDefault(dr => dr.Id == kv.Key);
                Team?   team      = d != null ? season.Teams.FirstOrDefault(t => t.Id == d.CurrentTeamId) : null;
                var     poleList  = driverPoles.TryGetValue(kv.Key, out var pl) ? pl : new();
                int     poles     = poleList.Count;
                string  poleTip   = string.Join("\n", poleList.Select(p => $"{p.raceName} ({p.sessionName})"));
                var     bestEntry = kv.Value.MinBy(x => x.pos);
                int     best      = bestEntry.pos;
                string  bestRace  = bestEntry.raceName;
                float   avg       = (float)kv.Value.Average(x => x.pos);
                int     races     = kv.Value.Count;
                var     elimRaces = kv.Value.Where(x => x.elimSession.HasValue).ToList();
                string  avgElim   = elimRaces.Count > 0
                    ? $"{elimRaces.Average(x => x.elimSession!.Value):F1}" : "-";
                return (driver: d, team, poles, poleTip, best, bestRace, avg, avgElim, races);
            })
            .OrderBy(x => x.avg)
            .ToList();

        ImGui.Indent();
        if (ImGui.BeginTable("##qualiinfo", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Driver", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Poles",  ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Best",   ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Avg",    ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Elim",   ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Races",  ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            foreach (var (driver, team, poles, poleTip, best, bestRace, avg, avgElim, races) in rows)
            {
                ImGui.TableNextRow();

                // Driver cell with team color background.
                ImGui.TableSetColumnIndex(0);
                if (team != null && team.Color != 0)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg,
                        ImGui.ColorConvertFloat4ToU32(ColorUtils.ToVec4(team.Color)));
                    ImGui.PushStyleColor(ImGuiCol.Text, ColorUtils.ContrastingText(team.Color));
                }
                CenterText(driver != null ? DriverLabel(driver) : "(unknown)");
                if (team != null && team.Color != 0)
                    ImGui.PopStyleColor();

                ImGui.TableSetColumnIndex(1);
                CenterText($"{poles}");
                if (poles > 0 && ImGui.IsItemHovered())
                    ImGui.SetTooltip(poleTip);

                ImGui.TableSetColumnIndex(2);
                CenterText($"P{best}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(bestRace);

                ImGui.TableSetColumnIndex(3);
                CenterText($"{avg:F1}");

                ImGui.TableSetColumnIndex(4);
                CenterText(avgElim);

                ImGui.TableSetColumnIndex(5);
                CenterText($"{races}");
            }

            ImGui.EndTable();
        }
        ImGui.Unindent();
    }

    // ── Fastest Laps ──────────────────────────────────────────────────────────

    private static void DrawFastestLaps(Season season, List<Race> orderedRaces, int selectedIdx)
    {
        var driverFLCount      = new Dictionary<ByteString, int>();
        var manufacturerFLCount = new Dictionary<ByteString, int>();

        for (int i = 0; i < selectedIdx; i++)
        {
            Race race = orderedRaces[i];
            foreach (RaceResult rr in race.RaceResults)
            {
                RaceDriverResult? fl = rr.Results
                    .Where(r => r.FastestLapSeconds > 0)
                    .OrderBy(r => r.FastestLapSeconds)
                    .FirstOrDefault();

                if (fl == null) continue;

                driverFLCount.TryGetValue(fl.DriverId, out int dc);
                driverFLCount[fl.DriverId] = dc + 1;

                Team? team = season.Teams.FirstOrDefault(t => t.Id == fl.TeamId);
                if (team != null)
                {
                    Manufacturer? mfr = season.Manufacturers.FirstOrDefault(m => m.Id == team.ManufacturerId);
                    if (mfr != null)
                    {
                        manufacturerFLCount.TryGetValue(mfr.Id, out int mc);
                        manufacturerFLCount[mfr.Id] = mc + 1;
                    }
                }
            }
        }

        ImGui.Indent();

        ImGui.Text("By Driver");
        if (ImGui.BeginTable("##fldriver", 2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Driver", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("FLs",    ImGuiTableColumnFlags.WidthFixed, 35);
            ImGui.TableHeadersRow();

            foreach (var kv in driverFLCount.OrderByDescending(x => x.Value))
            {
                Driver? d = season.Drivers.FirstOrDefault(x => x.Id == kv.Key);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); CenterText(d != null ? DriverLabel(d) : "(unknown)");
                ImGui.TableSetColumnIndex(1); CenterText($"{kv.Value}");
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Text("By Manufacturer");
        if (ImGui.BeginTable("##flmfr", 2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Manufacturer", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("FLs",          ImGuiTableColumnFlags.WidthFixed, 35);
            ImGui.TableHeadersRow();

            foreach (var kv in manufacturerFLCount.OrderByDescending(x => x.Value))
            {
                Manufacturer? m = season.Manufacturers.FirstOrDefault(x => x.Id == kv.Key);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); CenterText(m != null && m.Name.Length > 0 ? m.Name : "(unknown)");
                ImGui.TableSetColumnIndex(1); CenterText($"{kv.Value}");
            }
            ImGui.EndTable();
        }

        ImGui.Unindent();
    }

    // ── Team Standings ────────────────────────────────────────────────────────

    private static void DrawTeamStandings(Season season, Race? prevRace)
    {
        if (prevRace == null)
        {
            ImGui.Indent();
            ImGui.TextDisabled("No prior race data.");
            ImGui.Unindent();
            return;
        }

        var teamPts = new Dictionary<ByteString, int>();
        foreach (Race race in season.Races.OrderBy(r => r.Round))
        {
            foreach (Driver d in season.Drivers)
            {
                int pts = CCUtils.GetPointsForDriverInRace(season, race, d);
                if (pts == 0) continue;

                ByteString teamId = ByteString.Empty;
                foreach (RaceResult rr in race.RaceResults)
                {
                    RaceDriverResult? rdr = rr.Results.FirstOrDefault(r => r.DriverId == d.Id);
                    if (rdr != null) { teamId = rdr.TeamId; break; }
                }
                if (teamId.IsEmpty) continue;

                teamPts.TryGetValue(teamId, out int cur);
                teamPts[teamId] = cur + pts;
            }

            if (race.Id == prevRace.Id) break;
        }

        var sorted = teamPts
            .Select(kv => (team: season.Teams.FirstOrDefault(t => t.Id == kv.Key), pts: kv.Value))
            .Where(x => x.team != null)
            .OrderByDescending(x => x.pts)
            .ToList();

        ImGui.Indent();
        if (ImGui.BeginTable("##teamstand", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Pos",  ImGuiTableColumnFlags.WidthFixed,  35);
            ImGui.TableSetupColumn("Team", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pts",  ImGuiTableColumnFlags.WidthFixed,  40);
            ImGui.TableHeadersRow();

            for (int i = 0; i < sorted.Count; i++)
            {
                var (team, pts) = sorted[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); CenterText($"P{i + 1}");

                ImGui.TableSetColumnIndex(1);
                if (team!.Color != 0)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg,
                        ImGui.ColorConvertFloat4ToU32(ColorUtils.ToVec4(team.Color)));
                    ImGui.PushStyleColor(ImGuiCol.Text, ColorUtils.ContrastingText(team.Color));
                }
                CenterText(team.Name.Length > 0 ? team.Name : "(unnamed)");
                if (team.Color != 0) ImGui.PopStyleColor();

                ImGui.TableSetColumnIndex(2); CenterText($"{pts}");
            }

            ImGui.EndTable();
        }
        ImGui.Unindent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Score a pick based on actual race results.
    // Sprint results (race_name contains "sprint") count at 0.5×; all others at 1.0×.
    // Returns 0 if the driver did not finish in the points in any result.
    private static float GetPickScoreFromResults(
        Season season, PickRules rules, int pickIndex, ByteString driverId,
        Race race, int champPos)
    {
        float total = 0f;
        foreach (RaceResult rr in race.RaceResults)
        {
            RaceDriverResult? rdr = rr.Results.FirstOrDefault(r => r.DriverId == driverId);
            if (rdr == null) continue;

            float pts = CCUtils.CalculatePointsForResult(season, rr, rdr);
            if (pts <= 0) continue;

            bool  isSprint = rr.RaceName.Contains("sprint", StringComparison.OrdinalIgnoreCase);
            float factor   = isSprint ? 0.5f : 1.0f;
            total += CalculatePickScore(rules, pickIndex, champPos) * factor;
        }
        return total;
    }

    private static List<(Driver driver, int champPos)> GetEligibleDriversWithPos(
        Season season, Race? prevRace, int positionCutoff)
    {
        if (prevRace == null)
            return season.Drivers.Select(d => (d, 0)).ToList();

        var ordered = season.Drivers
            .Select(d => (driver: d,
                pts: CCUtils.GetDriverChampionshipPointSnapshotForRace(season, prevRace, d)))
            .OrderByDescending(x => x.pts)
            .ToList();

        var result = new List<(Driver, int)>();
        for (int i = 0; i < ordered.Count; i++)
        {
            int pos = i + 1;
            if (pos > positionCutoff)
                result.Add((ordered[i].driver, pos));
        }
        return result;
    }

    private static float GetStandingsMultiplier(PickRules rules, int pos)
    {
        var ordered = rules.StandingsMultipliers.OrderBy(kv => kv.Key).ToList();
        if (ordered.Count == 0) return 1.0f;
        float mult = ordered[ordered.Count - 1].Value;
        foreach (var kv in ordered)
            if (pos <= kv.Key) { mult = kv.Value; break; }
        return mult;
    }

    private static float CalculatePickScore(PickRules rules, int pickIndex, int champPos)
    {
        if (pickIndex >= rules.BasePickScores.Count) return 0f;
        float baseScore = rules.BasePickScores[pickIndex];

        // R1 special case: no prior standings → multiplier is always 1.0
        if (champPos == 0) return baseScore;

        if (rules.StandingsMultipliers.Count == 0) return baseScore;

        var   ordered    = rules.StandingsMultipliers.OrderBy(kv => kv.Key).ToList();
        float multiplier = ordered[ordered.Count - 1].Value;

        foreach (var kv in ordered)
            if (champPos <= kv.Key) { multiplier = kv.Value; break; }

        return baseScore * multiplier;
    }

    private static void CenterText(string text)
    {
        float avail = ImGui.GetContentRegionAvail().X;
        float tw    = ImGui.CalcTextSize(text).X;
        if (avail > tw) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - tw) * 0.5f);
        ImGui.Text(text);
    }

    private static string DriverLabel(Driver d)
    {
        string num  = d.Number > 0     ? $"#{d.Number} " : "";
        string name = d.Name.Length > 0 ? d.Name : "(unnamed)";
        return $"{num}{name}";
    }

    private static string FormatLapTime(float seconds)
    {
        int   mins = (int)(seconds / 60);
        float secs = seconds % 60;
        return mins > 0 ? $"{mins}:{secs:00.000}" : $"{secs:F3}s";
    }
}