using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogAce.PluginApi;

namespace LogAce.Plugins.CloudWatch;

/// <summary>Version 1 keeps the former built-in fields, including numeric OpenMode.</summary>
internal sealed record CloudWatchOptions(string LogGroup, string? Profile, string? Region,
    string? StreamFilter, string? FilterPattern, bool Follow, string Since)
{
    // Query order and escaping preserve identities used by existing .las sidecars.
    internal string DisplayPath
    {
        get
        {
            var query = new List<string>();
            void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) query.Add(name + "=" + Uri.EscapeDataString(value)); }
            Add("filter", FilterPattern); Add("mode", Follow ? "follow" : "snapshot");
            Add("since", Since); Add("stream", StreamFilter);
            if (Follow) query.Add("follow=1");
            return $"cloudwatch://{Uri.EscapeDataString(Profile ?? "default")}@{Uri.EscapeDataString(Region ?? "default")}/{Uri.EscapeDataString(LogGroup)}?{string.Join("&", query)}";
        }
    }
    internal string ShortName => LogGroup.TrimEnd('/').Split('/').LastOrDefault() is { Length: > 0 } name ? name : "CloudWatch Logs";
}

internal static class CloudWatchConfiguration
{
    private static readonly string[] Fields = ["logGroup", "profile", "region", "streamFilter", "filterPattern", "since", "openMode"];
    private static readonly Regex SincePattern = new(@"\A[0-9]+[smhdw]\z", RegexOptions.CultureInvariant);
    internal static PluginSourceConfiguration Create(CloudWatchOptions options) => new(1, PluginSourceJson.Object(new Dictionary<string, JsonElement>
    {
        ["logGroup"] = PluginSourceJson.Value(options.LogGroup), ["profile"] = PluginSourceJson.Value(options.Profile),
        ["region"] = PluginSourceJson.Value(options.Region), ["streamFilter"] = PluginSourceJson.Value(options.StreamFilter),
        ["filterPattern"] = PluginSourceJson.Value(options.FilterPattern), ["since"] = PluginSourceJson.Value(options.Since),
        ["openMode"] = PluginSourceJson.Value(options.Follow ? 1 : 0)
    }));

    internal static PluginSourceConfiguration ForForm(PluginSourceConfiguration configuration)
    {
        var values = configuration.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        values["follow"] = PluginSourceJson.Value(values.TryGetValue("openMode", out var mode) && mode.ValueKind == JsonValueKind.Number && mode.TryGetInt32(out int number) && number == 1);
        return new(configuration.Version, PluginSourceJson.Object(values), configuration.TransientValues);
    }

    internal static PluginSourceConfiguration Normalize(PluginSourceConfiguration configuration)
    {
        var values = new Dictionary<string, JsonElement>();
        foreach (string field in Fields)
        {
            JsonElement value = configuration.Values.TryGetProperty(field, out var saved) ? saved
                : field == "openMode" ? PluginSourceJson.Value(0) : PluginSourceJson.Value(field == "since" ? "1h" : "");
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()!.Trim();
                if (field == "since" && text.Length == 0) text = "1h";
                value = PluginSourceJson.Value(text.Length == 0 && field is not ("logGroup" or "since" or "openMode") ? null : text);
            }
            values[field] = value;
        }
        // Follow is a form-only checkbox; saved v1 payloads retain OpenMode 0/1.
        // Do not coerce malformed fields: validation must catch corrupt sessions.
        if (configuration.Values.TryGetProperty("follow", out var follow))
            values["openMode"] = follow.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? PluginSourceJson.Value(follow.GetBoolean() ? 1 : 0) : follow;
        return new(configuration.Version, PluginSourceJson.Object(values), configuration.TransientValues);
    }

    internal static PluginValidationResult Validate(PluginSourceConfiguration configuration)
    {
        if (configuration.Version != 1) return PluginValidationResult.Error("Unsupported CloudWatch source configuration version.");
        var errors = new List<PluginDiagnostic>();
        foreach (string field in Fields.Where(field => field != "openMode"))
        {
            bool required = field is "logGroup" or "since";
            if (!configuration.Values.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            { if (required) errors.Add(new("Enter " + field + ".", field)); continue; }
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.IndexOfAny(['\0', '\r', '\n']) >= 0)
            { errors.Add(new("Invalid CloudWatch " + field + " value.", field)); continue; }
            string text = value.GetString()!;
            if (required && string.IsNullOrWhiteSpace(text) || field == "logGroup" && text.StartsWith('-'))
                errors.Add(new("Enter a CloudWatch log group name.", field));
            if (field == "since" && !SincePattern.IsMatch(text) && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                errors.Add(new("Enter one duration such as 30m or 1h, or an ISO 8601 timestamp.", field));
        }
        if (!configuration.Values.TryGetProperty("openMode", out var mode) || mode.ValueKind != JsonValueKind.Number
            || !mode.TryGetInt32(out int number) || number is not (0 or 1))
            errors.Add(new("Choose snapshot or follow.", "follow"));
        return new(errors);
    }

    internal static CloudWatchOptions Read(PluginSourceConfiguration configuration)
    {
        string? Text(string field) => configuration.Values.GetProperty(field).ValueKind == JsonValueKind.Null ? null : configuration.Values.GetProperty(field).GetString();
        return new(Text("logGroup")!, Text("profile"), Text("region"), Text("streamFilter"), Text("filterPattern"),
            configuration.Values.GetProperty("openMode").GetInt32() == 1, Text("since")!);
    }
}
