using ToolBox.Commands;
using ToolBox.Configuration;
using ToolBox.Infrastructure;
using ToolBox.Installation;

namespace ToolBox;

internal static class Cli
{
    internal static async Task<int> RunAsync(
        string[] args,
        IReadOnlyDictionary<string, ToolCommand> commands,
        string? home = null,
        CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var options = ParseOptions(args);
            if (options.Help) { PrintHelp(); return 0; }

            home ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "toolbox");
            var files = ManifestReader.Enumerate(Path.Combine(home, "etc"), options.Tool);

            Directory.CreateDirectory(Path.Combine(home, "var", "tools"));
            Directory.CreateDirectory(Path.Combine(home, "bin"));
            Directory.CreateDirectory(Path.Combine(home, "var", "log"));
            using var installLock = options.Autostart ? null : AcquireLock(Path.Combine(home, "var", "tools", ".install.lock"));

            var installed = 0;
            var unchanged = 0;
            var started = 0;
            var failed = 0;
            var updates = new List<(string Id, ToolEvent Event, string Directory)>();
            foreach (var file in files)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var manifest = ManifestReader.Read(file);
                    if (options.Autostart)
                    {
                        if (manifest.Autostart is { } start)
                        {
                            await RunEventAsync(home, manifest.Id, "autostart", start,
                                Installer.GetActiveDirectory(home, manifest.Id), cancellation.Token);
                            started++;
                        }
                        continue;
                    }
                    if (!commands.TryGetValue(manifest.Cmd, out var command))
                        throw new InvalidDataException($"Unknown cmd '{manifest.Cmd}'. Register it in Commands/CommandRegistry.cs.");
                    var result = await Installer.InstallAsync(manifest, home, command, options.Force, cancellation.Token);
                    if (result.Changed) installed++; else unchanged++;
                    if (result.VersionChanged && manifest.Update is { } update)
                        updates.Add((manifest.Id, update, result.VersionDirectory));
                    Console.Out.WriteLine(result.Changed
                        ? $"{manifest.Id}: installed {result.VersionDirectory}"
                        : $"{manifest.Id}: unchanged");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    failed++;
                    Console.Error.WriteLine($"{Path.GetFileNameWithoutExtension(file)}: {exception.Message}");
                }
            }

            foreach (var update in updates)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    await RunEventAsync(home, update.Id, "update", update.Event, update.Directory, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    failed++;
                    Console.Error.WriteLine($"{update.Id}: {exception.Message}");
                }
            }

            if (options.Autostart)
            {
                Console.Out.WriteLine($"Autostart: {started} succeeded, {failed} failed.");
                return failed == 0 ? 0 : 1;
            }
            if (files.Length == 0) Console.Out.WriteLine("No tool manifests found.");
            Console.Out.WriteLine($"Tools: {installed} installed, {unchanged} unchanged, {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Toolbox: cancelled.");
            return 130;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Toolbox: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Toolbox: {exception.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static async Task RunEventAsync(string home, string id, string name, ToolEvent command,
        string versionDirectory, CancellationToken cancellation)
    {
        var executable = Path.Combine(versionDirectory, command.Binary.Path);
        var file = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{name}-{Guid.NewGuid():N}.log";
        using var log = new RunLog(Path.Combine(home, "var", "log", id, file));
        await ProcessRunner.RunAsync(executable, command.Arguments, Path.GetDirectoryName(executable)!,
            log, name, cancellationToken: cancellation);
    }

    private sealed record Options(string? Tool, bool Force, bool Help, bool Autostart);

    private static Options ParseOptions(string[] args)
    {
        string? tool = null;
        var force = false;
        var help = false;
        var autostart = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h": help = true; break;
                case "--force": force = true; break;
                case "--autostart": autostart = true; break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException($"Unknown option: {args[i]}");
                    if (tool is not null) throw new ArgumentException("Specify one tool name, or omit it to install all tools.");
                    tool = Names.Validate(args[i], "tool name");
                    break;
            }
        }
        if (autostart && force) throw new ArgumentException("--force applies only to installation.");
        return new Options(tool, force, help, autostart);
    }

    private static FileStream AcquireLock(string file)
    {
        try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception)
        {
            throw new IOException($"Could not acquire the installation lock. Another Toolbox invocation may be running. Lock: {file}", exception);
        }
    }

    private static void PrintHelp() => Console.Out.WriteLine("""
        Toolbox — install tools from ~/.config/toolbox/etc/*.json

        ToolBox [tool] [--force]
        ToolBox [tool] --autostart

        Omit tool           Install/update every tool manifest.
        --force             Rebuild even when the active source/settings match.
        --autostart         Run the configured commands from installed versions.
        --help, -h          Show this help.

        cmd selects a source command from Commands/CommandRegistry.cs: DotNet or GitHubRelease.
        Installation paths are fixed under ~/.config/toolbox; working data is stored in var.
        """);
}
