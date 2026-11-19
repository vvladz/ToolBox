using System.Text.Json;

namespace ToolBox.Configuration;

internal sealed record ToolManifest(string Id, string Cmd, IReadOnlyList<ToolBinary> Bin, JsonElement Args);
