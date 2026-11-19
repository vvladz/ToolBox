using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Toolbox;

internal static class DotNet
{
    private const string ReceiptFileName = ".build-info.json";
    private const string Builder = "dotnet-publish.v5";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static Task<PrepareResult> PrepareAsync(CommandContext context)
    {
        JsonFields.Validate(context.Args, "DotNet args", "repository", "branch", "project", "projects", "runtime");
        var repository = JsonFields.RequiredString(context.Args, "repository");
        var branch = JsonFields.RequiredString(context.Args, "branch");
        var runtime = JsonFields.OptionalString(context.Args, "runtime") ?? "win-x64";
        var project = JsonFields.OptionalString(context.Args, "project");
        var hasProjects = context.Args.TryGetProperty("projects", out var projects);
        if ((project is not null) == hasProjects)
            throw new InvalidDataException("DotNet args must specify exactly one of 'project' or 'projects'.");
        if (!hasProjects) return PrepareAsync(context, repository, branch, project!, runtime);
        if (projects.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("'projects' must be a nonempty array of project paths.");
        var paths = projects.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidDataException("'projects' entries must be nonempty project paths.");
            return item.GetString()!;
        }).ToArray();
        return PrepareAsync(context, repository, branch, paths, runtime);
    }

    internal static Task<PrepareResult> PrepareAsync(
        CommandContext context,
        string repository,
        string branch,
        string project,
        string runtime = "win-x64") =>
        PrepareProjectsAsync(context, repository, branch, [project], runtime, separateDirectories: false);

    internal static Task<PrepareResult> PrepareAsync(
        CommandContext context,
        string repository,
        string branch,
        IReadOnlyList<string> projects,
        string runtime = "win-x64") =>
        PrepareProjectsAsync(context, repository, branch, projects, runtime, separateDirectories: true);

    private static async Task<PrepareResult> PrepareProjectsAsync(
        CommandContext context,
        string repository,
        string branch,
        IReadOnlyList<string> projects,
        string runtime,
        bool separateDirectories)
    {
        if (projects.Count == 0 || projects.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("'projects' must contain at least one nonempty project path.");
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);

        var previous = await ReadReceiptAsync(context);
        var source = await Git.SyncAsync(repository, branch,
            Path.Combine(context.ToolDirectory, "source"), previous?.Commit ?? string.Empty,
            context.Log, context.CancellationToken);

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedProjects = projects.Select(project =>
        {
            var projectPath = Path.GetFullPath(project, source.SourceDirectory);
            var relative = Path.GetRelativePath(source.SourceDirectory, projectPath).Replace('\\', '/');
            if (Path.IsPathRooted(project) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
                throw new InvalidOperationException("Each project must be a path inside the source repository.");
            if (!File.Exists(projectPath))
                throw new FileNotFoundException($"Project was not found: {relative}", projectPath);
            var directory = separateDirectories
                ? Names.Validate(Path.GetFileNameWithoutExtension(projectPath), "project output directory")
                : string.Empty;
            if (!directories.Add(directory))
                throw new InvalidDataException($"Projects must have distinct filenames without extensions: {directory}");
            return new ProjectBuild(relative, directory);
        }).ToArray();

        var expected = new BuildInfo
        {
            Builder = Builder,
            Repository = repository,
            Branch = branch,
            Commit = source.Commit,
            Projects = selectedProjects,
            Runtime = runtime,
            Configuration = "Release",
            SingleFile = true,
            SelfContained = false,
            UseAppHost = true
        };

        if (!context.Force && !source.Changed && previous is not null && context.InstalledDirectory is { } installedDirectory &&
            SameSettings(previous, expected) && InstalledFilesExist(previous, installedDirectory))
        {
            context.Log.Info("Source and build settings match the active installation; build skipped.");
            return new Unchanged();
        }

        var publishDirectory = Path.Combine(context.WorkDirectory, "publish");
        if (Path.Exists(publishDirectory))
            throw new InvalidOperationException("The build attempt must use a fresh work directory.");

        Directory.CreateDirectory(publishDirectory);
        foreach (var project in selectedProjects)
        {
            var projectPath = Path.Combine(source.SourceDirectory, project.Project);
            var outputDirectory = Path.Combine(publishDirectory, project.Directory);
            context.Log.Info($"Publishing {project.Project} ({source.Commit}).");

            // Source SDK selection is independent from the SDK running Toolbox.
            // The source repository's global.json remains authoritative.
            await ProcessRunner.RunAsync("dotnet",
                ["publish", projectPath, "-c", "Release", "-r", runtime,
                 "-p:SelfContained=false", "-p:PublishSelfContained=false",
                 "-p:PublishSingleFile=true", "-p:UseAppHost=true", "-o", outputDirectory],
                source.SourceDirectory, context.Log, "publish",
                cancellationToken: context.CancellationToken);

            if (!Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                .Any(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Publish did not produce any EXE files for {project.Project}.");
        }

        if (Path.Exists(Path.Combine(publishDirectory, ReceiptFileName)))
            throw new InvalidDataException($"The publish output contains the reserved name '{ReceiptFileName}'.");
        var files = Directory.EnumerateFiles(publishDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new PublishedFile(Path.GetRelativePath(publishDirectory, path).Replace('\\', '/'), new FileInfo(path).Length))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        // Only a completed payload receives metadata; activation is the manager's job.
        var receipt = expected with { Files = files };
        await File.WriteAllTextAsync(Path.Combine(publishDirectory, ReceiptFileName),
            JsonSerializer.Serialize(receipt, JsonOptions) + "\n", new UTF8Encoding(false),
            context.CancellationToken);
        context.Log.Info($"Prepared {files.Length} published file(s).");
        return new Prepared(publishDirectory);
    }

    private static async Task<BuildInfo?> ReadReceiptAsync(CommandContext context)
    {
        if (context.InstalledDirectory is not { } directory) return null;
        var path = Path.Combine(directory, ReceiptFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<BuildInfo>(stream, JsonOptions,
                context.CancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            context.Log.Warn("Installed build metadata is unreadable; a new build is required.");
            return null;
        }
    }

    private static bool SameSettings(BuildInfo actual, BuildInfo expected) =>
        actual.Builder == expected.Builder &&
        actual.Repository == expected.Repository &&
        actual.Branch == expected.Branch &&
        actual.Commit == expected.Commit &&
        actual.Projects is { Length: > 0 } && actual.Projects.SequenceEqual(expected.Projects) &&
        actual.Runtime == expected.Runtime &&
        actual.Configuration == expected.Configuration &&
        actual.SingleFile == expected.SingleFile &&
        actual.SelfContained == expected.SelfContained &&
        actual.UseAppHost == expected.UseAppHost;

    private static bool InstalledFilesExist(BuildInfo receipt, string directory)
    {
        if (receipt.Files is not { Length: > 0 }) return false;
        try
        {
            return receipt.Files.All(file => file is not null && !string.IsNullOrWhiteSpace(file.Path) && file.Length >= 0 &&
                new FileInfo(Path.Combine(directory, Names.RelativePath(file.Path, "published file")))
                    is { Exists: true } info && info.Length == file.Length);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record PublishedFile(string Path, long Length);

    private sealed record ProjectBuild(string Project, string Directory);

    private sealed record BuildInfo
    {
        public string Builder { get; init; } = string.Empty;
        public string Repository { get; init; } = string.Empty;
        public string Branch { get; init; } = string.Empty;
        public string Commit { get; init; } = string.Empty;
        public ProjectBuild[] Projects { get; init; } = [];
        public string Runtime { get; init; } = string.Empty;
        public string Configuration { get; init; } = string.Empty;
        public bool SingleFile { get; init; }
        public bool SelfContained { get; init; }
        public bool UseAppHost { get; init; }
        public PublishedFile[] Files { get; init; } = [];
    }
}
