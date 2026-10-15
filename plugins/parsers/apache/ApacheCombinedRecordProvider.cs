using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.RegularExpressions;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class ApacheCombinedRecordProvider : ILogRecordProvider
{
    private const string ApacheTimePattern = @"\d{2}/[A-Za-z]{3}/\d{4}:\d{2}:\d{2}:\d{2}(?:\s[+-]\d{4})?";

    private static readonly Regex StartRegex = new(
        @"^\S+\s+\S+\s+\S+\s+\[",
        RegexOptions.Compiled);

    private static readonly Regex CombinedRegex = new(
        $"^(?<ip>\\S+)\\s+(?<ident>\\S+)\\s+(?<user>\\S+)\\s+\\[(?<time>{ApacheTimePattern})\\]\\s+\"(?<request>[^\"]*)\"\\s+(?<status>\\d{{3}})\\s+(?<size>\\S+)(?:\\s+\"(?<referrer>[^\"]*)\"\\s+\"(?<useragent>[^\"]*)\")?",
        RegexOptions.Compiled);

    private readonly ILogLineIndex _fileIndex;
    private readonly RecordSpan[] _recordSpans;

    public ApacheCombinedRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
        _recordSpans = RecordSpanUtility.BuildRecordSpans(fileIndex, StartRegex);
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; } =
    [
        new("Time", 21, 26, 18, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateTime),
        new("IP", 11, 18, 8),
        new("User", 8, 16, 4),
        new("Status", 6, 6, 3, Alignment.End),
        new("Size", 8, 12, 4, Alignment.End),
        new("Request", 24, 60, 16, WrapContent: true),
        new("Referrer", 12, 32, 8),
        new("UserAgent", 16, 4096, 12, ExpandToFill: true, WrapContent: true)
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

        Match match = CombinedRegex.Match(firstLine);
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
                new LogField(string.Empty),
                new LogField(RecordSpanUtility.ReadRawRecord(_fileIndex, span))
            ]);
        }

        string rawTime = match.Groups["time"].Value;
        DateTimeOffset? timestamp = ParseApacheTimestamp(rawTime);
        string userAgentRaw = match.Groups["useragent"].Success ? match.Groups["useragent"].Value : string.Empty;
        string userAgent = AppendContinuation(userAgentRaw, continuation);

        return new LogRecord(
        [
            CreateTimeField(rawTime, timestamp),
            new LogField(match.Groups["ip"].Value),
            new LogField(NormalizeDash(match.Groups["user"].Value)),
            new LogField(match.Groups["status"].Value),
            new LogField(NormalizeDash(match.Groups["size"].Value)),
            new LogField(match.Groups["request"].Value),
            new LogField(NormalizeDash(match.Groups["referrer"].Success ? match.Groups["referrer"].Value : string.Empty)),
            new LogField(userAgent)
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

    private static string NormalizeDash(string value)
    {
        return value == "-" ? string.Empty : value;
    }

    private static LogField CreateTimeField(string rawTime, DateTimeOffset? timestamp)
    {
        return timestamp is null
            ? new LogField(rawTime)
            : new LogField(rawTime, LogFieldType.Timestamp, timestamp);
    }

    private static DateTimeOffset? ParseApacheTimestamp(string value)
    {
        string trimmed = value.Trim();
        string[] formats =
        {
            "dd/MMM/yyyy:HH:mm:ss zzz",
            "dd/MMM/yyyy:HH:mm:ss"
        };

        foreach (string format in formats)
        {
            if (DateTimeOffset.TryParseExact(
                    trimmed,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out DateTimeOffset parsed))
            {
                return parsed;
            }
        }

        if (DateTime.TryParseExact(
                trimmed,
                "dd/MMM/yyyy:HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out DateTime fallback))
        {
            return new DateTimeOffset(fallback);
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
