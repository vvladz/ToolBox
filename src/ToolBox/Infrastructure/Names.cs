using System.Text.RegularExpressions;
using ToolBox.Configuration;

namespace ToolBox.Infrastructure;

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
