<#
.SYNOPSIS
  Dot-sourced by install.ps1, setup-admin.ps1 and update.ps1: the logon tasks of each Windows account.

.DESCRIPTION
  Each account that installs Lighthouse gets its own tasks in \Lighthouse\<user name>\ (the app reads the same folder:
  Lighthouse.Core\Autostart\LogonTasks.cs). Before 2026-11-18 one pair sat in \Lighthouse\ itself, so installing from a
  second account replaced the first account's tasks.

  The elevated copy of PowerShell can be a different account from the one installing: on a standard account the UAC
  prompt takes an admin's password and runs as that admin. So the account is always passed in by the half that runs
  as the user, never read from $env:USERNAME in the elevated half.
#>

function Get-LighthouseTaskFolder([string]$UserName) { "\Lighthouse\$UserName\" }

function Test-LighthouseUserName([string]$UserName) {
    $UserName.Trim().Length -gt 0 -and $UserName -notmatch '[\\/]' -and $UserName.Trim('.').Length -gt 0
}

# Run as the user, not elevated: is this account an administrator? On an admin account without admin rights (UAC's
# split token) Administrators is still in the token, marked "deny only". The SID, because group names are translated.
function Test-LighthouseAdminAccount {
    [bool](whoami /groups /fo csv /nh | ConvertFrom-Csv -Header Name, Type, Sid, Attributes |
        Where-Object { $_.Sid -eq 'S-1-5-32-544' })
}

# Run as the user: are this account's tasks there, in its own folder or still in the old shared one?
function Test-LighthouseTasksFor([string]$UserName) {
    if (Get-ScheduledTask -TaskPath (Get-LighthouseTaskFolder $UserName) -TaskName 'Start at logon' -ErrorAction SilentlyContinue) { return $true }
    $shared = Get-ScheduledTask -TaskPath '\Lighthouse\' -TaskName 'Start at logon' -ErrorAction SilentlyContinue
    [bool]($shared -and ($shared.Principal.UserId -split '\\')[-1] -eq $UserName)
}

# The old shared tasks, \Lighthouse\Start at logon and \Lighthouse\Sensors, into their own account's folder.
function Move-SharedLighthouseTasks {
    foreach ($t in @(Get-ScheduledTask -TaskPath '\Lighthouse\' -ErrorAction SilentlyContinue)) {
        $owner = ($t.Principal.UserId -split '\\')[-1]
        if (-not (Test-LighthouseUserName $owner)) { "left \Lighthouse\$($t.TaskName) where it is: its account '$($t.Principal.UserId)' isn't a user name"; continue }
        $folder = Get-LighthouseTaskFolder $owner
        $xml = Export-ScheduledTask -TaskPath '\Lighthouse\' -TaskName $t.TaskName
        Register-ScheduledTask -TaskPath $folder -TaskName $t.TaskName -Xml $xml -Force | Out-Null
        Unregister-ScheduledTask -TaskPath '\Lighthouse\' -TaskName $t.TaskName -Confirm:$false
        "moved task \Lighthouse\$($t.TaskName) to $folder$($t.TaskName)"
    }
}

# An account's two logon tasks, in its own folder. The app runs with the account's own rights. Its sensor helper is
# the only part that runs as admin, because the PawnIO driver needs it: no window, no files, it answers the app on a
# pipe only that account can open. A standard account gets no helper: "highest" would give it only its own rights,
# which can't reach the driver. The account can run either task (the app starts the helper's when it isn't running)
# but only admins can change them.
function Register-LighthouseTasks([string]$Account, [string]$UserName, [string]$Exe, [bool]$AdminAccount) {
    if (-not (Test-LighthouseUserName $UserName)) { throw "Not a user name: '$UserName'" }
    $folder = Get-LighthouseTaskFolder $UserName
    $workDir = Split-Path $Exe -Parent
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
        -MultipleInstances IgnoreNew -Priority 5
    $tasks = @(
        @{ Name = 'Sensors'; Arguments = '--sensors'; Level = 'Highest'; Delay = 'PT5S'
           Description = "Lighthouse's sensor helper: reads CPU, RAM and SSD sensors through the PawnIO driver, which needs admin. No window; the Lighthouse app asks it for readings." },
        @{ Name = 'Start at logon'; Arguments = $null; Level = 'Limited'; Delay = 'PT10S'
           Description = 'Starts Lighthouse, the CPU/GPU monitor, at logon, with your own rights.' })
    foreach ($task in $tasks) {
        if ($task.Name -eq 'Sensors' -and -not $AdminAccount) {
            if (Get-ScheduledTask -TaskPath $folder -TaskName 'Sensors' -ErrorAction SilentlyContinue) {
                Unregister-ScheduledTask -TaskPath $folder -TaskName 'Sensors' -Confirm:$false
            }
            "no sensor helper task for ${Account}: a standard account (CPU, RAM and SSD sensors need an admin account)"
            continue
        }
        $action = if ($task.Arguments) { New-ScheduledTaskAction -Execute $Exe -Argument $task.Arguments -WorkingDirectory $workDir }
                  else { New-ScheduledTaskAction -Execute $Exe -WorkingDirectory $workDir }
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $Account
        $trigger.Delay = $task.Delay
        $principal = New-ScheduledTaskPrincipal -UserId $Account -LogonType Interactive -RunLevel $task.Level
        Register-ScheduledTask -TaskPath $folder -TaskName $task.Name -Action $action -Trigger $trigger `
            -Principal $principal -Settings $settings -Force -Description $task.Description | Out-Null
        "registered task $folder$($task.Name) for $Account ($($task.Level), $($task.Delay.Substring(2).ToLower()) after logon)"
    }
}

# Stops every account's Lighthouse (the files in Program Files can't be replaced while any copy runs) and returns
# the accounts it was running for, so they can be started again on the new files.
function Stop-AllLighthouse {
    $running = @(Get-Process Lighthouse -IncludeUserName -ErrorAction SilentlyContinue)
    $users = @($running | Where-Object { $_.UserName } | ForEach-Object { ($_.UserName -split '\\')[-1] } | Sort-Object -Unique)
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 20 -and (Get-Process Lighthouse -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    }
    , $users
}

# Starts an account's tasks the way logon does: the sensor helper first (if the account has one), then the app.
# They run in that account's session, so this only works while the account is signed in.
function Start-LighthouseFor([string]$UserName) {
    $folder = Get-LighthouseTaskFolder $UserName
    foreach ($name in 'Sensors', 'Start at logon') {
        if (-not (Get-ScheduledTask -TaskPath $folder -TaskName $name -ErrorAction SilentlyContinue)) { continue }
        try { Start-ScheduledTask -TaskPath $folder -TaskName $name; "started $folder$name" }
        catch { "could not start $folder${name}: $($_.Exception.Message)" }
    }
}
