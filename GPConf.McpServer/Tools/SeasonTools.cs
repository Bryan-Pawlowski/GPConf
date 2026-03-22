using System.ComponentModel;
using System.Text.Json;
using GPConf.McpServer.DataAccess;
using ModelContextProtocol.Server;

namespace GPConf.McpServer.Tools;

[McpServerToolType]
public class SeasonTools(GpConfDataAccess data)
{
    [McpServerTool]
    [Description("Lists all seasons stored in the GPConf data file.")]
    public string ListSeasons()
    {
        var mainData = data.Load();
        var seasons = mainData.Seasons.Select(s => new
        {
            id           = Convert.ToHexString(s.Id.ToByteArray()),
            name         = s.Name,
            year         = s.Year,
            drivers      = s.Drivers.Count,
            teams        = s.Teams.Count,
            manufacturers = s.Manufacturers.Count,
            races        = s.Races.Count,
        });
        return JsonSerializer.Serialize(seasons);
    }

    [McpServerTool]
    [Description("Returns the drivers, teams, manufacturers, and race list for a season. Identify the season by name or year.")]
    public string GetSeason([Description("Season name or year number")] string season)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        var result = new
        {
            name  = s.Name,
            year  = s.Year,
            drivers = s.Drivers.Select(d => new
            {
                id          = Convert.ToHexString(d.Id.ToByteArray()),
                name        = d.Name,
                number      = d.Number,
                nationality = d.Nationality,
            }),
            teams = s.Teams.Select(t => new
            {
                id   = Convert.ToHexString(t.Id.ToByteArray()),
                name = t.Name,
            }),
            manufacturers = s.Manufacturers.Select(m => new
            {
                id   = Convert.ToHexString(m.Id.ToByteArray()),
                name = m.Name,
            }),
            races = s.Races.Select(r => new
            {
                id      = Convert.ToHexString(r.Id.ToByteArray()),
                name    = r.Name,
                circuit = r.Circuit,
                round   = r.Round,
                date    = r.Date,
            }),
        };
        return JsonSerializer.Serialize(result);
    }
}