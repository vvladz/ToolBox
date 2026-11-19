#:include ../usr/DotNet.cs
#:include ../usr/GitHubRelease.cs

namespace Toolbox;

internal static class Commands
{
    internal static IReadOnlyDictionary<string, ToolCommand> Create() =>
        new Dictionary<string, ToolCommand>(StringComparer.OrdinalIgnoreCase)
        {
            { "DotNet", DotNet.PrepareAsync },
            { "GitHubRelease", GitHubRelease.PrepareAsync }
        };
}
