using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Coolify;

/// <summary>Coolify discovery, log snapshots and polling through the public source API.</summary>
public sealed class CoolifySourcePlugin : ILogAceSourcePlugin, ILogAcePluginWithHostServices, ILogAceSourceConfigurationResolver
{
    private readonly Func<CoolifyOptions, string, CoolifyClient> _createClient;
    private CoolifyConnections? _connections;

    public CoolifySourcePlugin() : this((options, token) => new CoolifyClient(options, token))
    {
    }

    internal CoolifySourcePlugin(Func<CoolifyOptions, string, CoolifyClient> createClient)
    {
        _createClient = createClient;
    }

    public PluginMetadata Metadata
    {
        get;
    } = new("coolify.source", "Coolify logs",
        "Browse Coolify applications and read runtime or deployment logs with live polling.", PluginKind.Source,
        Version: "1.1.0", Capabilities: ["ProvideSourceConfigurationUi", "UsesNetwork", "ReadsLocalFiles", "WritesLocalFiles", "UsesPluginSettings", "UsesPluginSecrets"]);

    public PluginSourcePresentation Presentation { get; } = new("Cloud", ["coolify", "application", "docker", "build", "deployment", "polling"]);

    public PluginSourceConfiguration CreateDefaultConfiguration() => CoolifyConfiguration.Default();

    public PluginSourceConfiguration NormalizeConfiguration(PluginSourceConfiguration configuration) => CoolifyConfiguration.Normalize(configuration);

