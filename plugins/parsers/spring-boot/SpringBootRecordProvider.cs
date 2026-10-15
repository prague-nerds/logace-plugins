using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.RegularExpressions;
using LogAce.PluginApi;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class SpringBootRecordProvider : ILogRecordProvider
{
    private const string SpringTimestampPattern =
        @"(?<date>\d{4}-\d{2}-\d{2})(?:T|\s+)(?<time>\d{2}:\d{2}:\d{2}(?:[.,]\d{3,9})?(?:Z|[+-]\d{2}:\d{2})?)";

    private static readonly Regex TimestampStartRegex = new(
        $"^{SpringTimestampPattern}\\b",
        RegexOptions.Compiled);

    private static readonly Regex StructuredSpringRegex = new(
        $"^{SpringTimestampPattern}\\s+(?<level>[A-Z]+)\\s+(?<pid>\\d+)\\s+---\\s+\\[(?<thread>[^\\]]*)\\]\\s+(?<logger>[^:]+?)\\s*:\\s(?<message>.*)$",
        RegexOptions.Compiled);

    private readonly ILogLineIndex _fileIndex;
    private readonly RecordSpan[] _recordSpans;

    public SpringBootRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
        _recordSpans = RecordSpanUtility.BuildRecordSpans(fileIndex, TimestampStartRegex);
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; } =
    [
        new("Date", 10, 10, 10, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateOnly),
        new("Time", 16, 16, 12, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.TimeOnly),
        new("Level", 7, 7, 7),
        new("PID", 7, 7, 7, Alignment.End),
        new("Thread", 18, 24, 12),
        new("Logger", 24, 42, 18),
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

        Match structured = StructuredSpringRegex.Match(firstLine);
        if (structured.Success)
        {
            DateTimeOffset? timestamp = ParseTimestamp(structured.Groups["date"].Value, structured.Groups["time"].Value);
            string message = AppendContinuation(structured.Groups["message"].Value, continuation);

            return new LogRecord(
            [
                CreateDateField(structured.Groups["date"].Value, timestamp),
                CreateTimeField(structured.Groups["time"].Value, timestamp),
                new LogField(structured.Groups["level"].Value),
                new LogField(structured.Groups["pid"].Value),
                new LogField(structured.Groups["thread"].Value.Trim()),
                new LogField(structured.Groups["logger"].Value.Trim()),
                new LogField(message)
            ]);
        }

        Match timestampOnly = TimestampStartRegex.Match(firstLine);
        if (timestampOnly.Success)
        {
            DateTimeOffset? timestamp = ParseTimestamp(timestampOnly.Groups["date"].Value, timestampOnly.Groups["time"].Value);
            string remainder = firstLine[timestampOnly.Length..].TrimStart();
            string message = AppendContinuation(remainder, continuation);

            return new LogRecord(
            [
                CreateDateField(timestampOnly.Groups["date"].Value, timestamp),
                CreateTimeField(timestampOnly.Groups["time"].Value, timestamp),
                new LogField(string.Empty),
                new LogField(string.Empty),
                new LogField(string.Empty),
                new LogField(string.Empty),
                new LogField(message)
            ]);
        }

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
            : new LogField(rawTime, LogFieldType.Timestamp, timestamp, GetFractionalSecondDigits(rawTime));
    }

    private static DateTimeOffset? ParseTimestamp(string date, string time)
    {
        string normalizedTime = time.Replace(',', '.');
        string candidate = $"{date}T{normalizedTime}";

        if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsedOffset))
        {
            return parsedOffset;
        }

        if (DateTime.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsedLocal))
        {
            return new DateTimeOffset(parsedLocal);
        }

        return null;
    }

    private static int GetFractionalSecondDigits(string rawTime)
    {
        int separatorIndex = rawTime.IndexOfAny(['.', ',']);
        if (separatorIndex < 0)
        {
            return 0;
        }

        int digits = 0;
        for (int index = separatorIndex + 1; index < rawTime.Length && char.IsAsciiDigit(rawTime[index]); index++)
        {
            digits++;
        }

        return Math.Min(7, digits);
    }
}
