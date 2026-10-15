using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogAce.PluginApi;

namespace LogAce.Plugins.Kubernetes;

/// <summary>Version 1 retains the former built-in source's persistent field names.</summary>
internal sealed record KubernetesOptions(string Pod, string? Context, string? Namespace,
    string? Container, bool AllContainers, string Tail, string? Since, bool Follow, string? SinceTime = null)
{
    // Keep this identity compatible with existing kubectl:// sidecars and sessions.
    // Names/context are intentionally not escaped: the built-in identity was unescaped.
    internal string DisplayPath => "kubectl://" + string.Join("/", new[] { Context, Namespace, Pod }
        .Where(part => !string.IsNullOrWhiteSpace(part)))
        + (AllContainers ? "?all-containers=1" : Container is null ? "" : "?container=" + Container);

    internal string ShortName => Pod + (AllContainers ? "/*" : Container is null ? "" : "/" + Container);
}

internal static class KubernetesConfiguration
{
    internal const string DefaultContainerChoiceId = "__default__";
    private static readonly Regex Rfc3339Pattern = new(
        @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant);
    private static readonly string[] Fields =
        ["pod", "context", "namespace", "container", "allContainers", "tail", "since", "follow", "sinceTime"];

    internal static PluginSourceConfiguration Create(KubernetesOptions options) => new(1,
        PluginSourceJson.Object(new Dictionary<string, JsonElement>
        {
            ["pod"] = PluginSourceJson.Value(options.Pod),
            ["context"] = PluginSourceJson.Value(options.Context),
            ["namespace"] = PluginSourceJson.Value(options.Namespace),
            ["container"] = PluginSourceJson.Value(options.Container),
            ["allContainers"] = PluginSourceJson.Value(options.AllContainers),
            ["tail"] = PluginSourceJson.Value(options.Tail),
            ["since"] = PluginSourceJson.Value(options.Since),
            ["follow"] = PluginSourceJson.Value(options.Follow),
            ["sinceTime"] = PluginSourceJson.Value(options.SinceTime)
        }));

    internal static PluginSourceConfiguration Normalize(PluginSourceConfiguration configuration)
    {
        var values = new Dictionary<string, JsonElement>();
        foreach (string field in Fields)
        {
            // Preserve malformed types so validation catches corrupt session data.
            // Search and discovery filters never become persistent source settings.
            JsonElement value = configuration.Values.TryGetProperty(field, out var saved) ? saved
                : field == "follow" ? PluginSourceJson.Value(true)
                : field == "allContainers" ? PluginSourceJson.Value(false)
                : PluginSourceJson.Value(field == "tail" ? "2000" : "");
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()!.Trim();
                if (field == "tail" && text.Equals("all", StringComparison.OrdinalIgnoreCase)) text = "all";
                // Choice IDs cannot be empty. This browser-only sentinel cannot be
                // a Kubernetes container name and normalizes to kubectl's default.
                if (field == "container" && text == DefaultContainerChoiceId) text = "";
                value = PluginSourceJson.Value(text.Length == 0 && field is not ("pod" or "tail") ? null : text);
            }
            values[field] = value;
        }

        // A qualified name distinguishes equal pod names in different namespaces
        // in the browser. The logs command still receives a bare name plus -n.
        if (values["pod"].ValueKind == JsonValueKind.String)
        {
            string[] parts = values["pod"].GetString()!.Split('/');
            if (parts.Length == 2 && parts.All(part => part.Length > 0))
            {
                values["namespace"] = PluginSourceJson.Value(parts[0]);
                values["pod"] = PluginSourceJson.Value(parts[1]);
            }
        }
        return new(configuration.Version, PluginSourceJson.Object(values), configuration.TransientValues);
    }

    internal static PluginValidationResult Validate(PluginSourceConfiguration configuration)
    {
        if (configuration.Version != 1)
            return PluginValidationResult.Error("Unsupported Kubernetes source configuration version.");

        var errors = new List<PluginDiagnostic>();
        string? Text(string field, bool optional = true)
        {
            if (!configuration.Values.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (!optional) errors.Add(new("Enter " + field + ".", field));
                return null;
            }
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.IndexOfAny(['\0', '\r', '\n']) >= 0)
            {
                errors.Add(new("Invalid Kubernetes " + field + " value.", field));
                return null;
            }
            return value.GetString();
        }
        string? pod = Text("pod", optional: false);
        if (pod is not null && (string.IsNullOrWhiteSpace(pod) || pod.StartsWith('-') || pod.Contains('/')))
            errors.Add(new("Enter a pod name, or namespace/pod.", "pod"));
        _ = Text("context");
        _ = Text("namespace");
        string? container = Text("container");
        _ = Text("since");
        string? sinceTime = Text("sinceTime");
        if (sinceTime is not null && (!Rfc3339Pattern.IsMatch(sinceTime)
            || !DateTimeOffset.TryParse(sinceTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            errors.Add(new("Enter an RFC3339 timestamp, or leave Since time empty.", "sinceTime"));
        string? tail = Text("tail", optional: false);
        if (tail is not null && tail != "all" && (!int.TryParse(tail, NumberStyles.None,
            CultureInfo.InvariantCulture, out int count) || count < 0))
            errors.Add(new("Enter a non-negative line count or 'all'.", "tail"));
        foreach (string field in new[] { "follow", "allContainers" })
            if (!configuration.Values.TryGetProperty(field, out var flag)
                || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                errors.Add(new("This setting must be a boolean.", field));
        if (configuration.Values.TryGetProperty("allContainers", out var all) && all.ValueKind == JsonValueKind.True
            && !string.IsNullOrEmpty(container))
            errors.Add(new("Clear Container when All containers is selected.", "container"));
        return new(errors);
    }

    internal static KubernetesOptions Read(PluginSourceConfiguration configuration)
    {
        string? Text(string field) => configuration.Values.GetProperty(field).ValueKind == JsonValueKind.Null
            ? null : configuration.Values.GetProperty(field).GetString();
        return new(Text("pod")!, Text("context"), Text("namespace"), Text("container"),
            configuration.Values.GetProperty("allContainers").GetBoolean(), Text("tail")!, Text("since"),
            configuration.Values.GetProperty("follow").GetBoolean(), Text("sinceTime"));
    }
}
