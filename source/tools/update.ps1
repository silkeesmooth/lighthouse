<#
.SYNOPSIS
  Rebuilds Lighthouse and swaps the new build in. One UAC prompt.

.DESCRIPTION
  Builds as you, then asks for admin rights once to copy the build into %ProgramFiles%\Lighthouse. The copy
  needs admin because only administrators may change the program that the logon task runs elevated: if you
  could, so could any program you run. The elevated half asks the app to exit, stops its sensor helper, copies the
  files, and starts both again through their logon tasks (this account's, and those of any other account whose copy
  it stopped). setup-admin.ps1 must have run once for this account before; install.ps1 runs it when it hasn't.

  -UserName names the account updating, passed to the elevated half, because on a standard account that half runs as
  the admin who approved the UAC prompt.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\update.ps1
#>
param([switch]$NoBuild, [switch]$Install, [string]$UserName = $env:USERNAME)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo 'publish'
$target = Join-Path $env:ProgramFiles 'Lighthouse'
$installed = Join-Path $target 'Lighthouse.exe'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
. (Join-Path $PSScriptRoot 'logon-tasks.ps1')

if (-not $Install) {
    if (-not $NoBuild) {
        dotnet publish (Join-Path $repo 'src\Lighthouse\Lighthouse.csproj') -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -o $publish -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    }
    if (-not $admin) {
        # The elevated half only copies and restarts; it never builds.
        $p = Start-Process powershell -Verb RunAs -Wait -PassThru `
            -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Install',
                '-UserName', "`"$env:USERNAME`""
        if ($p.ExitCode -ne 0) { throw "The elevated copy failed (exit $($p.ExitCode))." }
        $session = (Get-Process -Id $PID).SessionId
        $running = @(Get-Process Lighthouse -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
        "updated $target; running: $(if ($running) { 'yes, pid ' + ($running.Id -join ',') } else { 'NO' })"
        return
    }
}

if (-not $admin) { throw 'The -Install half needs admin rights.' }
if (-not (Test-Path $installed)) { throw "No $installed. Run tools\setup-admin.ps1 once first." }

Move-SharedLighthouseTasks
if (-not (Get-ScheduledTask -TaskPath (Get-LighthouseTaskFolder $UserName) -TaskName 'Start at logon' -ErrorAction SilentlyContinue)) {
    throw "No logon tasks for $UserName in $(Get-LighthouseTaskFolder $UserName). Run Install.cmd from that account."
}

# Whose copies run, before any stops: they are started again on the new files.
$wasRunning = @(Get-Process Lighthouse -IncludeUserName -ErrorAction SilentlyContinue | Where-Object { $_.UserName } |
    ForEach-Object { ($_.UserName -split '\\')[-1] })
if (Get-Process Lighthouse -ErrorAction SilentlyContinue) {
    # Asks this session's app to exit cleanly first; Stop-AllLighthouse ends what is left (other accounts, the helpers).
    Start-Process -FilePath $installed -ArgumentList '--exit' -Wait
    for ($i = 0; $i -lt 20 -and (Get-Process Lighthouse -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    $wasRunning += Stop-AllLighthouse
    Start-Sleep -Milliseconds 500
}

Copy-Item (Join-Path $publish 'Lighthouse.exe'), (Join-Path $publish '*.dll') $target -Force
# The sensor helper first, then the app (each runs as its task says: the helper elevated, the app with the account's rights).
foreach ($u in @(@($UserName) + $wasRunning | Sort-Object -Unique)) { Start-LighthouseFor $u }
Start-Sleep -Seconds 3
