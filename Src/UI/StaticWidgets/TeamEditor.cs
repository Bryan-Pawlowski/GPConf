using System.Numerics;
using Google.Protobuf;
using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class TeamEditor
{
    public static void Draw(Season season)
    {
        var taken = season.Teams
            .SelectMany(t => t.DriverIds)
            .ToHashSet();

        int removeTeamIndex = -1;

        for (int i = 0; i < season.Teams.Count; i++)
        {
            ImGui.PushID(i);
            Team team = season.Teams[i];

            // Capture right edge before the header consumes the row.
            float rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;

            // Color the header background with the team color.
            Vector4 headerCol = ToVec4(team.Color);
            ImGui.PushStyleColor(ImGuiCol.Header,        headerCol);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Brighten(headerCol, 0.1f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive,  Brighten(headerCol, 0.2f));

            // Allow the name InputText and right-side buttons to overlap the header.
            ImGui.SetNextItemAllowOverlap();
            bool open = ImGui.CollapsingHeader("##header");

            ImGui.PopStyleColor(3);

            // Name InputText overlaid on the header row with transparent background.
            float arrowWidth = ImGui.GetTreeNodeToLabelSpacing();
            ImGui.SameLine(arrowWidth);
            ImGui.SetNextItemWidth(rightEdge - arrowWidth - 92);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(1, 1, 1, 0.1f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(1, 1, 1, 0.2f));
            string name = team.Name;
            if (ImGui.InputText("##teamname", ref name, 256))
                team.Name = name;
            ImGui.PopStyleColor(3);

            // Color picker and Remove button at the right of the header row.
            ImGui.SameLine(rightEdge - 86);
            Vector3 col = UnpackColor(team.Color);
            if (ImGui.ColorEdit3("##color", ref col, ImGuiColorEditFlags.NoInputs))
                team.Color = PackColor(col);
            ImGui.SameLine(rightEdge - 58);
            if (ImGui.SmallButton("Remove"))
                removeTeamIndex = i;

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
                    foreach (Manufacturer m in season.Manufacturers)
                    {
                        string mLabel = m.Name.Length > 0 ? m.Name : "(unnamed)";
                        if (ImGui.Selectable(mLabel, m.Id == team.ManufacturerId))
                            team.ManufacturerId = m.Id;
                    }
                    ImGui.EndCombo();
                }

                // Assigned drivers with individual remove buttons.
                int removeDriverIndex = -1;
                for (int d = 0; d < team.DriverIds.Count; d++)
                {
                    ImGui.PushID(d);
                    Driver? driver = season.Drivers.FirstOrDefault(dr => dr.Id == team.DriverIds[d]);
                    ImGui.Text(driver?.Name.Length > 0 ? driver.Name : "(unnamed)");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("x"))
                        removeDriverIndex = d;
                    ImGui.PopID();
                }
                if (removeDriverIndex >= 0)
                {
                    taken.Remove(team.DriverIds[removeDriverIndex]);
                    team.DriverIds.RemoveAt(removeDriverIndex);
                }

                // Unassigned driver picker.
                var available = season.Drivers
                    .Where(d => !taken.Contains(d.Id))
                    .ToList();
                if (available.Count > 0)
                {
                    if (ImGui.BeginCombo("##adddriver", "Add Driver..."))
                    {
                        foreach (Driver d in available)
                        {
                            string dLabel = d.Name.Length > 0 ? d.Name : "(unnamed)";
                            if (ImGui.Selectable(dLabel))
                            {
                                team.DriverIds.Add(d.Id);
                                taken.Add(d.Id);
                            }
                        }
                        ImGui.EndCombo();
                    }
                }

                ImGui.Unindent();
            }

            ImGui.PopID();
        }

        if (removeTeamIndex >= 0)
            season.Teams.RemoveAt(removeTeamIndex);

        if (ImGui.Button("Add Team##teams"))
            season.Teams.Add(new Team { Id = CCUtils.CreateUniqueId(), Name = "New Team" });
    }

    private static Vector3 UnpackColor(uint packed) => new(
        ((packed >> 16) & 0xFF) / 255f,
        ((packed >> 8)  & 0xFF) / 255f,
        ( packed        & 0xFF) / 255f);

    private static uint PackColor(Vector3 col) =>
        ((uint)(col.X * 255) << 16) |
        ((uint)(col.Y * 255) << 8)  |
         (uint)(col.Z * 255);

    private static Vector4 ToVec4(uint packed) => new(
        ((packed >> 16) & 0xFF) / 255f,
        ((packed >> 8)  & 0xFF) / 255f,
        ( packed        & 0xFF) / 255f,
        1.0f);

    private static Vector4 Brighten(Vector4 c, float amount) => new(
        Math.Min(1f, c.X + amount),
        Math.Min(1f, c.Y + amount),
        Math.Min(1f, c.Z + amount),
        c.W);
}