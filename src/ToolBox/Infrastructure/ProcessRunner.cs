using System.Diagnostics;
using System.Text;

namespace ToolBox.Infrastructure;

internal static class ProcessRunner
{
    internal static async Task<string> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        RunLog log,
        string label,
        bool captureOutput = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        log.Write(label, $"Starting {executable} {string.Join(' ', arguments.Select(DisplayArgument))}");
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException($"Could not start {executable}.");

        void StopChild()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        using var cancellation = cancellationToken.Register(StopChild);
        var output = captureOutput ? new StringBuilder() : null;

        async Task ReadAsync(StreamReader reader, string stream, StringBuilder? capture)
        {
            try
            {
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    log.Write($"{label}:{stream}", line);
                    capture?.AppendLine(line);
                }
            }
            catch
            {
                // A failed log write must not leave a child blocked on a full pipe.
                StopChild();
                throw;
            }
        }

        var stdout = ReadAsync(process.StandardOutput, "stdout", output);
        var stderr = ReadAsync(process.StandardError, "stderr", null);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdout, stderr);
        }
        catch
        {
            StopChild();
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        log.Write(label, $"Exit code: {process.ExitCode}");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} failed with exit code {process.ExitCode}. Log: {log.FilePath}");

        return output?.ToString().TrimEnd('\r', '\n') ?? string.Empty;
    }

    private static string DisplayArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? '"' + argument.Replace("\"", "\\\"") + '"'
            : argument;
}
