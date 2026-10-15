using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone Apache / W3C Combined format; retains the historic profile ID for saved sessions.</summary>
public sealed class ApacheParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("apache", "Apache / W3C Combined", "Apache combined access logs.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ApacheCombinedRecordProvider(index);
    }
}
