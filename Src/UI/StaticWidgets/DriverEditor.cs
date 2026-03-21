using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class DriverEditor
{
    public static void Draw(Season season)
    {
        int removeIndex = -1;

        if (ImGui.BeginTable("##drivers", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Number", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Nationality", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##driverremove", ImGuiTableColumnFlags.WidthFixed, 60);
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
                if (ImGui.SmallButton("Remove"))
                    removeIndex = i;

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (removeIndex >= 0)
            season.Drivers.RemoveAt(removeIndex);

        if (ImGui.Button("Add Driver##drivers"))
            season.Drivers.Add(new Driver { Id = CCUtils.CreateUniqueId() });
    }
}
