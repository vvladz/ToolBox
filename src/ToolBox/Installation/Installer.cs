using System.Globalization;
using System.Text;
using ToolBox.Commands;
using ToolBox.Configuration;
using ToolBox.Infrastructure;

namespace ToolBox.Installation;

internal static class Installer
{
    // The caller holds the global installation lock across preparation and activation.
    internal static async Task<InstallResult> InstallAsync(
        ToolManifest manifest,
        string home,
        ToolCommand command,
        bool force,
        CancellationToken cancellationToken)
    {
        Names.Validate(manifest.Id, "tool ID");
        manifest = manifest with { Bin = Names.Executables(manifest.Bin) };
        home = Path.GetFullPath(home);
        var attempt = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8];
        var toolDirectory = Path.Combine(home, "var", "tools", manifest.Id);
        var versions = Path.Combine(toolDirectory, "versions");
        var installedFile = Path.Combine(toolDirectory, "installed");
        var bin = Path.Combine(home, "bin");
        var logFile = Path.Combine(home, "var", "log", manifest.Id, attempt + ".log");

        Directory.CreateDirectory(toolDirectory);
        Directory.CreateDirectory(bin);
        using var log = new RunLog(logFile);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.Info($"Preparing {manifest.Id} with command '{manifest.Cmd}'. Log: {logFile}");
            var installedDirectory = ReadInstalledDirectory(installedFile, versions);
            AssertLaunchers(manifest.Bin, bin, toolDirectory);

            var context = new CommandContext(manifest.Id, toolDirectory, installedDirectory,
                Path.Combine(toolDirectory, "work", attempt), attempt, manifest.Args, force, log, cancellationToken);
            var prepared = await command(context);
            cancellationToken.ThrowIfCancellationRequested();

