using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.RegularExpressions;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class JournalctlRecordProvider : ILogRecordProvider
{
    private const string IsoTimestampPattern =
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:?\d{2})";

    private static readonly Regex StartRegex = new(
        $"^(?:{IsoTimestampPattern}|{SyslogParsing.Rfc3164TimestampPattern})\\s+",
        RegexOptions.Compiled);

    private static readonly Regex StructuredIsoRegex = new(
        $"^(?<timestamp>{IsoTimestampPattern})\\s+(?<host>\\S+)\\s+(?<source>.+?):\\s?(?<message>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex StructuredShortRegex = new(
        $"^(?<timestamp>{SyslogParsing.Rfc3164TimestampPattern})\\s+(?<host>\\S+)\\s+(?<source>.+?):\\s?(?<message>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex SourceRegex = new(
        @"^(?<identifier>.+?)(?:\[(?<pid>\d+)\])?$",
        RegexOptions.Compiled);

    private readonly ILogLineIndex _fileIndex;
    private readonly RecordSpan[] _recordSpans;

    public JournalctlRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
        _recordSpans = RecordSpanUtility.BuildRecordSpans(fileIndex, StartRegex);
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; } =
    [
        new("Date", 10, 10, 10, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateOnly),
        new("Time", 15, 18, 8, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.TimeOnly),
        new("Host", 12, 32, 8),
        new("Source", 12, 48, 8),
        new("PID", 7, 12, 4, Alignment.End),
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

        if (!TryParse(firstLine, continuation, out ParsedJournalctlRecord? parsed)
            || parsed is null)
        {
            return new LogRecord(
            [
                new LogField(string.Empty),
                new LogField(string.Empty),
                new LogField(string.Empty),
                new LogField(string.Empty),
                new LogField(string.Empty),
                new LogField(RecordSpanUtility.ReadRawRecord(_fileIndex, span))
            ]);
        }

        return new LogRecord(
        [
            CreateDateField(parsed.RawTimestamp, parsed.Timestamp),
            CreateTimeField(parsed.RawTimestamp, parsed.Timestamp, parsed.FractionalDigits),
            new LogField(parsed.Host),
            new LogField(parsed.Source),
            new LogField(parsed.Pid),
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

    public static bool LooksLikeJournalctl(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string trimmed = line.Trim();
        return StructuredIsoRegex.IsMatch(trimmed) || StructuredShortRegex.IsMatch(trimmed);
    }

    private static LogField CreateDateField(string rawTimestamp, DateTimeOffset? timestamp)
    {
        return timestamp is null
            ? new LogField(rawTimestamp)
            : new LogField(rawTimestamp, LogFieldType.Timestamp, timestamp);
    }

    private static LogField CreateTimeField(string rawTimestamp, DateTimeOffset? timestamp, int fractionalDigits)
    {
        return timestamp is null
            ? new LogField(rawTimestamp)
            : new LogField(rawTimestamp, LogFieldType.Timestamp, timestamp, fractionalDigits);
    }

    private static bool TryParse(
        string firstLine,
        string continuation,
        out ParsedJournalctlRecord? parsed)
    {
        parsed = TryParseIso(firstLine, continuation) ?? TryParseShort(firstLine, continuation);
        return parsed is not null;
    }

    private static ParsedJournalctlRecord? TryParseIso(string firstLine, string continuation)
    {
        Match match = StructuredIsoRegex.Match(firstLine);
        if (!match.Success)
        {
            return null;
        }

        DateTimeOffset? timestamp = TryParseIsoTimestamp(
            match.Groups["timestamp"].Value,
            out DateTimeOffset parsedTimestamp,
            out int fractionalDigits)
            ? parsedTimestamp
            : null;

        return CreateParsedRecord(match, continuation, timestamp, fractionalDigits);
    }

    private static ParsedJournalctlRecord? TryParseShort(string firstLine, string continuation)
    {
        Match match = StructuredShortRegex.Match(firstLine);
        if (!match.Success)
        {
            return null;
        }

        string rawTimestamp = match.Groups["timestamp"].Value;
        DateTimeOffset? timestamp = SyslogParsing.ParseRfc3164Timestamp(rawTimestamp);
        return CreateParsedRecord(match, continuation, timestamp, fractionalDigits: 0);
    }

    private static ParsedJournalctlRecord CreateParsedRecord(
        Match match,
        string continuation,
        DateTimeOffset? timestamp,
        int fractionalDigits)
    {
        string sourceText = match.Groups["source"].Value;
        string source = sourceText;
        string pid = string.Empty;
        Match sourceMatch = SourceRegex.Match(sourceText);
        if (sourceMatch.Success)
        {
            source = sourceMatch.Groups["identifier"].Value;
            pid = sourceMatch.Groups["pid"].Value;
        }

        return new ParsedJournalctlRecord(
            match.Groups["timestamp"].Value,
            timestamp,
            fractionalDigits,
            match.Groups["host"].Value,
            source,
            pid,
            AppendContinuation(match.Groups["message"].Value, continuation));
    }

    private static bool TryParseIsoTimestamp(string rawTimestamp, out DateTimeOffset timestamp, out int fractionalDigits)
    {
        timestamp = default;
        fractionalDigits = 0;

        if (!DateTimeOffset.TryParse(rawTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out timestamp))
        {
            return false;
        }

        fractionalDigits = GetFractionalSecondDigits(rawTimestamp);
        return true;
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

    private sealed record ParsedJournalctlRecord(
        string RawTimestamp,
        DateTimeOffset? Timestamp,
        int FractionalDigits,
        string Host,
        string Source,
        string Pid,
        string Message);
}
