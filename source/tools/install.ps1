<#
.SYNOPSIS
  Installs Lighthouse in one go: builds it, then asks for admin rights once to set it up. One UAC prompt.

.DESCRIPTION
  1. Installs the .NET 10 SDK with winget if it isn't there (it asks winget's own questions, as you).
  2. Builds Lighthouse into publish\, as you.
  3. Runs tools\setup-admin.ps1 elevated, behind one UAC prompt: PawnIO, the copy into %ProgramFiles%\Lighthouse,
     the two logon tasks, and Lighthouse started. See the README for what each step does.
  When Lighthouse is already installed, it updates it instead (tools\update.ps1), so Install.cmd does both.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install.ps1
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$installed = (Test-Path (Join-Path $env:ProgramFiles 'Lighthouse\Lighthouse.exe')) -and
    (Get-ScheduledTask -TaskPath '\Lighthouse\' -ErrorAction SilentlyContinue)

# 1. The .NET 10 SDK, which builds it.
$hasSdk = (Get-Command dotnet -ErrorAction SilentlyContinue) -and ((dotnet --list-sdks) -match '^10\.')
if (-not $hasSdk) {
    'Installing the .NET 10 SDK with winget...'
    winget install --id Microsoft.DotNet.SDK.10 --exact --source winget
    if ($LASTEXITCODE -ne 0) { throw "winget couldn't install the .NET 10 SDK (exit $LASTEXITCODE). Install it from https://dotnet.microsoft.com/download/dotnet/10.0, then run this again." }
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

if ($installed) {
    'Lighthouse is already installed: updating it (approve the UAC prompt)...'
    & (Join-Path $PSScriptRoot 'update.ps1')
    return
}

# 2. Build, as you: the same command tools\update.ps1 uses.
'Building Lighthouse...'
dotnet publish (Join-Path $repo 'src\Lighthouse\Lighthouse.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o (Join-Path $repo 'publish') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# 3. The one elevated step.
'Setting up (approve the UAC prompt)...'
$p = Start-Process powershell -Verb RunAs -Wait -PassThru -WorkingDirectory $repo `
    -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $PSScriptRoot 'setup-admin.ps1')`""
if ($p.ExitCode -ne 0) { throw "Setup failed (exit $($p.ExitCode)). Its log is in $(Join-Path $repo 'state')." }
$running = Get-Process Lighthouse -ErrorAction SilentlyContinue
"Lighthouse is installed; running: $(if ($running) { 'yes' } else { 'NO, see the log in state\' })"
