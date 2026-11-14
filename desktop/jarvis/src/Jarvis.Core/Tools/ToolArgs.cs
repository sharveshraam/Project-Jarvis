using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jarvis.Core.Tools;

/// <summary>Reads tool arguments defensively; works for parsed JSON and for nodes built in code.</summary>
public static class ToolArgs
{
    public static string? Str(this JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is null) return null;
        if (node.GetValueKind() == JsonValueKind.String) return node.GetValue<string>();
        if (node is JsonValue) return node.ToJsonString();
        return null;
    }

    public static string Required(this JsonObject args, string name)
    {
        var value = args.Str(name);
        if (string.IsNullOrWhiteSpace(value)) throw new ToolArgumentException($"Missing required argument '{name}'.");
        return value.Trim();
    }

    public static double? Number(this JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is null) return null;
        var raw = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static int Int(this JsonObject args, string name, int fallback)
    {
        var d = args.Number(name);
        return d.HasValue ? (int)Math.Round(d.Value) : fallback;
    }

    public static bool Bool(this JsonObject args, string name, bool fallback = false)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is null) return fallback;
        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(node.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase),
            _ => fallback
        };
    }

    public static List<string> StrList(this JsonObject args, string name)
    {
        var list = new List<string>();
        if (args.TryGetPropertyValue(name, out var node) && node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null && item.GetValueKind() == JsonValueKind.String)
                {
                    var s = item.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                }
            }
        }
        return list;
    }

    /// <summary>Compact, length-limited rendering of arguments for previews and audit entries.</summary>
    public static string Describe(JsonObject args)
    {
        if (args.Count == 0) return "";
        var parts = new List<string>();
        foreach (var kv in args)
        {
            var text = kv.Value is null ? "null" : kv.Value.ToJsonString();
            if (text.Length > 80) text = text.Substring(0, 77) + "...";
            parts.Add($"{kv.Key}={text}");
        }
        return "(" + string.Join(", ", parts) + ")";
    }
}

/// <summary>Validates model-supplied arguments against the tool's declared parameters before anything runs.</summary>
public static class SchemaValidator
{
    public const int MaxStringLength = 8000;

    public static List<string> Validate(ToolDefinition definition, JsonObject args)
    {
        var errors = new List<string>();
        var known = definition.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var kv in args)
        {
            if (!known.ContainsKey(kv.Key)) errors.Add($"Unknown argument '{kv.Key}'.");
        }

        foreach (var p in definition.Parameters)
        {
            var present = args.TryGetPropertyValue(p.Name, out var node) && node is not null;
            if (!present)
            {
                if (p.Required) errors.Add($"Missing required argument '{p.Name}'.");
                continue;
            }

            var kind = node!.GetValueKind();
            switch (p.Type)
            {
                case "string":
                    if (kind != JsonValueKind.String) { errors.Add($"'{p.Name}' must be a string."); break; }
                    var s = node.GetValue<string>();
                    if (s.Length > MaxStringLength) errors.Add($"'{p.Name}' is too long.");
                    if (p.Required && string.IsNullOrWhiteSpace(s)) errors.Add($"'{p.Name}' must not be empty.");
                    if (p.EnumValues is { Count: > 0 } && !p.EnumValues.Contains(s, StringComparer.Ordinal))
                        errors.Add($"'{p.Name}' must be one of: {string.Join(", ", p.EnumValues)}.");
                    break;
                case "integer":
                case "number":
                    if (kind != JsonValueKind.Number) { errors.Add($"'{p.Name}' must be a number."); break; }
                    if (!double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        errors.Add($"'{p.Name}' is not a valid number.");
                        break;
                    }
                    if (p.Type == "integer" && Math.Abs(d - Math.Round(d)) > 1e-9) errors.Add($"'{p.Name}' must be a whole number.");
                    if (p.Min.HasValue && d < p.Min.Value) errors.Add($"'{p.Name}' must be at least {p.Min.Value}.");
                    if (p.Max.HasValue && d > p.Max.Value) errors.Add($"'{p.Name}' must be at most {p.Max.Value}.");
                    break;
                case "boolean":
                    if (kind != JsonValueKind.True && kind != JsonValueKind.False) errors.Add($"'{p.Name}' must be true or false.");
                    break;
                case "array":
                    if (node is not JsonArray array) { errors.Add($"'{p.Name}' must be an array of strings."); break; }
                    if (array.Count > 200) errors.Add($"'{p.Name}' has too many items.");
                    if (array.Any(i => i is null || i.GetValueKind() != JsonValueKind.String))
                        errors.Add($"'{p.Name}' must contain only strings.");
                    break;
            }
        }
        return errors;
    }
}
