using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.RegularExpressions;
using LogAce.PluginApi;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class CloudWatchRecordProvider : ILogRecordProvider
{
    private const string TimestampPattern = @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2})";

    private static readonly Regex StartRegex = new(
        $"^{TimestampPattern}\\s",
        RegexOptions.Compiled);

    private static readonly Regex StructuredRegex = new(
        $"^(?<timestamp>{TimestampPattern})\\s+(?<stream>\\S+)\\s?(?<message>.*)$",
        RegexOptions.Compiled);

    private readonly ILogLineIndex _fileIndex;
    private readonly RecordSpan[] _recordSpans;

    public CloudWatchRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
        _recordSpans = RecordSpanUtility.BuildRecordSpans(fileIndex, StartRegex);
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; } =
    [
        new("Date", 10, 10, 10, LogColumnAlignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateOnly),
        new("Time", 18, 24, 10, LogColumnAlignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.TimeOnly),
        new("Stream", 16, 64, 8),
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
                new LogField(RecordSpanUtility.ReadRawRecord(_fileIndex, span))
            ]);
        }

        string rawTimestamp = match.Groups["timestamp"].Value;
        DateTimeOffset? timestamp = TryParseTimestamp(rawTimestamp, out int fractionalDigits);
        string rawDate = rawTimestamp;
        string rawTime = string.Empty;
        int separator = rawTimestamp.IndexOf('T');
        if (separator > 0 && separator + 1 < rawTimestamp.Length)
        {
            rawDate = rawTimestamp[..separator];
            rawTime = rawTimestamp[(separator + 1)..];
        }

        string message = AppendContinuation(match.Groups["message"].Value, continuation);
        return new LogRecord(
        [
            timestamp is null ? new LogField(rawDate) : new LogField(rawDate, LogFieldType.Timestamp, timestamp),
            timestamp is null ? new LogField(rawTime) : new LogField(rawTime, LogFieldType.Timestamp, timestamp, fractionalDigits),
            new LogField(match.Groups["stream"].Value),
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

    public static bool LooksLikeCloudWatchShort(string line)
    {
        return !string.IsNullOrWhiteSpace(line) && StructuredRegex.IsMatch(line.Trim());
    }

    public static string? TryExtractTimestampRaw(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        int separatorIndex = line.IndexOf(' ');
        if (separatorIndex <= 0)
        {
            return null;
        }

        string candidate = line[..separatorIndex];
        return StartRegex.IsMatch(candidate + " ") ? candidate : null;
    }

    private static DateTimeOffset? TryParseTimestamp(string rawTimestamp, out int fractionalDigits)
    {
        fractionalDigits = GetFractionalSecondDigits(rawTimestamp);
        return DateTimeOffset.TryParse(
            rawTimestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out DateTimeOffset timestamp)
                ? timestamp
                : null;
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
