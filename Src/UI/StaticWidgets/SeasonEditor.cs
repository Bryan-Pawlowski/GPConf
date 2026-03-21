using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class SeasonEditor
{
    public static void Draw(GpConfApp app)
    {
        MainData data = app.GetMainData();
        ImGui.Begin("Season Editor");

        Season currentSeason = data.CurrentSeason;
        SeasonPicker.Draw(data, ref currentSeason);

        if (currentSeason != null)
        {
            string name = currentSeason.Name;
            if (ImGui.InputText("Name##SeasonEditor", ref name, 256))
                currentSeason.Name = name;

            int year = currentSeason.Year;
            if (ImGui.InputInt("Year##SeasonEditor", ref year))
                currentSeason.Year = year;

            if (ImGui.CollapsingHeader("Teams##SeasonEditor"))
                TeamEditor.Draw(currentSeason);

            if (ImGui.CollapsingHeader("Drivers##SeasonEditor"))
                DriverEditor.Draw(currentSeason);

            if (ImGui.CollapsingHeader("Manufacturers##SeasonEditor"))
                ManufacturerEditor.Draw(currentSeason);
        }

        if (ImGui.Button("Save##SeasonEditor")) app.Save();
        ImGui.SameLine();
        if (ImGui.Button("Clear##SeasonEditor")) app.Clear();

        ImGui.End();
        data.CurrentSeason = currentSeason;
    }
}