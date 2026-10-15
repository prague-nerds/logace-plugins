using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone Logcat format; retains the historic profile ID for saved sessions.</summary>
public sealed class LogcatParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("logcat", "Logcat", "Android logcat in threadtime format, including multiline messages.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new LogcatRecordProvider(index);
    }
}
