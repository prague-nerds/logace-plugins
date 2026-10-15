using System;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;

/// <summary>Standalone Spring Boot format; retains the historic profile ID for saved sessions.</summary>
public sealed class SpringBootParserPlugin : ILogAceParserPlugin
{
    public PluginMetadata Metadata { get; } = new("spring-boot", "Spring Boot", "Spring Boot logs, including multiline messages.", PluginKind.Parser, Capabilities: ["ParseCustomText"]);
    public ParserDetectionHints DetectionHints => ParserDetectionHints.None;
    public int Probe(ParserDetectionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }
    public ILogRecordProvider CreateProvider(ILogLineIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new SpringBootRecordProvider(index);
    }
}
