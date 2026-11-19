using ToolBox.Commands;
using ToolBox.Configuration;
using ToolBox.Infrastructure;
using ToolBox.Installation;

namespace ToolBox;

internal static class Cli
{
    internal static async Task<int> RunAsync(
        string[] args,
        IReadOnlyDictionary<string, ToolCommand> commands)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var options = ParseOptions(args);
            if (options.Help) { PrintHelp(); return 0; }

            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".toolbox");
            var manifests = ManifestReader.ReadAll(Path.Combine(home, "etc"), options.Tool);
            foreach (var manifest in manifests)
                if (!commands.ContainsKey(manifest.Cmd))
                    throw new InvalidDataException($"{manifest.Id}: unknown cmd '{manifest.Cmd}'. Register it in Commands/CommandRegistry.cs.");

            Directory.CreateDirectory(Path.Combine(home, "tools"));
            Directory.CreateDirectory(Path.Combine(home, "bin"));
            Directory.CreateDirectory(Path.Combine(home, "logs"));
            using var installLock = AcquireLock(Path.Combine(home, "tools", ".install.lock"));

            foreach (var manifest in manifests)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var result = await Installer.InstallAsync(manifest, home, commands[manifest.Cmd], options.Force, cancellation.Token);
                Console.Out.WriteLine(result.Changed
                    ? $"{manifest.Id}: installed {result.VersionDirectory}"
                    : $"{manifest.Id}: unchanged");
            }

            if (manifests.Count == 0) Console.Out.WriteLine("No tool manifests found.");
            return 0;
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

    private sealed record Options(string? Tool, bool Force, bool Help);

    private static Options ParseOptions(string[] args)
    {
        string? tool = null;
        var force = false;
        var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h": help = true; break;
                case "--force": force = true; break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException($"Unknown option: {args[i]}");
                    if (tool is not null) throw new ArgumentException("Specify one tool name, or omit it to install all tools.");
                    tool = Names.Validate(args[i], "tool name");
                    break;
            }
        }
        return new Options(tool, force, help);
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
        Toolbox — install tools from ~/.toolbox/etc/*.json

        ToolBox [tool] [--force]

        Omit tool           Install/update every tool manifest.
        --force             Rebuild even when the active source/settings match.
        --help, -h          Show this help.

        cmd selects a source command from Commands/CommandRegistry.cs: DotNet or GitHubRelease.
        Installation paths are fixed under ~/.toolbox.
        """);
}
