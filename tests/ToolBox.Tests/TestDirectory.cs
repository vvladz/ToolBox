namespace ToolBox.Tests;

internal sealed class TestDirectory : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ToolBox.Tests-" + Guid.NewGuid().ToString("N"));

    internal TestDirectory() => Directory.CreateDirectory(Root);

    internal string CreateDirectory(params string[] parts)
    {
        var path = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        var path = Path.GetFullPath(Root);
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("ToolBox.Tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a path outside the test directory.");
        Directory.Delete(path, recursive: true);
    }
}
