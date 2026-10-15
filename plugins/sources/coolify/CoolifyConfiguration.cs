using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using LogAce.PluginApi;

namespace LogAce.Plugins.Coolify;

internal static class CoolifyConfiguration
{
    internal static PluginSourceConfiguration Default() => new(1, PluginSourceJson.Object(new Dictionary<string, JsonElement>
    {
        ["instanceUrl"] = PluginSourceJson.Value(""),
        ["connectionName"] = PluginSourceJson.Value("Default"),
        ["rememberToken"] = PluginSourceJson.Value(false),
        ["tokenEnvironment"] = PluginSourceJson.Value("COOLIFY_API_TOKEN"),
        ["applicationUuid"] = PluginSourceJson.Value(""),
        ["applicationName"] = PluginSourceJson.Value(""),
        ["mode"] = PluginSourceJson.Value("runtime"),
        ["deploymentUuid"] = PluginSourceJson.Value(""),
        ["serviceName"] = PluginSourceJson.Value(""),
        ["tail"] = PluginSourceJson.Value("2000"),
        ["follow"] = PluginSourceJson.Value(true),
        ["pollSeconds"] = PluginSourceJson.Value(3)
    }));

    internal static PluginSourceConfiguration Normalize(PluginSourceConfiguration configuration)
    {
        var values = new Dictionary<string, JsonElement>();
        foreach (JsonProperty field in Default().Values.EnumerateObject())
        {
            JsonElement value = configuration.Values.TryGetProperty(field.Name, out var saved) ? saved : field.Value;
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()!.Trim();
                if (field.Name == "instanceUrl")
                {
                    text = text.TrimEnd('/');
                    if (text.EndsWith("/api/v1", StringComparison.Ordinal))
                    {
                        text = text[..^7];
                    }
                }
                if (field.Name == "tail" && text.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    text = "all";
                }
                value = PluginSourceJson.Value(text);
            }
            values[field.Name] = value;
        }

        // Never migrate credentials found in persisted JSON into usable credentials.
        // Only the form's transient secret and the named environment variable are used.
        JsonElement? transient = configuration.TransientValues is { } secrets && secrets.TryGetProperty("token", out var token)
            ? PluginSourceJson.Object(new Dictionary<string, JsonElement> { ["token"] = token }) : null;
        return new(configuration.Version, PluginSourceJson.Object(values), transient);
    }

    internal static PluginSourceConfiguration FromForm(PluginSourceFormValues form)
    {
        var values = form.Values.Where(pair => pair.Key != "token").ToDictionary(pair => pair.Key, pair => pair.Value);
        JsonElement? transient = form.Values.TryGetValue("token", out var token)
            ? PluginSourceJson.Object(new Dictionary<string, JsonElement> { ["token"] = token }) : null;
        return Normalize(new(1, PluginSourceJson.Object(values), transient));
    }

    internal static PluginValidationResult Validate(PluginSourceConfiguration configuration)
    {
        if (configuration.Version != 1)
        {
            return PluginValidationResult.Error("Unsupported Coolify configuration version.");
        }

        var errors = new List<PluginDiagnostic>();
        foreach (JsonProperty field in configuration.Values.EnumerateObject())
        {
            if (field.Name is "follow" or "rememberToken" or "pollSeconds")
            {
                continue;
            }
            if (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString()!.Any(char.IsControl))
            {
                errors.Add(new("Enter a valid text value.", field.Name));
            }
        }
        if (errors.Count > 0)
        {
            return new(errors);
        }

        string Text(string field) => configuration.Values.GetProperty(field).GetString()!;
        if (string.IsNullOrWhiteSpace(Text("connectionName")) || Text("connectionName").Length > 128)
        {
            errors.Add(new("Enter a connection name up to 128 characters.", "connectionName"));
        }
        if (!Uri.TryCreate(Text("instanceUrl"), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            errors.Add(new("Enter the Coolify HTTP(S) URL without credentials, query or fragment.", "instanceUrl"));
        }
        if (string.IsNullOrWhiteSpace(Text("applicationUuid")))
        {
            errors.Add(new("Enter an application UUID or browse applications.", "applicationUuid"));
        }
        foreach (string field in new[] { "applicationUuid", "deploymentUuid" })
        {
            if (Text(field).Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            {
                errors.Add(new("Enter a UUID containing letters, digits, '-' or '_'.", field));
            }
        }
        if (Text("mode") is not ("runtime" or "deployment"))
        {
            errors.Add(new("Choose runtime or deployment logs.", "mode"));
        }
        string tail = Text("tail");
        if (Text("mode") == "runtime" && tail != "all" && (!int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            || count is < 1 or > 10000))
        {
            errors.Add(new("Enter 1–10000 lines, or 'all'.", "tail"));
        }
        if (!configuration.Values.TryGetProperty("follow", out var follow)
            || follow.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add(new("Follow live must be a boolean.", "follow"));
        }
        if (!configuration.Values.TryGetProperty("rememberToken", out var remember)
            || remember.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add(new("Remember token must be a boolean.", "rememberToken"));
        }
        if (!configuration.Values.TryGetProperty("pollSeconds", out var interval)
            || interval.ValueKind != JsonValueKind.Number || !interval.TryGetInt32(out int seconds) || seconds is < 1 or > 300)
        {
            errors.Add(new("Enter a polling interval from 1 to 300 seconds.", "pollSeconds"));
        }
        if (configuration.TransientValues is { } secrets && secrets.TryGetProperty("token", out var token)
            && (token.ValueKind != JsonValueKind.String || token.GetString()!.Any(char.IsControl)))
        {
            errors.Add(new("Enter a valid API token.", "token"));
        }
        return new(errors);
    }

    internal static CoolifyOptions Read(PluginSourceConfiguration configuration)
    {
        string Text(string field) => configuration.Values.GetProperty(field).GetString()!;
        return new(Text("instanceUrl"), Text("tokenEnvironment"), Text("applicationUuid"), Text("applicationName"),
            Text("mode"), Text("deploymentUuid"), Text("serviceName"), Text("tail"),
            configuration.Values.GetProperty("follow").GetBoolean(), configuration.Values.GetProperty("pollSeconds").GetInt32());
    }

    internal static string ResolveToken(PluginSourceConfiguration configuration)
    {
        string? token = configuration.TransientValues is { } secrets && secrets.TryGetProperty("token", out var value)
            ? value.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(token))
        {
            string environment = configuration.Values.GetProperty("tokenEnvironment").GetString()!;
            token = environment.Length > 0 ? Environment.GetEnvironmentVariable(environment)?.Trim() : null;
        }
        if (string.IsNullOrEmpty(token) || token.Any(char.IsControl))
        {
            throw new ArgumentException("Enter an API token or set the configured token environment variable. Log access requires read:sensitive.");
        }
        return token;
    }
}
