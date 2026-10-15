using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone OTel Logs format; retains the historic profile ID for saved sessions.</summary>
public sealed class OtelLogsParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("otel-logs", "OTel Logs", "OTLP logs with resource, scope, trace, span and message fields.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new OtelLogRecordProvider(index);
    }
}
