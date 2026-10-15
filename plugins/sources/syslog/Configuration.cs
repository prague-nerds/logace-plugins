using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LogAce.PluginApi;

namespace LogAce.Plugins.Syslog;

/// <summary>
/// Versioned neutral JSON preserves the former built-in camelCase/numeric-enum
/// schema. The explicit reflection resolver works for types compiled at runtime;
/// it never serializes host-private types or relies on host serializer settings.
/// </summary>
internal static class Configuration
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IgnoreReadOnlyProperties = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    internal static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    internal static PluginSourceConfiguration Create<T>(T options) => new(1, Element(options));

    internal static T Read<T>(PluginSourceConfiguration configuration) => JsonSerializer.Deserialize<T>(configuration.Values, Json)!;

    internal static Dictionary<string, JsonElement> Values(PluginSourceConfiguration configuration) =>
        configuration.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    internal static PluginSourceConfiguration Normalize(PluginSourceConfiguration configuration, PluginSourceConfiguration defaults,
        IReadOnlyDictionary<string, string>? enumFields = null, IReadOnlyDictionary<string, string>? booleanModes = null)
    {
        var values = Values(defaults);

        foreach (var pair in configuration.Values.EnumerateObject())
        {
            if (values.ContainsKey(pair.Name))
            {
                values[pair.Name] = pair.Value.ValueKind == JsonValueKind.String
                        ? PluginSourceJson.Value(pair.Value.GetString()!.Trim().Length == 0 && values[pair.Name].ValueKind == JsonValueKind.Null ? null : pair.Value.GetString()!.Trim()) : pair.Value.Clone();
            }
        }

        // UI-only fields map explicitly. Saved payloads with wrong JSON types stay
        // wrong and fail validation rather than silently becoming new defaults.
        foreach (var pair in enumFields ?? new Dictionary<string, string>())
        {
            if (configuration.Values.TryGetProperty(pair.Key, out var value))
            {
                values[pair.Value] = value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int number)
                                ? PluginSourceJson.Value(number) : value.Clone();
            }
        }

        foreach (var pair in booleanModes ?? new Dictionary<string, string>())
        {
            if (configuration.Values.TryGetProperty(pair.Key, out var value))
            {
                values[pair.Value] = value.ValueKind is JsonValueKind.True or JsonValueKind.False
                                ? PluginSourceJson.Value(value.GetBoolean() ? 1 : 0) : value.Clone();
            }
        }

        return new(configuration.Version, PluginSourceJson.Object(values), configuration.TransientValues);
    }

    internal static PluginSourceConfiguration ForForm(PluginSourceConfiguration configuration,
        IReadOnlyDictionary<string, string>? enumFields = null, IReadOnlyDictionary<string, string>? booleanModes = null)
    {
        var values = Values(configuration);

        foreach (var pair in enumFields ?? new Dictionary<string, string>())
        {
            if (values.TryGetValue(pair.Value, out var value))
            {
                values[pair.Key] = PluginSourceJson.Value(value.ToString());
            }
        }

        foreach (var pair in booleanModes ?? new Dictionary<string, string>())
        {
            if (values.TryGetValue(pair.Value, out var value))
            {
                values[pair.Key] = PluginSourceJson.Value(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int mode) && mode == 1);
            }
        }

        return new(configuration.Version, PluginSourceJson.Object(values), configuration.TransientValues);
    }
}
