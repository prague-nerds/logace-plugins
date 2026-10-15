using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone Merged format; retains the historic profile ID for saved sessions.</summary>
public sealed class MergedParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("merged", "Merged", "Tabs combined into a timeline, sorted by UTC timestamp.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return context.SampleLines.FirstOrDefault()?.StartsWith("#columns\t", StringComparison.Ordinal) == true ? 100 : 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new MergedRecordProvider(index);
    }
}