    public ValueTask InitializeAsync(PluginHostServices services, System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connections = new(services);
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginSourceConfiguration> ResolveConfigurationAsync(PluginSourceConfiguration configuration,
        System.Threading.CancellationToken cancellationToken = default) => _connections is null
            ? ValueTask.FromResult(configuration) : _connections.ResolveAsync(configuration, cancellationToken);

    public PluginValidationResult ValidateConfiguration(PluginSourceConfiguration configuration) =>
        CoolifyConfiguration.Validate(NormalizeConfiguration(configuration));

    public IPluginSourceUiRequest GetConfigurationUi(PluginSourceConfigurationContext context) =>
        new PluginSourceFormRequest(new("Open Coolify logs",
        [
            new("connectionName", "Connection name") { Required = true, HelpText = "For example Production or Staging. Connection settings are remembered after a successful open." },
            new("instanceUrl", "Coolify URL") { Required = true, HelpText = "Instance URL, for example https://coolify.example.com. HTTPS is recommended." },
            new("tokenEnvironment", "Token environment variable") { HelpText = "Used when API token is empty. Set it for automatic session restore." },
            new("token", "API token", PluginSourceFieldKind.Secret) { HelpText = "Requires read:sensitive. Leave empty to use the environment or the saved token. Never saved in sessions." },
            new("rememberToken", "Remember token", PluginSourceFieldKind.Boolean) { EnabledWhen = _ => _connections is not null,
                HelpText = _connections is null ? "Requires a host with plugin storage services." : "Store in " + _connections.ProviderName + ". Turn off for a temporary token." },
            new("applicationUuid", "Application UUID") { Required = true, HelpText = "Enter a known UUID or Browse applications." },
            new("mode", "Logs", PluginSourceFieldKind.Choice) { Required = true, Choices = [new("runtime", "Runtime logs"), new("deployment", "Deployment / build logs")] },
            new("serviceName", "Compose service") { HelpText = "Optional Docker Compose service name. Empty uses the API's default container.", VisibleWhen = values => values.GetString("mode") == "runtime" },
            new("tail", "Last lines") { Required = true, HelpText = "1–10000, or all. This is the window fetched on every poll.", VisibleWhen = values => values.GetString("mode") == "runtime" },
            new("deploymentUuid", "Deployment UUID") { HelpText = "Browse deployments, or leave empty to open the latest deployment. Follow stays on that deployment.", VisibleWhen = values => values.GetString("mode") == "deployment" },
            new("follow", "Follow live", PluginSourceFieldKind.Boolean),
            new("pollSeconds", "Poll interval (seconds)", PluginSourceFieldKind.Integer) { Required = true, Minimum = 1, Maximum = 300 }
        ])
        {
            Description = "Read application output or a deployment's build log. Live mode polls the API and appends new lines.",
            Actions = [new("connections", "Saved _connections...", BrowseConnectionsAsync, "connectionName"),
                new("forgetToken", "_Forget saved token", ForgetTokenAsync, "token"),
                new("applications", "_Browse applications...", BrowseApplicationsAsync, "applicationUuid"),
                new("deployments", "Browse _deployments...", BrowseDeploymentsAsync, "deploymentUuid")]
        }, NormalizeConfiguration(context.InitialConfiguration));

    public async ValueTask<ILogSource> OpenAsync(PluginSourceConfiguration configuration, PluginSourceOpenContext context)
    {
        configuration = await ResolveConfigurationAsync(NormalizeConfiguration(configuration), context.CancellationToken).ConfigureAwait(false);
        PluginValidationResult validation = ValidateConfiguration(configuration);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Errors[0].Message);
        }
        CoolifyOptions options = CoolifyConfiguration.Read(configuration);
        CoolifyClient client = _createClient(options, CoolifyConfiguration.ResolveToken(configuration));
        ILogSource source = await CoolifyLogSession.OpenAsync(client, options, context).ConfigureAwait(false);
        try
        {
            if (_connections is not null)
            {
                await _connections.SaveAsync(configuration, context.CancellationToken).ConfigureAwait(false);
            }
            else if (configuration.Values.GetProperty("rememberToken").GetBoolean())
            {
                throw new PluginStorageException(PluginStorageError.Unavailable, "This host cannot save tokens. Turn off Remember token or update LogAce.");
            }
            context.CancellationToken.ThrowIfCancellationRequested();
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private async ValueTask<CoolifyClient> DiscoveryClientAsync(PluginSourceFormValues values,
        System.Threading.CancellationToken cancellationToken, bool requireApplication = false)
    {
        PluginSourceConfiguration configuration = CoolifyConfiguration.FromForm(values);
        // Discovery does not require an application to have been selected yet.
        PluginValidationResult validation = ValidateConfiguration(configuration);
        var error = validation.Errors.FirstOrDefault(diagnostic => requireApplication || diagnostic.FieldId != "applicationUuid");
        if (error is not null)
        {
            throw new ArgumentException(error.Message);
        }
        configuration = _connections is null ? configuration
            : await _connections.ResolveAsync(configuration, cancellationToken, discovery: !requireApplication).ConfigureAwait(false);
        return _createClient(CoolifyConfiguration.Read(configuration), CoolifyConfiguration.ResolveToken(configuration));
    }

    private async ValueTask<PluginSourceFormUpdate> BrowseApplicationsAsync(PluginSourceFormActionContext context)
    {
        PluginSourceFormUpdate loaded = await RefreshApplicationsAsync(context).ConfigureAwait(false);
        if (!loaded.Choices.TryGetValue("applicationUuid", out var choices))
        {
            return loaded;
        }
        PluginSourceConfiguration initial = BrowserConfiguration(context.Values, "applicationUuid", choices);
        var request = new PluginSourceFormRequest(new("Choose Coolify application",
        [
            new("applicationUuid", "Application", PluginSourceFieldKind.Choice) { Required = true, Choices = choices },
            new("search", "Search name, domain, status or UUID")
        ])
        {
            ConfirmLabel = "_Use application",
            Description = choices.Count == 0 ? "No matching applications. Change Search and Refresh, or cancel to enter a UUID." : "Choose an application from your token's team.",
            Actions = [new("refresh", "_Refresh applications", RefreshApplicationsAsync, "applicationUuid")]
        }, initial);
        var result = await context.Host.ShowAsync(request, context.CancellationToken).ConfigureAwait(false);
        if (result.Status != PluginSourceConfigurationStatus.Confirmed || result.Configuration is not { } selected)
        {
            return new();
        }
        return new()
        {
            Values = new Dictionary<string, JsonElement>
            {
                ["applicationUuid"] = selected.Values.GetProperty("applicationUuid"),
                // Names are only display labels; the UUID remains the source identity.
                ["applicationName"] = PluginSourceJson.Value(choices.FirstOrDefault(choice => choice.Id == selected.Values.GetProperty("applicationUuid").GetString())?.Label.Split(" · ")[0] ?? ""),
                ["deploymentUuid"] = PluginSourceJson.Value("")
            }
        };
    }

    private async ValueTask<PluginSourceFormUpdate> RefreshApplicationsAsync(PluginSourceFormActionContext context)
    {
        try
        {
            using var client = await DiscoveryClientAsync(context.Values, context.CancellationToken).ConfigureAwait(false);
            var choices = await client.ApplicationsAsync(context.Values.GetString("search")?.Trim() ?? "", context.CancellationToken).ConfigureAwait(false);
            return ChoiceUpdate("applicationUuid", choices, context.Values.GetString("applicationUuid"), "No matching applications in this team.");
        }
        catch (Exception error) when (error is IOException or ArgumentException or PluginStorageException)
        {
            return new()
            {
                Diagnostics = [new(error.Message, "applicationUuid")]
            };
        }
    }

    private async ValueTask<PluginSourceFormUpdate> BrowseDeploymentsAsync(PluginSourceFormActionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Values.GetString("applicationUuid")))
        {
            return new()
            {
                Diagnostics = [new("Choose an application first.", "applicationUuid")]
            };
        }
        PluginSourceFormUpdate loaded = await RefreshDeploymentsAsync(context).ConfigureAwait(false);
        if (!loaded.Choices.TryGetValue("deploymentUuid", out var choices))
        {
            return loaded;
        }
        PluginSourceConfiguration initial = BrowserConfiguration(context.Values, "deploymentUuid", choices);
        var values = initial.Values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        values["mode"] = PluginSourceJson.Value("deployment");
        values["skip"] = PluginSourceJson.Value(0);
        initial = new(1, PluginSourceJson.Object(values), initial.TransientValues);
        var request = new PluginSourceFormRequest(new("Choose Coolify deployment",
        [
            new("deploymentUuid", "Deployment", PluginSourceFieldKind.Choice) { Required = true, Choices = choices },
            new("skip", "Skip newest deployments", PluginSourceFieldKind.Integer) { Minimum = 0, HelpText = "Shows 50 deployments. Use 50, 100, ... then Refresh to browse older history." }
        ])
        {
            ConfirmLabel = "_Use deployment",
            Description = "Choose a deployment by time, status, commit and UUID. Follow keeps reading this deployment.",
            Actions = [new("refresh", "_Refresh deployments", RefreshDeploymentsAsync, "deploymentUuid")]
        }, initial);
        var result = await context.Host.ShowAsync(request, context.CancellationToken).ConfigureAwait(false);
        return result.Status == PluginSourceConfigurationStatus.Confirmed && result.Configuration is { } selected
            ? new()
            {
                Values = new Dictionary<string, JsonElement> { ["mode"] = PluginSourceJson.Value("deployment"), ["deploymentUuid"] = selected.Values.GetProperty("deploymentUuid") }
            }
            : new();
    }

    private async ValueTask<PluginSourceFormUpdate> RefreshDeploymentsAsync(PluginSourceFormActionContext context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context.Values.GetString("applicationUuid")))
            {
                throw new ArgumentException("Choose an application first.");
            }
            long skip = context.Values.GetInteger("skip") ?? 0;
            if (skip is < 0 or > int.MaxValue)
            {
                throw new ArgumentException("Enter a valid non-negative history offset.");
            }
            using var client = await DiscoveryClientAsync(context.Values, context.CancellationToken, requireApplication: true).ConfigureAwait(false);
            var choices = await client.DeploymentsAsync(context.Values.GetString("applicationUuid")!, (int)skip, 50, context.CancellationToken).ConfigureAwait(false);
            return ChoiceUpdate("deploymentUuid", choices, context.Values.GetString("deploymentUuid"), "No deployments in this part of the application's history.");
        }
        catch (Exception error) when (error is IOException or ArgumentException or PluginStorageException)
        {
            return new()
            {
                Diagnostics = [new(error.Message, "deploymentUuid")]
            };
        }
    }

    private async ValueTask<PluginSourceFormUpdate> BrowseConnectionsAsync(PluginSourceFormActionContext context)
    {
        try
        {
            if (_connections is null)
            {
                return new()
                {
                    Diagnostics = [new("Update LogAce to use saved connections.", "connectionName")]
                };
            }
            var choices = await _connections.ListAsync(context.CancellationToken).ConfigureAwait(false);
            var request = new PluginSourceFormRequest(new("Choose Coolify connection",
                [new("savedConnection", "Connection", PluginSourceFieldKind.Choice) { Required = true, Choices = choices }])
            {
                ConfirmLabel = "_Use connection",
                Description = choices.Count == 0 ? "No saved connections yet. Open a connection successfully to remember it." : "Load settings for a saved connection. Tokens stay in the system credential store."
            }, new(1, PluginSourceJson.Object(new Dictionary<string, JsonElement>())));
            var result = await context.Host.ShowAsync(request, context.CancellationToken).ConfigureAwait(false);
            if (result.Status != PluginSourceConfigurationStatus.Confirmed || result.Configuration is not { } selected)
            {
                return new();
            }
            string key = selected.Values.GetProperty("savedConnection").GetString()!;
            var connection = await _connections.LoadAsync(key, context.CancellationToken).ConfigureAwait(false);
            var values = connection.Values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            // Do not carry an unsaved typed token over to a different connection.
            values["token"] = PluginSourceJson.Value("");
            return new()
            {
                Values = values
            };
        }
        catch (PluginStorageException error)
        {
            return new()
            {
                Diagnostics = [new(error.Message, "connectionName")]
            };
        }
    }

    private async ValueTask<PluginSourceFormUpdate> ForgetTokenAsync(PluginSourceFormActionContext context)
    {
        try
        {
            if (_connections is null)
            {
                return new()
                {
                    Diagnostics = [new("No credential store is available in this host.", "token")]
                };
            }
            await _connections.ForgetTokenAsync(CoolifyConfiguration.FromForm(context.Values), context.CancellationToken).ConfigureAwait(false);
            return new()
            {
                Values = new Dictionary<string, JsonElement> { ["token"] = PluginSourceJson.Value(""), ["rememberToken"] = PluginSourceJson.Value(false) }
            };
        }
        catch (PluginStorageException error)
        {
            return new()
            {
                Diagnostics = [new(error.Message, "token")]
            };
        }
    }

    private static PluginSourceConfiguration BrowserConfiguration(PluginSourceFormValues form, string field, IReadOnlyList<PluginSourceChoice> choices)
    {
        PluginSourceConfiguration configuration = CoolifyConfiguration.FromForm(form);
        var values = configuration.Values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        values[field] = PluginSourceJson.Value(choices.Any(choice => choice.Id == form.GetString(field)) ? form.GetString(field) : "");
        return new(1, PluginSourceJson.Object(values), configuration.TransientValues);
    }

    private static PluginSourceFormUpdate ChoiceUpdate(string field, IReadOnlyList<PluginSourceChoice> choices, string? selected, string emptyMessage) => new()
    {
        Choices = new Dictionary<string, IReadOnlyList<PluginSourceChoice>> { [field] = choices },
        Values = new Dictionary<string, JsonElement> { [field] = PluginSourceJson.Value(choices.Any(choice => choice.Id == selected) ? selected : "") },
        Diagnostics = choices.Count == 0 ? [new(emptyMessage, field)] : []
    };
}
