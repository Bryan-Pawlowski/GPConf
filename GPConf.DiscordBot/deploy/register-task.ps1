# One-time setup: publishes GPConf.DiscordBot and registers a persistent Windows Scheduled Task
# that runs it in the background, restarts it automatically if it crashes, and starts it again
# at logon. CONF_BOT_TOKEN must already be set as a User- or Machine-level environment variable
# (Program.cs reads it via Environment.GetEnvironmentVariable) since the task runs detached from
# any interactive terminal session.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File register-task.ps1

$ErrorActionPreference = "Stop"

$root = Resolve-Path "$PSScriptRoot\..\.."
$project = Join-Path $root "GPConf.DiscordBot"
$publishDir = Join-Path $project "publish"
$taskName = "GPConf-DiscordBot"

# Stop any existing run so `dotnet publish` doesn't hit a file lock on the running exe.
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
}
Get-Process -Name "GPConf.DiscordBot" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

dotnet publish $project -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exePath = Join-Path $publishDir "GPConf.DiscordBot.exe"
$action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $publishDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings `
    -Description "Keeps the GPConf Discord bot running in the background; restarts on crash, starts at logon." `
    -Force | Out-Null

Start-ScheduledTask -TaskName $taskName
Write-Output "Registered and started scheduled task '$taskName'."
Write-Output "Bot binary: $exePath"
