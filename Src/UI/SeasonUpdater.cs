using System.Numerics;
using GPConf.UI.StaticWidgets;
using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI;

public class SeasonUpdater
{
    private static readonly Vector4 ColorNoResults  = new(1.0f, 0.5f,  0.0f, 1.0f); // orange
    private static readonly Vector4 ColorHasResults = new(0.3f, 0.85f, 0.1f, 1.0f); // lime green

    public static void Draw(GpConfApp app)
    {
        MainData data = app.GetMainData();
        ImGui.Begin("Season Updater");

        Season currentSeason = data.CurrentSeason;
        SeasonPicker.Draw(data, ref currentSeason);
        data.CurrentSeason = currentSeason;

        if (currentSeason != null)
        {
            if (currentSeason.Races.Count == 0)
            {
                ImGui.TextDisabled("No races in schedule. Add races in the Season Editor.");
            }
            else
            {
                foreach (Race race in currentSeason.Races.OrderBy(r => r.Round))
                {
                    ImGui.PushID(race.Id.ToBase64());

                    string name  = race.Name.Length > 0 ? race.Name : $"Round {race.Round}";
                    string label = string.IsNullOrEmpty(race.Circuit)
                        ? $"R{race.Round}: {name}"
                        : $"R{race.Round}: {name} ({race.Circuit})";

                    Vector4 hdr  = race.RaceResults.Count > 0 ? ColorHasResults : ColorNoResults;
                    Vector4 text = ColorUtils.ContrastingText(hdr);
                    ImGui.PushStyleColor(ImGuiCol.Header,        hdr);
                    ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Brighten(hdr, 0.1f));
                    ImGui.PushStyleColor(ImGuiCol.HeaderActive,  Brighten(hdr, 0.2f));
                    ImGui.PushStyleColor(ImGuiCol.Text,          text);
                    bool open = ImGui.CollapsingHeader(label);
                    ImGui.PopStyleColor(4);

                    if (ImGui.IsItemHovered())
                        DrawStandingsTooltip(currentSeason, race);

                    if (open)
                        RaceUpdater.Draw(currentSeason, race);

                    ImGui.PopID();
                }
            }
        }

        ImGui.Separator();
        if (ImGui.Button("Save##SeasonUpdater")) app.Save();

        ImGui.End();
    }

    private static void DrawStandingsTooltip(Season season, Race race)
    {
        var standings = season.Drivers
            .Select(d => (driver: d, pts: CCUtils.GetDriverChampionshipPointSnapshotForRace(season, race, d)))
            .Where(x => x.pts > 0)
            .OrderByDescending(x => x.pts)
            .ToList();

        if (standings.Count == 0) return;

        ImGui.BeginTooltip();
        if (ImGui.BeginTable("##standings_tip", 3, ImGuiTableFlags.Borders))
        {
            ImGui.TableSetupColumn("Pos",    ImGuiTableColumnFlags.WidthFixed,  35);
            ImGui.TableSetupColumn("Driver", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pts",    ImGuiTableColumnFlags.WidthFixed,  40);
            ImGui.TableHeadersRow();

            for (int i = 0; i < standings.Count; i++)
            {
                var (d, pts) = standings[i];
                string num  = d.Number > 0     ? $"#{d.Number} " : "";
                string name = d.Name.Length > 0 ? d.Name         : "(unnamed)";

                Team? team = season.Teams.FirstOrDefault(t => t.Id == d.CurrentTeamId);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Text($"P{i + 1}");
                ImGui.TableSetColumnIndex(1);
                int tipPush = 0;
                if (team != null && team.Color != 0)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg,
                        ImGui.ColorConvertFloat4ToU32(ColorUtils.ToVec4(team.Color)));
                    ImGui.PushStyleColor(ImGuiCol.Text, ColorUtils.ContrastingText(team.Color));
                    tipPush = 1;
                }
                ImGui.Text($"{num}{name}");
                ImGui.PopStyleColor(tipPush);
                ImGui.TableSetColumnIndex(2); ImGui.Text($"{pts}");
            }

            ImGui.EndTable();
        }
        ImGui.EndTooltip();
    }

    private static Vector4 Brighten(Vector4 c, float amount) => new(
        Math.Min(1f, c.X + amount),
        Math.Min(1f, c.Y + amount),
        Math.Min(1f, c.Z + amount),
        c.W);
}
