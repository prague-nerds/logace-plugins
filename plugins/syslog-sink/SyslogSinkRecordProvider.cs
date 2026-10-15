using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.RegularExpressions;
using LogAce.PluginApi;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class SyslogSinkRecordProvider : ILogRecordProvider
{
    public const string HeaderLineMarker = "#syslog-sink-v2";
    public const string RecordTimestampPattern = @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z";

    public static readonly string HeaderLine =
        $"{HeaderLineMarker}\tTime\tHost\tApp\tPID\tMsgId\tPRI\tFacility\tSeverity\tFormat\tTransport\tRemoteIp\tListenerPort\tStructuredData\tMessage";

    private static readonly Regex RecordStartRegex = new(
        $"^{RecordTimestampPattern}\\t",
        RegexOptions.Compiled);

    private readonly ILogLineIndex _fileIndex;
    private readonly RecordSpan[] _recordSpans;

    public SyslogSinkRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
        int startLine = HasHeader(fileIndex) ? 1 : 0;
        _recordSpans = fileIndex.LineCount <= startLine
            ? []
            : RecordSpanUtility.BuildRecordSpans(fileIndex, RecordStartRegex, startLine: startLine);
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; } =
    [
        new("Time", 15, 28, 12, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateTime),
        new("ReceivedAt", 19, 28, 16, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateTime),
        new("Host", 10, 24, 6),
        new("App", 10, 24, 6),
        new("PID", 6, 16, 4, Alignment.End),
        new("MsgId", 8, 24, 6),
        new("PRI", 5, 5, 3, Alignment.End),
        new("Facility", 9, 12, 7),
        new("Severity", 9, 10, 7),
        new("Format", 7, 8, 7),
        new("Transport", 9, 9, 3),
        new("RemoteIp", 15, 45, 9),
        new("ListenerPort", 12, 12, 4, Alignment.End),
        new("StructuredData", 18, 160, 12),
        new("Message", 24, 4096, 24, ExpandToFill: true, WrapContent: true)
    ];

    public bool ExpandLastColumn => true;

    public int MaxCellWidth => 4096;

    public bool SupportsTimestampNavigation => true;

    public int RecordCount => _recordSpans.Length;

    public LogRecord GetRecord(int recordIndex)
    {
        RecordSpan span = _recordSpans[recordIndex];
        string firstLine = _fileIndex.GetLine(span.StartLine);
        string continuation = RecordSpanUtility.ReadContinuation(_fileIndex, span);

        string[] parts = firstLine.Split('\t', Columns.Count);
        if (parts.Length == 0)
        {
            return CreateFallbackRecord(RecordSpanUtility.ReadRawRecord(_fileIndex, span));
        }

        string rawReceivedAt = parts[0];
        string rawTime = GetPart(parts, 1);
        string format = GetPart(parts, 9);
        string message = SyslogParsing.AppendContinuation(GetPart(parts, 14), continuation);

        DateTimeOffset? receivedAt = SyslogParsing.ParseRfc5424Timestamp(rawReceivedAt);
        SyslogParsing.TryParseEventTimestamp(format, rawTime, out DateTimeOffset? eventTime, out int eventFractionalDigits);

        return new LogRecord(
        [
            SyslogParsing.CreateTimestampField(rawTime, eventTime, eventFractionalDigits),
            SyslogParsing.CreateTimestampField(rawReceivedAt, receivedAt, SyslogParsing.GetFractionalSecondDigits(rawReceivedAt)),
            new LogField(GetPart(parts, 2)),
            new LogField(GetPart(parts, 3)),
            new LogField(GetPart(parts, 4)),
            new LogField(GetPart(parts, 5)),
            new LogField(GetPart(parts, 6)),
            new LogField(GetPart(parts, 7)),
            new LogField(GetPart(parts, 8)),
            new LogField(format),
            new LogField(GetPart(parts, 10)),
            new LogField(GetPart(parts, 11)),
            new LogField(GetPart(parts, 12)),
            new LogField(GetPart(parts, 13)),
            new LogField(message)
        ]);
    }

    public int GetRecordStartLine(int recordIndex)
    {
        if (_recordSpans.Length == 0)
        {
            return -1;
        }

        int clampedIndex = Math.Clamp(recordIndex, 0, _recordSpans.Length - 1);
        return _recordSpans[clampedIndex].StartLine;
    }

    public int FindNearestRecordIndexForLine(int lineIndex)
    {
        return RecordSpanUtility.FindNearestRecordIndexForLine(_recordSpans, lineIndex);
    }

    private static bool HasHeader(ILogLineIndex fileIndex)
    {
        return fileIndex.LineCount > 0
            && string.Equals(fileIndex.GetLine(0), HeaderLine, StringComparison.Ordinal);
    }

    private static string GetPart(IReadOnlyList<string> parts, int index)
    {
        return index < parts.Count ? parts[index] : string.Empty;
    }

    private static LogRecord CreateFallbackRecord(string rawRecord)
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
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(string.Empty),
            new LogField(rawRecord)
        ]);
    }
}
