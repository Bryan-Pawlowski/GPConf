using System.Numerics;
using Google.Protobuf;
using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class LeagueEditor
{
    public static void Draw(GpConfApp app)
    {
        MainData data = app.GetMainData();
        ImGui.Begin("League Editor");

        League? toRemove = null;

        foreach (League league in data.Leagues)
        {
            ImGui.PushID(league.Id.ToBase64());

            float leftEdge  = ImGui.GetCursorPosX();
            float rightEdge = leftEdge + ImGui.GetContentRegionAvail().X;

            ImGui.SetNextItemAllowOverlap();
            bool open = ImGui.CollapsingHeader("##leaguehdr");

            float arrowWidth = ImGui.GetTreeNodeToLabelSpacing();
            ImGui.SameLine(leftEdge + arrowWidth);
            ImGui.SetNextItemWidth(rightEdge - leftEdge - arrowWidth - 70);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(1, 1, 1, 0.1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(1, 1, 1, 0.2f));
            string lname = league.LeagueName;
            if (ImGui.InputText("##leaguename", ref lname, 256))
                league.LeagueName = lname;
            ImGui.PopStyleColor(3);

            ImGui.SameLine(rightEdge - 58);
            if (ImGui.SmallButton("Remove"))
                toRemove = league;

            if (open)
            {
                ImGui.Indent();
                DrawPlayers(league);
                DrawGameSeasons(data, league);
                ImGui.Unindent();
            }

            ImGui.PopID();
        }

        if (toRemove != null)
            data.Leagues.Remove(toRemove);

        if (ImGui.Button("Add League##addleague"))
            data.Leagues.Add(new League { Id = CCUtils.CreateUniqueId(), LeagueName = "New League" });

        ImGui.Separator();
        if (ImGui.Button("Save##LeagueEditor")) app.Save();

        ImGui.End();
    }

    // ── Players ───────────────────────────────────────────────────────────────

    private static void DrawPlayers(League league)
    {
        if (!ImGui.CollapsingHeader("Players##leagueplayers")) return;

        Player? toRemove = null;

        ImGui.Indent();
        foreach (Player player in league.Players)
        {
            ImGui.PushID(player.Id.ToBase64());

            float rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;

            ImGui.SetNextItemWidth(rightEdge - ImGui.GetCursorPosX() - 92);
            string pname = player.PlayerName;
            if (ImGui.InputText("##pname", ref pname, 256))
                player.PlayerName = pname;

            ImGui.SameLine(rightEdge - 86);
            Vector3 col = ColorUtils.ToVec3(player.Color);
            if (ImGui.ColorEdit3("##pcolor", ref col, ImGuiColorEditFlags.NoInputs))
                player.Color = ColorUtils.Pack(col);

            ImGui.SameLine(rightEdge - 58);
            if (ImGui.SmallButton("Remove"))
                toRemove = player;

            ImGui.PopID();
        }
        ImGui.Unindent();

        if (toRemove != null)
            league.Players.Remove(toRemove);

        if (ImGui.Button("Add Player##addplayer"))
            league.Players.Add(new Player { Id = CCUtils.CreateUniqueId(), PlayerName = "New Player" });
    }

    // ── Game Seasons ──────────────────────────────────────────────────────────

    private static void DrawGameSeasons(MainData data, League league)
    {
        if (!ImGui.CollapsingHeader("Game Seasons##leaguegs")) return;

        GameSeason? toRemove = null;

        ImGui.Indent();
        foreach (GameSeason gs in league.Seasons)
        {
            ImGui.PushID(gs.Id.ToBase64());

            float leftEdge  = ImGui.GetCursorPosX();
            float rightEdge = leftEdge + ImGui.GetContentRegionAvail().X;

            ImGui.SetNextItemAllowOverlap();
            bool open = ImGui.CollapsingHeader("##gsheader");

            float arrowWidth = ImGui.GetTreeNodeToLabelSpacing();
            ImGui.SameLine(leftEdge + arrowWidth);
            ImGui.SetNextItemWidth(rightEdge - leftEdge - arrowWidth - 70);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(1, 1, 1, 0.1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(1, 1, 1, 0.2f));

            Season? linked  = data.Seasons.FirstOrDefault(s => s.Id == gs.SeasonId);
            string  gsLabel = linked != null
                ? (linked.Name.Length > 0 ? linked.Name : $"{linked.Year}")
                : "(no season linked)";

            if (ImGui.BeginCombo("##gsseason", gsLabel))
            {
                if (ImGui.Selectable("None", gs.SeasonId.IsEmpty))
                {
                    gs.SeasonId = ByteString.Empty;
                    gs.Races.Clear();
                }
                foreach (Season s in data.Seasons)
                {
                    string slabel = s.Name.Length > 0 ? s.Name : $"{s.Year}";
                    if (ImGui.Selectable(slabel, s.Id == gs.SeasonId))
                    {
                        gs.SeasonId = s.Id;
                        gs.Races.Clear();
                        foreach (Race r in s.Races.OrderBy(r => r.Round))
                            gs.Races.Add(new GameRace { RaceId = r.Id });
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.PopStyleColor(3);

            ImGui.SameLine(rightEdge - 58);
            if (ImGui.SmallButton("Remove"))
                toRemove = gs;

            if (open)
            {
                ImGui.Indent();
                DrawParticipatingPlayers(league, gs);
                DrawPickRules(gs);
                DrawGameRacePreview(data, gs);
                ImGui.Unindent();
            }

            ImGui.PopID();
        }
        ImGui.Unindent();

        if (toRemove != null)
            league.Seasons.Remove(toRemove);

        if (ImGui.Button("Add Game Season##addgs"))
            league.Seasons.Add(new GameSeason { Id = CCUtils.CreateUniqueId() });
    }

    // ── Participating Players ─────────────────────────────────────────────────

    private static void DrawParticipatingPlayers(League league, GameSeason gs)
    {
        if (!ImGui.CollapsingHeader("Participating Players##gspp")) return;

        Player? toRemove = null;

        ImGui.Indent();
        if (ImGui.BeginTable("##pplist", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Player",     ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##ppremove", ImGuiTableColumnFlags.WidthFixed, 60);

            foreach (Player p in gs.ParticipatingPlayers)
            {
                ImGui.PushID(p.Id.ToBase64());
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                if (p.Color != 0)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg,
                        ImGui.ColorConvertFloat4ToU32(ColorUtils.ToVec4(p.Color)));
                    ImGui.PushStyleColor(ImGuiCol.Text, ColorUtils.ContrastingText(p.Color));
                }
                ImGui.Text(p.PlayerName.Length > 0 ? p.PlayerName : "(unnamed)");
                if (p.Color != 0)
                    ImGui.PopStyleColor();

                ImGui.TableSetColumnIndex(1);
                if (ImGui.SmallButton("Remove"))
                    toRemove = p;

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
        ImGui.Unindent();

        if (toRemove != null)
            gs.ParticipatingPlayers.Remove(toRemove);

        // Only show league players not already participating.
        var takenIds  = gs.ParticipatingPlayers.Select(p => p.Id).ToHashSet();
        var available = league.Players
            .Where(p => !takenIds.Contains(p.Id))
            .OrderBy(p => p.PlayerName)
            .ToList();

        if (available.Count == 0) return;

        if (ImGui.BeginCombo("##addgsp", "Add player..."))
        {
            foreach (Player p in available)
            {
                string label  = p.PlayerName.Length > 0 ? p.PlayerName : "(unnamed)";
                int    pushed = 0;
                if (p.Color != 0)
                {
                    Vector4 c = ColorUtils.ToVec4(p.Color);
                    ImGui.PushStyleColor(ImGuiCol.Header,        c);
                    ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Brighten(c, 0.1f));
                    ImGui.PushStyleColor(ImGuiCol.Text,          ColorUtils.ContrastingText(p.Color));
                    pushed = 3;
                }
                if (ImGui.Selectable(label))
                    gs.ParticipatingPlayers.Add(p);
                ImGui.PopStyleColor(pushed);
            }
            ImGui.EndCombo();
        }
    }

    // ── Pick Rules ────────────────────────────────────────────────────────────

    // Tracks pending "add multiplier" inputs per GameSeason (keyed by ID).
    private static readonly Dictionary<string, (int Pos, float Mult)> _pendingMult = new();

    private static void DrawPickRules(GameSeason gs)
    {
        if (!ImGui.CollapsingHeader("Pick Rules##gspickrules")) return;

        gs.PickRules ??= new PickRules();
        PickRules rules = gs.PickRules;

        ImGui.Indent();

        int numPicks = rules.NumPicks;
        if (ImGui.InputInt("Num Picks##numPicks", ref numPicks, 1))
            rules.NumPicks = Math.Max(0, numPicks);

        int cutoff = rules.PositionCutoff;
        if (ImGui.InputInt("Position Cutoff##cutoff", ref cutoff, 1))
            rules.PositionCutoff = Math.Max(0, cutoff);

        // Base Pick Scores
        if (ImGui.CollapsingHeader("Base Pick Scores##bps"))
        {
            int removeIdx = -1;

            ImGui.Indent();
            for (int i = 0; i < rules.BasePickScores.Count; i++)
            {
                ImGui.PushID(i);
                float score = rules.BasePickScores[i];
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 70);
                if (ImGui.InputFloat($"Pick {i + 1}##bpsval", ref score, 0f, 0f))
                    rules.BasePickScores[i] = score;
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove##rmbps"))
                    removeIdx = i;
                ImGui.PopID();
            }
            ImGui.Unindent();

            if (removeIdx >= 0)
                rules.BasePickScores.RemoveAt(removeIdx);

            if (ImGui.Button("Add Score##addbps"))
                rules.BasePickScores.Add(0f);
        }

        // Standings Multipliers
        if (ImGui.CollapsingHeader("Standings Multipliers##sm"))
        {
            int removeKey = int.MinValue;

            // Snapshot entries sorted ascending so ranges are computed correctly.
            var entries = rules.StandingsMultipliers.OrderBy(kv => kv.Key).ToList();

            ImGui.Indent();
            int prevUpper = rules.PositionCutoff;
            foreach (var (key, val) in entries)
            {
                ImGui.PushID(key);

                int   rangeStart = prevUpper + 1;
                string rangeLabel = rangeStart >= key ? $"P{key}" : $"P{rangeStart} - P{key}";
                ImGui.Text(rangeLabel);
                ImGui.SameLine();
                float v = val;
                ImGui.SetNextItemWidth(80);
                if (ImGui.InputFloat("##smval", ref v, 0f, 0f))
                    rules.StandingsMultipliers[key] = v;
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove##rmsm"))
                    removeKey = key;

                prevUpper = key;
                ImGui.PopID();
            }
            ImGui.Unindent();

            if (removeKey != int.MinValue)
                rules.StandingsMultipliers.Remove(removeKey);

            // Add new entry — per-season pending state.
            string gsKey = gs.Id.ToBase64();
            if (!_pendingMult.TryGetValue(gsKey, out var pending))
                pending = (0, 1.0f);

            int   newPos  = pending.Pos;
            float newMult = pending.Mult;
            ImGui.SetNextItemWidth(60); ImGui.InputInt("##newsmpos",  ref newPos,  0);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80); ImGui.InputFloat("##newsmval", ref newMult, 0f, 0f);
            ImGui.SameLine();
            if (ImGui.Button("Add##addsm"))
            {
                rules.StandingsMultipliers[newPos] = newMult;
                _pendingMult.Remove(gsKey);
            }
            else
            {
                _pendingMult[gsKey] = (newPos, newMult);
            }
        }

        ImGui.Unindent();
    }

    // ── Game Race Preview ─────────────────────────────────────────────────────

    private static void DrawGameRacePreview(MainData data, GameSeason gs)
    {
        if (!ImGui.CollapsingHeader("Races##gsraces")) return;

        Season? season = data.Seasons.FirstOrDefault(s => s.Id == gs.SeasonId);

        if (season == null || season.Races.Count == 0)
        {
            ImGui.Indent();
            ImGui.TextDisabled("Link a season to populate races.");
            ImGui.Unindent();
            return;
        }

        // Sync gs.Races: ensure every season race has a GameRace entry (preserves picks).
        var existingRaceIds = gs.Races.Select(gr => gr.RaceId).ToHashSet();
        foreach (Race r in season.Races)
            if (!existingRaceIds.Contains(r.Id))
                gs.Races.Add(new GameRace { RaceId = r.Id });

        ImGui.Indent();
        if (ImGui.BeginTable("##gsracelist", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Rnd",     ImGuiTableColumnFlags.WidthFixed,  40);
            ImGui.TableSetupColumn("Name",    ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Circuit", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (Race r in season.Races.OrderBy(r => r.Round))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Text($"{r.Round}");
                ImGui.TableSetColumnIndex(1); ImGui.Text(r.Name.Length    > 0 ? r.Name    : "(unnamed)");
                ImGui.TableSetColumnIndex(2); ImGui.Text(r.Circuit.Length > 0 ? r.Circuit : "-");
            }

            ImGui.EndTable();
        }
        ImGui.Unindent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Vector4 Brighten(Vector4 c, float amount) => new(
        Math.Min(1f, c.X + amount),
        Math.Min(1f, c.Y + amount),
        Math.Min(1f, c.Z + amount),
        c.W);
}
