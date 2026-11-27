namespace ToolBox.Commands;

internal readonly record struct Unchanged;
internal sealed record Prepared(string Directory, bool VersionChanged = true);

// A default union contains null; the installer rejects that missing result.
internal union PrepareResult(Unchanged, Prepared);
