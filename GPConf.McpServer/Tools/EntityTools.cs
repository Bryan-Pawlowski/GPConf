using System.ComponentModel;
using System.Text.Json;
using GPConf.McpServer.DataAccess;
using ModelContextProtocol.Server;

namespace GPConf.McpServer.Tools;

[McpServerToolType]
public class EntityTools(GpConfDataAccess data)
{
    // ── Manufacturers ────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Creates a manufacturer in a season if it does not exist, or renames it if it does.")]
    public string UpsertManufacturer(
        [Description("Season name or year")] string season,
        [Description("Manufacturer name, e.g. 'Mercedes', 'Ferrari'")] string name)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        var m = GpConfDataAccess.FindManufacturer(s, name);
        if (m is null)
        {
            m = new Manufacturer { Id = GpConfDataAccess.NewId(), Name = name };
            s.Manufacturers.Add(m);
            data.Save(mainData);
            return $"Manufacturer '{name}' created in season '{s.Name}'.";
        }

        m.Name = name;
        data.Save(mainData);
        return $"Manufacturer '{name}' already exists in season '{s.Name}'.";
    }

    [McpServerTool]
    [Description("Removes a manufacturer from a season by name.")]
    public string RemoveManufacturer(
        [Description("Season name or year")] string season,
        [Description("Manufacturer name")] string name)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        var m = GpConfDataAccess.FindManufacturer(s, name);
        if (m is null) return $"Manufacturer '{name}' not found in season '{s.Name}'.";

        s.Manufacturers.Remove(m);
        data.Save(mainData);
        return $"Manufacturer '{name}' removed from season '{s.Name}'.";
    }

    // ── Teams ────────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Creates a team in a season if it does not exist, or updates its manufacturer if it does. ManufacturerName must already exist in the season (call UpsertManufacturer first).")]
    public string UpsertTeam(
        [Description("Season name or year")] string season,
        [Description("Team name, e.g. 'McLaren', 'Red Bull Racing'")] string name,
        [Description("Manufacturer name for this team (optional)")] string? manufacturerName = null)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        Google.Protobuf.ByteString mfrId = Google.Protobuf.ByteString.Empty;
        if (manufacturerName is not null)
        {
            var mfr = GpConfDataAccess.FindManufacturer(s, manufacturerName);
            if (mfr is null)
                return $"Manufacturer '{manufacturerName}' not found in season '{s.Name}'. Call UpsertManufacturer first.";
            mfrId = mfr.Id;
        }

        var team = GpConfDataAccess.FindTeam(s, name);
        bool created = team is null;
        if (created)
        {
            team = new Team { Id = GpConfDataAccess.NewId(), Name = name };
            s.Teams.Add(team);
        }

        team!.Name = name;
        if (manufacturerName is not null)
            team.ManufacturerId = mfrId;

        data.Save(mainData);
        return created
            ? $"Team '{name}' created in season '{s.Name}'."
            : $"Team '{name}' updated in season '{s.Name}'.";
    }

    [McpServerTool]
    [Description("Removes a team from a season by name.")]
    public string RemoveTeam(
        [Description("Season name or year")] string season,
        [Description("Team name")] string name)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        var team = GpConfDataAccess.FindTeam(s, name);
        if (team is null) return $"Team '{name}' not found in season '{s.Name}'.";

        s.Teams.Remove(team);
        data.Save(mainData);
        return $"Team '{name}' removed from season '{s.Name}'.";
    }

    // ── Drivers ──────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Creates a driver in a season if they do not exist, or updates their details if they do. TeamName must already exist in the season (call UpsertTeam first).")]
    public string UpsertDriver(
        [Description("Season name or year")] string season,
        [Description("Driver full name, e.g. 'Lando Norris'")] string name,
        [Description("Car number")] int number,
        [Description("Nationality, e.g. 'British'")] string nationality,
        [Description("Team name (optional)")] string? teamName = null)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        Google.Protobuf.ByteString teamId = Google.Protobuf.ByteString.Empty;
        if (teamName is not null)
        {
            var team = GpConfDataAccess.FindTeam(s, teamName);
            if (team is null)
                return $"Team '{teamName}' not found in season '{s.Name}'. Call UpsertTeam first.";
            teamId = team.Id;
        }

        var driver = GpConfDataAccess.FindDriver(s, name);
        bool created = driver is null;
        if (created)
        {
            driver = new Driver { Id = GpConfDataAccess.NewId() };
            s.Drivers.Add(driver);
        }

        driver!.Name        = name;
        driver.Number       = number;
        driver.Nationality  = nationality;
        if (teamName is not null)
            driver.CurrentTeamId = teamId;

        data.Save(mainData);
        return created
            ? $"Driver '{name}' created in season '{s.Name}'."
            : $"Driver '{name}' updated in season '{s.Name}'.";
    }

    [McpServerTool]
    [Description("Removes a driver from a season by name.")]
    public string RemoveDriver(
        [Description("Season name or year")] string season,
        [Description("Driver full name")] string name)
    {
        var mainData = data.Load();
        var s = GpConfDataAccess.FindSeason(mainData, season);
        if (s is null) return $"Season '{season}' not found.";

        var driver = GpConfDataAccess.FindDriver(s, name);
        if (driver is null) return $"Driver '{name}' not found in season '{s.Name}'.";

        s.Drivers.Remove(driver);
        data.Save(mainData);
        return $"Driver '{name}' removed from season '{s.Name}'.";
    }
}
