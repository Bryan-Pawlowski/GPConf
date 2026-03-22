using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public class SeasonPicker
{
    public static void Draw(MainData data, ref Season outSeason)
    {
        string currentSeasonName = data.CurrentSeason != null ? data.CurrentSeason.Name : "No Season Selected";
        if (ImGui.BeginCombo("##SeasonPicker", currentSeasonName, ImGuiComboFlags.NoArrowButton))
        {
            if (ImGui.Selectable("New Season"))
            {
                Season newSeason = new Season
                {
                    Id   = CCUtils.CreateUniqueId(),
                    Name = "New Season",
                    Year = DateTime.Now.Year
                };
                data.Seasons.Add(newSeason);
                outSeason = newSeason;
            }

            foreach (Season season in data.Seasons)
            {
                if (ImGui.Selectable(season.Name))
                {
                    outSeason = season;
                }
            }
            ImGui.EndCombo();
        }
    }
}