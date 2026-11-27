using System.Text.Json;
using ToolBox.Infrastructure;

namespace ToolBox.Configuration;

internal static class ManifestReader
{
    internal static List<ToolManifest> ReadAll(string directory, string? selected) =>
        Enumerate(directory, selected).Select(Read).ToList();

    internal static string[] Enumerate(string directory, string? selected)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Manifest directory was not found: {directory}");
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var duplicate = files.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate tool name: {duplicate.Key}");
        var selectedFiles = selected is null ? files : files.Where(file =>
            Path.GetFileNameWithoutExtension(file).Equals(selected, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected is not null && selectedFiles.Length == 0)
            throw new FileNotFoundException($"Tool manifest was not found: {selected}.json");
        return selectedFiles;
    }

    internal static ToolManifest Read(string file)
    {
        var id = Names.Validate(Path.GetFileNameWithoutExtension(file), "manifest filename");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        JsonFields.Validate(root, file, "cmd", "bin", "args", "autostart", "update");
        var command = Names.Validate(JsonFields.RequiredString(root, "cmd"), "cmd");
        if (!root.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{file}: args must be an object containing the command parameters.");

        var executables = new List<ToolBinary>();
        if (root.TryGetProperty("bin", out var bin))
        {
            if (bin.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in bin.EnumerateArray())
                {
                    var path = ReadExecutable(item, file);
                    executables.Add(new ToolBinary(path, Path.GetFileNameWithoutExtension(path)));
                }
            }
            else if (bin.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in bin.EnumerateObject())
                    executables.Add(new ToolBinary(ReadExecutable(property.Value, file), property.Name));
            }
            else
                throw new InvalidDataException($"{file}: bin must be an array of relative EXE paths or an object mapping aliases to paths.");
        }
        var binaries = Names.Executables(executables);
        return new ToolManifest(id, command, binaries, args.Clone(),
            ReadEvent("autostart"), ReadEvent("update"));

        ToolEvent? ReadEvent(string field)
        {
            if (!root.TryGetProperty(field, out var value)) return null;
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0 ||
                value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString()!.Contains('\0')))
                throw new InvalidDataException($"{file}: {field} must be an array containing a bin alias and its string arguments.");

            var tokens = value.EnumerateArray().Select(item => item.GetString()!).ToArray();
            var binary = binaries.SingleOrDefault(item => item.Alias.Equals(tokens[0], StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{file}: {field} refers to an unknown bin alias '{tokens[0]}'.");
            return new ToolEvent(binary, tokens[1..]);
        }
    }

    private static string ReadExecutable(JsonElement value, string file)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{file}: bin entries must be relative EXE paths.");
        return Names.RelativePath(value.GetString()!, "relative EXE path");
    }
}
