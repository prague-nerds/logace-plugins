using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LogAce.Plugins.Coolify;

internal static class CoolifyLogParser
{
    internal static string[] Runtime(JsonElement logs)
    {
        if (logs.ValueKind != JsonValueKind.String)
        {
            throw new CoolifyApiException("Coolify returned an unexpected runtime log format.");
        }
        return Lines(logs.GetString()!);
    }

    internal static string[] Deployment(JsonElement logs)
    {
        if (logs.ValueKind is JsonValueKind.Null)
        {
            return [];
        }
        if (logs.ValueKind == JsonValueKind.String)
        {
            string text = logs.GetString()!;
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }
            // Coolify stores deployment records as a JSON-encoded string, while some
            // API versions/integrations return the decoded array or plain text.
            string candidate = text.TrimStart();
            string afterBracket = candidate.StartsWith('[') ? candidate[1..].TrimStart() : "";
            if (!candidate.StartsWith('[') || !(afterBracket.StartsWith('{') || afterBracket.StartsWith(']')))
            {
                return Lines(text);
            }
            try
            {
                using var document = JsonDocument.Parse(text);
                return Deployment(document.RootElement);
            }
            catch (JsonException)
            {
                throw new CoolifyApiException("Coolify returned invalid deployment log JSON.");
            }
        }
        if (logs.ValueKind != JsonValueKind.Array)
        {
            throw new CoolifyApiException("Coolify returned an unexpected deployment log format.");
        }

        var lines = new List<string>();
        foreach (JsonElement record in logs.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.String)
            {
                throw new CoolifyApiException("Coolify returned an unexpected deployment log record.");
            }
            // Keep hidden/debug records as well: hidden is a UI flag, not a permission.
            string time = CoolifyClient.Text(record, "timestamp");
            string type = CoolifyClient.Text(record, "type");
            foreach (string line in Lines(output.GetString()!))
            {
                lines.Add((time.Length > 0 ? time + " " : "") + (type.Length > 0 ? "[" + type + "] " : "") + line);
            }
        }
        return lines.ToArray();
    }

    private static string[] Lines(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        // Drop only the delimiter's final empty element, preserving intentional blank lines.
        return text.Length == 0 ? [] : lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}
