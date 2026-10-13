// LogAce-Plugin:
// Id: oslog.source
// Version: 1.0.0
// DisplayName: macOS unified log
// Kind: Source
// EntryPoint: LogAce.Plugins.OsLog.OsLogSourcePlugin
// ApiVersion: 1
// Capabilities: ProvideSourceConfigurationUi, RunsProcesses, ReadsLocalFiles, WritesLocalFiles
// Description: Host macOS unified log snapshots and live streams through the log command.
// Packages:

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.OsLog;

/// <summary>
/// Optional macOS unified log source. Transport and discovery stay in this package;
/// LogAce owns form rendering, spool locations and persisted tab state.
/// </summary>
public sealed class OsLogSourcePlugin : ILogAceSourcePlugin
{
    /// <summary>Review metadata describes behavior; it does not grant sandbox permissions.</summary>
    public PluginMetadata Metadata
    {
        get;
    } = new("oslog.source", "macOS unified log", "Host macOS unified log snapshots and live streams through the log command.", PluginKind.Source,
        Capabilities: ["ProvideSourceConfigurationUi", "RunsProcesses", "ReadsLocalFiles", "WritesLocalFiles"]);

    /// <summary>Source-picker category and search aliases.</summary>
    public PluginSourcePresentation Presentation { get; } = new("Devices & system", ["apple", "mac", "macos", "oslog", "unified"]);

    private static readonly Dictionary<string, string> EnumFields = new() { ["_level"] = "minimumLevel" };
    private static readonly Dictionary<string, string> BooleanModes = new() { ["_follow"] = "openMode" };

    /// <summary>Version 1 preserves the former built-in persistent fields and numeric enums.</summary>
    public PluginSourceConfiguration CreateDefaultConfiguration() => Configuration.Create(new OsLogSourceOptions(null, null, null, OsLogLevel.Default, OsLogOpenMode.Snapshot, "15m"));

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

            var options = Configuration.Read<OsLogSourceOptions>(configuration);

            if (!Enum.IsDefined(options.MinimumLevel) || !Enum.IsDefined(options.OpenMode))
            {
                return PluginValidationResult.Error("Choose valid capture mode and level.");
            }

            if (string.IsNullOrWhiteSpace(options.Last))
            {
                return PluginValidationResult.Error("Enter a Last range.", "last");
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

        var form = new PluginSourceFormDefinition("Open macOS unified log", [
            new("process", "Process") { HelpText="Optional process name or PID." },
            new("subsystem", "Subsystem") { HelpText="Optional exact subsystem name." },
            new("predicate", "Predicate", PluginSourceFieldKind.MultilineText) { HelpText="Optional macOS log predicate; combined with the subsystem filter." },
            Choice("_level","Minimum level","Default","Info","Debug"),
            new("last", "Last") { Required=true, HelpText="Snapshot range and discovery sample window, for example 15m." },
            new("_follow", "Follow live", PluginSourceFieldKind.Boolean)
        ])
        {
            Description = "Open this Mac’s unified log. Browse samples the selected Last window.",
            Actions = [new("processes", "_Browse processes...", context => BrowseFacetAsync(context, OsLogFacetKind.Process, "process"), "process"), new("subsystems", "Browse _subsystems...", context => BrowseFacetAsync(context, OsLogFacetKind.Subsystem, "subsystem"), "subsystem")]
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
            if (!OsLogSourceFactory.TryCreateCore(Configuration.Read<OsLogSourceOptions>(configuration), out var source, out string? error))
            {
                throw new IOException(error);
            }

            return source!;
        });
    }

    private static PluginSourceFieldDefinition Choice(string id, string label, params string[] labels) => new(id, label, PluginSourceFieldKind.Choice)
    {
        Choices = labels.Select((text, index) => new PluginSourceChoice(index.ToString(), text)).ToArray()
    };

    private ValueTask<PluginSourceFormUpdate> BrowseFacetAsync(PluginSourceFormActionContext context, OsLogFacetKind kind, string field) => Browse.ChooseAsync(context,
        field, field, "Choose macOS Log " + (kind == OsLogFacetKind.Process ? "Process" : "Subsystem"), NormalizeConfiguration, () =>
        {
            if (!OsLogFacetCatalog.TrySample(kind, context.Values.GetString("last") ?? "15m", out var facets, out string? error))
            {
                throw new IOException(error);
            }

            return facets.Select(facet => new BrowseOption(facet.Value, facet.Value + " (" + facet.Count + " events)")).ToArray();
        });
}
