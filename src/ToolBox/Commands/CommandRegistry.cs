namespace ToolBox.Commands;

internal static class CommandRegistry
{
    internal static IReadOnlyDictionary<string, ToolCommand> Create() =>
        new Dictionary<string, ToolCommand>(StringComparer.OrdinalIgnoreCase)
        {
            { "DotNet", DotNet.PrepareAsync },
            { "GitHubRelease", GitHubRelease.PrepareAsync },
            { "Url", Url.PrepareAsync }
        };
}
