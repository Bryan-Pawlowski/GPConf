using System.Numerics;
using Google.Protobuf;
using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class TeamEditor
{
    public static void Draw(Season season)
    {
        Team? removeTeam = null;

        ImGui.Indent();
        int i = 0;
        foreach (Team team in season.Teams.OrderBy(t => t.Name))
        {
            ImGui.PushID(i++);

            // Capture left and right edges before the header consumes the row.
            float leftEdge  = ImGui.GetCursorPosX();
            float rightEdge = leftEdge + ImGui.GetContentRegionAvail().X;

            // Color the header background with the team color.
            Vector4 headerCol = ColorUtils.ToVec4(team.Color);
            ImGui.PushStyleColor(ImGuiCol.Header,        headerCol);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Brighten(headerCol, 0.1f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive,  Brighten(headerCol, 0.2f));

            // Allow the name InputText and right-side buttons to overlap the header.
            ImGui.SetNextItemAllowOverlap();
            bool open = ImGui.CollapsingHeader("##header");

            ImGui.PopStyleColor(3);

            // Name InputText overlaid on the header row with transparent background.
            float arrowWidth = ImGui.GetTreeNodeToLabelSpacing();
            ImGui.SameLine(leftEdge + arrowWidth);
            ImGui.SetNextItemWidth(rightEdge - leftEdge - arrowWidth - 92);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(1, 1, 1, 0.1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(1, 1, 1, 0.2f));
            string name = team.Name;
            if (ImGui.InputText("##teamname", ref name, 256))
                team.Name = name;
            ImGui.PopStyleColor(3);

            // Color picker and Remove button at the right of the header row.
            ImGui.SameLine(rightEdge - 86);
            Vector3 col = ColorUtils.ToVec3(team.Color);
            if (ImGui.ColorEdit3("##color", ref col, ImGuiColorEditFlags.NoInputs))
                team.Color = ColorUtils.Pack(col);
            ImGui.SameLine(rightEdge - 58);
            if (ImGui.SmallButton("Remove"))
                removeTeam = team;

            if (open)
            {
                ImGui.Indent();

                // Manufacturer picker.
                Manufacturer? currentMfg = season.Manufacturers.FirstOrDefault(m => m.Id == team.ManufacturerId);
                string mfgLabel = currentMfg?.Name.Length > 0 ? currentMfg.Name : "No Manufacturer";
                if (ImGui.BeginCombo("Manufacturer##teammfg", mfgLabel))
                {
                    if (ImGui.Selectable("None", team.ManufacturerId.IsEmpty))
                        team.ManufacturerId = ByteString.Empty;
                    foreach (Manufacturer m in season.Manufacturers.OrderBy(m => m.Name))
                    {
                        string mLabel = m.Name.Length > 0 ? m.Name : "(unnamed)";
                        if (ImGui.Selectable(mLabel, m.Id == team.ManufacturerId))
                            team.ManufacturerId = m.Id;
                    }
                    ImGui.EndCombo();
                }

                // Read-only list of drivers whose current team is this team.
                var drivers = season.Drivers.Where(d => d.CurrentTeamId == team.Id).OrderBy(d => d.Name).ToList();
                ImGui.Text($"Drivers ({drivers.Count}):");
                ImGui.Indent();
                if (drivers.Count == 0)
                {
                    ImGui.TextDisabled("None");
                }
                else
                {
                    foreach (Driver d in drivers)
                        ImGui.Text(d.Name.Length > 0 ? d.Name : "(unnamed)");
                }
                ImGui.Unindent();

                ImGui.Unindent();
            }
            ImGui.PopID();
        }

        ImGui.Unindent();

        if (removeTeam != null)
            season.Teams.Remove(removeTeam);

        if (ImGui.Button("Add Team##teams"))
            season.Teams.Add(new Team { Id = CCUtils.CreateUniqueId(), Name = "New Team" });
    }

    private static Vector4 Brighten(Vector4 c, float amount) => new(
        Math.Min(1f, c.X + amount),
        Math.Min(1f, c.Y + amount),
        Math.Min(1f, c.Z + amount),
        c.W);
}