using System.Collections.Generic;

namespace LogAce.Plugins.Azure;

/// <summary>Converts provider streaming output to one structured log event per spool line.</summary>
internal sealed class PlainTextOutputTransformer : IStreamingOutputTransformer
{
    public IEnumerable<string> ProcessLine(string line)
    {
        yield return line;
    }

    public IEnumerable<string> Complete()
    {
        return [];
    }
}
