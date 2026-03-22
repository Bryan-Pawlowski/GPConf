using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class RaceConfig
{
    private const string DragType = "RACE_ROW";

    public static unsafe void Draw(Season season)
    {
        // Sync round numbers to position every frame.
        for (int i = 0; i < season.Races.Count; i++)
            season.Races[i].Round = i + 1;

        int removeIndex  = -1;
        int dragSource   = -1;
        int dragTarget   = -1;

        ImGui.Indent();
        if (ImGui.BeginTable("##races", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("##drag",    ImGuiTableColumnFlags.WidthFixed,   20);
            ImGui.TableSetupColumn("Round",     ImGuiTableColumnFlags.WidthFixed,   50);
            ImGui.TableSetupColumn("Name",      ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Circuit",   ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##remove",  ImGuiTableColumnFlags.WidthFixed,   60);
            ImGui.TableHeadersRow();

            for (int i = 0; i < season.Races.Count; i++)
            {
                Race race = season.Races[i];
                ImGui.PushID(race.Id.ToBase64());

                ImGui.TableNextRow();

                // Drag handle column.
                ImGui.TableSetColumnIndex(0);
                ImGui.SmallButton("::");
                if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoHoldToOpenOthers))
                {
                    int sourceIdx = i;
                    ImGui.SetDragDropPayload(DragType, &sourceIdx, (nuint)sizeof(int));
                    ImGui.Text($"Round {race.Round}");
                    ImGui.EndDragDropSource();
                }
                if (ImGui.BeginDragDropTarget())
                {
                    ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(DragType);
                    if (payload.Handle != null && payload.IsDelivery())
                    {
                        dragSource = *(int*)payload.Data;
                        dragTarget = i;
                    }
                    ImGui.EndDragDropTarget();
                }

                // Round column (read-only).
                ImGui.TableSetColumnIndex(1);
                ImGui.Text($"{race.Round}");

                // Name column.
                ImGui.TableSetColumnIndex(2);
                string name = race.Name;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##name", ref name, 256))
                    race.Name = name;

                // Circuit column.
                ImGui.TableSetColumnIndex(3);
                string circuit = race.Circuit;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##circuit", ref circuit, 256))
                    race.Circuit = circuit;

                // Remove column.
                ImGui.TableSetColumnIndex(4);
                if (ImGui.SmallButton("Remove"))
                    removeIndex = i;

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
        ImGui.Unindent();

        if (removeIndex >= 0)
            season.Races.RemoveAt(removeIndex);

        // Apply drag-and-drop reorder after the loop.
        if (dragSource >= 0 && dragTarget >= 0 && dragSource != dragTarget)
        {
            Race moved = season.Races[dragSource];
            season.Races.RemoveAt(dragSource);
            int insertAt = dragTarget > dragSource ? dragTarget : dragTarget;
            season.Races.Insert(insertAt, moved);
        }

        if (ImGui.Button("Add Race##races"))
            season.Races.Add(new Race { Id = CCUtils.CreateUniqueId() });
    }
}
