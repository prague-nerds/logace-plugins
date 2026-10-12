// LogAce-Plugin:
// Id: simctl.source
// Version: 1.0.0
// DisplayName: iOS simulator
// Kind: Source
// EntryPoint: LogAce.Plugins.Simctl.SimctlSourcePlugin
// ApiVersion: 1
// Capabilities: ProvideSourceConfigurationUi, RunsProcesses, ReadsLocalFiles, WritesLocalFiles
// Description: Unified log snapshots from booted iOS simulators through xcrun simctl.
// Packages:

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Simctl;

/// <summary>
/// Optional iOS simulator source. Transport and discovery stay in this package;
/// LogAce owns form rendering, spool locations and persisted tab state.
/// </summary>
public sealed class SimctlSourcePlugin : ILogAceSourcePlugin
{
    /// <summary>Review metadata describes behavior; it does not grant sandbox permissions.</summary>
    public PluginMetadata Metadata
    {
        get;
    } = new("simctl.source", "iOS simulator", "Unified log snapshots from booted iOS simulators through xcrun simctl.", PluginKind.Source,
        Capabilities: ["ProvideSourceConfigurationUi", "RunsProcesses", "ReadsLocalFiles", "WritesLocalFiles"]);

    /// <summary>Source-picker category and search aliases.</summary>
    public PluginSourcePresentation Presentation { get; } = new("Devices & system", ["apple", "ios", "iphone", "xcode", "simctl", "simulator"]);

    private static readonly Dictionary<string, string> EnumFields = new() { };
    private static readonly Dictionary<string, string> BooleanModes = new() { };

    /// <summary>Version 1 preserves the former built-in persistent fields and numeric enums.</summary>
    public PluginSourceConfiguration CreateDefaultConfiguration() => Configuration.Create(new SimctlLogSourceOptions("booted", null, null, null, null, null, null, null, null, "15m", false, false));

    /// <summary>Normalization is deterministic and performs no discovery or network I/O.</summary>
    public PluginSourceConfiguration NormalizeConfiguration(PluginSourceConfiguration configuration)
    {
        var normalized = Configuration.Normalize(configuration, CreateDefaultConfiguration(), EnumFields, BooleanModes);
        var values = Configuration.Values(normalized);

        if (configuration.Values.TryGetProperty("_device", out var device))
        {
            string? selected = device.ValueKind == JsonValueKind.String ? device.GetString()?.Trim() : null;
            string? previous = values["deviceUdid"].ValueKind == JsonValueKind.String ? values["deviceUdid"].GetString() : values["deviceSelector"].GetString();
            values["deviceSelector"] = selected is not null ? PluginSourceJson.Value(selected) : device;

            if (selected != previous)
            {
                foreach (string key in new[] { "deviceUdid", "deviceName", "runtimeDisplayName", "appDisplayName", "appExecutable" })
                {
                    values[key] = PluginSourceJson.Value((string?)null);
                }
            }
        }

        if (configuration.Values.TryGetProperty("_app", out var app))
        {
            string? selected = app.ValueKind == JsonValueKind.String ? app.GetString()?.Trim() : null;
            string? previous = values["appBundleId"].ValueKind == JsonValueKind.String ? values["appBundleId"].GetString() : null;
            values["appBundleId"] = string.IsNullOrWhiteSpace(selected) ? PluginSourceJson.Value((string?)null) : PluginSourceJson.Value(selected);

            if (selected != previous)
            {
                foreach (string key in new[] { "appDisplayName", "appExecutable" })
                {
                    values[key] = PluginSourceJson.Value((string?)null);
                }
            }
        }

        normalized = new(configuration.Version, PluginSourceJson.Object(values), configuration.TransientValues);
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

            var options = Configuration.Read<SimctlLogSourceOptions>(configuration);

            if (string.IsNullOrWhiteSpace(options.DeviceSelector))
            {
                return PluginValidationResult.Error("Enter a simulator or booted.", "_device");
            }

            if (string.IsNullOrWhiteSpace(options.Last))
            {
                return PluginValidationResult.Error("Enter a Last range.", "last");
            }

            if (options.ProcessId is <= 0)
            {
                return PluginValidationResult.Error("PID must be positive or empty.", "processId");
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
        var values = Configuration.Values(initial);
        values["_device"] = values["deviceUdid"].ValueKind == JsonValueKind.String ? values["deviceUdid"] : values["deviceSelector"];
        values["_app"] = values["appBundleId"];
        initial = new(initial.Version, PluginSourceJson.Object(values), initial.TransientValues);
        var form = new PluginSourceFormDefinition("Open iOS simulator", [
            new("_device", "Simulator") { Required=true, HelpText="booted, simulator name or UDID." },
            new("_app", "App bundle ID") { HelpText="Optional; browse installed apps or enter a known bundle ID." },
            new("process", "Process") { HelpText="Optional name; clear PID to filter by name." },
            new("processId", "PID", PluginSourceFieldKind.Integer) { Minimum=1 },
            new("last", "Last") { Required=true, HelpText="Snapshot range, for example 15m." },
            new("includeInfo", "Include info", PluginSourceFieldKind.Boolean),
            new("includeDebug", "Include debug", PluginSourceFieldKind.Boolean)
        ])
        {
            Description = "Import unified-log events from a booted simulator, optionally narrowed to an app or process.",
            Actions = [new("devices", "_Browse simulators...", BrowseDevicesAsync, "_device"), new("apps", "Browse _apps...", BrowseAppsAsync, "_app"), new("processes", "Browse _processes...", BrowseProcessesAsync, "process")]
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
            if (!SimctlLogSourceFactory.TryCreateCore(Configuration.Read<SimctlLogSourceOptions>(configuration), out var source, out string? error))
            {
                throw new IOException(error);
            }

            return source!;
        });
    }

