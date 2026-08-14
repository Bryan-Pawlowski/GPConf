using Google.Protobuf;
using GPConf.Utilities;

namespace GPConf.McpServer.DataAccess;

public class GpConfDataAccess
{
    public static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GPConf", "gpconf.data");

    public MainData Load()
    {
        if (!File.Exists(DataPath)) return new MainData();
        using var fs = File.OpenRead(DataPath);
        return MainData.Parser.ParseFrom(fs);
    }

    // Pairs a load with a version marker (the file's mtime at load time) so a later Save can
    // detect whether another process wrote to gpconf.data in between — see Save(data, expectedVersion).
    public (MainData data, DateTime version) LoadWithVersion()
    {
        var data = Load();
        var version = File.Exists(DataPath) ? File.GetLastWriteTimeUtc(DataPath) : DateTime.MinValue;
        return (data, version);
    }

    public void Save(MainData data) => Save(data, null);

    // expectedVersion, if given, must match gpconf.data's current mtime or the save is refused —
    // an optimistic-concurrency guard against another process (a concurrent MCP server instance,
    // the desktop app, or the bot) having written the file since it was loaded. Optional so every
    // pre-existing caller (Save(data), no version) keeps its prior unconditional-overwrite behavior;
    // only writers that can't tolerate silently clobbering a concurrent write should pass one.
    public void Save(MainData data, DateTime? expectedVersion)
    {
        if (expectedVersion is { } expected)
        {
            var current = File.Exists(DataPath) ? File.GetLastWriteTimeUtc(DataPath) : DateTime.MinValue;
            if (current != expected)
                throw new ConcurrentSaveException(
                    "gpconf.data was modified by another process since it was loaded; refusing to overwrite.");
        }

        // Keep CurrentSeason in sync with the matching entry in data.Seasons so that
        // the app's Migrate() step (which replaces the list entry with CurrentSeason)
        // does not overwrite MCP-written changes with stale data.
        if (data.CurrentSeason != null)
        {
            Season? match = data.Seasons.FirstOrDefault(s =>
                (!s.Id.IsEmpty && s.Id == data.CurrentSeason.Id) ||
                s.Name.Equals(data.CurrentSeason.Name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                data.CurrentSeason = match;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        BackupUtils.RotateBackups(DataPath);
        using var fs = new FileStream(DataPath, FileMode.Create, FileAccess.Write);
        data.WriteTo(fs);
    }

    // --- Lookup helpers ---

    public static Season? FindSeason(MainData data, string nameOrYear)
    {
        if (int.TryParse(nameOrYear, out int year))
            return data.Seasons.FirstOrDefault(s => s.Year == year);
        return data.Seasons.FirstOrDefault(s =>
            s.Name.Equals(nameOrYear, StringComparison.OrdinalIgnoreCase));
    }

    public static Driver? FindDriver(Season season, string name) =>
        season.Drivers.FirstOrDefault(d =>
            d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static Team? FindTeam(Season season, string name) =>
        season.Teams.FirstOrDefault(t =>
            t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static Manufacturer? FindManufacturer(Season season, string name) =>
        season.Manufacturers.FirstOrDefault(m =>
            m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static Race? FindRace(Season season, string nameOrRound)
    {
        if (int.TryParse(nameOrRound, out int round))
            return season.Races.FirstOrDefault(r => r.Round == round);
        return season.Races.FirstOrDefault(r =>
            r.Name.Equals(nameOrRound, StringComparison.OrdinalIgnoreCase));
    }

    public static ByteString NewId() =>
        ByteString.CopyFrom(Guid.NewGuid().ToByteArray());
}

public sealed class ConcurrentSaveException(string message) : Exception(message);