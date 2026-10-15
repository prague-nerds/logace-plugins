using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.RegularExpressions;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class LogcatRecordProvider : ILogRecordProvider
{
    private const string TimestampPattern = @"\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}";

    private static readonly Regex StartRegex = new(
        $"^{TimestampPattern}\\s",
        RegexOptions.Compiled);

    private static readonly Regex StructuredRegex = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\s+(?<pid>\d+)\s+(?<tid>\d+)\s+(?<level>[VDIWEFS])\s+(?<tag>.*?):\s?(?<message>.*)$",
        RegexOptions.Compiled);

    private readonly ILogLineIndex _fileIndex;
    private readonly RecordSpan[] _recordSpans;

    public LogcatRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
        _recordSpans = RecordSpanUtility.BuildRecordSpans(fileIndex, StartRegex);
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; } =
    [
        new("Date", 10, 10, 10, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateOnly),
        new("Time", 12, 12, 8, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.TimeOnly),
        new("PID", 7, 9, 4, Alignment.End),
        new("TID", 7, 9, 4, Alignment.End),
        new("Level", 5, 5, 3),
        new("Tag", 14, 40, 8),
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
                new LogField(string.Empty),
                new LogField(string.Empty),
                new LogField(RecordSpanUtility.ReadRawRecord(_fileIndex, span))
            ]);
        }

        string rawDate = match.Groups["date"].Value;
        string rawTime = match.Groups["time"].Value;
        DateTimeOffset? timestamp = ParseTimestamp(rawDate, rawTime);
        string message = AppendContinuation(match.Groups["message"].Value, continuation);

        return new LogRecord(
        [
            CreateDateField(rawDate, timestamp),
            CreateTimeField(rawTime, timestamp),
            new LogField(match.Groups["pid"].Value),
            new LogField(match.Groups["tid"].Value),
            new LogField(match.Groups["level"].Value),
            new LogField(match.Groups["tag"].Value.Trim()),
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

    public static bool LooksLikeLogcat(string line)
    {
        return !string.IsNullOrWhiteSpace(line) && StructuredRegex.IsMatch(line.Trim());
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
            : new LogField(rawTime, LogFieldType.Timestamp, timestamp, 3);
    }

    private static DateTimeOffset? ParseTimestamp(string date, string time)
    {
        string candidate = $"{date}T{time}";
        return DateTime.TryParseExact(
            candidate,
            "yyyy-MM-ddTHH:mm:ss.fff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out DateTime parsed)
            ? new DateTimeOffset(parsed)
            : null;
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
