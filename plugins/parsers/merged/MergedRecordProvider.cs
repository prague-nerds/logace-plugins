using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.RegularExpressions;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class MergedRecordProvider : ILogRecordProvider
{
    public const string DefaultSourceColumnName = "Source";
    public const string DefaultFlattenedMessageColumnName = "Message";
    public const string HeaderLineMarker = "#columns";

    private const string MergedTimestampPattern =
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z";

    private static readonly Regex RecordStartRegex = new(
        $"^{MergedTimestampPattern}\\t",
        RegexOptions.Compiled);

    private static readonly IReadOnlyList<LogColumnDefinition> FallbackColumns =
    [
        new("Date", 10, 10, 10, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateOnly),
        new("Time", 16, 16, 12, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.TimeOnly),
        new(DefaultSourceColumnName, 12, 24, 8),
        new(DefaultFlattenedMessageColumnName, 24, 4096, 24, ExpandToFill: true, WrapContent: true)
    ];

    private readonly ILogLineIndex _fileIndex;
    private readonly RecordSpan[] _recordSpans;

    public MergedRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
        string[]? headerNames = ParseHeader(fileIndex);
        Columns = BuildColumns(headerNames);
        int startLine = headerNames is null ? 0 : 1;
        _recordSpans = fileIndex.LineCount <= startLine
            ? []
            : RecordSpanUtility.BuildRecordSpans(fileIndex, RecordStartRegex, startLine: startLine);
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; }

    public bool ExpandLastColumn => Columns[^1].ExpandToFill;

    public int MaxCellWidth => 4096;

    public bool SupportsTimestampNavigation => true;

    public int RecordCount => _recordSpans.Length;

    public LogRecord GetRecord(int recordIndex)
    {
        RecordSpan span = _recordSpans[recordIndex];
        string firstLine = _fileIndex.GetLine(span.StartLine);
        string continuation = RecordSpanUtility.ReadContinuation(_fileIndex, span);

        int columnCount = Columns.Count;
        int valueCount = columnCount - 1;
        string[] parts = firstLine.Split('\t', valueCount);

        string rawTimestamp = parts.Length > 0 ? parts[0] : string.Empty;
        DateTimeOffset? timestamp = ParseTimestamp(rawTimestamp);
        int fractionalDigits = GetFractionalSecondDigits(rawTimestamp);

        var resultFields = new LogField[columnCount];
        resultFields[0] = CreateTimestampField(rawTimestamp, timestamp, fractionalDigits);
        resultFields[1] = CreateTimestampField(rawTimestamp, timestamp, fractionalDigits);

        int dataColumns = columnCount - 2;
        for (int dataIndex = 0; dataIndex < dataColumns; dataIndex++)
        {
            string value = dataIndex + 1 < parts.Length ? parts[dataIndex + 1] : string.Empty;
            bool isLastColumn = dataIndex == dataColumns - 1;

            if (isLastColumn && !string.IsNullOrEmpty(continuation))
            {
                value = AppendContinuation(value, continuation);
            }

            resultFields[dataIndex + 2] = new LogField(value);
        }

        return new LogRecord(resultFields);
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

    private static string[]? ParseHeader(ILogLineIndex fileIndex)
    {
        if (fileIndex.LineCount == 0)
        {
            return null;
        }

        string firstLine = fileIndex.GetLine(0);
        if (!firstLine.StartsWith($"{HeaderLineMarker}\t", StringComparison.Ordinal))
        {
            return null;
        }

        return firstLine[(HeaderLineMarker.Length + 1)..].Split('\t');
    }

    private static IReadOnlyList<LogColumnDefinition> BuildColumns(string[]? headerNames)
    {
        if (headerNames is null || headerNames.Length < 4)
        {
            return FallbackColumns;
        }

        var columns = new List<LogColumnDefinition>(headerNames.Length)
        {
            new("Date", 10, 10, 10, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateOnly),
            new("Time", 16, 16, 12, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.TimeOnly),
            new(EnsureNonEmpty(headerNames[2], DefaultSourceColumnName), 12, 24, 8)
        };

        for (int index = 3; index < headerNames.Length; index++)
        {
            string name = EnsureNonEmpty(headerNames[index], $"C{index - 2}");
            bool isLast = index == headerNames.Length - 1;
            if (isLast)
            {
                int width = Math.Max(24, name.Length);
                columns.Add(new(
                    name,
                    MinWidth: Math.Clamp(width, 24, 48),
                    MaxWidth: 4096,
                    MinAcceptableWidth: 12,
                    ExpandToFill: true,
                    WrapContent: true));
                continue;
            }

            int targetWidth = Math.Max(8, name.Length);
            columns.Add(new(
                name,
                MinWidth: Math.Clamp(targetWidth, 4, 24),
                MaxWidth: Math.Clamp(Math.Max(targetWidth, 16), 16, 80),
                MinAcceptableWidth: Math.Clamp(Math.Max(name.Length, Math.Min(targetWidth, 10)), 4, 16)));
        }

        return columns;
    }

    private static string EnsureNonEmpty(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
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

    private static LogField CreateTimestampField(string rawTimestamp, DateTimeOffset? timestamp, int fractionalDigits)
    {
        return timestamp is null
            ? new LogField(rawTimestamp)
            : new LogField(rawTimestamp, LogFieldType.Timestamp, timestamp, fractionalDigits);
    }

    private static DateTimeOffset? ParseTimestamp(string rawTimestamp)
    {
        if (DateTimeOffset.TryParse(
                rawTimestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            return parsed;
        }

        return null;
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
}
