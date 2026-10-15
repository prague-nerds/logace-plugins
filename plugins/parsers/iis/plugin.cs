using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone IIS / W3C Extended format; retains the historic profile ID for saved sessions.</summary>
public sealed class IisParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("iis", "IIS / W3C Extended", "IIS W3C Extended logs. Columns follow the #Fields: header.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return context.SampleLines.Any(line => line.TrimStart('\ufeff').StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase)) ? 100 : 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new IisRecordProvider(index);
    }
}
