using System.Numerics;
using Google.Protobuf;
using Google.Protobuf.Collections;
using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class RaceUpdater
{
    public static void Draw(Season season, Race race)
    {
        ImGui.Indent();

        if (ImGui.CollapsingHeader("Practice##RaceUpdater"))
            DrawPractice(season, race);

        if (ImGui.CollapsingHeader("Qualifying##RaceUpdater"))
            DrawQualifying(season, race);

        if (ImGui.CollapsingHeader("Race Results##RaceUpdater"))
            DrawRaceResults(season, race);

        ImGui.Unindent();
    }

    // ── Practice ─────────────────────────────────────────────────────────────

    private static void DrawPractice(Season season, Race race)
    {
        PracticeSession? toRemove = null;

        ImGui.Indent();
        int i = 0;
        foreach (PracticeSession ps in race.Practices.OrderBy(p => p.SessionNumber))
        {
            ImGui.PushID(i++);

            float leftEdge  = ImGui.GetCursorPosX();
            float rightEdge = leftEdge + ImGui.GetContentRegionAvail().X;

            ImGui.SetNextItemAllowOverlap();
            bool open = ImGui.CollapsingHeader($"FP{ps.SessionNumber}##psheader");

            ImGui.SameLine(rightEdge - 58);
            if (ImGui.SmallButton("Remove"))
                toRemove = ps;

            if (open)
            {
                ImGui.Indent();
                DrawLapTable(season, ps.LapData);
                ImGui.Unindent();
            }

            ImGui.PopID();
        }

        ImGui.Unindent();

        if (toRemove != null)
            race.Practices.Remove(toRemove);

        if (ImGui.Button("Add Practice Session##addps"))
        {
            int next = race.Practices.Count == 0 ? 1 : race.Practices.Max(p => p.SessionNumber) + 1;
            var session = new PracticeSession { SessionNumber = next };
            foreach (Driver d in season.Drivers.Where(d => !d.CurrentTeamId.IsEmpty))
                session.LapData.Add(new LapData { DriverId = d.Id });
            race.Practices.Add(session);
        }
    }

    // ── Qualifying ────────────────────────────────────────────────────────────

    private static void DrawQualifying(Season season, Race race)
    {
        QualifyingSession? toRemove = null;

        ImGui.Indent();
        int i = 0;
        foreach (QualifyingSession qs in race.QualifyingSessions)
        {
            ImGui.PushID(i++);

            float leftEdge  = ImGui.GetCursorPosX();
            float rightEdge = leftEdge + ImGui.GetContentRegionAvail().X;

            ImGui.SetNextItemAllowOverlap();
            bool open = ImGui.CollapsingHeader("##qsheader");

            float arrowWidth = ImGui.GetTreeNodeToLabelSpacing();
            ImGui.SameLine(leftEdge + arrowWidth);
            ImGui.SetNextItemWidth(rightEdge - leftEdge - arrowWidth - 70);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(1, 1, 1, 0.1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(1, 1, 1, 0.2f));
            string sname = qs.SessionName;
            if (ImGui.InputText("##qsname", ref sname, 64))
                qs.SessionName = sname;
            ImGui.PopStyleColor(3);

            ImGui.SameLine(rightEdge - 58);
            if (ImGui.SmallButton("Remove"))
                toRemove = qs;

            if (open)
            {
                ImGui.Indent();
                DrawLapTable(season, qs.LapData, showEliminated: true);
                ImGui.Unindent();
            }

            ImGui.PopID();
        }

        ImGui.Unindent();

        if (toRemove != null)
            race.QualifyingSessions.Remove(toRemove);

        if (ImGui.Button("Add Q Session##addqs"))
        {
            int stage = race.QualifyingSessions.Count == 0 ? 1
                : race.QualifyingSessions.Max(q => q.Stage) + 1;
            var session = new QualifyingSession { Stage = stage, SessionName = $"Q{stage}" };
            foreach (Driver d in season.Drivers.Where(d => !d.CurrentTeamId.IsEmpty))
                session.LapData.Add(new LapData { DriverId = d.Id });
            race.QualifyingSessions.Add(session);
        }
    }

    // ── Race Results ──────────────────────────────────────────────────────────

    private static void DrawRaceResults(Season season, Race race)
    {
        RaceResult? toRemove = null;

        ImGui.Indent();
        int i = 0;
        foreach (RaceResult rr in race.RaceResults)
        {
            ImGui.PushID(i++);

            float leftEdge  = ImGui.GetCursorPosX();
            float rightEdge = leftEdge + ImGui.GetContentRegionAvail().X;

            ImGui.SetNextItemAllowOverlap();
            bool open = ImGui.CollapsingHeader("##rrheader");

            float arrowWidth = ImGui.GetTreeNodeToLabelSpacing();
            ImGui.SameLine(leftEdge + arrowWidth);
            ImGui.SetNextItemWidth(rightEdge - leftEdge - arrowWidth - 70);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(1, 1, 1, 0.1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(1, 1, 1, 0.2f));
            string rname = rr.RaceName;
            if (ImGui.InputText("##rrname", ref rname, 64))
                rr.RaceName = rname;
            ImGui.PopStyleColor(3);

            ImGui.SameLine(rightEdge - 58);
            if (ImGui.SmallButton("Remove"))
                toRemove = rr;

            if (open)
            {
                ImGui.Indent();

                // Points rules picker.
                PointsScoringRules? activeRules = season.Rules.FirstOrDefault(r => r.Id == rr.PointRulesId);
                string rulesLabel = activeRules?.Name.Length > 0 ? activeRules.Name : "None";
                if (ImGui.BeginCombo("Points Rules##rr", rulesLabel))
                {
                    if (ImGui.Selectable("None", rr.PointRulesId.IsEmpty))
                        rr.PointRulesId = ByteString.Empty;
                    foreach (PointsScoringRules r in season.Rules)
                    {
                        string rlbl = r.Name.Length > 0 ? r.Name : "(unnamed)";
                        if (ImGui.Selectable(rlbl, r.Id == rr.PointRulesId))
                            rr.PointRulesId = r.Id;
                    }
                    ImGui.EndCombo();
                }

                DrawResultsTable(season, rr);
                ImGui.Unindent();
            }

            ImGui.PopID();
        }

        ImGui.Unindent();

        if (toRemove != null)
            race.RaceResults.Remove(toRemove);

        if (ImGui.Button("Add Race Result##addrr"))
        {
            var result = new RaceResult { RaceName = "Race" };
            foreach (Driver d in season.Drivers.Where(d => !d.CurrentTeamId.IsEmpty))
                result.Results.Add(new RaceDriverResult { DriverId = d.Id, TeamId = d.CurrentTeamId });
            race.RaceResults.Add(result);
        }
    }

    // ── Lap data table (Practice + Qualifying) ────────────────────────────────

    private static void DrawLapTable(Season season, RepeatedField<LapData> lapData, bool showEliminated = false)
    {
        LapData? toRemove = null;

        int colCount = showEliminated ? 5 : 4;
        if (ImGui.BeginTable("##laps", colCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Driver",      ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Fastest",     ImGuiTableColumnFlags.None,  85);
            ImGui.TableSetupColumn("Avg",         ImGuiTableColumnFlags.None,  85);
            if (showEliminated)
                ImGui.TableSetupColumn("Elim",    ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("##lapremove", ImGuiTableColumnFlags.WidthFixed,  60);
            ImGui.TableHeadersRow();

            // For qualifying: Elim=0 (not eliminated) → top group, then descending Elim number,
            // then by time within each group (no-time sinks to bottom of group).
            // For practice: sort by time only.
            IEnumerable<LapData> sorted = showEliminated
                ? lapData
                    .OrderByDescending(ld => ld.QualiSessionEliminated == 0 ? int.MaxValue : ld.QualiSessionEliminated)
                    .ThenBy(ld => ld.FastestLapSeconds <= 0)
                    .ThenBy(ld => ld.FastestLapSeconds > 0 ? ld.FastestLapSeconds : float.MaxValue)
                : lapData
                    .OrderBy(ld => ld.FastestLapSeconds <= 0)
                    .ThenBy(ld => ld.FastestLapSeconds > 0 ? ld.FastestLapSeconds : float.MaxValue);

            foreach (LapData ld in sorted)
            {
                ImGui.PushID(ld.DriverId.ToBase64());
                ImGui.TableNextRow();

                Driver? drv  = season.Drivers.FirstOrDefault(d => d.Id == ld.DriverId);
                Team?   team = drv != null ? season.Teams.FirstOrDefault(t => t.Id == drv.CurrentTeamId) : null;

                ImGui.TableSetColumnIndex(0);
                int drvPush0 = SetDriverCellBg(team);
                ImGui.Text(DriverLabel(drv));
                ImGui.PopStyleColor(drvPush0);

                ImGui.TableSetColumnIndex(1);
                float fastest = ld.FastestLapSeconds;
                ImGui.SetNextItemWidth(-1);
                DrawLapTimeInput("##fl", ref fastest);
                ld.FastestLapSeconds = fastest;

                ImGui.TableSetColumnIndex(2);
                float avg = ld.AverageLapSeconds;
                ImGui.SetNextItemWidth(-1);
                DrawLapTimeInput("##avg", ref avg);
                ld.AverageLapSeconds = avg;

                if (showEliminated)
                {
                    ImGui.TableSetColumnIndex(3);
                    int elim = ld.QualiSessionEliminated;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputInt("##elim", ref elim, 0))
                        ld.QualiSessionEliminated = Math.Max(0, elim);
                }

                ImGui.TableSetColumnIndex(showEliminated ? 4 : 3);
                if (ImGui.SmallButton("Remove"))
                    toRemove = ld;

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (toRemove != null)
            lapData.Remove(toRemove);

        DrawAddDriverCombo(season, lapData.Select(ld => ld.DriverId),
            d => lapData.Add(new LapData { DriverId = d.Id }));
    }

    // ── Race results table ────────────────────────────────────────────────────

    private static readonly string[] StatusLabels = { "-", "Finished", "DNF", "DNS", "DSQ" };

    // Pre-compute row highlight colors once.
    private static uint ToRowColor(float r, float g, float b, float a)
        => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    private static void DrawResultsTable(Season season, RaceResult rr)
    {
        // Build the sorted order and assign calculated positions.
        var ordered = CCUtils.GenerateRaceOrder(rr);
        for (int p = 0; p < ordered.Count; p++)
            ordered[p].Position = p + 1;

        PointsScoringRules? rules = season.Rules.FirstOrDefault(r => r.Id == rr.PointRulesId);
        int scoringPositions = rules?.Score.Count ?? 0;

        uint colorP1     = ToRowColor(0.45f, 0.05f, 0.75f, 0.85f); // purple
        uint colorPoints = ToRowColor(0.10f, 0.50f, 0.10f, 0.60f); // green

        RaceDriverResult? toRemove = null;

        if (ImGui.BeginTable("##results", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Pos",          ImGuiTableColumnFlags.WidthFixed,   40);
            ImGui.TableSetupColumn("Driver",       ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Team",         ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pts",          ImGuiTableColumnFlags.WidthFixed,   45);
            ImGui.TableSetupColumn("Race Time");
            ImGui.TableSetupColumn("FL");
            ImGui.TableSetupColumn("Laps",         ImGuiTableColumnFlags.WidthFixed,   45);
            ImGui.TableSetupColumn("Status");
            ImGui.TableSetupColumn("##resremove",  ImGuiTableColumnFlags.WidthFixed,   60);
            ImGui.TableHeadersRow();

            foreach (RaceDriverResult res in ordered)
            {
                ImGui.PushID(res.DriverId.ToBase64());
                ImGui.TableNextRow();

                // Row background: purple for P1, green for other scoring positions.
                if (res.Position == 1)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, colorP1);
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, colorP1);
                }
                else if (res.Position > 1 && res.Position <= scoringPositions)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, colorPoints);
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, colorPoints);
                }

                // Pos (calculated, read-only).
                ImGui.TableSetColumnIndex(0);
                ImGui.Text(res.Position > 0 ? $"P{res.Position}" : "—");

                // Driver colored by race team.
                Driver? drv = season.Drivers.FirstOrDefault(d => d.Id == res.DriverId);
                ByteString effectiveTeamId = !res.TeamId.IsEmpty ? res.TeamId
                    : drv?.CurrentTeamId ?? ByteString.Empty;
                Team? team = season.Teams.FirstOrDefault(t => t.Id == effectiveTeamId);

                ImGui.TableSetColumnIndex(1);
                int drvPush1 = SetDriverCellBg(team);
                ImGui.Text(DriverLabel(drv));
                ImGui.PopStyleColor(drvPush1);

                // Per-race team override.
                ImGui.TableSetColumnIndex(2);
                bool teamPinned = !res.TeamId.IsEmpty;
                // `team` is already resolved to the effective team (pinned override or driver fallback).
                string teamLabel = team?.Name.Length > 0 ? team.Name : "-";
                if (!teamPinned) teamLabel += " *";
                ImGui.SetNextItemWidth(-1);
                if (ImGui.BeginCombo("##teamoverride", teamLabel))
                {
                    foreach (Team tm in season.Teams.OrderBy(t => t.Name))
                    {
                        string tlbl = tm.Name.Length > 0 ? tm.Name : "(unnamed)";
                        if (ImGui.Selectable(tlbl, tm.Id == res.TeamId))
                            res.TeamId = tm.Id;
                    }
                    ImGui.EndCombo();
                }
                if (!teamPinned && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Team not pinned — currently inherits from driver's registered team.\nSelect a team to lock it to this result.");

                // Points (calculated, read-only).
                ImGui.TableSetColumnIndex(3);
                res.Points = CCUtils.CalculatePointsForResult(season, rr, res);
                int points = (int)res.Points;
                string ptsText = points > 0 ? $"{points}" : "-";
                float ptsAvail = ImGui.GetContentRegionAvail().X;
                float ptsWidth = ImGui.CalcTextSize(ptsText).X;
                if (ptsAvail > ptsWidth)
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ptsAvail - ptsWidth) * 0.5f);
                ImGui.Text(ptsText);

                ImGui.TableSetColumnIndex(4);
                float raceTime = res.RaceTime;
                ImGui.SetNextItemWidth(-1);
                DrawRaceTimeInput("##racetime", ref raceTime);
                res.RaceTime = raceTime;

                ImGui.TableSetColumnIndex(5);
                float fl = res.FastestLapSeconds;
                ImGui.SetNextItemWidth(-1);
                DrawLapTimeInput("##fl", ref fl);
                res.FastestLapSeconds = fl;

                ImGui.TableSetColumnIndex(6);
                int laps = res.LapsCompleted;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##laps", ref laps, 0))
                    res.LapsCompleted = laps;

                ImGui.TableSetColumnIndex(7);
                int statusIdx = (int)res.Status;
                string statusLabel = statusIdx < StatusLabels.Length ? StatusLabels[statusIdx] : "?";
                ImGui.SetNextItemWidth(-1);
                if (ImGui.BeginCombo("##status", statusLabel))
                {
                    for (int s = 0; s < StatusLabels.Length; s++)
                        if (ImGui.Selectable(StatusLabels[s], statusIdx == s))
                            res.Status = (FinishStatus)s;
                    ImGui.EndCombo();
                }

                ImGui.TableSetColumnIndex(8);
                if (ImGui.SmallButton("Remove"))
                    toRemove = res;

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (toRemove != null)
            rr.Results.Remove(toRemove);

        DrawAddDriverCombo(season, rr.Results.Select(r => r.DriverId),
            d => rr.Results.Add(new RaceDriverResult
            {
                DriverId = d.Id,
                TeamId   = d.CurrentTeamId
            }));
    }

    // ── Add-driver combo ──────────────────────────────────────────────────────

    private static void DrawAddDriverCombo(Season season, IEnumerable<ByteString> existing, Action<Driver> onAdd)
    {
        var taken     = existing.ToHashSet();
        var available = season.Drivers.Where(d => !taken.Contains(d.Id)).OrderBy(d => d.Name).ToList();
        if (available.Count == 0) return;

        if (ImGui.BeginCombo("##adddriver", "Add driver..."))
        {
            foreach (Driver d in available)
            {
                Team? t = season.Teams.FirstOrDefault(tm => tm.Id == d.CurrentTeamId);
                int pushed = 0;
                if (t != null && t.Color != 0)
                {
                    Vector4 c = ColorUtils.ToVec4(t.Color);
                    ImGui.PushStyleColor(ImGuiCol.Header,        c);
                    ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Brighten(c, 0.1f));
                    ImGui.PushStyleColor(ImGuiCol.Text,          ColorUtils.ContrastingText(t.Color));
                    pushed = 3;
                }
                if (ImGui.Selectable(DriverLabel(d)))
                    onAdd(d);
                ImGui.PopStyleColor(pushed);
            }
            ImGui.EndCombo();
        }
    }

    // ── Time input widgets ────────────────────────────────────────────────────

    // Maps ImGui item ID → last-returned text buffer, so we don't overwrite
    // the user's in-progress text with a reformatted value every frame.
    private static readonly Dictionary<uint, string> _timeBuffers = new();

    private static void DrawLapTimeInput(string label, ref float seconds)
        => DrawTimeInput(label, ref seconds, isRace: false);

    private static void DrawRaceTimeInput(string label, ref float seconds)
        => DrawTimeInput(label, ref seconds, isRace: true);

    private static void DrawTimeInput(string label, ref float seconds, bool isRace)
    {
        uint id = ImGui.GetID(label);
        string text = _timeBuffers.TryGetValue(id, out var buf)
            ? buf
            : (isRace ? FormatRaceTime(seconds) : FormatLapTime(seconds));

        ImGui.InputText(label, ref text, 16);

        if (ImGui.IsItemActive())
            _timeBuffers[id] = text;   // preserve the user's typed text
        else if (_timeBuffers.Remove(id))
            seconds = isRace ? ParseRaceTime(text) : ParseLapTime(text);
    }

    // ── Time formatting ───────────────────────────────────────────────────────

    // Formats a lap time (seconds) as "M:SS.sss". Returns "" for 0.
    private static string FormatLapTime(float seconds)
    {
        if (seconds <= 0) return "";
        int totalMs = (int)Math.Round(seconds * 1000);
        int ms = totalMs % 1000;
        int s  = (totalMs / 1000) % 60;
        int m  = totalMs / 60000;
        return $"{m}:{s:D2}.{ms:D3}";
    }

    // Parses "M:SS.sss", "M'SS.sss", "M:SS" or bare seconds string. Returns 0 on failure.
    private static float ParseLapTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        try
        {
            // Accept M'SS.sss (apostrophe as minute/second separator)
            int apos = text.IndexOf('\'');
            if (apos >= 0)
            {
                int m     = int.Parse(text[..apos]);
                string sr = text[(apos + 1)..];
                int dot   = sr.IndexOf('.');
                if (dot < 0) return m * 60 + int.Parse(sr);
                int s = int.Parse(sr[..dot]);
                string ms = sr[(dot + 1)..].PadRight(3, '0')[..3];
                return m * 60 + s + int.Parse(ms) / 1000f;
            }

            int colon = text.IndexOf(':');
            if (colon < 0)
                return float.TryParse(text, out float f) ? f : 0;
            int min    = int.Parse(text[..colon]);
            string rest = text[(colon + 1)..];
            int dot2   = rest.IndexOf('.');
            if (dot2 < 0)
                return min * 60 + int.Parse(rest);
            int sec    = int.Parse(rest[..dot2]);
            string msPart = rest[(dot2 + 1)..].PadRight(3, '0')[..3];
            return min * 60 + sec + int.Parse(msPart) / 1000f;
        }
        catch { return 0; }
    }

    // Formats a race time (seconds) as "H:MM:SS.sss". Returns "" for 0.
    private static string FormatRaceTime(float seconds)
    {
        if (seconds <= 0) return "";
        int totalMs = (int)Math.Round(seconds * 1000);
        int ms = totalMs % 1000;
        int s  = (totalMs / 1000) % 60;
        int m  = (totalMs / 60000) % 60;
        int h  = totalMs / 3600000;
        return $"{h}:{m:D2}:{s:D2}.{ms:D3}";
    }

    // Parses "H:MM:SS.sss" or "H:MM'SS.sss". Falls back to ParseLapTime for single-colon input.
    private static float ParseRaceTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        try
        {
            // Accept H:MM'SS.sss (apostrophe as minute/second separator)
            int apos = text.IndexOf('\'');
            if (apos >= 0)
            {
                int colon = text.IndexOf(':');
                if (colon >= 0 && colon < apos)
                {
                    int h     = int.Parse(text[..colon]);
                    int m     = int.Parse(text[(colon + 1)..apos]);
                    string sr = text[(apos + 1)..];
                    int dot   = sr.IndexOf('.');
                    if (dot < 0) return h * 3600 + m * 60 + int.Parse(sr);
                    int s = int.Parse(sr[..dot]);
                    string ms = sr[(dot + 1)..].PadRight(3, '0')[..3];
                    return h * 3600 + m * 60 + s + int.Parse(ms) / 1000f;
                }
                return ParseLapTime(text); // no colon before apostrophe → lap-time style
            }

            int first = text.IndexOf(':');
            int last  = text.LastIndexOf(':');
            if (first == last)
                return ParseLapTime(text);  // only one colon → treat as lap time
            int hh = int.Parse(text[..first]);
            int mm = int.Parse(text[(first + 1)..last]);
            string ssPart = text[(last + 1)..];
            int dot2 = ssPart.IndexOf('.');
            if (dot2 < 0)
                return hh * 3600 + mm * 60 + int.Parse(ssPart);
            int ss = int.Parse(ssPart[..dot2]);
            string msPart = ssPart[(dot2 + 1)..].PadRight(3, '0')[..3];
            return hh * 3600 + mm * 60 + ss + int.Parse(msPart) / 1000f;
        }
        catch { return 0; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Sets cell background and pushes a contrasting text color. Returns push count for PopStyleColor.
    private static int SetDriverCellBg(Team? team)
    {
        if (team == null || team.Color == 0) return 0;
        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg,
            ImGui.ColorConvertFloat4ToU32(ColorUtils.ToVec4(team.Color)));
        ImGui.PushStyleColor(ImGuiCol.Text, ColorUtils.ContrastingText(team.Color));
        return 1;
    }

    private static string DriverLabel(Driver? d)
    {
        if (d == null) return "(unknown)";
        string num  = d.Number > 0     ? $"#{d.Number} " : "";
        string name = d.Name.Length > 0 ? d.Name : "(unnamed)";
        return $"{num}{name}";
    }

    private static Vector4 Brighten(Vector4 c, float amount) => new(
        Math.Min(1f, c.X + amount),
        Math.Min(1f, c.Y + amount),
        Math.Min(1f, c.Z + amount),
        c.W);
}