using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Logcat;

/// <summary>
/// Optional Android device (logcat) source. Transport and discovery stay in this package;
/// LogAce owns form rendering, spool locations and persisted tab state.
/// </summary>
public sealed class LogcatSourcePlugin : ILogAceSourcePlugin
{
    /// <summary>Review metadata describes behavior; it does not grant sandbox permissions.</summary>
    public PluginMetadata Metadata
    {
        get;
    } = new("logcat.source", "Android device (logcat)", "Android logcat snapshots and live streams through adb.", PluginKind.Source,
        Version: "1.0.1", Capabilities: ["ProvideSourceConfigurationUi", "RunsProcesses", "UsesNetwork", "ReadsLocalFiles", "WritesLocalFiles"]);

    /// <summary>Source-picker category and search aliases.</summary>
    public PluginSourcePresentation Presentation { get; } = new("Devices & system", ["android", "adb", "logcat", "mobile", "device"]);

    private static readonly Dictionary<string, string> EnumFields = new() { ["_level"] = "minimumLevel" };
    private static readonly Dictionary<string, string> BooleanModes = new() { ["_follow"] = "openMode" };

    /// <summary>Version 1 preserves the former built-in persistent fields and numeric enums.</summary>
    public PluginSourceConfiguration CreateDefaultConfiguration() => Configuration.Create(new LogcatSourceOptions("any", null, null, [LogcatBuffer.Main], LogcatLevel.Info, [], null, null, LogcatOpenMode.Snapshot, "2000", false));

