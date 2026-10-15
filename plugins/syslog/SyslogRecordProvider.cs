using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.RegularExpressions;
using LogAce.PluginApi;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class SyslogRecordProvider : ILogRecordProvider
{
    private readonly ILogLineIndex _fileIndex;
    private readonly RecordSpan[] _recordSpans;

    public SyslogRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
        _recordSpans = RecordSpanUtility.BuildRecordSpans(fileIndex, SyslogParsing.RecordStartRegex);
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; } =
    [
        new("Time", 15, 28, 12, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateTime),
        new("Host", 10, 24, 6),
        new("App", 10, 24, 6),
        new("PID", 6, 16, 4, Alignment.End),
        new("MsgId", 8, 24, 6),
        new("PRI", 5, 5, 3, Alignment.End),
        new("Facility", 9, 12, 7),
        new("Severity", 9, 10, 7),
        new("Format", 7, 8, 7),
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
        SyslogParsedMessage? parsed = SyslogParsing.TryParse(firstLine, continuation);

        if (parsed is null)
        {
            return CreateFallbackRecord(RecordSpanUtility.ReadRawRecord(_fileIndex, span));
        }

        return new LogRecord(
        [
            SyslogParsing.CreateTimestampField(parsed.RawTime, parsed.Timestamp, parsed.TimestampFractionalDigits),
            new LogField(parsed.Host),
            new LogField(parsed.App),
            new LogField(parsed.ProcId),
            new LogField(parsed.MsgId),
            new LogField(parsed.Priority),
            new LogField(parsed.Facility),
            new LogField(parsed.Severity),
            new LogField(parsed.Format),
            new LogField(parsed.StructuredData),
            new LogField(parsed.Message)
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
            new LogField(rawRecord)
        ]);
    }
}
