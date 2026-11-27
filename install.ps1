#Requires -Version 7.0

& {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    if (-not $IsWindows) { throw 'ToolBox requires Windows.' }
    $toolboxHome = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.config\toolbox'
    $binDirectory = Join-Path $toolboxHome 'bin'
    $installedExecutable = Join-Path $binDirectory 'ToolBox.exe'
    $log = Join-Path $toolboxHome 'var\log\tasks'
    New-Item -ItemType Directory -Force -Path $binDirectory,$log,(Join-Path $toolboxHome 'etc') | Out-Null

    $download = Join-Path $binDirectory ("ToolBox-{0}.exe" -f [guid]::NewGuid())
    try {
        Invoke-WebRequest -Uri 'https://github.com/vvladz/ToolBox/releases/latest/download/ToolBox.exe' -OutFile $download
        & $download --help | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Downloaded ToolBox failed to start (exit $LASTEXITCODE). Check the .NET 11 x64 runtime." }
        [IO.File]::Move($download, $installedExecutable, $true)
    }
    finally {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download }
    }

    function Add-PathEntry {
        param([string]$Value, [string]$Directory)

        foreach ($entry in ($Value -split ';')) {
            $expanded = [Environment]::ExpandEnvironmentVariables($entry.Trim().Trim('"')).TrimEnd([char[]]'\/')
            if ($expanded.Equals($Directory.TrimEnd([char[]]'\/'), [StringComparison]::OrdinalIgnoreCase)) {
                return $Value
            }
        }
        if ([string]::IsNullOrEmpty($Value)) { return $Directory }
        return $Value.TrimEnd(';') + ';' + $Directory
    }

    $userPath = [string][Environment]::GetEnvironmentVariable('Path', [EnvironmentVariableTarget]::User)
    $updatedUserPath = Add-PathEntry -Value $userPath -Directory $binDirectory
    if ($updatedUserPath -cne $userPath) {
        [Environment]::SetEnvironmentVariable('Path', $updatedUserPath, [EnvironmentVariableTarget]::User)
    }
    $env:Path = Add-PathEntry -Value $env:Path -Directory $binDirectory

    & $installedExecutable
    if ($LASTEXITCODE -ne 0) { throw "ToolBox failed with exit code $LASTEXITCODE." }

    function New-DeploymentAction([string]$Executable, [string[]]$Arguments, [string]$Log) {
        function Literal([string]$Value) { "'" + $Value.Replace("'", "''") + "'" }
        $invocation = '& ' + (Literal $Executable)
        foreach ($argument in $Arguments) { $invocation += ' ' + (Literal $argument) }
        $command = '$ErrorActionPreference = ''Stop''; $PSNativeCommandUseErrorActionPreference = $false; ' +
            $invocation + ' >> ' + (Literal $Log) + ' 2>&1; exit $LASTEXITCODE'
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        New-ScheduledTaskAction -Execute 'pwsh' -Argument "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand $encoded" -WorkingDirectory (Split-Path -Parent $Executable)
    }

    $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 1)
    $start = New-ScheduledTaskTrigger -AtLogOn -User $user
    $start.Delay = 'PT30S'
    $update = New-ScheduledTaskTrigger -Daily -At ([datetime]::Today.AddDays(1).AddHours(3))
    $tasks = @(
        @{ Name = 'ToolBox - Start'; Trigger = $start; Arguments = @('--autostart'); Log = (Join-Path $log 'start.log') },
        @{ Name = 'ToolBox - Update'; Trigger = $update; Arguments = @(); Log = (Join-Path $log 'update.log') }
    )
    foreach ($task in $tasks) {
        $action = New-DeploymentAction -Executable $installedExecutable -Arguments $task.Arguments -Log $task.Log
        Register-ScheduledTask -TaskName $task.Name -Action $action -Trigger $task.Trigger -Principal $principal -Settings $settings -Description 'User tool environment managed by ToolBox.' -Force | Out-Null
    }

    Write-Host 'Installed ToolBox and configured the start and update tasks.'
}
