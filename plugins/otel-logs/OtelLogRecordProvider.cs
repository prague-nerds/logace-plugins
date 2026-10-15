using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.Json;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class OtelLogRecordProvider : ILogRecordProvider
{
    private readonly ILogLineIndex _fileIndex;

    public OtelLogRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; } =
    [
        new("Time", 19, 28, 16, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateTime),
        new("ObservedAt", 19, 28, 16, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateTime),
        new("Severity", 9, 14, 5),
        new("SevNo", 5, 5, 3, Alignment.End),
        new("Service", 12, 32, 7),
        new("Scope", 12, 40, 7),
        new("TraceId", 16, 32, 8),
        new("SpanId", 12, 16, 8),
        new("Event", 10, 32, 6),
        new("Attributes", 20, 240, 12),
        new("Resource", 20, 240, 12),
        new("Body", 24, 4096, 24, ExpandToFill: true, WrapContent: true)
    ];

    public bool ExpandLastColumn => true;

    public int MaxCellWidth => 4096;

    public bool SupportsTimestampNavigation => true;

    public int RecordCount => _fileIndex.LineCount;

    public LogRecord GetRecord(int recordIndex)
    {
        string line = _fileIndex.GetLine(recordIndex);
        if (!TryParseLine(line, out NormalizedOtelLogRecord record))
        {
            return CreateFallbackRecord(line);
        }

        return new LogRecord(
        [
            CreateTimestampField(record.Time),
            CreateTimestampField(record.ObservedAt),
            new LogField(record.Severity),
            new LogField(record.SeverityNumber),
            new LogField(record.ServiceName),
            new LogField(BuildScope(record.ScopeName, record.ScopeVersion)),
            new LogField(record.TraceId),
            new LogField(record.SpanId),
            new LogField(record.EventName),
            new LogField(record.Attributes),
            new LogField(record.Resource),
            new LogField(record.Body)
        ]);
    }

    public int GetRecordStartLine(int recordIndex)
    {
        if (_fileIndex.LineCount == 0)
        {
            return -1;
        }

        return Math.Clamp(recordIndex, 0, _fileIndex.LineCount - 1);
    }

    public int FindNearestRecordIndexForLine(int lineIndex)
    {
        if (_fileIndex.LineCount == 0)
        {
            return -1;
        }

        return Math.Clamp(lineIndex, 0, _fileIndex.LineCount - 1);
    }

    private static bool TryParseLine(string line, out NormalizedOtelLogRecord record)
    {
        record = NormalizedOtelLogRecord.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            record = new NormalizedOtelLogRecord(
                GetString(root, "time"),
                GetString(root, "observedAt"),
                GetString(root, "severity"),
                GetString(root, "severityNumber"),
                GetString(root, "serviceName"),
                GetString(root, "scopeName"),
                GetString(root, "scopeVersion"),
                GetString(root, "traceId"),
                GetString(root, "spanId"),
                GetString(root, "eventName"),
                GetString(root, "attributes"),
                GetString(root, "resource"),
                GetString(root, "body"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => value.GetRawText()
        };
    }

    private static LogField CreateTimestampField(string rawTimestamp)
    {
        if (string.IsNullOrWhiteSpace(rawTimestamp)
            || !DateTimeOffset.TryParse(
                rawTimestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestamp))
        {
            return new LogField(rawTimestamp);
        }

        return new LogField(rawTimestamp, LogFieldType.Timestamp, timestamp, GetFractionalSecondDigits(rawTimestamp));
    }

    private static int GetFractionalSecondDigits(string rawTimestamp)
    {
        int separatorIndex = rawTimestamp.IndexOf('.');
        if (separatorIndex < 0)
        {
            return 0;
        }

        int digits = 0;
        for (int index = separatorIndex + 1; index < rawTimestamp.Length && char.IsAsciiDigit(rawTimestamp[index]); index++)
        {
            digits++;
        }

        return Math.Min(7, digits);
    }

    private static string BuildScope(string name, string version)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(version)
            ? name
            : $"{name}@{version}";
    }

    private static LogRecord CreateFallbackRecord(string rawLine)
    {
        return new LogRecord(
        [
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(rawLine)
        ]);
    }

    private readonly record struct NormalizedOtelLogRecord(
        string Time,
        string ObservedAt,
        string Severity,
        string SeverityNumber,
        string ServiceName,
        string ScopeName,
        string ScopeVersion,
        string TraceId,
        string SpanId,
        string EventName,
        string Attributes,
        string Resource,
        string Body)
    {
        public static readonly NormalizedOtelLogRecord Empty = new(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    }
}
