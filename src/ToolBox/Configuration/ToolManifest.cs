using System.Text.Json;

namespace ToolBox.Configuration;

internal sealed record ToolManifest(string Id, string Cmd, IReadOnlyList<ToolBinary> Bin, JsonElement Args,
    ToolEvent? Autostart = null, ToolEvent? Update = null);

internal sealed record ToolEvent(ToolBinary Binary, string[] Arguments);
