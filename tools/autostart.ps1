<#
.SYNOPSIS
  Switches off (or back on) the startup entries listed under "autostartOff" in Lighthouse's settings.json.
  Uninstalls nothing.

.DESCRIPTION
  -List     Show each entry's state. No admin needed.
  -Disable  Show the list and ask, then record the current state in state\autostart-before.json (first time per
            entry, so it always holds the original), switch every entry off, and save the list in
            HKLM\SOFTWARE\Lighthouse, where only administrators can change it. The running Lighthouse keeps exactly
            that saved list off after updates (unless %LOCALAPPDATA%\Lighthouse\autostart-guard-off exists).
            Needs admin. -Yes skips the question.
  -Restore  Put every entry back as recorded, clear the saved list and create autostart-guard-off. Needs admin.
  -WhatIf   With -Disable or -Restore: print what would change and change nothing.

  Windows' own entries are refused whatever the list says: tasks under \Microsoft\, Windows Security's Run values
  and Windows notifications.
  -SettingsFile  Read the list from another file (default: %LOCALAPPDATA%\Lighthouse\settings.json).

  Entry kinds: "run" (a Run-key value; "hive" HKLM or HKCU), "task" (a scheduled task; "path" is its folder),
  "toast" (an app's Windows notifications), "cmdpal" (a PowerToys Command Palette extension).
  See examples\settings.example.json. Only listed entries are touched.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\autostart.ps1 -List
#>
param([switch]$List, [switch]$Disable, [switch]$Restore, [switch]$WhatIf, [switch]$Yes,
      [string]$SettingsFile = (Join-Path $env:LOCALAPPDATA 'Lighthouse\settings.json'))

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stateDir = Join-Path $repo 'state'
$stateFile = Join-Path $stateDir 'autostart-before.json'
$guardOff = Join-Path $env:LOCALAPPDATA 'Lighthouse\autostart-guard-off'
$cmdPalSettings = Join-Path $env:LOCALAPPDATA 'Packages\Microsoft.CommandPalette_8wekyb3d8bbwe\LocalState\settings.json'
$cmdPalBackup = Join-Path $stateDir 'cmdpal-settings.before.json'

$runKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$approvedKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$toastKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'

# The list. PowerShell 5.1's JSON reader has no comments, so whole-line // comments are dropped first.
if (-not (Test-Path $SettingsFile)) { throw "No $SettingsFile. Start Lighthouse once, or pass -SettingsFile." }
$json = [regex]::Replace((Get-Content $SettingsFile -Raw), '(?m)^\s*//.*$', '')
$listed = @((ConvertFrom-Json $json).autostartOff | Where-Object { $_ })

# settings.json can be edited by any program you run, and this script runs as admin, so Windows' own entries are
# refused whatever the file says (the running Lighthouse refuses the same ones: AutostartList.Allowed).
function Test-Allowed($e) {
    switch ($e.kind) {
        'run' { return ($null -eq $e.hive -or $e.hive -in 'HKLM', 'HKCU') -and $e.name -notin 'SecurityHealth', 'WindowsDefender', 'Windows Defender' }
        'task' { return -not ([string]$e.path).TrimStart('\').StartsWith('Microsoft', [StringComparison]::OrdinalIgnoreCase) }
        'toast' { return -not ($e.name -like 'Windows.*' -or $e.name -like 'Microsoft.Windows*') }
        'cmdpal' { return $true }
        default { return $false }
    }
}
$entries = @($listed | Where-Object { Test-Allowed $_ })
foreach ($r in @($listed | Where-Object { -not (Test-Allowed $_) })) { "refused  $($r.kind) $($r.path)$($r.name): part of Windows, never switched off" }
if ($entries.Count -eq 0) { "Nothing Lighthouse may switch off is listed under autostartOff in $SettingsFile."; return }

# Where the running Lighthouse reads its list: only administrators can change HKLM\SOFTWARE.
$savedKey = 'HKLM:\SOFTWARE\Lighthouse'

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Command Palette keeps each extension as "<Name>": { "IsEnabled": true|false }. A targeted text edit, so
# nothing else in its settings file moves.
function Get-CmdPalPattern($name) { '("' + [regex]::Escape($name) + '"\s*:\s*\{\s*"IsEnabled"\s*:\s*)(true|false)' }

function Get-CmdPal($name) {
    if (-not (Test-Path $cmdPalSettings)) { return $null }
    $m = [regex]::Match((Get-Content $cmdPalSettings -Raw), (Get-CmdPalPattern $name))
    if (-not $m.Success) { return $null }
    return ($m.Groups[2].Value -eq 'true')
}

function Set-CmdPal($name, [bool]$on) {
    $running = Get-Process Microsoft.CmdPal.UI -ErrorAction SilentlyContinue
    # Command Palette rewrites its settings on exit, so stop it before editing and start it again after.
    if ($running) { $running | Stop-Process -Force; Start-Sleep -Seconds 2 }
    $text = Get-Content $cmdPalSettings -Raw
    $text = [regex]::Replace($text, (Get-CmdPalPattern $name), '${1}' + $(if ($on) { 'true' } else { 'false' }))
    [IO.File]::WriteAllText($cmdPalSettings, $text, (New-Object System.Text.UTF8Encoding($false)))
    if ($running) { Start-Process 'shell:AppsFolder\Microsoft.CommandPalette_8wekyb3d8bbwe!App' }
}

function Get-EntryState($e) {
    switch ($e.kind) {
        'run' {
            $root = "$($e.hive):\"
            $value = (Get-ItemProperty -Path ($root + $runKey) -Name $e.name -ErrorAction SilentlyContinue).($e.name)
            $bytes = (Get-ItemProperty -Path ($root + $approvedKey) -Name $e.name -ErrorAction SilentlyContinue).($e.name)
            $enabled = ($null -eq $bytes) -or (($bytes[0] -band 1) -eq 0)
            return @{ exists = ($null -ne $value); enabled = $enabled; approved = $(if ($bytes) { [Convert]::ToBase64String($bytes) } else { $null }) }
        }
        'task' {
            $t = Get-ScheduledTask -TaskPath $e.path -TaskName $e.name -ErrorAction SilentlyContinue
            return @{ exists = ($null -ne $t); enabled = ($null -ne $t -and $t.State -ne 'Disabled') }
        }
        'toast' {
            $v = (Get-ItemProperty -Path "$toastKey\$($e.name)" -Name Enabled -ErrorAction SilentlyContinue).Enabled
            return @{ exists = $true; enabled = ($null -eq $v -or $v -ne 0); value = $v }
        }
        'cmdpal' {
            $on = Get-CmdPal $e.name
            return @{ exists = ($null -ne $on); enabled = ($on -eq $true) }
        }
        default { throw "Unknown kind '$($e.kind)' for $($e.name) (run, task, toast or cmdpal)." }
    }
}

function Set-EntryOff($e) {
    switch ($e.kind) {
        'run' {
            $path = "$($e.hive):\$approvedKey"
            if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
            # Task Manager's format: 03 = disabled, then the FILETIME it was switched off.
            $bytes = [byte[]](3, 0, 0, 0) + [BitConverter]::GetBytes([DateTime]::UtcNow.ToFileTimeUtc())
            Set-ItemProperty -Path $path -Name $e.name -Value $bytes -Type Binary
        }
        'task' { Disable-ScheduledTask -TaskPath $e.path -TaskName $e.name | Out-Null }
        'toast' {
            $path = "$toastKey\$($e.name)"
            if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
            Set-ItemProperty -Path $path -Name Enabled -Value 0 -Type DWord
        }
        'cmdpal' {
            if (-not (Test-Path $cmdPalBackup)) { Copy-Item $cmdPalSettings $cmdPalBackup }
            Set-CmdPal $e.name $false
        }
    }
}

function Set-EntryBack($e, $before) {
    switch ($e.kind) {
        'run' {
            $path = "$($e.hive):\$approvedKey"
            if ($null -eq $before.approved) { Remove-ItemProperty -Path $path -Name $e.name -ErrorAction SilentlyContinue }
            else { Set-ItemProperty -Path $path -Name $e.name -Value ([Convert]::FromBase64String($before.approved)) -Type Binary }
        }
        'task' { if ($before.enabled) { Enable-ScheduledTask -TaskPath $e.path -TaskName $e.name | Out-Null } }
        'toast' {
            $path = "$toastKey\$($e.name)"
            if ($null -eq $before.value) { Remove-ItemProperty -Path $path -Name Enabled -ErrorAction SilentlyContinue }
            else { Set-ItemProperty -Path $path -Name Enabled -Value $before.value -Type DWord }
        }
        'cmdpal' { if ($before.enabled) { Set-CmdPal $e.name $true } }
    }
}

function Get-Label($e) {
    switch ($e.kind) {
        'task' { return "task $($e.path)$($e.name)" }
        'run' { return "$($e.hive) Run '$($e.name)'" }
        'cmdpal' { return "Command Palette $($e.name)" }
        default { return "$($e.name) notifications" }
    }
}

if (-not ($List -or $Disable -or $Restore)) { $List = $true }

if ($List) {
    foreach ($e in $entries) {
        $s = Get-EntryState $e
        $state = if (-not $s.exists) { 'missing' } elseif ($s.enabled) { 'ON' } else { 'off' }
        '{0,-8} {1,-40} {2}' -f $state, (Get-Label $e), $e.why
    }
    $saved = (Get-ItemProperty -Path $savedKey -Name AutostartOff -ErrorAction SilentlyContinue).AutostartOff
    $savedCount = if ($saved) { @(ConvertFrom-Json $saved).Count } else { 0 }
    '{0,-8} {1,-40} {2}' -f $(if (Test-Path $guardOff) { 'off' } else { 'ON' }), 'Lighthouse autostart guard',
        "keeps the $savedCount entries saved in $savedKey off after updates"
    return
}

if (-not $WhatIf -and -not (Test-Admin)) { throw 'Run this from an admin PowerShell (HKLM and system tasks need it).' }
New-Item -ItemType Directory -Force $stateDir | Out-Null

if ($Disable) {
    # Say exactly what will be switched off and kept off, and ask: the list came from a file you don't control alone.
    'These will be switched off, and Lighthouse will keep them off:'
    foreach ($e in $entries) { "  $(Get-Label $e)$(if ($e.why) { '  (' + $e.why + ')' })" }
    if (-not $WhatIf -and -not $Yes) {
        $answer = Read-Host 'Go ahead? (y/N)'
        if ($answer -notmatch '^(y|yes)$') { 'Nothing changed.'; return }
    }
    $before = @{}
    if (Test-Path $stateFile) {
        (Get-Content $stateFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $before[$_.Name] = $_.Value }
    }
    foreach ($e in $entries) {
        $s = Get-EntryState $e
        $label = Get-Label $e
        if (-not $before.ContainsKey($label)) { $before[$label] = $s } # keep the very first state
        if (-not $s.exists) { "skip     $label (not on this PC)"; continue }
        if (-not $s.enabled) { "already  $label is off"; continue }
        if ($WhatIf) { "would    switch off $label"; continue }
        Set-EntryOff $e
        "off      $label"
    }
    if (-not $WhatIf) {
        $before | ConvertTo-Json -Depth 5 | Set-Content -Path $stateFile -Encoding UTF8
        if (Test-Path $guardOff) { Remove-Item $guardOff }
        "recorded $stateFile"
        $guarded = @($entries | Where-Object { $_.kind -ne 'cmdpal' }) # the running copy handles run, task and toast
        if (-not (Test-Path $savedKey)) { New-Item -Path $savedKey -Force | Out-Null }
        Set-ItemProperty -Path $savedKey -Name AutostartOff -Value (ConvertTo-Json -InputObject $guarded -Compress -Depth 3)
        "saved    $($guarded.Count) entries in $savedKey; Lighthouse keeps these off after updates"
    }
}

if ($Restore) {
    if (-not (Test-Path $stateFile)) { throw "No ${stateFile}: nothing was recorded, so there is nothing to restore." }
    $before = @{}
    (Get-Content $stateFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $before[$_.Name] = $_.Value }
    # Stop the guard first, or the running Lighthouse would switch everything off again.
    if ($WhatIf) { "would    create $guardOff and clear the list saved in $savedKey" }
    else {
        New-Item -ItemType Directory -Force (Split-Path $guardOff) | Out-Null
        New-Item -ItemType File -Force $guardOff | Out-Null
        Remove-ItemProperty -Path $savedKey -Name AutostartOff -ErrorAction SilentlyContinue
        "guard    off ($guardOff; saved list cleared)"
    }
    foreach ($e in $entries) {
        $label = Get-Label $e
        $b = $before[$label]
        # Before 2026-10-22 a Command Palette extension was only backed up, not recorded: the backup means it was on.
        if ($null -eq $b -and $e.kind -eq 'cmdpal' -and (Test-Path $cmdPalBackup)) { $b = @{ exists = $true; enabled = $true } }
        if ($null -eq $b -or -not $b.exists) { "skip     $label (nothing recorded)"; continue }
        if ($WhatIf) { "would    restore $label to $(if ($b.enabled) { 'ON' } else { 'off' })"; continue }
        Set-EntryBack $e $b
        "restored $label ($(if ($b.enabled) { 'ON' } else { 'off' }))"
    }
}
