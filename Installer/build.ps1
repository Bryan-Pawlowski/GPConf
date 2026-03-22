$ErrorActionPreference = "Stop"
$Root = "$PSScriptRoot\.."

Write-Host "==> Publishing GPConf..." -ForegroundColor Cyan
dotnet publish "$Root\GPConf.csproj" /p:PublishProfile=win-x64

Write-Host "==> Publishing GPConf.McpServer..." -ForegroundColor Cyan
dotnet publish "$Root\GPConf.McpServer\GPConf.McpServer.csproj" /p:PublishProfile=win-x64

Write-Host "==> Running Inno Setup..." -ForegroundColor Cyan
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    Write-Error "Inno Setup not found at: $iscc`nDownload from https://jrsoftware.org/isinfo.php"
}
& $iscc "$PSScriptRoot\GPConf.iss"

Write-Host "==> Done! Installer is at: $PSScriptRoot\output\GPConf-1.0-setup.exe" -ForegroundColor Green