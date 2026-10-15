using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using System.Text.RegularExpressions;
using LogAce.PluginApi;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class DockerRecordProvider : ILogRecordProvider
{
    private readonly ILogLineIndex _fileIndex;

    public DockerRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; } =
    [
        new("Docker Time", 26, 40, 20, Alignment.Start, LogFieldType.Timestamp, LogColumnDisplayMode.DateTime),
        new("Message", 24, 4096, 24, ExpandToFill: true, WrapContent: true)
    ];

    public bool ExpandLastColumn => true;

    public int MaxCellWidth => 4096;

    public bool SupportsTimestampNavigation => true;

    public int RecordCount => _fileIndex.LineCount;

    public LogRecord GetRecord(int recordIndex)
    {
        string line = _fileIndex.GetLine(recordIndex);
        int separatorIndex = line.IndexOf(' ');
        if (separatorIndex <= 0)
        {
            return new LogRecord([new LogField(string.Empty), new LogField(line)]);
        }

        string rawTimestamp = line[..separatorIndex];
        string message = separatorIndex + 1 < line.Length ? line[(separatorIndex + 1)..] : string.Empty;
        if (!TryParseDockerTimestamp(rawTimestamp, out DateTimeOffset timestamp, out int fractionalDigits))
        {
            return new LogRecord([new LogField(string.Empty), new LogField(line)]);
        }

        return new LogRecord(
        [
            new LogField(rawTimestamp, LogFieldType.Timestamp, timestamp, fractionalDigits),
            new LogField(message)
        ]);
    }

    public int GetRecordStartLine(int recordIndex)
    {
        return Math.Clamp(recordIndex, 0, Math.Max(0, RecordCount - 1));
    }

    public int FindNearestRecordIndexForLine(int lineIndex)
    {
        if (RecordCount == 0)
        {
            return -1;
        }

        return Math.Clamp(lineIndex, 0, RecordCount - 1);
    }

    private static bool TryParseDockerTimestamp(string rawTimestamp, out DateTimeOffset timestamp, out int fractionalDigits)
    {
        timestamp = default;
        fractionalDigits = 0;

        string trimmed = rawTimestamp.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out timestamp))
        {
            return false;
        }

        fractionalDigits = GetFractionalSecondDigits(trimmed);
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
}
