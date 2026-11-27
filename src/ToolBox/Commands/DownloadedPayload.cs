using System.IO.Compression;
using System.Text.Json;
using ToolBox.Infrastructure;

namespace ToolBox.Commands;

internal sealed record PayloadFile(string Path, long Length);

internal static class DownloadedPayload
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static async Task<T?> ReadReceiptAsync<T>(CommandContext context, string name) where T : class
    {
        if (context.InstalledDirectory is not { } directory) return null;
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, context.CancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            context.Log.Warn("Installed download metadata is unreadable; a new download is required.");
            return null;
        }
    }

    internal static void ExtractZip(Stream input, string destination, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Path, bool Directory)>();
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (kind is not (0 or 0x8000 or 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP payloads cannot contain links or special files.");
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var name = Names.RelativePath(directory ? entry.FullName[..^1] : entry.FullName, "ZIP entry");
            if (!names.Add(name)) throw new InvalidDataException($"ZIP entry '{name}' occurs more than once or differs only by case.");
            entries.Add((entry, Within(destination, name), directory));
        }
        foreach (var item in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Directory) Directory.CreateDirectory(item.Path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
                item.Entry.ExtractToFile(item.Path, overwrite: false);
            }
        }
    }

    private static string Within(string directory, string relative)
    {
        relative = Names.RelativePath(relative, "relative payload path");
        return Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    internal static PayloadFile[] ListFiles(string payload, string receiptName)
    {
        if (File.Exists(Path.Combine(payload, receiptName)) || Directory.Exists(Path.Combine(payload, receiptName)))
            throw new InvalidDataException($"The payload contains the reserved name '{receiptName}'.");

        var files = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
            .Select(path => new PayloadFile(Path.GetRelativePath(payload, path).Replace('\\', '/'), new FileInfo(path).Length))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("The selected payload contains no files.");
        return files;
    }

    internal static bool InstalledFilesExist(PayloadFile[]? files, string directory)
    {
        if (files is not { Length: > 0 }) return false;
        try
        {
            return files.All(file => file is not null && !string.IsNullOrWhiteSpace(file.Path) && file.Length >= 0 &&
                new FileInfo(Within(directory, file.Path)) is { Exists: true } info && info.Length == file.Length);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
