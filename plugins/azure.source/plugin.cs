using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Azure;

/// <summary>
/// Optional Azure logs source. Transport and discovery stay in this package;
/// LogAce owns form rendering, spool locations and persisted tab state.
/// </summary>
public sealed class AzureSourcePlugin : ILogAceSourcePlugin
{
    /// <summary>Review metadata describes behavior; it does not grant sandbox permissions.</summary>
    public PluginMetadata Metadata
    {
        get;
    } = new("azure.source", "Azure logs", "Azure App Service streams and Log Analytics queries through Azure CLI.", PluginKind.Source,
        Version: "1.0.1", Capabilities: ["ProvideSourceConfigurationUi", "RunsProcesses", "UsesNetwork", "ReadsLocalFiles", "WritesLocalFiles"]);

    /// <summary>Source-picker category and search aliases.</summary>
    public PluginSourcePresentation Presentation { get; } = new("Cloud", ["microsoft", "azure", "az", "kql", "analytics", "webapp"]);

    private static readonly Dictionary<string, string> EnumFields = new() { };
    private static readonly Dictionary<string, string> BooleanModes = new() { };

    /// <summary>Version 1 preserves the former built-in persistent fields and numeric enums.</summary>
    public PluginSourceConfiguration CreateDefaultConfiguration() =>
        new(1, PluginSourceJson.Object(new Dictionary<string, JsonElement>
        {
            ["sourceMode"] = PluginSourceJson.Value("appservice"),
            ["subscription"] = PluginSourceJson.Value((string?)null),
            ["resourceGroup"] = PluginSourceJson.Value(""),
            ["appName"] = PluginSourceJson.Value(""),
            ["slot"] = PluginSourceJson.Value((string?)null),
            ["workspaceId"] = PluginSourceJson.Value(""),
            ["query"] = PluginSourceJson.Value("AppExceptions | take 500"),
            ["timespan"] = PluginSourceJson.Value("PT1H")
        }));

    /// <summary>Normalization is deterministic and performs no discovery or network I/O.</summary>
    public PluginSourceConfiguration NormalizeConfiguration(PluginSourceConfiguration configuration)
    {
        var normalized = Configuration.Normalize(configuration, CreateDefaultConfiguration(), EnumFields, BooleanModes);

        return normalized;
    }

    /// <summary>Reject incompatible versions, malformed JSON and invalid source settings.</summary>
    public PluginValidationResult ValidateConfiguration(PluginSourceConfiguration configuration)
    {
        if (configuration.Version != 1)
        {
            return PluginValidationResult.Error("Unsupported source configuration version.");
        }

        try
        {
            foreach (var field in configuration.Values.EnumerateObject())
            {
                if (field.Value.ValueKind == JsonValueKind.String && field.Value.GetString()!.Contains('\0'))
                {
                    return PluginValidationResult.Error("Values must not contain NUL characters.", field.Name);
                }
            }

            string mode = configuration.Values.GetProperty("sourceMode").GetString()!;

            if (mode == "appservice")
            {
                var options = Configuration.Read<AzureAppServiceOptions>(configuration);

                if (string.IsNullOrWhiteSpace(options.ResourceGroup))
                {
                    return PluginValidationResult.Error("Enter a resource group.", "resourceGroup");
                }

                if (string.IsNullOrWhiteSpace(options.AppName))
                {
                    return PluginValidationResult.Error("Enter an app name.", "appName");
                }
            }
            else if (mode == "analytics")
            {
                var options = Configuration.Read<AzureLogAnalyticsOptions>(configuration);

                if (string.IsNullOrWhiteSpace(options.WorkspaceId))
                {
                    return PluginValidationResult.Error("Enter a workspace ID.", "workspaceId");
                }

                if (string.IsNullOrWhiteSpace(options.Query))
                {
                    return PluginValidationResult.Error("Enter a KQL query.", "query");
                }

                if (string.IsNullOrWhiteSpace(options.Timespan))
                {
                    return PluginValidationResult.Error("Enter a timespan.", "timespan");
                }
            }
            else
            {
                return PluginValidationResult.Error("Choose App Service or Log Analytics.", "sourceMode");
            }

            return PluginValidationResult.Success;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or NullReferenceException)
        {
            return PluginValidationResult.Error("Invalid source configuration. Check the connection fields.");
        }
    }

    /// <summary>The host renders these neutral fields and marshals nested chooser dialogs.</summary>
    public IPluginSourceUiRequest GetConfigurationUi(PluginSourceConfigurationContext context)
    {
        var initial = Configuration.ForForm(NormalizeConfiguration(context.InitialConfiguration), EnumFields, BooleanModes);

        var form = new PluginSourceFormDefinition("Open Azure logs", [
            new("sourceMode", "Source", PluginSourceFieldKind.Choice) { Choices = [new("appservice", "App Service stream"),new("analytics", "Log Analytics query")] },
            new("subscription", "Subscription") { HelpText = "App Service: empty uses the active az subscription." , VisibleWhen = values => values.GetString("sourceMode") == "appservice" },
            new("resourceGroup", "Resource group") { HelpText = "Required for App Service." , VisibleWhen = values => values.GetString("sourceMode") == "appservice" },
            new("appName", "App name") { HelpText = "Required for App Service." , VisibleWhen = values => values.GetString("sourceMode") == "appservice" },
            new("slot", "Deployment slot") { HelpText = "Optional App Service slot." , VisibleWhen = values => values.GetString("sourceMode") == "appservice" },
            new("workspaceId", "Workspace ID") { HelpText = "Required for Log Analytics." , VisibleWhen = values => values.GetString("sourceMode") == "analytics" },
            new("query", "KQL query", PluginSourceFieldKind.MultilineText) { HelpText = "Required for Log Analytics." , VisibleWhen = values => values.GetString("sourceMode") == "analytics" },
            new("timespan", "Timespan") { HelpText = "Log Analytics: for example PT1H." , VisibleWhen = values => values.GetString("sourceMode") == "analytics" }
        ])
        {
            Description = "Choose App Service for a live stream or Log Analytics for a query snapshot.",
            Actions = []
        };
        return new PluginSourceFormRequest(form, initial);
    }

    /// <summary>Successful live sources retain their own lifetime, independent of this opening token.</summary>
    public ValueTask<ILogSource> OpenAsync(PluginSourceConfiguration configuration, PluginSourceOpenContext context)
    {
        configuration = NormalizeConfiguration(configuration);
        var validation = ValidateConfiguration(configuration);

        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Errors[0].Message);
        }

        return SourceRuntime.OpenAsync(context, () =>
        {
            LogSourceHandle? source;
            string? error;
            bool success = configuration.Values.GetProperty("sourceMode").GetString() == "appservice"
                ? AzureAppServiceSourceFactory.TryCreateCore(Configuration.Read<AzureAppServiceOptions>(configuration), out source, out error)
                : AzureLogAnalyticsSourceFactory.TryCreateCore(Configuration.Read<AzureLogAnalyticsOptions>(configuration), out source, out error);

            if (!success)
            {
                throw new IOException(error);
            }

            return source!;
        });
    }

}
