#requires -Version 5.1
<#
.SYNOPSIS
  Install, remove, or check MapRepo Server as an always-on background process on Windows.

.DESCRIPTION
  Two mutually exclusive autostart modes:

    Service  A real Windows Service (Local System, sc.exe/New-Service). Starts at boot before any
             user logs in, survives logoff. Requires an elevated (Administrator) PowerShell.

    Logon    A Scheduled Task that starts the server when the current user logs in (no admin
             rights required). Runs in the user's session; stops when the user logs off.

  Publishes MapRepo.Server (dotnet publish) to -PublishDir, then registers it under the chosen
  mode. Re-running Install replaces any prior registration. The server keeps binding all
  interfaces (0.0.0.0) unless you pass -Port with a different value; the --urls argument is
  always passed explicitly so this script's -Port is authoritative regardless of appsettings.json.

.PARAMETER Action
  Install | Uninstall | Status | Start | Stop

.PARAMETER Mode
  Service | Logon - required for Install/Uninstall, ignored otherwise.

.PARAMETER Port
  TCP port to bind (default 5087).

.PARAMETER PublishDir
  Where `dotnet publish` output goes (default: <repo>\publish). Re-used on Uninstall/Status to
  locate the executable; keep it the same across calls unless you also re-run Install.

.EXAMPLE
  .\scripts\manage-service.ps1 -Action Install -Mode Logon
  Starts MapRepo automatically whenever you log in - no admin needed.

.EXAMPLE
  .\scripts\manage-service.ps1 -Action Install -Mode Service
  (Run as Administrator) Installs a Windows Service that starts at boot.

.EXAMPLE
  .\scripts\manage-service.ps1 -Action Status
  Reports whichever mode is currently registered, plus a live health check.

.EXAMPLE
  .\scripts\manage-service.ps1 -Action Uninstall -Mode Service
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Uninstall', 'Status', 'Start', 'Stop')]
    [string]$Action,

    [ValidateSet('Service', 'Logon')]
    [string]$Mode,

    [int]$Port = 5087,

    [string]$PublishDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish'),

    [string]$ServiceName = 'MapRepoServer',
    [string]$TaskName = 'MapRepoServer'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $repoRoot 'MapRepo.Server\MapRepo.Server.csproj'
$exePath = Join-Path $PublishDir 'MapRepo.Server.exe'

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

function Assert-Admin {
    if (-not (Test-Admin)) {
        throw "Service mode requires an elevated PowerShell. Right-click PowerShell -> 'Run as administrator', then re-run this command."
    }
}

function Publish-Server {
    Write-Host "Publishing MapRepo.Server -> $PublishDir" -ForegroundColor Cyan
    if (-not (Test-Path $PublishDir)) { New-Item -ItemType Directory -Path $PublishDir | Out-Null }
    & dotnet publish $projectPath -c Release -o $PublishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path $exePath)) { throw "Publish succeeded but $exePath was not produced." }
}

function Install-AsService {
    Assert-Admin
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host "Removing existing service '$ServiceName'..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 1
    }
    $binPath = "`"$exePath`" --urls=http://0.0.0.0:$Port"
    New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName 'MapRepo Server' `
        -Description 'Resident MCP code-navigation server (map-repo-server).' -StartupType Automatic -ErrorAction Stop | Out-Null
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        throw "New-Service returned without error but the service is not present - treat as failed."
    }
    # LocalSystem (the default service account) needs write access to the publish folder for data-v4/.
    & icacls $PublishDir /grant '*S-1-5-18:(OI)(CI)F' /T | Out-Null
    Start-Service -Name $ServiceName -ErrorAction Stop
    Write-Host "Service '$ServiceName' installed and started (port $Port)." -ForegroundColor Green
}

function Uninstall-Service {
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { Write-Host "Service '$ServiceName' is not registered."; return }
    Assert-Admin
    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Service '$ServiceName' removed." -ForegroundColor Green
}

function Install-AsLogonTask {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Write-Host "Replacing existing task '$TaskName'..." -ForegroundColor Yellow
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    $action = New-ScheduledTaskAction -Execute $exePath -Argument "--urls=http://0.0.0.0:$Port" -WorkingDirectory $PublishDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -ErrorAction Stop | Out-Null
    if (-not (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
        throw "Register-ScheduledTask returned without error but the task is not present - treat as failed."
    }
    try { Start-ScheduledTask -TaskName $TaskName -ErrorAction Stop }
    catch { Write-Host "Task registered but did not start immediately: $($_.Exception.Message). It will still run at next login." -ForegroundColor Yellow }
    Write-Host "Logon task '$TaskName' installed for $env:USERNAME (port $Port)." -ForegroundColor Green
    Write-Host "It starts automatically at your next login." -ForegroundColor DarkGray
}

function Uninstall-LogonTask {
    if (-not (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) { Write-Host "Task '$TaskName' is not registered."; return }
    try { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue } catch {}
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Logon task '$TaskName' removed." -ForegroundColor Green
}

function Show-Status {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($service) { Write-Host "Windows Service '$ServiceName': $($service.Status)" -ForegroundColor Cyan }
    if ($task) { Write-Host "Scheduled Task '$TaskName': $($task.State)" -ForegroundColor Cyan }
    if (-not $service -and -not $task) { Write-Host "No autostart registration found (neither service nor logon task)." -ForegroundColor Yellow }
    try {
        $health = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 3
        Write-Host "Health check: $($health.status) ($($health.service))" -ForegroundColor Green
    } catch { Write-Host "Health check: server not responding on port $Port" -ForegroundColor Red }
}

switch ($Action) {
    'Install' {
        if (-not $Mode) { throw "-Mode Service|Logon is required for Install." }
        Publish-Server
        if ($Mode -eq 'Service') { Install-AsService } else { Install-AsLogonTask }
    }
    'Uninstall' {
        if (-not $Mode) { throw "-Mode Service|Logon is required for Uninstall." }
        if ($Mode -eq 'Service') { Uninstall-Service } else { Uninstall-LogonTask }
    }
    'Status' { Show-Status }
    'Start' {
        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { Start-Service -Name $ServiceName }
        elseif (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Start-ScheduledTask -TaskName $TaskName }
        else { throw "Nothing registered - run -Action Install first." }
    }
    'Stop' {
        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { Stop-Service -Name $ServiceName }
        elseif (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $TaskName }
        else { Write-Host "Nothing registered." }
    }
}
