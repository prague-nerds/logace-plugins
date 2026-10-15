using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone Docker format; retains the historic profile ID for saved sessions.</summary>
public sealed class DockerParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("docker", "Docker", "Docker logs with timestamps.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new DockerRecordProvider(index);
    }
}
