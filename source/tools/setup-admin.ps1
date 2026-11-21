<#
.SYNOPSIS
  The one elevated step: installs PawnIO, installs Lighthouse into Program Files, creates its logon task, offers
  to switch off the startup entries listed under autostartOff (if any), and starts Lighthouse. Everything it
  prints goes to state\setup-<time>.log.

.DESCRIPTION
  Run `dotnet publish` first (see README); this copies publish\Lighthouse.exe and its DLLs into
  %ProgramFiles%\Lighthouse. Two logon tasks start it, in a folder of their own for each account: "\Lighthouse\<user
  name>\Start at logon" runs the app with your own rights, and "\Lighthouse\<user name>\Sensors" runs its sensor
  helper (`Lighthouse.exe --sensors`) elevated, without a UAC prompt, because CPU temperatures need the PawnIO driver.
  A standard account gets no Sensors task. Other accounts' tasks are left as they are.

  -UserName, -Account and -StandardAccount name the account being set up. install.ps1 passes them, because on a
  standard account this elevated copy runs as the admin who approved the UAC prompt. Left out: the account running it.

  Program Files, because only administrators can write there. An elevated task that starts a program from a
  folder you can write to is a way for any program you run to get admin rights: replace the exe, start the task.

.EXAMPLE
  Start-Process powershell -Verb RunAs -WorkingDirectory (Get-Location) -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','tools\setup-admin.ps1'
#>
param([switch]$SkipPawnIO, [switch]$SkipAutostart, [string]$UserName = $env:USERNAME,
      [string]$Account = "$env:USERDOMAIN\$env:USERNAME", [switch]$StandardAccount)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stateDir = Join-Path $repo 'state'
New-Item -ItemType Directory -Force $stateDir | Out-Null
$log = Join-Path $stateDir ('setup-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))
Start-Transcript -Path $log | Out-Null
$failed = $false
. (Join-Path $PSScriptRoot 'logon-tasks.ps1')
# Steps that read or write a profile (settings, old copies, the log) only when this copy runs as the account itself.
$sameAccount = $UserName -eq $env:USERNAME

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
    # Every account's copy holds the files; the ones that were running are started again at the end.
    $wasRunning = Stop-AllLighthouse
    Start-Sleep -Seconds 1
    if (Test-Path $target) { Remove-Item (Join-Path $target '*') -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $publish 'Lighthouse.exe'), (Join-Path $publish '*.dll') $target -Force
    "installed $target ($((Get-ChildItem $target).Count) files)"

    # 3. This account's two logon tasks (see Register-LighthouseTasks), after moving the old shared ones into
    #    their own accounts' folders, so installing here leaves another account's tasks alone.
    Move-SharedLighthouseTasks
    Register-LighthouseTasks -Account $Account -UserName $UserName -Exe $exe -AdminAccount (-not $StandardAccount)

    # 4. The startup entries listed under autostartOff: asks first, then switches them off and saves the list where
    #    only administrators can change it. The running copy keeps exactly that list off.
    $settingsFile = Join-Path $env:LOCALAPPDATA 'Lighthouse\settings.json'
    if (-not $sameAccount) { "skipped the startup entries and old copies: this runs as $env:USERNAME, not $UserName" }
    elseif (-not $SkipAutostart -and (Test-Path $settingsFile)) { & (Join-Path $PSScriptRoot 'autostart.ps1') -Disable }

    # 5. Earlier versions ran from a per-user folder and unpacked their DLLs into %TEMP%: remove both. Last, and
    #    never fatal: the task already points at Program Files, so a locked leftover only stays on disk.
    #    The long form of %TEMP%: elevated PowerShell can't resolve a short one like C:\Users\ABCDEF~1.
    $old = Join-Path $env:LOCALAPPDATA 'Programs\Lighthouse'
    $extracted = Join-Path ([IO.Path]::GetTempPath() | ForEach-Object { (New-Object IO.DirectoryInfo $_).FullName }) '.net\Lighthouse'
    if ($extracted -match '~') { $extracted = Join-Path $env:LOCALAPPDATA 'Temp\.net\Lighthouse' }
    foreach ($leftover in @(if ($sameAccount) { $old, $extracted })) {
        try {
            if (-not [IO.Directory]::Exists($leftover)) { continue }
            [IO.Directory]::Delete($leftover, $true)
            "removed the old $leftover"
        }
        catch { "could not remove $leftover ($($_.Exception.Message)); it is no longer used, delete it later" }
    }

    # 6. Start this account's tasks the way logon will (and again any other account's copy that was stopped), then
    #    show what the app logged.
    $logFile = Join-Path $env:LOCALAPPDATA 'Lighthouse\lighthouse.log'
    foreach ($u in @(@($UserName) + $wasRunning | Sort-Object -Unique)) { Start-LighthouseFor $u }
    Start-Sleep -Seconds 12
    $p = @(Get-Process Lighthouse -ErrorAction SilentlyContinue)
    "running: $($p.Count) Lighthouse processes (the app and its sensor helper): pids $($p.Id -join ', ')"
    '--- lighthouse.log (tail)'
    if ($sameAccount -and (Test-Path $logFile)) { Get-Content $logFile -Tail 8 }
}
catch {
    "FAILED: $_"
    $failed = $true
}
finally {
    Stop-Transcript | Out-Null
}
if ($failed) { exit 1 }
