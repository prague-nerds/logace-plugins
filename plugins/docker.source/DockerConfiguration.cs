using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using LogAce.PluginApi;

namespace LogAce.Plugins.Docker;

/// <summary>Version 1 is the five-field payload formerly used by the built-in source.</summary>
internal sealed record DockerOptions(string Container, string? Context, string Tail, string? Since, bool Follow)
{
    public string DisplayPath => Context is null ? "docker://" + Uri.EscapeDataString(Container)
        : "docker://" + Uri.EscapeDataString(Context) + "/" + Uri.EscapeDataString(Container);
}

internal static class DockerConfiguration
{
    internal static PluginSourceConfiguration Create(string container, string? context, string tail, string? since, bool follow) =>
        new(1, PluginSourceJson.Object(new Dictionary<string, JsonElement>
        {
            ["container"] = PluginSourceJson.Value(container), ["context"] = PluginSourceJson.Value(context),
            ["tail"] = PluginSourceJson.Value(tail), ["since"] = PluginSourceJson.Value(since), ["follow"] = PluginSourceJson.Value(follow)
        }));

    internal static PluginSourceConfiguration Normalize(PluginSourceConfiguration configuration)
    {
        // Preserve incorrect value types for validation rather than silently turning
        // malformed session data into valid defaults. Discovery-only fields are dropped.
        var values = new Dictionary<string, JsonElement>();
        foreach (string field in new[] { "container", "context", "tail", "since", "follow" })
        {
            JsonElement value = configuration.Values.TryGetProperty(field, out var saved) ? saved
                : field == "follow" ? PluginSourceJson.Value(true)
                : field == "tail" ? PluginSourceJson.Value("2000") : PluginSourceJson.Value("");
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()!.Trim();
                if (field == "tail" && text.Equals("all", StringComparison.OrdinalIgnoreCase)) text = "all";
                value = PluginSourceJson.Value(text.Length == 0 && field is "context" or "since" ? null : text);
            }
            values[field] = value;
        }
        return new(configuration.Version, PluginSourceJson.Object(values), configuration.TransientValues);
    }

    internal static PluginValidationResult Validate(PluginSourceConfiguration configuration)
    {
        if (configuration.Version != 1) return PluginValidationResult.Error("Unsupported Docker source configuration version.");
        var errors = new List<PluginDiagnostic>();
        string? Text(string field, bool optional = false)
        {
            if (!configuration.Values.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (!optional) errors.Add(new("Enter " + field + ".", field));
                return null;
            }
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.IndexOfAny(['\0', '\r', '\n']) >= 0)
            { errors.Add(new("Invalid Docker " + field + " value.", field)); return null; }
            return value.GetString();
        }
        string? container = Text("container");
        if (container is not null && (string.IsNullOrWhiteSpace(container) || container.StartsWith('-')))
            errors.Add(new("Enter a container name or ID.", "container"));
        _ = Text("context", optional: true); _ = Text("since", optional: true);
        string? tail = Text("tail");
        if (tail is not null && tail != "all" && (!long.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out long count) || count < 0))
            errors.Add(new("Enter a non-negative line count or 'all'.", "tail"));
        if (!configuration.Values.TryGetProperty("follow", out var follow) || follow.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            errors.Add(new("Follow live must be a boolean.", "follow"));
        return new(errors);
    }

    internal static DockerOptions Read(PluginSourceConfiguration configuration)
    {
        string? Text(string field) => configuration.Values.GetProperty(field).ValueKind == JsonValueKind.Null
            ? null : configuration.Values.GetProperty(field).GetString();
        return new(Text("container")!, Text("context"), Text("tail")!, Text("since"), configuration.Values.GetProperty("follow").GetBoolean());
    }
}
