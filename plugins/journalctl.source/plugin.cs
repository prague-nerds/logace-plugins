// LogAce-Plugin:
// Id: journalctl.source
// Version: 1.0.0
// DisplayName: Journalctl service
// Kind: Source
// EntryPoint: LogAce.Plugins.Journalctl.JournalctlSourcePlugin
// ApiVersion: 1
// Capabilities: ProvideSourceConfigurationUi, RunsProcesses, ReadsLocalFiles, WritesLocalFiles
// Description: Local systemd journal snapshots and live streams through journalctl.
// Packages:

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Journalctl;

/// <summary>
/// Optional Journalctl service source. Transport and discovery stay in this package;
/// LogAce owns form rendering, spool locations and persisted tab state.
/// </summary>
public sealed class JournalctlSourcePlugin : ILogAceSourcePlugin
{
    /// <summary>Review metadata describes behavior; it does not grant sandbox permissions.</summary>
    public PluginMetadata Metadata
    {
        get;
    } = new("journalctl.source", "Journalctl service", "Local systemd journal snapshots and live streams through journalctl.", PluginKind.Source,
        Capabilities: ["ProvideSourceConfigurationUi", "RunsProcesses", "ReadsLocalFiles", "WritesLocalFiles"]);

    /// <summary>Source-picker category and search aliases.</summary>
    public PluginSourcePresentation Presentation { get; } = new("Devices & system", ["linux", "systemd", "journalctl", "journal", "service"]);

    private static readonly Dictionary<string, string> EnumFields = new() { };
    private static readonly Dictionary<string, string> BooleanModes = new() { };

    /// <summary>Version 1 preserves the former built-in persistent fields and numeric enums.</summary>
    public PluginSourceConfiguration CreateDefaultConfiguration() => Configuration.Create(new JournalctlSourceOptions(null, "2000", null, true));

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

            var options = Configuration.Read<JournalctlSourceOptions>(configuration);

            if (string.IsNullOrWhiteSpace(options.Tail) || options.Tail != "all" && (!int.TryParse(options.Tail, out int count) || count < 0))
            {
                return PluginValidationResult.Error("Tail must be all or a non-negative count.", "tail");
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

        var form = new PluginSourceFormDefinition("Open Journalctl service", [
            new("unit", "Service unit") { HelpText = "Empty opens the whole journal." },
            new("tail", "Tail") { Required=true, HelpText="all or a non-negative line count." },
            new("since", "Since") { HelpText="Empty/all, boot, a duration (30m) or a journalctl timestamp." },
            new("follow", "Follow live", PluginSourceFieldKind.Boolean)
        ])
        {
            Description = "Choose a systemd service or enter a known unit. Empty uses the whole journal.",
            Actions = [new("services", "_Browse services...", BrowseServicesAsync, "unit")]
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
            if (!OperatingSystem.IsLinux())
            {
                throw new IOException("Local journalctl logs require Linux.");
            }

            if (!JournalctlSourceFactory.TryCreateCore(Configuration.Read<JournalctlSourceOptions>(configuration), out var source, out string? error))
            {
                throw new IOException(error);
            }

            return source!;
        });
    }

    private ValueTask<PluginSourceFormUpdate> BrowseServicesAsync(PluginSourceFormActionContext context) => Browse.ChooseAsync(context,
        "unit", "unit", "Choose Journalctl Service", NormalizeConfiguration, () =>
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new IOException("Local journalctl discovery requires Linux.");
            }

            if (!JournalctlServiceCatalog.TryListServices(out var services, out string? error))
            {
                throw new IOException(error);
            }

            return services.Where(service => service.Unit is not null).Select(service => new BrowseOption(service.Unit!, service.Unit + " — " + service.Description)).ToArray();
        });
}
