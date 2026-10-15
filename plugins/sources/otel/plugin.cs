using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Otel;

/// <summary>
/// Optional OpenTelemetry logs source. Transport and discovery stay in this package;
/// LogAce owns form rendering, spool locations and persisted tab state.
/// </summary>
public sealed class OtelSourcePlugin : ILogAceSourcePlugin
{
    /// <summary>Review metadata describes behavior; it does not grant sandbox permissions.</summary>
    public PluginMetadata Metadata
    {
        get;
    } = new("otel.source", "OpenTelemetry logs", "Receive OpenTelemetry logs over OTLP/HTTP as JSON or protobuf.", PluginKind.Source,
        Version: "1.0.2", Capabilities: ["ProvideSourceConfigurationUi", "UsesNetwork", "ReadsLocalFiles", "WritesLocalFiles"]);

    /// <summary>Source-picker category and search aliases.</summary>
    public PluginSourcePresentation Presentation { get; } = new("Listeners", ["otel", "otlp", "opentelemetry", "http", "sink", "receiver"]);

    private static readonly Dictionary<string, string> EnumFields = new() { };
    private static readonly Dictionary<string, string> BooleanModes = new() { };

    /// <summary>Version 1 preserves the former built-in persistent fields and numeric enums.</summary>
    public PluginSourceConfiguration CreateDefaultConfiguration() => Configuration.Create(new OtelLogSourceOptions(OtelLogSourceFactory.DefaultBindAddress, OtelLogSourceFactory.DefaultPort, OtelLogSourceFactory.DefaultPath));

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

            var options = Configuration.Read<OtelLogSourceOptions>(configuration);

            if (!OtelLogSourceFactory.ValidateOptions(options, out string? error))
            {
                return PluginValidationResult.Error(error!);
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

        var form = new PluginSourceFormDefinition("Open OpenTelemetry logs", [
            new("bindAddress","Bind address") { Required=true, HelpText="Use 127.0.0.1 for local senders or 0.0.0.0 for all interfaces." },
            new("port","HTTP port",PluginSourceFieldKind.Integer) { Required=true, Minimum=1, Maximum=65535 },
            new("path","HTTP path") { Required=true, HelpText="OTLP senders usually post logs to /v1/logs." }
        ])
        {
            Description = "Receive OpenTelemetry logs over OTLP/HTTP.",
            Describe = values => "Receive OpenTelemetry logs over OTLP/HTTP.\n" + ExporterHint(values),
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
            if (!OtelLogSourceFactory.TryCreateCore(Configuration.Read<OtelLogSourceOptions>(configuration), out var source, out string? error))
            {
                throw new IOException(error);
            }

            return source!;
        });
    }

    internal static string ExporterHint(PluginSourceFormValues values)
    {
        string bind = values.GetString("bindAddress")?.Trim() ?? "";

        if (bind is "0.0.0.0" or "*" or "+")
        {
            bind = "127.0.0.1";
        }

        if (bind is "::" or "[::]")
        {
            bind = "::1";
        }

        if (string.IsNullOrWhiteSpace(bind) || bind.Any(char.IsWhiteSpace) || bind.IndexOfAny(['/', '?', '#', '@']) >= 0
            || values.GetInteger("port") is not { } port || port is < 1 or > 65535)
        {
            return "Enter a valid address and port to see the exporter endpoint.";
        }

        string path = OtelLogSourceFactory.NormalizePath(values.GetString("path"));
        string host = bind.Contains(':') ? "[" + bind.Trim('[', ']') + "]" : bind;
        return $"OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=http://{host}:{port}{path}";
    }
}
