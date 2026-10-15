using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Gcp;

/// <summary>
/// Optional GCP Cloud Logging source. Transport and discovery stay in this package;
/// LogAce owns form rendering, spool locations and persisted tab state.
/// </summary>
public sealed class GcpSourcePlugin : ILogAceSourcePlugin
{
    /// <summary>Review metadata describes behavior; it does not grant sandbox permissions.</summary>
    public PluginMetadata Metadata
    {
        get;
    } = new("gcloud.source", "GCP Cloud Logging", "Cloud Logging snapshots and live streams through gcloud.", PluginKind.Source,
        Version: "1.0.1", Capabilities: ["ProvideSourceConfigurationUi", "RunsProcesses", "UsesNetwork", "ReadsLocalFiles", "WritesLocalFiles"]);

    /// <summary>Source-picker category and search aliases.</summary>
    public PluginSourcePresentation Presentation { get; } = new("Cloud", ["google", "gcp", "gcloud", "logging", "snapshot", "follow"]);

    private static readonly Dictionary<string, string> EnumFields = new() { };
    private static readonly Dictionary<string, string> BooleanModes = new() { ["_follow"] = "openMode" };

    /// <summary>Version 1 preserves the former built-in persistent fields and numeric enums.</summary>
    public PluginSourceConfiguration CreateDefaultConfiguration() => Configuration.Create(new GcpLoggingSourceOptions(null, null, GcpLoggingOpenMode.Snapshot, "1h", 1000));

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

            var options = Configuration.Read<GcpLoggingSourceOptions>(configuration);

            if (!Enum.IsDefined(options.OpenMode))
            {
                return PluginValidationResult.Error("Choose snapshot or follow.", "_follow");
            }

            if (options.Limit <= 0)
            {
                return PluginValidationResult.Error("Limit must be positive.", "limit");
            }

            if (string.IsNullOrWhiteSpace(options.Freshness))
            {
                return PluginValidationResult.Error("Enter a freshness range.", "freshness");
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

        var form = new PluginSourceFormDefinition("Open GCP Cloud Logging", [
            new("project", "Project") { HelpText = "Empty uses the active gcloud project." },
            new("filter", "Logging filter", PluginSourceFieldKind.MultilineText) { HelpText = "Optional Cloud Logging filter expression." },
            new("freshness", "Freshness") { Required = true, HelpText = "Snapshot range, for example 1h or 30m." },
            new("limit", "Limit", PluginSourceFieldKind.Integer) { Required = true, Minimum = 1 },
            new("_follow", "Follow live", PluginSourceFieldKind.Boolean)
        ])
        {
            Description = "Enter a project or browse the current gcloud account. Choose snapshot or follow.",
            Actions = [new("projects", "_Browse projects...", BrowseProjectsAsync, "project")]
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
            if (!GcpLoggingSourceFactory.TryCreateCore(Configuration.Read<GcpLoggingSourceOptions>(configuration), out var source, out string? error))
            {
                throw new IOException(error);
            }

            return source!;
        });
    }

    private ValueTask<PluginSourceFormUpdate> BrowseProjectsAsync(PluginSourceFormActionContext context) => Browse.ChooseAsync(context,
        "project", "project", "Choose GCP Project", NormalizeConfiguration, () =>
        {
            if (!GcpProjectCatalog.TryListProjects(out var projects, out string? error))
            {
                throw new IOException(error);
            }

            return projects.Select(project => new BrowseOption(project.ProjectId, project.ProjectId)).ToArray();
        });
}
