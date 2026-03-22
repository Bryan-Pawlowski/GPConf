using Google.Protobuf;
using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class ManufacturerEditor
{
    public static void Draw(Season season)
    {
        Manufacturer? toRemove = null;

        ImGui.Indent();
        if (ImGui.BeginTable("##manufacturers", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##mfgremove", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            foreach (Manufacturer mfg in season.Manufacturers.OrderBy(m => m.Name))
            {
                ImGui.PushID(mfg.Id.ToBase64());

                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                string name = mfg.Name;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##name", ref name, 256))
                    mfg.Name = name;

                ImGui.TableSetColumnIndex(1);
                if (ImGui.SmallButton("Remove"))
                    toRemove = mfg;

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
        ImGui.Unindent();

        if (toRemove != null)
            season.Manufacturers.Remove(toRemove);

        if (ImGui.Button("Add Manufacturer##mfg"))
            season.Manufacturers.Add(new Manufacturer { Id = CCUtils.CreateUniqueId(), Name = "New Manufacturer" });
    }
}