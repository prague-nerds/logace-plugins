using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone Syslog Sink format; retains the historic profile ID for saved sessions.</summary>
public sealed class SyslogSinkParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("syslog-sink", "Syslog Sink", "Received syslog messages with sender, transport and severity fields.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new SyslogSinkRecordProvider(index);
    }
}
