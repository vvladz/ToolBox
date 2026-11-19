using System.Text.Json;
using ToolBox.Infrastructure;

namespace ToolBox.Commands;

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
