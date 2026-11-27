#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\install.ps1') -Raw
# Redirect the profile and persistent PATH, and use a CMD fixture for the downloaded EXE.
$substitutions = [ordered]@{
    "[Environment]::GetFolderPath('UserProfile')" = '$script:fixtureProfile'
    '[EnvironmentVariableTarget]::User' = '[EnvironmentVariableTarget]::Process'
    "Join-Path `$binDirectory 'ToolBox.exe'" = "Join-Path `$binDirectory 'ToolBox.cmd'"
    '"ToolBox-{0}.exe"' = '"ToolBox-{0}.cmd"'
}
foreach ($change in $substitutions.GetEnumerator()) {
    Assert ($source.Contains($change.Key)) "Installer fixture cannot isolate $($change.Key)"
    $source = $source.Replace($change.Key, $change.Value)
}
$installer = [scriptblock]::Create($source)
$root = Join-Path $PSScriptRoot ("..\artifacts\installer\" + [guid]::NewGuid())
$script:fixtureProfile = [IO.Path]::GetFullPath((Join-Path $root "user's home & space"))
$config = New-Item -ItemType Directory -Force -Path (Join-Path $root 'config')
$toolboxHome = Join-Path $script:fixtureProfile '.config\toolbox'
New-Item -ItemType Directory -Force -Path $toolboxHome | Out-Null
$link = New-Item -ItemType Junction -Path (Join-Path $toolboxHome 'etc') -Target $config.FullName
$script:registered = @{}
$script:registrationCalls = 0
$script:downloadExit = 0
$script:installExit = 0

function Invoke-WebRequest([string]$Uri, [string]$OutFile) {
    Assert ($Uri -eq 'https://github.com/vvladz/ToolBox/releases/latest/download/ToolBox.exe') 'Incorrect release asset.'
    $fixture = @(
        '@echo off'
        ('if "%~1"=="--help" exit /b {0}' -f $script:downloadExit)
        'echo invoked %*>>"%~dp0calls.txt"'
        'echo standard output'
        'echo standard error 1>&2'
        ('exit /b {0}' -f $script:installExit)
    ) -join "`r`n"
    [IO.File]::WriteAllText($OutFile, $fixture)
}

function New-ScheduledTaskPrincipal([string]$UserId, [ValidateSet('Interactive')][string]$LogonType, [ValidateSet('Limited')][string]$RunLevel) {
    [pscustomobject]@{ UserId = $UserId; LogonType = $LogonType; RunLevel = $RunLevel }
}
function New-ScheduledTaskSettingsSet([ValidateSet('IgnoreNew')][string]$MultipleInstances,
    [switch]$StartWhenAvailable, [switch]$AllowStartIfOnBatteries, [switch]$DontStopIfGoingOnBatteries, [timespan]$ExecutionTimeLimit) {
    [pscustomobject]@{ MultipleInstances = $MultipleInstances; StartWhenAvailable = $StartWhenAvailable; ExecutionTimeLimit = $ExecutionTimeLimit }
}
function New-ScheduledTaskTrigger([switch]$AtLogOn, [string]$User, [switch]$Daily, [datetime]$At) {
    [pscustomobject]@{ AtLogOn = $AtLogOn; UserId = $User; Daily = $Daily; StartBoundary = $At; Delay = $null }
}
function New-ScheduledTaskAction([string]$Execute, [string]$Argument, [string]$WorkingDirectory) {
    [pscustomobject]@{ Execute = $Execute; Arguments = $Argument; WorkingDirectory = $WorkingDirectory }
}
function Register-ScheduledTask([string]$TaskName, [object]$Action, [object]$Trigger, [object]$Principal, [object]$Settings, [string]$Description, [switch]$Force) {
    Assert $Force 'Deployment must refresh its named tasks.'
    $script:registrationCalls++
    $script:registered[$TaskName] = [pscustomobject]@{ Action = $Action; Trigger = $Trigger; Principal = $Principal; Settings = $Settings }
}

$originalPath = $env:Path
try {
    & $installer
    & $installer
    Assert ($script:registrationCalls -eq 4 -and $script:registered.Count -eq 2) 'Expected both tasks to be refreshed.'
    $bin = Join-Path $toolboxHome 'bin'
    Assert (@(($env:Path -split ';') | Where-Object { $_ -eq $bin }).Count -eq 1) 'Expected one bin entry in PATH.'
    Assert ((Get-Item -LiteralPath $link.FullName).ResolveLinkTarget($true).FullName -eq $config.FullName) 'Configuration junction changed.'
    $start = $script:registered['ToolBox - Start']
    $update = $script:registered['ToolBox - Update']
    Assert ($start.Trigger.AtLogOn -and $start.Trigger.Delay -eq 'PT30S') 'Invalid sign-in trigger.'
    Assert ($update.Trigger.Daily -and $update.Trigger.StartBoundary -eq [datetime]::Today.AddDays(1).AddHours(3)) 'Invalid update trigger.'
    foreach ($task in @($start,$update)) {
        Assert ($task.Principal.LogonType -eq 'Interactive' -and $task.Principal.RunLevel -eq 'Limited') 'Incorrect principal.'
        $arguments = $task.Action.Arguments -split ' '
        & $task.Action.Execute @arguments
        Assert ($LASTEXITCODE -eq 0) 'Task failed.'
    }
    $calls = @(Get-Content -LiteralPath (Join-Path $bin 'calls.txt'))
    Assert ($calls.Count -eq 4 -and $calls[2].TrimEnd() -eq 'invoked --autostart' -and $calls[3].TrimEnd() -eq 'invoked') 'Unexpected task commands.'
    foreach ($file in @('start.log', 'update.log')) {
        $output = Get-Content -LiteralPath (Join-Path $toolboxHome "var\log\tasks\$file") -Raw
        Assert ($output.Contains('standard output') -and $output.Contains('standard error')) 'Task output missing.'
    }

    $installed = Join-Path $bin 'ToolBox.cmd'
    $original = Get-Content -LiteralPath $installed -Raw
    $script:downloadExit = 8
    try { & $installer; throw 'Expected a download validation failure.' }
    catch { if ($_.Exception.Message -notmatch 'exit 8') { throw } }
    Assert ((Get-Content -LiteralPath $installed -Raw) -ceq $original) 'Failed download replaced the installed tool.'
    $script:downloadExit = 0
    $script:installExit = 7
    try { & $installer; throw 'Expected a batch failure.' }
    catch { if ($_.Exception.Message -notmatch 'exit code 7') { throw } }
    Assert ($script:registrationCalls -eq 4) 'Failed installation refreshed the tasks.'
    $arguments = $start.Action.Arguments -split ' '
    & $start.Action.Execute @arguments
    Assert ($LASTEXITCODE -eq 7) 'Task lost native exit code.'
    Write-Host 'PASS release bootstrap, repeated deployment, both scheduled actions and failure handling'
}
finally { $env:Path = $originalPath }
exit 0
