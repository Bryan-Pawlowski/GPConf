#define AppName      "GPConf"
#define AppVersion   "1.0"
#define AppPublisher "GPConf"
#define AppExeName   "GPConf.exe"
#define McpExeName   "GPConf.McpServer.exe"
#define SourceDir    "..\publish\win-x64"

[Setup]
AppId={{B0863442-19CB-4C07-BE24-225AF38B718B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputBaseFilename=GPConf-{#AppVersion}-setup
OutputDir=output
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
; Main app
Source: "{#SourceDir}\GPConf.exe";               DestDir: "{app}";     Flags: ignoreversion
; MCP server
Source: "{#SourceDir}\mcp\GPConf.McpServer.exe"; DestDir: "{app}\mcp"; Flags: ignoreversion
; README — copied as a plain .txt so it opens in Notepad on the final wizard page
Source: "..\README.md";                          DestDir: "{app}";     DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";            Filename: "{app}\{#AppExeName}"
Name: "{group}\README";                Filename: "{app}\README.txt"
Name: "{group}\Uninstall {#AppName}";  Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}";      Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Open README after install
Filename: "{app}\README.txt"; Description: "View README"; Flags: postinstall shellexec skipifsilent unchecked
; Launch the app after install
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// Show the MCP config snippet so users know how to wire up Claude.
procedure CurStepChanged(CurStep: TSetupStep);
var
  McpPath, Msg: String;
begin
  if CurStep = ssPostInstall then
  begin
    McpPath := ExpandConstant('{app}\mcp\{#McpExeName}');
    Msg :=
      'To connect Claude to GPConf, add the following to your Claude config:'  + #13#10 +
      '%APPDATA%\Claude\claude_desktop_config.json'                             + #13#10#13#10 +
      '{'                                                                        + #13#10 +
      '  "mcpServers": {'                                                        + #13#10 +
      '    "gpconf": {'                                                          + #13#10 +
      '      "command": "' + McpPath + '"'                                       + #13#10 +
      '    }'                                                                    + #13#10 +
      '  }'                                                                      + #13#10 +
      '}'                                                                        + #13#10#13#10 +
      'For Claude Code (CLI), run:'                                              + #13#10 +
      '  claude mcp add gpconf "' + McpPath + '"';
    MsgBox(Msg, mbInformation, MB_OK);
  end;
end;