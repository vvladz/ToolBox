using System.Text;

namespace Toolbox;

internal sealed class RunLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;

    internal string FilePath { get; }

    internal RunLog(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        writer = new StreamWriter(
            new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
    }

    internal void Info(string message) => Write("toolbox", message, echo: true);
    internal void Warn(string message) => Write("toolbox:warning", message, echo: true);
    internal void Error(string message) => Write("toolbox:error", message, echo: true);

    internal void Write(string source, string message, bool echo = false)
    {
        lock (gate)
        {
            foreach (var line in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var entry = $"{DateTimeOffset.UtcNow:O} [{source}] {line}";
                writer.WriteLine(entry);
                if (echo) Console.Error.WriteLine($"[{source}] {line}");
            }
        }
    }

    public void Dispose()
    {
        lock (gate) writer.Dispose();
    }
}