            return prepared switch
            {
                Unchanged => CompleteUnchanged(manifest, bin, toolDirectory, installedFile, versions, log),
                Prepared payload => CompletePrepared(manifest, payload, bin, installedFile,
                    versions, toolDirectory, attempt, log, cancellationToken),
                null => throw new InvalidOperationException("The command returned no preparation result.")
            };
        }
        catch (OperationCanceledException exception)
        {
            log.Warn("Installation cancelled.");
            throw new OperationCanceledException($"{manifest.Id}: cancelled. Log: {logFile}",
                exception, cancellationToken);
        }
        catch (Exception exception)
        {
            log.Error(exception.ToString());
            throw new InvalidOperationException($"{manifest.Id}: {exception.Message} Log: {logFile}", exception);
        }
    }

    private static InstallResult CompleteUnchanged(
        ToolManifest manifest, string bin, string toolDirectory, string installedFile, string versions, RunLog log)
    {
        var target = ReadInstalledDirectory(installedFile, versions);
        if (target is null || !Directory.Exists(target))
            throw new InvalidOperationException("The command returned unchanged, but no active installation exists.");

        AssertExecutables(target, manifest.Bin);
        var created = new List<(string Path, string Content)>();
        try
        {
            AddLaunchers(manifest.Bin, bin, toolDirectory, created);
        }
        catch
        {
            UndoNewLaunchers(created, log);
            throw;
        }

        log.Info($"{manifest.Id} is unchanged; command launchers are present.");
        return new InstallResult(false, target, log.FilePath, false);
    }

    private static InstallResult CompletePrepared(
        ToolManifest manifest, Prepared prepared, string bin, string installedFile, string versions,
        string toolDirectory, string attempt, RunLog log, CancellationToken cancellationToken)
    {
        var payload = prepared.Directory;
        if (string.IsNullOrWhiteSpace(payload) || !Path.IsPathFullyQualified(payload) || !Directory.Exists(payload))
            throw new InvalidOperationException("A prepared result must contain an absolute path to an existing directory.");

        payload = Path.GetFullPath(payload);
        var version = Path.Combine(versions, attempt);
        var relative = Path.GetRelativePath(payload, version);
        if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new InvalidOperationException("The prepared directory cannot contain its installation destination.");

        Directory.CreateDirectory(versions);
        RequireAbsent(version);
        log.Info($"Copying prepared files to {version}.");
        CopyDirectory(payload, version, cancellationToken);
        AssertExecutables(version, manifest.Bin);
        cancellationToken.ThrowIfCancellationRequested();

        var pending = Path.Combine(toolDirectory, "installed.next." + attempt);
        RequireAbsent(pending);
        var created = new List<(string Path, string Content)>();

        try
        {
            // Publish the version name only after its files and launchers are ready.
            ReadInstalledDirectory(installedFile, versions);
            AssertLaunchers(manifest.Bin, bin, toolDirectory);
            File.WriteAllText(pending, attempt + "\n", new UTF8Encoding(false));
            AddLaunchers(manifest.Bin, bin, toolDirectory, created);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, installedFile, overwrite: true);
        }
        catch
        {
            UndoNewLaunchers(created, log);
            throw;
        }
        finally
        {
            try { File.Delete(pending); }
            catch (Exception exception)
            {
                log.Warn($"Could not remove the temporary version file: {pending}. {exception.Message}");
            }
        }

        log.Info($"Activated {manifest.Id}: {version}");
        return new InstallResult(true, version, log.FilePath, prepared.VersionChanged);
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = new DirectoryInfo(source);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Prepared payloads cannot contain symbolic links or other reparse points: {source}");

        Directory.CreateDirectory(destination);
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Prepared payloads cannot contain symbolic links or other reparse points: {item.FullName}");
            var target = Path.Combine(destination, item.Name);
            if (item is DirectoryInfo)
                CopyDirectory(item.FullName, target, cancellationToken);
            else
                File.Copy(item.FullName, target, overwrite: false);
        }
    }

    private static void AssertExecutables(string directory, IReadOnlyList<ToolBinary> executables)
    {
        foreach (var executable in executables)
        {
            if (!File.Exists(Path.Combine(directory, executable.Path)))
                throw new IOException($"Installation does not contain '{executable.Path}': {directory}");
        }
    }

    internal static string GetActiveDirectory(string home, string id)
    {
        var tool = Path.Combine(home, "var", "tools", id);
        return ReadInstalledDirectory(Path.Combine(tool, "installed"), Path.Combine(tool, "versions"))
            ?? throw new InvalidOperationException($"{id} is not installed.");
    }

    private static string? ReadInstalledDirectory(string installedFile, string versions)
    {
        var entry = ReadEntry(installedFile);
        if (entry is null)
            return null;
        if (entry.IsDirectory || entry.LinkTarget is not null)
            throw new IOException($"The installed version must be stored in a regular file: {installedFile}");

        using var stream = new FileStream(installedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var version = reader.ReadToEnd().TrimEnd('\r', '\n');
        if (version.Length == 0) return null;
        Names.Validate(version, "installed version");
        var target = Path.Combine(versions, version);
        var versionEntry = ReadEntry(target);
        if (versionEntry is not null && (!versionEntry.IsDirectory || versionEntry.LinkTarget is not null))
            throw new IOException($"The installed version must name a version directory: {target}");
        return target;
    }

    private static void AssertLaunchers(IReadOnlyList<ToolBinary> executables, string bin, string toolDirectory)
    {
        foreach (var executable in executables)
        {
            var path = LauncherPath(bin, executable);
            var entry = ReadEntry(path);
            if (entry is null)
                continue;
            if (entry.IsDirectory || entry.LinkTarget is not null
                || File.ReadAllText(path) != LauncherContent(bin, toolDirectory, executable.Path))
                throw new IOException($"PATH name is occupied by another file or launcher: {path}");
        }
    }

    private static void AddLaunchers(IReadOnlyList<ToolBinary> executables, string bin, string toolDirectory,
        List<(string Path, string Content)> created)
    {
        foreach (var executable in executables)
        {
            var path = LauncherPath(bin, executable);
            if (ReadEntry(path) is null)
            {
                var content = LauncherContent(bin, toolDirectory, executable.Path);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(content);
                writer.Flush();
                created.Add((path, content));
            }
            else
            {
                AssertLaunchers([executable], bin, toolDirectory);
            }
        }
    }

    private static void UndoNewLaunchers(List<(string Path, string Content)> created, RunLog log)
    {
        foreach (var (path, content) in created)
        {
            try
            {
                var entry = ReadEntry(path);
                if (entry is { IsDirectory: false, LinkTarget: null } && File.ReadAllText(path) == content)
                    File.Delete(path);
            }
            catch (Exception exception)
            {
                log.Warn($"Could not remove a newly created command launcher: {path}. {exception.Message}");
            }
        }
    }

    private static string LauncherPath(string bin, ToolBinary executable) =>
        Path.Combine(bin, executable.Alias + ".cmd");

    private static string LauncherContent(string bin, string toolDirectory, string executable)
    {
        var directory = Path.GetRelativePath(bin, toolDirectory)
            .Replace('/', '\\').Replace("%", "%%");
        var target = executable.Replace('/', '\\').Replace("%", "%%");
        var toolId = Path.GetFileName(toolDirectory).Replace("%", "%%");
        return "@echo off\r\nsetlocal DisableDelayedExpansion\r\n"
            + $"set \"_toolbox_dir=%~dp0{directory}\"\r\n"
            + "set \"_toolbox_version=\"\r\n"
            + "if not exist \"%_toolbox_dir%\\installed\" goto not_installed\r\n"
            + "set /p \"_toolbox_version=\"<\"%_toolbox_dir%\\installed\"\r\n"
            + "if not defined _toolbox_version goto not_installed\r\n"
            + $"if not exist \"%_toolbox_dir%\\versions\\%_toolbox_version%\\{target}\" goto not_installed\r\n"
            + $"\"%_toolbox_dir%\\versions\\%_toolbox_version%\\{target}\" %*\r\n"
            + "exit /b %errorlevel%\r\n"
            + ":not_installed\r\n"
            + $">&2 echo Toolbox: \"{toolId}\" is not installed.\r\n"
            + "exit /b 1\r\n";
    }

    private static void RequireAbsent(string path)
    {
        if (ReadEntry(path) is not null)
            throw new IOException($"Installation path already exists: {path}");
    }

    private sealed record Entry(bool IsDirectory, string? LinkTarget);

    private static Entry? ReadEntry(string path)
    {
        try
        {
            // File.GetAttributes also sees dangling links; Exists alone would miss them.
            var attributes = File.GetAttributes(path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            return new Entry(isDirectory, info.LinkTarget);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
