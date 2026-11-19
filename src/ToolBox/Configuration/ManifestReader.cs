using System.Text.Json;
using ToolBox.Infrastructure;

namespace ToolBox.Configuration;

internal static class ManifestReader
{
    internal static List<ToolManifest> ReadAll(string directory, string? selected)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Manifest directory was not found: {directory}");
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifests = new List<ToolManifest>();
        foreach (var file in files)
        {
            var id = Names.Validate(Path.GetFileNameWithoutExtension(file), "manifest filename");
            if (!names.Add(id)) throw new InvalidDataException($"Duplicate tool name: {id}");
            if (selected is not null && !id.Equals(selected, StringComparison.OrdinalIgnoreCase)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            JsonFields.Validate(root, file, "cmd", "bin", "args");
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
            manifests.Add(new ToolManifest(id, command, Names.Executables(executables), args.Clone()));
        }
        if (selected is not null && manifests.Count == 0)
            throw new FileNotFoundException($"Tool manifest was not found: {selected}.json");
        return manifests;
    }

    private static string ReadExecutable(JsonElement value, string file)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{file}: bin entries must be relative EXE paths.");
        return Names.RelativePath(value.GetString()!, "relative EXE path");
    }
}
