using System.ComponentModel;
using System.Text.Json;
using GPConf.McpServer.DataAccess;
using ModelContextProtocol.Server;

namespace GPConf.McpServer.Tools;

[McpServerToolType]
public class SeasonTools(GpConfDataAccess data)
{
    [McpServerTool]
    [Description("Creates a new season. Returns an error if a season with the same name already exists, allowing multiple series (e.g. Formula 1, WEC, MotoGP) to share the same year.")]
    public string CreateSeason(
        [Description("Season display name, e.g. 'F1 2025' or 'WEC 2025'")] string name,
        [Description("Season year, e.g. 2025")] int year)
    {
        var mainData = data.Load();
        if (GpConfDataAccess.FindSeason(mainData, name) is not null)
            return $"Season '{name}' already exists.";

        var season = new Season
        {
            Id   = GpConfDataAccess.NewId(),
            Name = name,
            Year = year,
        };
        mainData.Seasons.Add(season);
        data.Save(mainData);
        return $"Season '{name}' ({year}) created.";
    }

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