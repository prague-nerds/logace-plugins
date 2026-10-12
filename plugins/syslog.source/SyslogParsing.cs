using System;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LogAce.Plugins.Syslog;

/// <summary>Pure message normalization; the host retains its ordinary file parser.</summary>
internal static class SyslogParsing
{
    public const string Rfc3164TimestampPattern = @"[A-Z][a-z]{2}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}";
    public const string Rfc5424TimestampPattern =
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2})";

    private static readonly string[] FacilityNames =
    [
        "Kernel",
        "User",
        "Mail",
        "Daemon",
        "Auth",
        "Syslog",
        "LPR",
        "News",
        "UUCP",
        "Cron",
        "AuthPriv",
        "FTP",
        "NTP",
        "Audit",
        "Alert",
        "Clock",
        "Local0",
        "Local1",
        "Local2",
        "Local3",
        "Local4",
        "Local5",
        "Local6",
        "Local7"
    ];

    private static readonly string[] SeverityNames =
    [
        "Emergency",
        "Alert",
        "Critical",
        "Error",
        "Warning",
        "Notice",
        "Info",
        "Debug"
    ];

    public static readonly Regex RecordStartRegex = new(
        $"^(?:<\\d{{1,3}}>)?(?:{Rfc3164TimestampPattern}\\s|\\d{{1,3}}\\s+(?:-|{Rfc5424TimestampPattern})\\s)",
        RegexOptions.Compiled);

    private static readonly Regex Rfc3164Regex = new(
        $"^(?:<(?<priority>\\d{{1,3}})>)?(?<time>{Rfc3164TimestampPattern})\\s+(?<host>\\S+)\\s+(?<app>[^\\[:\\s]+)(?:\\[(?<procid>[^\\]]+)\\])?:?\\s*(?<message>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex Rfc5424HeaderRegex = new(
        $"^(?:<(?<priority>\\d{{1,3}})>)?(?<version>\\d{{1,3}})\\s+(?<time>-|{Rfc5424TimestampPattern})\\s+(?<host>\\S+)\\s+(?<app>\\S+)\\s+(?<procid>\\S+)\\s+(?<msgid>\\S+)\\s+(?<tail>.*)$",
        RegexOptions.Compiled);

    public static SyslogParsedMessage? TryParse(string firstLine, string continuation)
    {
        return TryParseRfc5424(firstLine, continuation)
            ?? TryParseRfc3164(firstLine, continuation);
    }

    public static LogField CreateTimestampField(string rawTime, DateTimeOffset? timestamp, int fractionalDigits = 0)
    {
        return timestamp is null
            ? new LogField(rawTime)
            : new LogField(rawTime, LogFieldType.Timestamp, timestamp, fractionalDigits);
    }

    public static bool TryParseEventTimestamp(
        string format,
        string rawTimestamp,
        out DateTimeOffset? timestamp,
        out int fractionalDigits)
    {
        timestamp = null;
        fractionalDigits = 0;

        if (string.IsNullOrWhiteSpace(rawTimestamp))
        {
            return false;
        }

        if (string.Equals(format, "RFC5424", StringComparison.Ordinal))
        {
            timestamp = ParseRfc5424Timestamp(rawTimestamp);
            fractionalDigits = GetFractionalSecondDigits(rawTimestamp);
            return timestamp is not null;
        }

        if (string.Equals(format, "RFC3164", StringComparison.Ordinal))
        {
            timestamp = ParseRfc3164Timestamp(rawTimestamp);
            return timestamp is not null;
        }

        timestamp = ParseRfc3164Timestamp(rawTimestamp);

        if (timestamp is not null)
        {
            return true;
        }

        timestamp = ParseRfc5424Timestamp(rawTimestamp);
        fractionalDigits = timestamp is null ? 0 : GetFractionalSecondDigits(rawTimestamp);
        return timestamp is not null;
    }

    public static DateTimeOffset? ParseRfc3164Timestamp(string value)
    {
        string normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        string candidate = $"{normalized} {DateTime.Now.Year}";

        if (DateTime.TryParseExact(
                candidate,
                "MMM d HH:mm:ss yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime parsed))
        {
            return new DateTimeOffset(parsed);
        }

        return null;
    }

    public static DateTimeOffset? ParseRfc5424Timestamp(string rawTimestamp)
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

    public static int GetFractionalSecondDigits(string rawTimestamp)
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

    public static string AppendContinuation(string message, string continuation)
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

    public static string SanitizeInlineField(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace('\t', ' ')
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static SyslogParsedMessage? TryParseRfc3164(string firstLine, string continuation)
    {
        Match match = Rfc3164Regex.Match(firstLine);

        if (!match.Success)
        {
            return null;
        }

        string priority = match.Groups["priority"].Success ? match.Groups["priority"].Value : string.Empty;
        ResolvePriorityLabels(priority, out string facility, out string severity);
        string rawTime = match.Groups["time"].Value;

        return new SyslogParsedMessage(
            Format: "RFC3164",
            Priority: priority,
            RawTime: rawTime,
            Timestamp: ParseRfc3164Timestamp(rawTime),
            TimestampFractionalDigits: 0,
            Host: match.Groups["host"].Value,
            App: match.Groups["app"].Value,
            ProcId: match.Groups["procid"].Success ? match.Groups["procid"].Value : string.Empty,
            MsgId: string.Empty,
            StructuredData: string.Empty,
            Facility: facility,
            Severity: severity,
            Message: AppendContinuation(match.Groups["message"].Value, continuation));
    }

    private static SyslogParsedMessage? TryParseRfc5424(string firstLine, string continuation)
    {
        Match match = Rfc5424HeaderRegex.Match(firstLine);

        if (!match.Success)
        {
            return null;
        }

        string priority = match.Groups["priority"].Success ? match.Groups["priority"].Value : string.Empty;
        ResolvePriorityLabels(priority, out string facility, out string severity);

        string rawTime = NormalizeNilValue(match.Groups["time"].Value);
        DateTimeOffset? timestamp = rawTime.Length == 0 ? null : ParseRfc5424Timestamp(rawTime);
        int fractionalDigits = rawTime.Length == 0 ? 0 : GetFractionalSecondDigits(rawTime);
        string tail = match.Groups["tail"].Value;
        ParseStructuredDataTail(tail, out string structuredData, out string message);

        return new SyslogParsedMessage(
            Format: "RFC5424",
            Priority: priority,
            RawTime: rawTime,
            Timestamp: timestamp,
            TimestampFractionalDigits: fractionalDigits,
            Host: NormalizeNilValue(match.Groups["host"].Value),
            App: NormalizeNilValue(match.Groups["app"].Value),
            ProcId: NormalizeNilValue(match.Groups["procid"].Value),
            MsgId: NormalizeNilValue(match.Groups["msgid"].Value),
            StructuredData: structuredData,
            Facility: facility,
            Severity: severity,
            Message: AppendContinuation(message, continuation));
    }

    private static void ParseStructuredDataTail(string tail, out string structuredData, out string message)
    {
        structuredData = string.Empty;
        message = string.Empty;

        if (tail.Length == 0)
        {
            return;
        }

        if (tail[0] == '-')
        {
            message = tail.Length > 2 && tail[1] == ' '
                ? tail[2..]
                : string.Empty;
            return;
        }

        if (tail[0] != '[')
        {
            message = tail;
            return;
        }

        int index = 0;

        while (index < tail.Length && tail[index] == '[')
        {
            int closingIndex = FindStructuredDataBlockEnd(tail, index);

            if (closingIndex < 0)
            {
                structuredData = string.Empty;
                message = tail;
                return;
            }

            index = closingIndex + 1;
        }

        structuredData = tail[..index];
        message = index < tail.Length && tail[index] == ' '
            ? tail[(index + 1)..]
            : index < tail.Length
                ? tail[index..]
                : string.Empty;
    }

    private static int FindStructuredDataBlockEnd(string value, int startIndex)
    {
        bool escaped = false;

        for (int index = startIndex + 1; index < value.Length; index++)
        {
            char current = value[index];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                continue;
            }

            if (current == ']')
            {
                return index;
            }
        }

        return -1;
    }

    private static string NormalizeNilValue(string value)
    {
        return string.Equals(value, "-", StringComparison.Ordinal)
            ? string.Empty
            : value;
    }

    private static void ResolvePriorityLabels(string priorityValue, out string facility, out string severity)
    {
        facility = string.Empty;
        severity = string.Empty;

        if (!int.TryParse(priorityValue, out int priority) || priority is < 0 or > 191)
        {
            return;
        }

        int facilityIndex = priority / 8;
        int severityIndex = priority % 8;

        facility = facilityIndex >= 0 && facilityIndex < FacilityNames.Length
            ? FacilityNames[facilityIndex]
            : string.Empty;
        severity = severityIndex >= 0 && severityIndex < SeverityNames.Length
            ? SeverityNames[severityIndex]
            : string.Empty;
    }
}
