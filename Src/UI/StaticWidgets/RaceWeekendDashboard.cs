using System.Linq;
using System.Numerics;
using GPConf.Utilities;
using Hexa.NET.ImGui;

namespace GPConf.UI.StaticWidgets;

public static class RaceWeekendDashboard
{
    private static readonly Vector4 ColorNoResults  = new(1.0f, 0.5f, 0.0f, 1.0f);
    private static readonly Vector4 ColorHasResults = new(0.3f, 0.85f, 0.1f, 1.0f);

    public static void Draw(GpConfApp app)
    {
        MainData data = app.GetMainData();
        ImGui.Begin("Race Weekend Dashboard");

        Season currentSeason = data.Seasons.FirstOrDefault(s => s.Id == data.CurrentSeason?.Id)
                             ?? data.CurrentSeason;
        SeasonPicker.Draw(data, ref currentSeason);

        if (currentSeason == null)
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginTable("##RaceWeekendDashboardTable", 7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Round",        ImGuiTableColumnFlags.WidthFixed,  60);
            ImGui.TableSetupColumn("Name",         ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Circuit",      ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Date",         ImGuiTableColumnFlags.WidthFixed,  120);
            ImGui.TableSetupColumn("Practice",     ImGuiTableColumnFlags.WidthFixed,  90);
            ImGui.TableSetupColumn("Qualifying",   ImGuiTableColumnFlags.WidthFixed,  90);
            ImGui.TableSetupColumn("Race Results", ImGuiTableColumnFlags.WidthFixed,  150);
            ImGui.TableHeadersRow();

            foreach (Race race in currentSeason.Races.OrderBy(r => r.Round))
            {
                ImGui.PushID(race.Id.ToBase64());
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.Text(race.Round.ToString());

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(race.Name.Length > 0 ? race.Name : $"Round {race.Round}");

                ImGui.TableSetColumnIndex(2);
                ImGui.Text(race.Circuit.Length > 0 ? race.Circuit : "-");

                ImGui.TableSetColumnIndex(3);
                ImGui.Text(race.Date.Length > 0 ? race.Date : "-");

                ImGui.TableSetColumnIndex(4);
                DrawSessionStatus(race.Practices);

                ImGui.TableSetColumnIndex(5);
                DrawSessionStatus(race.QualifyingSessions);

                ImGui.TableSetColumnIndex(6);
                DrawRaceResultsStatus(race.RaceResults);

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        data.CurrentSeason = currentSeason;
        ImGui.End();
    }

    private static void DrawSessionStatus(IList<PracticeSession> sessions)
    {
        int total = sessions.Count;
        if (total == 0)
        {
            ImGui.TextUnformatted("none");
            return;
        }

        int populated = sessions.Count(s => s.LapData.Any(ld => ld.FastestLapSeconds > 0f));
        Vector4 color = populated == total
            ? ColorHasResults
            : populated > 0
                ? ColorNoResults
                : default;
        bool colored = populated > 0;
        if (colored) ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted($"{populated}/{total}");
        if (colored) ImGui.PopStyleColor();
    }

    private static void DrawSessionStatus(IList<QualifyingSession> sessions)
    {
        int total = sessions.Count;
        if (total == 0)
        {
            ImGui.TextUnformatted("none");
            return;
        }

        int populated = sessions.Count(s => s.LapData.Any(ld => ld.FastestLapSeconds > 0f));
        Vector4 color = populated == total
            ? ColorHasResults
            : populated > 0
                ? ColorNoResults
                : default;
        bool colored = populated > 0;
        if (colored) ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted($"{populated}/{total}");
        if (colored) ImGui.PopStyleColor();
    }

    private static void DrawRaceResultsStatus(IList<RaceResult> results)
    {
        int count = results.Count;
        if (count == 0)
        {
            ImGui.TextUnformatted("none");
            return;
        }

        // All recorded results are treated as complete (regardless of IsComplete flag).
        Vector4 color = count > 0 ? ColorHasResults : default;
        if (count > 0) ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted($"{count} results");
        if (count > 0) ImGui.PopStyleColor();
    }
}