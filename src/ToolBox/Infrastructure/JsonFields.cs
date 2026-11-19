using System.Text.Json;

namespace ToolBox.Infrastructure;

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
