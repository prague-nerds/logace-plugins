using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone CloudWatch format; retains the historic profile ID for saved sessions.</summary>
public sealed class CloudwatchParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("cloudwatch", "CloudWatch", "CloudWatch detailed-format logs, including multiline messages.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new CloudWatchRecordProvider(index);
    }
}
