using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone NGINX Error format; retains the historic profile ID for saved sessions.</summary>
public sealed class NginxErrorParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("nginx-error", "NGINX Error", "NGINX error logs, including multiline messages.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new NginxErrorRecordProvider(index);
    }
}
