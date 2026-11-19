# Toolbox

Toolbox installs and updates command-line tools on Windows. It can publish .NET projects from a Git repository or download EXE and ZIP assets from GitHub releases. Each tool is described by a JSON manifest, and each installation keeps the complete application directory, including configuration and resources.

Installed tools are launched through small `.cmd` files. Updates select a new version without overwriting files from the previous version.

## Requirements

- Windows x64, PowerShell 7, Git, and .NET SDK **11.0.100 or later**. `global.json` selects the latest compatible installed stable SDK with `rollForward: latestMajor`.
- .NET 11 runtime for the published Toolbox application. It is included with the .NET 11 SDK.
- For the `DotNet` source: Git, the SDK required by the source repository, and the runtime needed by the published application.
- Access to the repository or release selected in each manifest.

Toolbox is a .NET console application. The installer publishes a framework-dependent single-file executable, which uses the installed .NET runtime.

## Install and update Toolbox

Run this command in **PowerShell 7** from any directory:

```powershell
iex (irm https://raw.githubusercontent.com/vvladz/ToolBox/main/install.ps1)
```

The script downloads and builds the source from the public [ToolBox repository](https://github.com/vvladz/ToolBox). A separate checkout is not required. Run the same command again to update.

You can also run the installer from a local checkout:

```powershell
git clone https://github.com/vvladz/ToolBox.git
Set-Location ToolBox
pwsh -File .\install.ps1
```

The installer clones or updates Toolbox's source checkout at `~/.config/toolbox/var/src`, publishes `src/ToolBox/ToolBox.csproj` into `~/.config/toolbox/bin`, and adds that directory to your user PATH. The publish output is a single `ToolBox.exe`, including its managed assemblies, runtime configuration, and debug symbols. The .NET runtime is not bundled. Other tools' `.cmd` launchers are preserved.

Run `ToolBox --help` in the same PowerShell session after the one-line installation. If you ran the installer in a separate PowerShell process, open a new terminal first.

To update an existing installation:

```powershell
pwsh -File "$env:USERPROFILE\.config\toolbox\var\src\install.ps1"
```

The repository URL defaults to the cached checkout's `origin`, or `https://github.com/vvladz/ToolBox.git` on first installation. When running the script from a file, use `-Repository <git-url>` to supply another URL. `-Branch` defaults to `main`. Git uses your existing SSH or HTTPS credentials when needed. The source checkout is updated to the selected branch's commit; local changes are never discarded. Close the installed ToolBox process before updating its executable.

## Quick start

In this guide, `ToolBox` is the directory containing this checkout, and `~` is your Windows user profile. `Tool`, `ToolOne`, and `ToolTwo` are example application names. Replace repository placeholders and project paths with your own values.

1. After installing Toolbox, create the manifest directory if needed:

   ```powershell
   New-Item -ItemType Directory -Path "$env:USERPROFILE\.config\toolbox\etc" -Force
   ```

2. Save this as `~/.config/toolbox/etc/Tool.json` to build a .NET tool from Git:

   ```json
   {
     "cmd": "DotNet",
     "bin": ["Tool.exe"],
     "args": {
       "repository": "<git-repository-url>",
       "branch": "main",
       "project": "src/Tool/Tool.csproj",
       "runtime": "win-x64"
     }
   }
   ```

3. Install the tool:

   ```powershell
   ToolBox Tool
   ```

4. Run the installed application:

   ```powershell
   & "$env:USERPROFILE\.config\toolbox\bin\Tool.cmd"
   ```

The installer adds `%USERPROFILE%\.config\toolbox\bin` to PATH, so installed commands and aliases can be invoked by name from a new terminal.

## Commands

```powershell
ToolBox Tool          # install or update one tool
ToolBox               # install or update every manifest
ToolBox Tool --force  # rebuild or download again
ToolBox --help
```

All configuration and installation paths are fixed under `~/.config/toolbox`. The CLI preserves the calling shell's working directory. PATH setup belongs to `install.ps1`.

## Manifests and command aliases

Toolbox reads `*.json` files directly inside `~/.config/toolbox/etc`. The filename without `.json` is the tool ID. You can keep this directory in a separate Git repository.

| Field | Purpose |
| --- | --- |
| `cmd` | Registered version source command: `DotNet` or `GitHubRelease` |
| `args` | Parameters for the selected source command |
| `bin` | EXEs to expose as commands; optional |

There are two forms of `bin`.

An array uses each EXE's filename without its extension as the command name:

```json
"bin": ["app/Tool.exe"]
```

This creates `~/.config/toolbox/bin/Tool.cmd`.

An object maps command aliases to relative EXE paths:

```json
"bin": {
  "tool": "app/Tool.exe",
  "t": "app/Tool.exe"
}
```

This creates `tool.cmd` and `t.cmd`, both launching the same EXE. The keys are launcher names; Toolbox appends `.cmd`. The object form creates exactly the aliases you list. Different aliases can also expose different EXEs that share the same filename.

EXE paths are relative to the installed directory. Both `/` and `\` are supported; escape a backslash as `\\` in JSON. Absolute paths, `.` and `..` are rejected. Aliases must be plain filenames without directory components and must be unique, ignoring case. Existing files belonging to another tool or user are not replaced.

`bin` controls only the launchers. The entire prepared directory is installed, including DLLs, configuration, resources, and subdirectories. You can omit `bin`, use an empty array, or use an empty object to install files without creating commands.

## Version sources

### DotNet

The `DotNet` command builds one or more executable projects from the same Git repository.

| Argument | Purpose |
| --- | --- |
| `repository` | Git repository URL or absolute path to a local repository |
| `branch` | Branch to build |
| `project` | One project path relative to the repository root; publishes directly into the installed directory |
| `projects` | A nonempty array of project paths; publishes each project into its own subdirectory |
| `runtime` | RID; defaults to `win-x64` and applies to every selected project |

Specify exactly one of `project` or `projects`.

For multiple projects, a manifest can expose each application under an alias:

```json
{
  "cmd": "DotNet",
  "bin": {
    "one": "ToolOne/ToolOne.exe",
    "two": "ToolTwo/ToolTwo.exe"
  },
  "args": {
    "repository": "<git-repository-url>",
    "branch": "main",
    "projects": [
      "src/ToolOne/ToolOne.csproj",
      "src/ToolTwo/ToolTwo.csproj"
    ],
    "runtime": "win-x64"
  }
}
```

Each output subdirectory is named after the project file without its extension. For example, `src/ToolOne/ToolOne.csproj` publishes into `ToolOne/`. These names must be distinct, ignoring case. The `projects` form uses subdirectories even for a single entry.

Projects are published sequentially from a dedicated checkout. Each output directory keeps its own DLLs, configuration, and resources. The installer selects the new version only after every project has been published successfully.

Publishing uses Release configuration with `UseAppHost=true` and `SelfContained=false`. The command does not force single-file publishing; the source project's setting applies. The .NET runtime is not bundled. The complete publish directory is installed, including EXEs, DLLs, configuration, and resources.

An unchanged commit, project list, output layout, build settings, and recorded files allow publishing to be skipped. Local checkout changes are not discarded; submodules are not supported. Use `--force` after changing the SDK or command implementation when the recorded build settings remain the same.

### GitHubRelease

Use `GitHubRelease` to install an EXE or ZIP from the latest stable release. Replace `<owner>/<repository>` and the asset pattern with your values:

```json
{
  "cmd": "GitHubRelease",
  "bin": { "tool": "app/Tool.exe" },
  "args": {
    "repository": "<owner>/<repository>",
    "assetPattern": "Tool-*-win-x64.zip"
  }
}
```

`assetPattern` supports `*` and `?` and must match exactly one release asset. ZIP contents are installed in full. The optional `archiveDirectory` selects a subdirectory inside the archive; `bin` paths are then relative to that subdirectory. For a standalone EXE, the optional `executableName` sets its installed filename.

Public releases do not require a token; set `GITHUB_TOKEN` if authentication is needed. Matching asset metadata and installed files allow the download to be skipped. `--force` downloads the asset again.

Unknown or duplicate manifest fields and source arguments are rejected.

## Directories and updates

The source checkout contains:

```text
ToolBox/
  ToolBox.slnx
  global.json           # SDK used by the solution
  install.ps1           # source installation/update and user PATH setup
  src/ToolBox/
    ToolBox.csproj
    Program.cs
    Cli.cs              # command-line handling
    Commands/           # source commands, registry, and command contracts
    Configuration/      # manifest models and parsing
    Installation/       # version installation and launchers
    Infrastructure/     # Git, process execution, logging, and validation
  tests/ToolBox.Tests/   # xUnit regression tests
```

User configuration and installed files live separately:

```text
~/.config/toolbox/
  etc/                  # JSON manifests, such as Tool.json
  bin/                  # single-file ToolBox.exe and .cmd launchers
  var/
    log/<tool>/         # one log per installation attempt
    src/                # complete Toolbox Git checkout
      src/ToolBox/      # application project inside that checkout
    tools/<tool>/
      source/           # Git checkout for DotNet
      work/<attempt>/   # build or download output
      versions/<attempt>/ # complete installed directory
      installed         # selected version directory name, plain text
```

`src/ToolBox/Commands/CommandRegistry.cs` registers the application's source commands. User manifests remain in `~/.config/toolbox/etc`.

`installed` contains one UTF-8 line without a BOM: the selected directory name under `versions`. Each launcher reads it once, starts the EXE from that directory, forwards arguments, and returns its exit code. The caller's working directory is preserved. Applications can read files next to their EXE.

Updates fully prepare and check a new version before replacing `installed` through a temporary file. Launchers stay unchanged while their aliases and EXE paths stay the same. Missing launchers are restored even when no update is needed. If `installed` is missing or empty, or the selected EXE is missing, a launcher reports that the tool is not installed and exits with code `1`.

A shared lock at `var/tools/.install.lock` prevents concurrent installations. Running programs are not stopped. Old versions, work directories, and logs are retained. Toolbox creates no symbolic links and does not manage daemons.

## Adding a source command

Adding a tool that uses an existing source command requires only a JSON manifest. To add a new installation method, create a C# file in `src/ToolBox/Commands`, register it in `CommandRegistry.cs`, and rebuild Toolbox. The project includes C# source files automatically.

The handler signature is `Task<PrepareResult> PrepareAsync(CommandContext context)`. `context.Args` contains the manifest arguments. `context.InstalledDirectory` is the selected version's path, or `null` when none is selected. The context also provides the tool directory, an attempt-specific work directory, `Force`, `Log`, and `CancellationToken`.

Return `new Prepared(absoluteDirectory)` when files are ready to install, or `new Unchanged()` when the installed version already matches the source. Throw an exception if preparation fails. Prepared directories must not contain symlinks or other reparse points.

All source commands are compiled into one C# application. JSON manifests are read at runtime.

## Build and test

From the repository root:

```powershell
dotnet build ToolBox.slnx -c Release
dotnet test ToolBox.slnx -c Release
dotnet run --project src/ToolBox -- --help
```

To publish the application to a separate directory for inspection:

```powershell
dotnet publish src/ToolBox/ToolBox.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=embedded -o artifacts/publish
```

## Logs and exit codes

Installation results go to stdout. Events and errors go to stderr. Full Git and .NET output is written to `var/log/<tool>/<attempt>.log`; installation errors include that path.

| Exit code | Meaning |
| --- | --- |
| `0` | Success, including no update needed |
| `1` | Manifest, preparation, or installation error |
| `2` | Invalid CLI options |
| `130` | Cancellation |
