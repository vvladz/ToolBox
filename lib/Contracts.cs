using System.Text.Json;

namespace Toolbox;

internal readonly record struct Unchanged;
internal sealed record Prepared(string Directory);

// A default union contains null; the installer rejects that missing result.
internal union PrepareResult(Unchanged, Prepared);

internal delegate Task<PrepareResult> ToolCommand(CommandContext context);

internal sealed record CommandContext(
    string ToolId,
    string ToolDirectory,
    string? InstalledDirectory,
    string WorkDirectory,
    string AttemptId,
    JsonElement Args,
    bool Force,
    RunLog Log,
    CancellationToken CancellationToken);

internal sealed record ToolBinary(string Path, string Alias);

internal sealed record ToolManifest(string Id, string Cmd, IReadOnlyList<ToolBinary> Bin, JsonElement Args);
