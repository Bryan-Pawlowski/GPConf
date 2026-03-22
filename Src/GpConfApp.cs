using System;
using System.IO;
using System.Numerics;
using Hexa.NET.ImGui;
using Google.Protobuf;
using GPConf.UI;
using GPConf.UI.StaticWidgets;
using GPConf.Utilities;

namespace GPConf;

public class GpConfApp
{
    //Cached file path for AppData.
    private static readonly string AppFolder = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GPConf");

    private static readonly string AppPath =
        Path.Combine(AppFolder, "gpconf.data");

    private MainData _mainAppData;
    private volatile bool _pendingReload = false;
    private FileSystemWatcher _watcher = null!;

    //Do any creation-time prep here.
    public GpConfApp()
    {
        Directory.CreateDirectory(AppFolder);
        _mainAppData = Open();
        Migrate(_mainAppData);
        StartWatcher();
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(AppFolder, "gpconf.data")
        {
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => _pendingReload = true;
    }

    // Assigns IDs to any entities that predate the id field being added.
    private static void Migrate(MainData data)
    {
        foreach (Season s in data.Seasons)
            if (s.Id.IsEmpty)
                s.Id = CCUtils.CreateUniqueId();

        // Resolve CurrentSeason divergence: SeasonEditor previously wrote edits to
        // data.CurrentSeason (a standalone copy) instead of the matching data.Seasons[i].
        // On load, replace the in-list entry with CurrentSeason so the canonical list
        // reflects whatever was last edited.
        if (data.CurrentSeason != null && data.CurrentSeason.Races.Count > 0)
        {
            int matchIdx = -1;

            // Prefer matching by ID.
            if (!data.CurrentSeason.Id.IsEmpty)
            {
                for (int i = 0; i < data.Seasons.Count; i++)
                {
                    if (data.Seasons[i].Id == data.CurrentSeason.Id)
                    {
                        matchIdx = i;
                        break;
                    }
                }
            }

            // Fallback: match by name when CurrentSeason.Id is empty (pre-ID saves).
            if (matchIdx < 0)
            {
                for (int i = 0; i < data.Seasons.Count; i++)
                {
                    if (data.Seasons[i].Name == data.CurrentSeason.Name
                        && data.Seasons[i].Races.Count == 0)
                    {
                        // Give CurrentSeason the in-list ID so future saves stay correlated.
                        data.CurrentSeason.Id = data.Seasons[i].Id;
                        matchIdx = i;
                        break;
                    }
                }
            }

            if (matchIdx >= 0)
                data.Seasons[matchIdx] = data.CurrentSeason;
        }
    }

    public void Save()
    {
        // Suppress the watcher so our own save doesn't trigger a reload.
        _watcher.EnableRaisingEvents = false;
        using FileStream file = File.Create(AppPath); // Create truncates; OpenWrite does not.
        _mainAppData.WriteTo(file);
        _watcher.EnableRaisingEvents = true;
    }

    private MainData Open()
    {
        if (!File.Exists(AppPath))
            return new MainData();

        try
        {
            using FileStream data = File.OpenRead(AppPath);
            return MainData.Parser.ParseFrom(data);
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            // Saved data is corrupt or from an incompatible schema version — start fresh.
            File.Delete(AppPath);
            return new MainData();
        }
    }

    public void Update()
    {
        if (_pendingReload)
        {
            _pendingReload = false;
            _mainAppData = Open();
            Migrate(_mainAppData);
        }

        SetupDockspace();
        DoMenuBar();
    }

    private static void SetupDockspace()
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDocking
            | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoResize   | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoBackground;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("##MainDockspace", flags);
        ImGui.PopStyleVar(3);

        ImGui.DockSpace(ImGui.GetID("MainDockspace"), Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);

        ImGui.End();
    }

    public void Clear()
    {
        _mainAppData.Seasons.Clear();
        _mainAppData.CurrentSeason = new Season();
    }

    private void DoMenuBar()
    {
        if (ImGui.BeginMainMenuBar())
        {
            SeasonMenu();
            ConfCupMenu();
            ImGui.EndMainMenuBar();
        }
        
        if (_bOpenSeasonEditor)  { SeasonEditor.Draw(this); }
        if (_bOpenSeasonUpdater) { SeasonUpdater.Draw(this); }
        if (_bOpenLeagueEditor)  { LeagueEditor.Draw(this); }
        if (_bOpenLeagueUpdater) { LeagueUpdater.Draw(this); }
    }

    bool _bOpenSeasonEditor  = false;
    bool _bOpenSeasonUpdater = false;
    bool _bOpenLeagueEditor  = false;
    bool _bOpenLeagueUpdater = false;
    private void SeasonMenu()
    {
        if (ImGui.BeginMenu("Seasons"))
        {
            if (ImGui.MenuItem("Season Editor"))  { _bOpenSeasonEditor  = !_bOpenSeasonEditor; }
            if (ImGui.MenuItem("Season Updater")) { _bOpenSeasonUpdater = !_bOpenSeasonUpdater; }
            ImGui.EndMenu();
        }
    }

    private void ConfCupMenu()
    {
        if (ImGui.BeginMenu("Conf Cup"))
        {
            if (ImGui.MenuItem("League Editor"))  { _bOpenLeagueEditor  = !_bOpenLeagueEditor; }
            if (ImGui.MenuItem("League Updater")) { _bOpenLeagueUpdater = !_bOpenLeagueUpdater; }
            ImGui.EndMenu();
        }
    }
    
    public MainData GetMainData() => _mainAppData;
}