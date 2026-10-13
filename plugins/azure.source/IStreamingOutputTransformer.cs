using System.Collections.Generic;

namespace LogAce.Plugins.Azure;

/// <summary>Provider-specific configuration and transport implementation; no host-private dependencies.</summary>
internal interface IStreamingOutputTransformer
{
    IEnumerable<string> ProcessLine(string line);
    IEnumerable<string> Complete();
}
