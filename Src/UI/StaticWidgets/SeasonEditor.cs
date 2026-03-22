using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class SeasonEditor
{
    public static void Draw(GpConfApp app)
    {
        MainData data = app.GetMainData();
        ImGui.Begin("Season Editor");

        // After a save/reload, data.CurrentSeason is a separate protobuf copy from the
        // matching entry in data.Seasons. Resolve against the list so edits always go
        // to data.Seasons[i] and are visible to LeagueEditor's race prepopulation.
        Season currentSeason = data.Seasons.FirstOrDefault(s => s.Id == data.CurrentSeason?.Id)
                            ?? data.CurrentSeason;
        SeasonPicker.Draw(data, ref currentSeason);

        if (currentSeason != null)
        {
            string name = currentSeason.Name;
            if (ImGui.InputText("Name##SeasonEditor", ref name, 256))
                currentSeason.Name = name;

            int year = currentSeason.Year;
            if (ImGui.InputInt("Year##SeasonEditor", ref year))
                currentSeason.Year = year;

            if (ImGui.CollapsingHeader("Manufacturers##SeasonEditor"))
                ManufacturerEditor.Draw(currentSeason);

            if (ImGui.CollapsingHeader("Teams##SeasonEditor"))
                TeamEditor.Draw(currentSeason);

            if (ImGui.CollapsingHeader("Drivers##SeasonEditor"))
                DriverEditor.Draw(currentSeason);

            if (ImGui.CollapsingHeader("Schedule##SeasonEditor"))
                RaceConfig.Draw(currentSeason);

            if (ImGui.CollapsingHeader("Points Scoring Rules##SeasonEditor"))
                PointsScoringEditor.Draw(currentSeason);
        }

        if (ImGui.Button("Save##SeasonEditor")) app.Save();
        ImGui.SameLine();
        if (ImGui.Button("Clear##SeasonEditor")) app.Clear();

        ImGui.End();
        data.CurrentSeason = currentSeason;
    }
}