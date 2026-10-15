using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone Syslog (RFC 3164 / RFC 5424) format; retains the historic profile ID for saved sessions.</summary>
public sealed class SyslogParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("syslog", "Syslog (RFC 3164 / RFC 5424)", "Syslog in RFC 3164 or RFC 5424 format, detected automatically.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new SyslogRecordProvider(index);
    }
}
