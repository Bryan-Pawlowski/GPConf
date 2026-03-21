using System;
using System.IO;
using System.Numerics;
using Hexa.NET.ImGui;
using Google.Protobuf;
using GPConf.UI;
using GPConf.UI.StaticWidgets;

namespace GPConf;

public class GpConfApp
{
    //Cached file path for AppData.
    private static readonly string AppFolder = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GPConf");

    private static readonly string AppPath =
        Path.Combine(AppFolder, "gpconf.data");

    private MainData _mainAppData;
    
    //Do any creation-time prep here.
    public GpConfApp()
    {
        Directory.CreateDirectory(AppFolder);
        _mainAppData = Open();
    }

    public void Save()
    {
        //File.Delete(AppPath);
        FileStream file = File.OpenWrite(AppPath);
        _mainAppData.WriteTo(file);
        file.Close();
    }

    private MainData Open()
    {
        MainData outData;
        if (File.Exists(AppPath))
        {
            FileStream data = File.OpenRead(AppPath);
            outData = MainData.Parser.ParseFrom(data);
            data.Close();
        }
        else
        {
            File.Create(AppPath).Close();
            outData = new MainData();
        }
        return outData;
    }

    public void Update()
    {
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
        
        if (_bOpenSeasonEditor) { SeasonEditor.Draw(this); }
    }

    bool _bOpenSeasonEditor = false;
    private void SeasonMenu()
    {
        if (ImGui.BeginMenu("Seasons"))
        {
            if (ImGui.MenuItem("Season Editor")) { _bOpenSeasonEditor = !_bOpenSeasonEditor; }
            
            if (ImGui.MenuItem("Season Updater")) { }
            ImGui.EndMenu();
        }

    }

    private void ConfCupMenu()
    {
        if (ImGui.BeginMenu("Conf Cup"))
        {
            if (ImGui.BeginMenu("Leagues"))
            {
                string league = "POOP LEAGUE";
                if (ImGui.BeginMenu($"Current League: {league}##ConfCup"))
                {
                    
                    ImGui.EndMenu();
                }
                if(ImGui.MenuItem("Rules")) { }
                if(ImGui.MenuItem("Picks")) { }

                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Season Updater")) { }
            ImGui.EndMenu();
        }
    }
    
    public MainData GetMainData() => _mainAppData;
}