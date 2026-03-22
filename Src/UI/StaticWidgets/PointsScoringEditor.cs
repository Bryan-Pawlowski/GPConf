using System.Numerics;
using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class PointsScoringEditor
{
    public static void Draw(Season season)
    {
        PointsScoringRules? toRemove = null;

        ImGui.Indent();
        foreach (PointsScoringRules rules in season.Rules)
        {
            ImGui.PushID(rules.Id.ToBase64());

            float leftEdge  = ImGui.GetCursorPosX();
            float rightEdge = leftEdge + ImGui.GetContentRegionAvail().X;

            ImGui.SetNextItemAllowOverlap();
            bool open = ImGui.CollapsingHeader("##header");

            // Editable name overlaid on header row.
            float arrowWidth = ImGui.GetTreeNodeToLabelSpacing();
            ImGui.SameLine(leftEdge + arrowWidth);
            ImGui.SetNextItemWidth(rightEdge - leftEdge - arrowWidth - 70);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(1, 1, 1, 0.1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(1, 1, 1, 0.2f));
            string name = rules.Name;
            if (ImGui.InputText("##rulesname", ref name, 256))
                rules.Name = name;
            ImGui.PopStyleColor(3);

            ImGui.SameLine(rightEdge - 58);
            if (ImGui.SmallButton("Remove"))
                toRemove = rules;

            if (open)
            {
                ImGui.Indent();

                int removeScoreIndex = -1;

                if (ImGui.BeginTable("##scores", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                {
                    ImGui.TableSetupColumn("Position",      ImGuiTableColumnFlags.WidthFixed,   70);
                    ImGui.TableSetupColumn("Points",        ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("##scoreremove", ImGuiTableColumnFlags.WidthFixed,   60);
                    ImGui.TableHeadersRow();

                    for (int j = 0; j < rules.Score.Count; j++)
                    {
                        ImGui.PushID(j);
                        ImGui.TableNextRow();

                        ImGui.TableSetColumnIndex(0);
                        ImGui.Text($"P{j + 1}");

                        ImGui.TableSetColumnIndex(1);
                        int score = rules.Score[j];
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputInt("##score", ref score, 0))
                            rules.Score[j] = score;

                        ImGui.TableSetColumnIndex(2);
                        if (ImGui.SmallButton("Remove"))
                            removeScoreIndex = j;

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }

                if (removeScoreIndex >= 0)
                    rules.Score.RemoveAt(removeScoreIndex);

                if (ImGui.Button("Add Position##pos"))
                    rules.Score.Add(0);

                ImGui.Unindent();
            }

            ImGui.PopID();
        }
        ImGui.Unindent();

        if (toRemove != null)
            season.Rules.Remove(toRemove);

        if (ImGui.Button("Add Ruleset##rules"))
            season.Rules.Add(new PointsScoringRules { Id = CCUtils.CreateUniqueId(), Name = "New Ruleset" });
    }
}
