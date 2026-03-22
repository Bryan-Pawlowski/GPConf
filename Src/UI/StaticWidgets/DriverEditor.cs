using System.Numerics;
using Google.Protobuf;
using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class DriverEditor
{
    public static void Draw(Season season)
    {
        int removeIndex = -1;

        ImGui.Indent();
        if (ImGui.BeginTable("##drivers", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name",        ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Number",      ImGuiTableColumnFlags.WidthFixed,   80);
            ImGui.TableSetupColumn("Nationality", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Team",        ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##remove",    ImGuiTableColumnFlags.WidthFixed,   60);
            ImGui.TableHeadersRow();

            for (int i = 0; i < season.Drivers.Count; i++)
            {
                ImGui.PushID(i);
                Driver driver = season.Drivers[i];

                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                string name = driver.Name;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##name", ref name, 256))
                    driver.Name = name;

                ImGui.TableSetColumnIndex(1);
                int number = driver.Number;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##number", ref number, 0))
                    driver.Number = number;

                ImGui.TableSetColumnIndex(2);
                string nationality = driver.Nationality;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##nationality", ref nationality, 256))
                    driver.Nationality = nationality;

                ImGui.TableSetColumnIndex(3);
                Team? currentTeam = season.Teams.FirstOrDefault(t => t.Id == driver.CurrentTeamId);
                string teamLabel = currentTeam?.Name.Length > 0 ? currentTeam.Name : "None";

                // Color the combo button to match the current team.
                int pushedColors = 0;
                if (currentTeam != null)
                {
                    Vector4 fc = ColorUtils.ToVec4(currentTeam.Color);
                    ImGui.PushStyleColor(ImGuiCol.FrameBg,        fc);
                    ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Brighten(fc, 0.1f));
                    ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  Brighten(fc, 0.2f));
                    pushedColors = 3;
                }

                ImGui.SetNextItemWidth(-1);
                bool open = ImGui.BeginCombo("##team", teamLabel);
                ImGui.PopStyleColor(pushedColors); // Pop before popup draws so text color doesn't leak.

                if (open)
                {
                    if (ImGui.Selectable("None", driver.CurrentTeamId.IsEmpty))
                        driver.CurrentTeamId = ByteString.Empty;

                    foreach (Team t in season.Teams.OrderBy(t => t.Name))
                    {
                        Vector4 tc = ColorUtils.ToVec4(t.Color);
                        ImGui.PushStyleColor(ImGuiCol.Header,        tc);
                        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Brighten(tc, 0.1f));
                        string tLabel = t.Name.Length > 0 ? t.Name : "(unnamed)";
                        if (ImGui.Selectable(tLabel, t.Id == driver.CurrentTeamId))
                            driver.CurrentTeamId = t.Id;
                        ImGui.PopStyleColor(2);
                    }
                    ImGui.EndCombo();
                }

                ImGui.TableSetColumnIndex(4);
                if (ImGui.SmallButton("Remove"))
                    removeIndex = i;

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
        ImGui.Unindent();

        if (removeIndex >= 0)
            season.Drivers.RemoveAt(removeIndex);

        if (ImGui.Button("Add Driver##drivers"))
            season.Drivers.Add(new Driver { Id = CCUtils.CreateUniqueId() });
    }

    private static Vector4 Brighten(Vector4 c, float amount) => new(
        Math.Min(1f, c.X + amount),
        Math.Min(1f, c.Y + amount),
        Math.Min(1f, c.Z + amount),
        c.W);


}
