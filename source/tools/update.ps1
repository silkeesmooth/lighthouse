<#
.SYNOPSIS
  Rebuilds Lighthouse and swaps the new build in. One UAC prompt.

.DESCRIPTION
  Builds as you, then asks for admin rights once to copy the build into %ProgramFiles%\Lighthouse. The copy
  needs admin because only administrators may change the program that the logon task runs elevated: if you
  could, so could any program you run. The elevated half asks the app to exit, stops its sensor helper, copies the
  files, and starts both again through their logon tasks. setup-admin.ps1 must have run once before.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\update.ps1
#>
param([switch]$NoBuild, [switch]$Install)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo 'publish'
$target = Join-Path $env:ProgramFiles 'Lighthouse'
$installed = Join-Path $target 'Lighthouse.exe'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $Install) {
    if (-not $NoBuild) {
        dotnet publish (Join-Path $repo 'src\Lighthouse\Lighthouse.csproj') -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -o $publish -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    }
    if (-not $admin) {
        # The elevated half only copies and restarts; it never builds.
        $p = Start-Process powershell -Verb RunAs -Wait -PassThru `
            -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Install'
        if ($p.ExitCode -ne 0) { throw "The elevated copy failed (exit $($p.ExitCode))." }
        $running = Get-Process Lighthouse -ErrorAction SilentlyContinue
        "updated $target; running: $(if ($running) { 'yes, pid ' + ($running.Id -join ',') } else { 'NO' })"
        return
    }
}

if (-not $admin) { throw 'The -Install half needs admin rights.' }
if (-not (Test-Path $installed)) { throw "No $installed. Run tools\setup-admin.ps1 once first." }

if (Get-Process Lighthouse -ErrorAction SilentlyContinue) {
    Start-Process -FilePath $installed -ArgumentList '--exit' -Wait
    for ($i = 0; $i -lt 20 -and (Get-Process Lighthouse -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    Get-Process Lighthouse -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

Copy-Item (Join-Path $publish 'Lighthouse.exe'), (Join-Path $publish '*.dll') $target -Force
# The sensor helper first, then the app (each runs as its task says: the helper elevated, the app with your rights).
schtasks /run /tn '\Lighthouse\Sensors' | Out-Null
schtasks /run /tn '\Lighthouse\Start at logon' | Out-Null
Start-Sleep -Seconds 3
