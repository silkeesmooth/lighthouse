<#
.SYNOPSIS
  The one elevated step: installs PawnIO, installs Lighthouse into Program Files, creates its logon task, offers
  to switch off the startup entries listed under autostartOff (if any), and starts Lighthouse. Everything it
  prints goes to state\setup-<time>.log.

.DESCRIPTION
  Run `dotnet publish` first (see README); this copies publish\Lighthouse.exe and its DLLs into
  %ProgramFiles%\Lighthouse. Two logon tasks start it: "\Lighthouse\Start at logon" runs the app with your own
  rights, and "\Lighthouse\Sensors" runs its sensor helper (`Lighthouse.exe --sensors`) elevated, without a UAC
  prompt, because CPU temperatures need the PawnIO driver.

  Program Files, because only administrators can write there. An elevated task that starts a program from a
  folder you can write to is a way for any program you run to get admin rights: replace the exe, start the task.

.EXAMPLE
  Start-Process powershell -Verb RunAs -WorkingDirectory (Get-Location) -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','tools\setup-admin.ps1'
#>
param([switch]$SkipPawnIO, [switch]$SkipAutostart)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stateDir = Join-Path $repo 'state'
New-Item -ItemType Directory -Force $stateDir | Out-Null
$log = Join-Path $stateDir ('setup-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))
Start-Transcript -Path $log | Out-Null
$failed = $false

try {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { throw 'Not elevated. Start it with -Verb RunAs.' }

    # 1. PawnIO, the signed driver LibreHardwareMonitor uses for CPU, RAM and SSD sensors.
    if (-not $SkipPawnIO) {
        $pawn = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'PawnIO*' } | Select-Object -First 1
        if ($pawn) { "PawnIO already installed: $($pawn.DisplayVersion)" }
        else {
            winget install --id namazso.PawnIO --source winget --exact --silent --disable-interactivity
            "winget exit code: $LASTEXITCODE (3010 = installed, restart needed)"
        }
    }

    # 2. Install the published exe and the native DLLs next to it (published without self-extraction, so nothing
    #    is unpacked into %TEMP% where other programs could swap a DLL before the elevated copy loads it).
    $publish = Join-Path $repo 'publish'
    if (-not (Test-Path (Join-Path $publish 'Lighthouse.exe'))) { throw "No $publish\Lighthouse.exe. Run dotnet publish first." }
    $target = Join-Path $env:ProgramFiles 'Lighthouse'
    $exe = Join-Path $target 'Lighthouse.exe'
    Get-Process Lighthouse -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
    if (Test-Path $target) { Remove-Item (Join-Path $target '*') -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $publish 'Lighthouse.exe'), (Join-Path $publish '*.dll') $target -Force
    "installed $target ($((Get-ChildItem $target).Count) files)"

    # 3. Two logon tasks. The app runs with your own rights. Its sensor helper is the only part that runs as admin,
    #    because the PawnIO driver needs it: no window, no files, it answers the app on a pipe only you can open.
    #    You can run either task (the app starts the helper's when it isn't running) but only admins can change them.
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
        -MultipleInstances IgnoreNew -Priority 5
    foreach ($task in @(
            @{ Name = 'Sensors'; Arguments = '--sensors'; Level = 'Highest'; Delay = 'PT5S'
               Description = "Lighthouse's sensor helper: reads CPU, RAM and SSD sensors through the PawnIO driver, which needs admin. No window; the Lighthouse app asks it for readings." },
            @{ Name = 'Start at logon'; Arguments = $null; Level = 'Limited'; Delay = 'PT10S'
               Description = 'Starts Lighthouse, the CPU/GPU monitor, at logon, with your own rights.' })) {
        $action = if ($task.Arguments) { New-ScheduledTaskAction -Execute $exe -Argument $task.Arguments -WorkingDirectory $target }
                  else { New-ScheduledTaskAction -Execute $exe -WorkingDirectory $target }
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $trigger.Delay = $task.Delay
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel $task.Level
        Register-ScheduledTask -TaskPath '\Lighthouse\' -TaskName $task.Name -Action $action -Trigger $trigger `
            -Principal $principal -Settings $settings -Force -Description $task.Description | Out-Null
        "registered task \Lighthouse\$($task.Name) for $user ($($task.Level), $($task.Delay.Substring(2).ToLower()) after logon)"
    }

    # 4. The startup entries listed under autostartOff: asks first, then switches them off and saves the list where
    #    only administrators can change it. The running copy keeps exactly that list off.
    $settingsFile = Join-Path $env:LOCALAPPDATA 'Lighthouse\settings.json'
    if (-not $SkipAutostart -and (Test-Path $settingsFile)) { & (Join-Path $PSScriptRoot 'autostart.ps1') -Disable }

    # 5. Earlier versions ran from a per-user folder and unpacked their DLLs into %TEMP%: remove both. Last, and
    #    never fatal: the task already points at Program Files, so a locked leftover only stays on disk.
    #    The long form of %TEMP%: elevated PowerShell can't resolve a short one like C:\Users\ABCDEF~1.
    $old = Join-Path $env:LOCALAPPDATA 'Programs\Lighthouse'
    $extracted = Join-Path ([IO.Path]::GetTempPath() | ForEach-Object { (New-Object IO.DirectoryInfo $_).FullName }) '.net\Lighthouse'
    if ($extracted -match '~') { $extracted = Join-Path $env:LOCALAPPDATA 'Temp\.net\Lighthouse' }
    foreach ($leftover in $old, $extracted) {
        try {
            if (-not [IO.Directory]::Exists($leftover)) { continue }
            [IO.Directory]::Delete($leftover, $true)
            "removed the old $leftover"
        }
        catch { "could not remove $leftover ($($_.Exception.Message)); it is no longer used, delete it later" }
    }

    # 6. Start both the way logon will, then show what the app logged.
    $logFile = Join-Path $env:LOCALAPPDATA 'Lighthouse\lighthouse.log'
    Start-ScheduledTask -TaskPath '\Lighthouse\' -TaskName 'Sensors'
    Start-ScheduledTask -TaskPath '\Lighthouse\' -TaskName 'Start at logon'
    Start-Sleep -Seconds 12
    $p = @(Get-Process Lighthouse -ErrorAction SilentlyContinue)
    "running: $($p.Count) Lighthouse processes (the app and its sensor helper): pids $($p.Id -join ', ')"
    '--- lighthouse.log (tail)'
    if (Test-Path $logFile) { Get-Content $logFile -Tail 8 }
}
catch {
    "FAILED: $_"
    $failed = $true
}
finally {
    Stop-Transcript | Out-Null
}
if ($failed) { exit 1 }
