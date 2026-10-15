using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone Journalctl format; retains the historic profile ID for saved sessions.</summary>
public sealed class JournalctlParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("journalctl", "Journalctl", "systemd logs in journalctl short, short-iso or short-iso-precise format.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return context.SampleLines.Any(JournalctlRecordProvider.LooksLikeJournalctl) ? 75 : 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new JournalctlRecordProvider(index);
    }
}
