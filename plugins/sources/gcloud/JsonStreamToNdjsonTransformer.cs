using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Buffers;

namespace LogAce.Plugins.Gcp;

/// <summary>Converts provider streaming output to one structured log event per spool line.</summary>
internal sealed class JsonStreamToNdjsonTransformer : IStreamingOutputTransformer
{
    private readonly StringBuilder _pending = new();

    public IEnumerable<string> ProcessLine(string line)
    {
        string trimmed = line.Trim();

        if (trimmed.Length == 0)
        {
            yield break;
        }

        if (_pending.Length > 0)
        {
            _pending.AppendLine();
        }

        _pending.Append(trimmed);

        if (!TryParsePending(out string[] lines))
        {
            yield break;
        }

        foreach (string outputLine in lines)
        {
            yield return outputLine;
        }
    }

    public IEnumerable<string> Complete()
    {
        if (_pending.Length == 0)
        {
            return [];
        }

        if (TryParsePending(out string[] lines))
        {
            return lines;
        }

        _pending.Clear();
        return [];
    }

    public static IEnumerable<string> ToNdjsonLines(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                yield return item.GetRawText();
            }

            yield break;
        }

        if (element.ValueKind != JsonValueKind.Null
            && element.ValueKind != JsonValueKind.Undefined)
        {
            yield return element.GetRawText();
        }
    }

    private bool TryParsePending(out string[] lines)
    {
        lines = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(_pending.ToString());
            lines = ToNdjsonLines(document.RootElement).ToArray();
            _pending.Clear();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
