using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Coolify;

/// <summary>Named nonsecret connection profiles and instance-bound vault keys.</summary>
internal sealed class CoolifyConnections(PluginHostServices services)
{
    internal string ProviderName => services.SecretsBag.ProviderName;

    internal static string Key(PluginSourceConfiguration configuration)
    {
        string url = configuration.Values.GetProperty("instanceUrl").GetString()!;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            url = uri.AbsoluteUri.TrimEnd('/');
        }
        string name = configuration.Values.GetProperty("connectionName").GetString()!.ToUpperInvariant();
        return "connection/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url + "\0" + name))).ToLowerInvariant();
    }

    internal async ValueTask<PluginSourceConfiguration> ResolveAsync(PluginSourceConfiguration configuration, CancellationToken token, bool discovery = false)
    {
        configuration = CoolifyConfiguration.Normalize(configuration);
        if (CoolifyConfiguration.Validate(configuration).Errors.Any(error => !discovery || error.FieldId != "applicationUuid"))
        {
            return configuration;
        }
        string? credential = configuration.TransientValues is { } transient && transient.TryGetProperty("token", out var typed)
            ? typed.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(credential))
        {
            string environment = configuration.Values.GetProperty("tokenEnvironment").GetString()!;
            credential = environment.Length > 0 ? Environment.GetEnvironmentVariable(environment)?.Trim() : null;
        }
        if (string.IsNullOrEmpty(credential))
        {
            // Only consult the vault for a profile which was explicitly saved with a token.
            // Temporary connections never require an installed/unlocked credential store.
            PluginSettingsValue? saved = await services.SettingsBag.GetAsync(Key(configuration), token).ConfigureAwait(false);
            if (saved is { Version: 1 } && saved.Value.ValueKind == JsonValueKind.Object
                && HasStoredToken(saved.Value))
            {
                credential = await services.SecretsBag.GetAsync(Key(configuration) + "/token", token).ConfigureAwait(false);
            }
        }
        return string.IsNullOrEmpty(credential) ? configuration : new(configuration.Version, configuration.Values,
            PluginSourceJson.Object(new Dictionary<string, JsonElement> { ["token"] = PluginSourceJson.Value(credential) }));
    }

    internal async ValueTask SaveAsync(PluginSourceConfiguration configuration, CancellationToken token)
    {
        string key = Key(configuration);
        bool remember = configuration.Values.GetProperty("rememberToken").GetBoolean();
        PluginSettingsValue? existing = await services.SettingsBag.GetAsync(key, token).ConfigureAwait(false);
        if (remember)
        {
            await services.SecretsBag.SetAsync(key + "/token", CoolifyConfiguration.ResolveToken(configuration), token).ConfigureAwait(false);
        }
        // A profile is a persistent JSON payload only, never the configuration wrapper
        // containing transient secrets. Distinct profiles use distinct atomic settings keys.
        var values = configuration.Values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        values["hasStoredToken"] = PluginSourceJson.Value(remember || (existing is { Version: 1 } && HasStoredToken(existing.Value)));
        await services.SettingsBag.SetAsync(key, new(1, PluginSourceJson.Object(values)), token).ConfigureAwait(false);
    }

    internal async ValueTask<IReadOnlyList<PluginSourceChoice>> ListAsync(CancellationToken token)
    {
        var result = new List<PluginSourceChoice>();
        foreach (string key in await services.SettingsBag.GetKeysAsync(token).ConfigureAwait(false))
        {
            if (!key.StartsWith("connection/", StringComparison.Ordinal))
            {
                continue;
            }
            PluginSettingsValue? saved = await services.SettingsBag.GetAsync(key, token).ConfigureAwait(false);
            if (saved is { Version: 1 } && saved.Value.ValueKind == JsonValueKind.Object)
            {
                string name = CoolifyClient.Text(saved.Value, "connectionName");
                string url = CoolifyClient.Text(saved.Value, "instanceUrl");
                result.Add(new(key, name + " · " + url));
            }
        }
        return result.OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal async ValueTask<PluginSourceConfiguration> LoadAsync(string key, CancellationToken token)
    {
        PluginSettingsValue? saved = await services.SettingsBag.GetAsync(key, token).ConfigureAwait(false);
        if (saved is not { Version: 1 } || saved.Value.ValueKind != JsonValueKind.Object)
        {
            throw new PluginStorageException(PluginStorageError.Corrupt, "This saved connection is missing or incompatible.");
        }
        var configuration = CoolifyConfiguration.Normalize(new(1, saved.Value));
        if (Key(configuration) != key)
        {
            throw new PluginStorageException(PluginStorageError.Corrupt, "This saved connection has an invalid identity.");
        }
        return configuration;
    }

    internal async ValueTask ForgetTokenAsync(PluginSourceConfiguration configuration, CancellationToken token)
    {
        string key = Key(configuration);
        await services.SecretsBag.DeleteAsync(key + "/token", token).ConfigureAwait(false);
        PluginSettingsValue? saved = await services.SettingsBag.GetAsync(key, token).ConfigureAwait(false);
        if (saved is { Version: 1 } && saved.Value.ValueKind == JsonValueKind.Object)
        {
            var values = saved.Value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            values["rememberToken"] = PluginSourceJson.Value(false);
            values["hasStoredToken"] = PluginSourceJson.Value(false);
            await services.SettingsBag.SetAsync(key, new(1, PluginSourceJson.Object(values)), token).ConfigureAwait(false);
        }
    }

    private static bool HasStoredToken(JsonElement values) => values.ValueKind == JsonValueKind.Object
        && ((values.TryGetProperty("hasStoredToken", out var stored) && stored.ValueKind == JsonValueKind.True)
            || (!values.TryGetProperty("hasStoredToken", out _) && values.TryGetProperty("rememberToken", out var remember) && remember.ValueKind == JsonValueKind.True));
}
