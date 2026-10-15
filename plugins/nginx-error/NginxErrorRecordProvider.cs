using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.RegularExpressions;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class NginxErrorRecordProvider : ILogRecordProvider
{
    private const string DateTimePattern = @"\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2}";

    private static readonly Regex StartRegex = new(
        $"^{DateTimePattern}\\s+\\[[a-z]+\\]",
        RegexOptions.Compiled);

    private static readonly Regex StructuredRegex = new(
        @"^(?<date>\d{4}/\d{2}/\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})\s+\[(?<level>[a-z]+)\]\s+(?<pid>\d+(?:#\d+)?)(?::\s*\*?\d+)?\s*(?<message>.*)$",
        RegexOptions.Compiled);

    private readonly ILogLineIndex _fileIndex;
    private readonly RecordSpan[] _recordSpans;

    public NginxErrorRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
        _recordSpans = RecordSpanUtility.BuildRecordSpans(fileIndex, StartRegex);
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; } =
    [
        new("Date", 10, 10, 10, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateOnly),
        new("Time", 12, 12, 8, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.TimeOnly),
        new("Level", 7, 8, 5),
        new("PID", 9, 12, 6, Alignment.End),
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

        Match match = StructuredRegex.Match(firstLine);
        if (!match.Success)
        {
            return new LogRecord(
            [
                new LogField(string.Empty),
                new LogField(string.Empty),
                new LogField(string.Empty),
                new LogField(string.Empty),
                new LogField(RecordSpanUtility.ReadRawRecord(_fileIndex, span))
            ]);
        }

        DateTimeOffset? timestamp = ParseTimestamp(match.Groups["date"].Value, match.Groups["time"].Value);
        string message = AppendContinuation(match.Groups["message"].Value, continuation);

        return new LogRecord(
        [
            CreateDateField(match.Groups["date"].Value, timestamp),
            CreateTimeField(match.Groups["time"].Value, timestamp),
            new LogField(match.Groups["level"].Value),
            new LogField(match.Groups["pid"].Value),
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

    private static LogField CreateDateField(string rawDate, DateTimeOffset? timestamp)
    {
        return timestamp is null
            ? new LogField(rawDate)
            : new LogField(rawDate, LogFieldType.Timestamp, timestamp);
    }

    private static LogField CreateTimeField(string rawTime, DateTimeOffset? timestamp)
    {
        return timestamp is null
            ? new LogField(rawTime)
            : new LogField(rawTime, LogFieldType.Timestamp, timestamp);
    }

    private static DateTimeOffset? ParseTimestamp(string date, string time)
    {
        string candidate = $"{date} {time}";
        if (DateTime.TryParseExact(
                candidate,
                "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime parsed))
        {
            return new DateTimeOffset(parsed);
        }

        return null;
    }

    private static string AppendContinuation(string message, string continuation)
    {
        if (string.IsNullOrEmpty(continuation))
        {
            return message;
        }

        if (string.IsNullOrEmpty(message))
        {
            return continuation;
        }

        return $"{message}{Environment.NewLine}{continuation}";
    }
}