    /// <summary>Normalization is deterministic and performs no discovery or network I/O.</summary>
    public PluginSourceConfiguration NormalizeConfiguration(PluginSourceConfiguration configuration)
    {
        var normalized = Configuration.Normalize(configuration, CreateDefaultConfiguration(), EnumFields, BooleanModes);
        var values = Configuration.Values(normalized);

        if (configuration.Values.TryGetProperty("_device", out var device))
        {
            string? selected = device.ValueKind == JsonValueKind.String ? device.GetString()?.Trim() : null;
            string? previous = values["deviceSerial"].ValueKind == JsonValueKind.String ? values["deviceSerial"].GetString() : values["deviceSelector"].GetString();
            values["deviceSelector"] = selected is not null ? PluginSourceJson.Value(selected) : device;

            if (selected != previous)
            {
                foreach (string key in new[] { "deviceSerial", "deviceModel", "processId" })
                {
                    values[key] = PluginSourceJson.Value((string?)null);
                }
            }
        }

        if (configuration.Values.TryGetProperty("_package", out var package))
        {
            string? selected = package.ValueKind == JsonValueKind.String ? package.GetString()?.Trim() : null;
            string? previous = values["package"].ValueKind == JsonValueKind.String ? values["package"].GetString() : null;
            values["package"] = string.IsNullOrWhiteSpace(selected) ? PluginSourceJson.Value((string?)null) : PluginSourceJson.Value(selected);

            if (selected != previous)
            {
                values["processId"] = PluginSourceJson.Value((string?)null);
            }
        }

        if (configuration.Values.TryGetProperty("_buffers", out var buffers))
        {
            values["buffers"] = buffers.ValueKind == JsonValueKind.String ? Configuration.Element(Split(buffers.GetString()).Select(text => LogcatOptionTokens.TryParseBuffer(text, out var buffer) ? (int)buffer : -1).ToArray()) : buffers;
        }

        if (configuration.Values.TryGetProperty("_tags", out var tags))
        {
            values["tags"] = tags.ValueKind == JsonValueKind.String ? Configuration.Element(Split(tags.GetString())) : tags;
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

            var options = Configuration.Read<LogcatSourceOptions>(configuration);

            if (string.IsNullOrWhiteSpace(options.DeviceSelector))
            {
                return PluginValidationResult.Error("Enter a device selector.", "_device");
            }

            if (!Enum.IsDefined(options.OpenMode) || !Enum.IsDefined(options.MinimumLevel))
            {
                return PluginValidationResult.Error("Choose valid capture mode and level.");
            }

            if (options.Buffers is null || options.Buffers.Count == 0 || options.Buffers.Any(buffer => !Enum.IsDefined(buffer)))
            {
                return PluginValidationResult.Error("Enter valid logcat buffers.", "_buffers");
            }

            if (options.Tags is null || options.Tags.Any(string.IsNullOrWhiteSpace))
            {
                return PluginValidationResult.Error("Enter valid tags.", "_tags");
            }

            if (options.ProcessId is <= 0)
            {
                return PluginValidationResult.Error("PID must be positive or empty.", "processId");
            }

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
        var values = Configuration.Values(initial);
        values["_device"] = values["deviceSerial"].ValueKind == JsonValueKind.String ? values["deviceSerial"] : values["deviceSelector"];
        values["_package"] = values["package"];
        values["_buffers"] = PluginSourceJson.Value(values["buffers"].ValueKind == JsonValueKind.Array ? string.Join(", ", values["buffers"].EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _)).Select(v => LogcatOptionTokens.ToBufferArgument((LogcatBuffer)v.GetInt32()))) : "");
        values["_tags"] = PluginSourceJson.Value(values["tags"].ValueKind == JsonValueKind.Array ? string.Join(", ", values["tags"].EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())) : "");
        initial = new(initial.Version, PluginSourceJson.Object(values), initial.TransientValues);
        var form = new PluginSourceFormDefinition("Open Android device (logcat)", [
            new("_device", "Device") { Required=true, HelpText="any, a serial, a model or device description." },
            new("_package", "Package") { HelpText="Optional running app; its PID is resolved again after reconnect." },
            new("processId", "PID", PluginSourceFieldKind.Integer) { Minimum=1, HelpText="Explicit PID overrides package lookup." },
            new("_buffers", "Buffers") { Required=true, HelpText="Comma-separated: main, system, crash, radio, events, kernel, all." },
            Choice("_level","Minimum level","Verbose","Debug","Info","Warn","Error","Fatal"),
            new("_tags", "Tags") { HelpText="Comma-separated tags; empty accepts all tags." },
            new("tail", "Tail") { Required=true, HelpText="all or a non-negative line count." },
            new("_follow", "Follow live", PluginSourceFieldKind.Boolean),
            new("clearBufferFirst", "Clear device buffers first", PluginSourceFieldKind.Boolean) { HelpText="Explicitly clears device logs before capture." }
        ])
        {
            Description = "Choose an online device and capture logcat. Buffer clearing requires the checked option.",
            Actions = [new("devices", "_Browse devices...", BrowseDevicesAsync, "_device"), new("packages", "Browse _packages...", BrowsePackagesAsync, "_package")]
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
            if (!LogcatSourceFactory.TryCreateCore(Configuration.Read<LogcatSourceOptions>(configuration), out var source, out string? error))
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

    private static string[] Split(string? text) => (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private ValueTask<PluginSourceFormUpdate> BrowseDevicesAsync(PluginSourceFormActionContext context) => Browse.ChooseAsync(context,
        "deviceSelector", "_device", "Choose Android Device", NormalizeConfiguration, () =>
        {
            if (!AdbDeviceCatalog.TryListDevices(out var devices, out string? error))
            {
                throw new IOException(error);
            }

            return devices.Where(device => device.IsOnline).Select(device => new BrowseOption(device.Serial, (device.Model ?? "Android") + " (" + device.Serial + ")",
                new Dictionary<string, JsonElement> { ["deviceSerial"] = PluginSourceJson.Value(device.Serial), ["deviceModel"] = PluginSourceJson.Value(device.Model), ["processId"] = PluginSourceJson.Value((string?)null) })).ToArray();
        });

    private ValueTask<PluginSourceFormUpdate> BrowsePackagesAsync(PluginSourceFormActionContext context) => Browse.ChooseAsync(context,
        "package", "_package", "Choose Android Package", NormalizeConfiguration, () =>
        {
            var options = Configuration.Read<LogcatSourceOptions>(NormalizeConfiguration(new(1, PluginSourceJson.Object(context.Values.Values))));

            if (!AdbDeviceCatalog.TryResolveDevice(options.DeviceSerial ?? options.DeviceSelector, out var device, out string? error))
            {
                throw new IOException(error);
            }

            if (!AdbPackageCatalog.TryListPackages(device!.Serial, false, out var packages, out error))
            {
                throw new IOException(error);
            }

            return packages.Select(package => new BrowseOption(package, package, new Dictionary<string, JsonElement> { ["processId"] = PluginSourceJson.Value((string?)null) })).ToArray();
        });

}
