#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$Repository,
    [ValidateNotNullOrEmpty()]
    [string]$Branch = 'main'
)

# Keep preferences and helper functions local when invoked through Invoke-Expression.
& {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Toolbox installation requires PowerShell 7 or later.' }
    if (-not $IsWindows) { throw 'Toolbox installation requires Windows.' }

    $gitExecutable = (Get-Command git -CommandType Application -ErrorAction Stop).Source
    $dotnetExecutable = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    $toolboxHome = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.toolbox'
    $sourceDirectory = Join-Path $toolboxHome 'src'
    $binDirectory = Join-Path $toolboxHome 'bin'

    function Invoke-Git {
        param([string[]]$Arguments)

        $output = & $gitExecutable @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Git failed with exit code $LASTEXITCODE." }
        return ($output -join [Environment]::NewLine).TrimEnd()
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

    Invoke-Git -Arguments @('check-ref-format', "refs/heads/$Branch") | Out-Null

    if ([string]::IsNullOrWhiteSpace($Repository)) {
        $Repository = if (Test-Path -LiteralPath (Join-Path $sourceDirectory '.git')) {
            Invoke-Git -Arguments @('-C', $sourceDirectory, 'config', '--get', 'remote.origin.url')
        } else {
            'https://github.com/vvladz/ToolBox.git'
        }
    }
    if (Test-Path -LiteralPath $Repository -ErrorAction SilentlyContinue) {
        $Repository = [System.IO.Path]::GetFullPath($Repository)
    }

    New-Item -ItemType Directory -Path $toolboxHome -Force | Out-Null
    if (-not (Test-Path -LiteralPath $sourceDirectory)) {
        Invoke-Git -Arguments @('clone', '--single-branch', '--no-tags', '--branch', $Branch, '--', $Repository, $sourceDirectory) | Out-Null
    }
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory '.git'))) {
        throw "The source directory must be a dedicated Git checkout: $sourceDirectory"
    }

    $actualRoot = Invoke-Git -Arguments @('-C', $sourceDirectory, 'rev-parse', '--show-toplevel')
    if (-not [System.IO.Path]::GetFullPath($actualRoot).Equals($sourceDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The source directory resolves to a different repository root.'
    }
    $origin = Invoke-Git -Arguments @('-C', $sourceDirectory, 'config', '--get', 'remote.origin.url')
    $originComparison = [StringComparison]::Ordinal
    if (Test-Path -LiteralPath $Repository -ErrorAction SilentlyContinue) {
        $origin = [System.IO.Path]::GetFullPath($origin)
        $originComparison = [StringComparison]::OrdinalIgnoreCase
    }
    if (-not $origin.Equals($Repository, $originComparison)) {
        throw 'The cached source uses a different repository. Use its existing origin URL.'
    }
    $dirty = Invoke-Git -Arguments @('-C', $sourceDirectory, 'status', '--porcelain=v1', '--untracked-files=normal')
    if ($dirty.Length -ne 0) {
        throw "The source checkout has local changes. Commit or move them before updating: $sourceDirectory"
    }

    Invoke-Git -Arguments @('-C', $sourceDirectory, 'fetch', '--no-tags', 'origin', "refs/heads/$Branch") | Out-Null
    $commit = Invoke-Git -Arguments @('-C', $sourceDirectory, 'rev-parse', '--verify', 'FETCH_HEAD^{commit}')
    Invoke-Git -Arguments @('-C', $sourceDirectory, '-c', 'advice.detachedHead=false', 'checkout', '--detach', $commit) | Out-Null

    $projectFile = Join-Path $sourceDirectory 'src\ToolBox\ToolBox.csproj'
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw "The selected revision does not contain the Toolbox project: $projectFile"
    }

    New-Item -ItemType Directory -Path $binDirectory -Force | Out-Null
    $installedExecutable = Join-Path $binDirectory 'ToolBox.exe'
    $running = Get-Process -Name ToolBox -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExecutable }
    if ($running) { throw 'Close the installed ToolBox process before updating it.' }

    Write-Host "Publishing Toolbox from $commit to $binDirectory"
    Push-Location $sourceDirectory
    try {
        & $dotnetExecutable publish $projectFile -c Release -r win-x64 --self-contained false -p:UseAppHost=true -o $binDirectory
        if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }
    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
        throw 'Publish did not produce ToolBox.exe.'
    }

    foreach ($directoryName in @('etc', 'logs', 'tools')) {
        New-Item -ItemType Directory -Path (Join-Path $toolboxHome $directoryName) -Force | Out-Null
    }

    $userPath = [string][Environment]::GetEnvironmentVariable('Path', [EnvironmentVariableTarget]::User)
    $updatedUserPath = Add-PathEntry -Value $userPath -Directory $binDirectory
    if ($updatedUserPath -cne $userPath) {
        [Environment]::SetEnvironmentVariable('Path', $updatedUserPath, [EnvironmentVariableTarget]::User)
    }
    $env:Path = Add-PathEntry -Value $env:Path -Directory $binDirectory

    Write-Host "Installed $installedExecutable"
    Write-Host 'User PATH is configured. Open a new terminal if you ran the installer in a separate PowerShell process.'
}
