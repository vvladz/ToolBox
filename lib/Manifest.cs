using System.Text.Json;
using System.Text.RegularExpressions;

namespace Toolbox;

internal static class Manifest
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

internal static class JsonFields
{
    internal static void Validate(JsonElement value, string label, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label}: expected a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"{label}: unknown or duplicate field '{property.Name}'.");
    }

    internal static string RequiredString(JsonElement value, string name) =>
        OptionalString(value, name) ?? throw new InvalidDataException($"Missing required field '{name}'.");

    internal static string? OptionalString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"'{name}' must be a nonempty string.");
        return property.GetString();
    }
}

internal static class Names
{
    internal static string Validate(string name, string label)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name != name.Trim()
            || name.EndsWith('.') || name.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 || name.Any(char.IsControl)
            || Regex.IsMatch(name.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException($"Invalid {label}: '{name}'. Use a filename/name without a path.");
        return name;
    }

    internal static string RelativePath(string path, string label)
    {
        var normalized = path.Replace('\\', '/');
        foreach (var part in normalized.Split('/')) Validate(part, label);
        return normalized;
    }

    internal static ToolBinary[] Executables(IEnumerable<ToolBinary> executables)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return executables.Select(executable =>
        {
            var relative = RelativePath(executable.Path, "relative EXE path");
            var name = Path.GetFileName(relative);
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.Length == 4)
                throw new InvalidDataException($"bin entries must point to EXE files: {executable.Path}");
            var alias = Validate(executable.Alias, "command alias");
            if (!aliases.Add(alias))
                throw new InvalidDataException($"bin entries must have distinct command aliases for PATH: {alias}");
            return new ToolBinary(relative, alias);
        }).ToArray();
    }
}
