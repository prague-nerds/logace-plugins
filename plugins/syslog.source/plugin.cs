using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Syslog;

/// <summary>
/// Optional Syslog sink source. Transport and discovery stay in this package;
/// LogAce owns form rendering, spool locations and persisted tab state.
/// </summary>
public sealed class SyslogSourcePlugin : ILogAceSourcePlugin
{
    /// <summary>Review metadata describes behavior; it does not grant sandbox permissions.</summary>
    public PluginMetadata Metadata
    {
        get;
    } = new("syslog.source", "Syslog sink", "Receive syslog messages over UDP and TCP with newline or octet-counted framing.", PluginKind.Source,
        Version: "1.0.1", Capabilities: ["ProvideSourceConfigurationUi", "UsesNetwork", "ReadsLocalFiles", "WritesLocalFiles"]);

    /// <summary>Source-picker category and search aliases.</summary>
    public PluginSourcePresentation Presentation { get; } = new("Listeners", ["syslog", "udp", "tcp", "rfc3164", "rfc5424", "receiver"]);

    private static readonly Dictionary<string, string> EnumFields = new() { };
    private static readonly Dictionary<string, string> BooleanModes = new() { };

    /// <summary>Version 1 preserves the former built-in persistent fields and numeric enums.</summary>
    public PluginSourceConfiguration CreateDefaultConfiguration() => Configuration.Create(new SyslogSinkSourceOptions(SyslogSinkSourceFactory.DefaultBindAddress, SyslogSinkSourceFactory.DefaultPort, SyslogSinkSourceFactory.DefaultPort));

    /// <summary>Normalization is deterministic and performs no discovery or network I/O.</summary>
    public PluginSourceConfiguration NormalizeConfiguration(PluginSourceConfiguration configuration)
    {
        var normalized = Configuration.Normalize(configuration, CreateDefaultConfiguration(), EnumFields, BooleanModes);
        var values = Configuration.Values(normalized);

        // A missing nullable port in the old JSON schema means a disabled
        // transport. Do not replace it with the defaults for a brand-new form.
        foreach (string field in new[] { "udpPort", "tcpPort" })
        {
            if (!configuration.Values.TryGetProperty(field, out _))
            {
                values[field] = PluginSourceJson.Value((string?)null);
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

            var options = Configuration.Read<SyslogSinkSourceOptions>(configuration);

            if (string.IsNullOrWhiteSpace(options.BindAddress))
            {
                return PluginValidationResult.Error("Enter a bind address.", "bindAddress");
            }

            if (options.UdpPort is null && options.TcpPort is null)
            {
                return PluginValidationResult.Error("Enable at least one transport.", "udpPort");
            }

            if (options.UdpPort is < 1 or > 65535)
            {
                return PluginValidationResult.Error("UDP port must be between 1 and 65535.", "udpPort");
            }

            if (options.TcpPort is < 1 or > 65535)
            {
                return PluginValidationResult.Error("TCP port must be between 1 and 65535.", "tcpPort");
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

        var form = new PluginSourceFormDefinition("Open Syslog sink", [
            new("bindAddress","Bind address") { Required=true },
            new("udpPort","UDP port",PluginSourceFieldKind.Integer) { Minimum=1, Maximum=65535, HelpText="Leave blank to disable UDP." },
            new("tcpPort","TCP port",PluginSourceFieldKind.Integer) { Minimum=1, Maximum=65535, HelpText="Leave blank to disable TCP." }
        ])
        {
            Description = "Receive syslog over UDP or TCP. Enable at least one transport.",
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
            if (!SyslogSinkSourceFactory.TryCreateCore(Configuration.Read<SyslogSinkSourceOptions>(configuration), out var source, out string? error))
            {
                throw new IOException(error);
            }

            return source!;
        });
    }

}
