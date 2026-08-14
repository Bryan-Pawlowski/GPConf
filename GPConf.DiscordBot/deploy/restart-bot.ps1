# Republishes GPConf.DiscordBot and restarts the persistent scheduled task running it.
# Run this after code changes instead of manually killing/relaunching the bot process.
# Requires register-task.ps1 to have been run once already.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File restart-bot.ps1

$ErrorActionPreference = "Stop"

$root = Resolve-Path "$PSScriptRoot\..\.."
$project = Join-Path $root "GPConf.DiscordBot"
$publishDir = Join-Path $project "publish"
$taskName = "GPConf-DiscordBot"

if (-not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) {
    throw "Scheduled task '$taskName' not found. Run register-task.ps1 first."
}

Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Get-Process -Name "GPConf.DiscordBot" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

dotnet publish $project -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Start-ScheduledTask -TaskName $taskName
Write-Output "Bot republished and restarted."