    private ValueTask<PluginSourceFormUpdate> BrowseDevicesAsync(PluginSourceFormActionContext context) => Browse.ChooseAsync(context,
        "deviceSelector", "_device", "Choose iOS Simulator", NormalizeConfiguration, () =>
        {
            if (!SimctlDeviceCatalog.TryListIosSimulators(out var devices, out string? error))
            {
                throw new IOException(error);
            }

            return devices.Where(device => device.IsBooted).Select(device => new BrowseOption(device.Udid, device.Name + " — " + device.RuntimeDisplayName,
                new Dictionary<string, JsonElement> { ["deviceUdid"] = PluginSourceJson.Value(device.Udid), ["deviceName"] = PluginSourceJson.Value(device.Name), ["runtimeDisplayName"] = PluginSourceJson.Value(device.RuntimeDisplayName) })).ToArray();
        });

    private string ResolveDevice(PluginSourceFormActionContext context)
    {
        var options = Configuration.Read<SimctlLogSourceOptions>(NormalizeConfiguration(new(1, PluginSourceJson.Object(context.Values.Values))));

        if (!SimctlDeviceCatalog.TryResolveDevice(options.DeviceUdid ?? options.DeviceSelector, out var device, out string? error))
        {
            throw new IOException(error);
        }

        if (device is null || !device.IsBooted)
        {
            throw new IOException("Boot the simulator before browsing apps or processes.");
        }

        return device.Udid;
    }

    private ValueTask<PluginSourceFormUpdate> BrowseAppsAsync(PluginSourceFormActionContext context) => Browse.ChooseAsync(context,
        "appBundleId", "_app", "Choose Simulator App", NormalizeConfiguration, () =>
        {
            if (!SimctlAppCatalog.TryListApps(ResolveDevice(context), out var apps, out string? error))
            {
                throw new IOException(error);
            }

            return apps.Select(app => new BrowseOption(app.BundleId, app.DisplayName + " (" + app.BundleId + ")",
                new Dictionary<string, JsonElement> { ["appDisplayName"] = PluginSourceJson.Value(app.DisplayName), ["appExecutable"] = PluginSourceJson.Value(app.Executable) })).ToArray();
        });

    private ValueTask<PluginSourceFormUpdate> BrowseProcessesAsync(PluginSourceFormActionContext context) => Browse.ChooseAsync(context,
        "process", "process", "Choose Simulator Process", NormalizeConfiguration, () =>
        {
            if (!SimctlProcessCatalog.TryListProcesses(ResolveDevice(context), out var processes, out string? error))
            {
                throw new IOException(error);
            }

            return processes.Select(process => new BrowseOption(process.Pid.ToString(), process.Name + " (" + process.Pid + ")",
                new Dictionary<string, JsonElement> { ["process"] = PluginSourceJson.Value((string?)null), ["processId"] = PluginSourceJson.Value(process.Pid) })).ToArray();
        });

}
