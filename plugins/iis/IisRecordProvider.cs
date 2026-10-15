using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LogAce.PluginApi;
using System.Globalization;
using Alignment = LogAce.PluginApi.LogColumnAlignment;

internal sealed class IisRecordProvider : ILogRecordProvider, IRawLogRecordProvider
{
    private sealed record HeaderSection(int StartLine, int[] Columns);
    private readonly HeaderSection[] _headers;
    private readonly ILogLineIndex _fileIndex;
    private readonly int[] _dataLineIndices;
    private readonly string[] _fieldNames;
    private readonly int _dateColumnIndex;
    private readonly int _timeColumnIndex;

    public IisRecordProvider(ILogLineIndex fileIndex)
    {
        _fileIndex = fileIndex;
        (_fieldNames, _dataLineIndices, _headers) = Parse(fileIndex);
        _dateColumnIndex = Array.FindIndex(_fieldNames, name => string.Equals(name, "date", StringComparison.OrdinalIgnoreCase));
        _timeColumnIndex = Array.FindIndex(_fieldNames, name => string.Equals(name, "time", StringComparison.OrdinalIgnoreCase));
        Columns = BuildColumns(_fieldNames);
    }

    public IReadOnlyList<LogColumnDefinition> Columns { get; }

    public bool ExpandLastColumn => Columns.Count > 0 && Columns[^1].ExpandToFill;

    public int MaxCellWidth => 4096;

    public bool SupportsTimestampNavigation => _dateColumnIndex >= 0 || _timeColumnIndex >= 0;

    public int RecordCount => _dataLineIndices.Length;

    public LogRecord GetRecord(int recordIndex)
    {
        if (recordIndex < 0 || recordIndex >= _dataLineIndices.Length)
        {
            return new LogRecord([]);
        }

        string line = _fileIndex.GetLine(_dataLineIndices[recordIndex]);
        string[] cells = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (_fieldNames.Length == 0)
        {
            return new LogRecord([new LogField(line)]);
        }

        int low = 0, high = _headers.Length - 1;
        while (low < high)
        {
            int mid = low + (high - low + 1) / 2;
            if (_headers[mid].StartLine <= _dataLineIndices[recordIndex]) low = mid; else high = mid - 1;
        }
        int[] mapping = _headers[low].Columns;
        var alignedCells = new string[_fieldNames.Length];
        Array.Fill(alignedCells, string.Empty);
        for (int index = 0; index < Math.Min(cells.Length, mapping.Length); index++)
            alignedCells[mapping[index]] = cells[index];
        cells = alignedCells;

        var fields = new LogField[_fieldNames.Length];
        DateTimeOffset? timestamp = TryBuildTimestamp(cells);

        for (int index = 0; index < _fieldNames.Length; index++)
        {
            string rawValue = index < cells.Length ? cells[index] : string.Empty;
            string displayValue = rawValue == "-" ? string.Empty : rawValue;

            if (index == _dateColumnIndex && timestamp is not null)
            {
                fields[index] = new LogField(displayValue, LogFieldType.Timestamp, timestamp);
                continue;
            }

            if (index == _timeColumnIndex && timestamp is not null)
            {
                fields[index] = new LogField(displayValue, LogFieldType.Timestamp, timestamp);
                continue;
            }

            fields[index] = new LogField(displayValue);
        }

        return new LogRecord(fields);
    }

    public int GetRecordStartLine(int recordIndex)
    {
        if (_dataLineIndices.Length == 0)
        {
            return -1;
        }

        int clampedIndex = Math.Clamp(recordIndex, 0, _dataLineIndices.Length - 1);
        return _dataLineIndices[clampedIndex];
    }

    public string GetRawRecordText(int recordIndex) => _fileIndex.GetLine(_dataLineIndices[recordIndex]);

    public int FindNearestRecordIndexForLine(int lineIndex)
    {
        if (_dataLineIndices.Length == 0)
        {
            return -1;
        }

        int low = 0;
        int high = _dataLineIndices.Length - 1;

        if (lineIndex <= _dataLineIndices[low])
        {
            return 0;
        }

        if (lineIndex >= _dataLineIndices[high])
        {
            return high;
        }

        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            int candidate = _dataLineIndices[mid];

            if (candidate == lineIndex)
            {
                return mid;
            }

            if (candidate < lineIndex)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (low >= _dataLineIndices.Length)
        {
            return _dataLineIndices.Length - 1;
        }

        if (low <= 0)
        {
            return 0;
        }

        int lower = _dataLineIndices[low - 1];
        int upper = _dataLineIndices[low];
        return lineIndex - lower <= upper - lineIndex ? low - 1 : low;
    }

    private DateTimeOffset? TryBuildTimestamp(string[] cells)
    {
        if (_dateColumnIndex < 0 && _timeColumnIndex < 0)
        {
            return null;
        }

        string date = _dateColumnIndex >= 0 && _dateColumnIndex < cells.Length ? cells[_dateColumnIndex] : string.Empty;
        string time = _timeColumnIndex >= 0 && _timeColumnIndex < cells.Length ? cells[_timeColumnIndex] : string.Empty;

        if (date.Length == 0 && time.Length == 0)
        {
            return null;
        }

        string candidate = (date.Length > 0 && time.Length > 0)
            ? $"{date} {time}"
            : date.Length > 0 ? date : time;

        string[] formats =
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-dd",
            "HH:mm:ss"
        };

        foreach (string format in formats)
        {
            if (DateTime.TryParseExact(
                    candidate,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime parsed))
            {
                return new DateTimeOffset(parsed, TimeSpan.Zero);
            }
        }

        return null;
    }

    private static (string[] Fields, int[] DataLines, HeaderSection[] Headers) Parse(ILogLineIndex fileIndex)
    {
        var fields = new List<string>();
        var fieldIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var headers = new List<HeaderSection>();
        bool hasFieldHeader = false;
        var dataLines = new List<int>();
        var dataLinesBeforeHeader = new List<int>();

        ParserProgress.Report(
            "Reading IIS headers",
            0,
            fileIndex.LineCount,
            fileIndex.LineCount > 0 ? $"0 / {fileIndex.LineCount:N0} lines" : string.Empty);

        for (int index = 0; index < fileIndex.LineCount; index++)
        {
            string line = fileIndex.GetLine(index);
            if (index == 0) line = line.TrimStart('\ufeff');
            int completed = index + 1;

            if (line.Length == 0)
            {
                if ((completed & 2047) == 0 || completed == fileIndex.LineCount)
                {
                    ParserProgress.Report(
                        "Reading IIS headers",
                        completed,
                        fileIndex.LineCount,
                        $"{completed:N0} / {fileIndex.LineCount:N0} lines");
                }

                continue;
            }

            if (line[0] == '#')
            {
                if (line.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parsedFields = line[8..]
                        .Trim()
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    var mapping = new int[parsedFields.Length];
                    for (int field = 0; field < parsedFields.Length; field++)
                    {
                        string name = parsedFields[field];
                        if (!fieldIndices.TryGetValue(name, out int target))
                        {
                            target = fields.Count;
                            fields.Add(name);
                            fieldIndices.Add(name, target);
                        }
                        mapping[field] = target;
                    }
                    headers.Add(new HeaderSection(index, mapping));
                    hasFieldHeader = true;
                }

                if ((completed & 2047) == 0 || completed == fileIndex.LineCount)
                {
                    ParserProgress.Report(
                        "Reading IIS headers",
                        completed,
                        fileIndex.LineCount,
                        $"{completed:N0} / {fileIndex.LineCount:N0} lines");
                }

                continue;
            }

            if (hasFieldHeader)
            {
                dataLines.Add(index);
            }
            else
            {
                dataLinesBeforeHeader.Add(index);
            }

            if ((completed & 2047) == 0 || completed == fileIndex.LineCount)
            {
                ParserProgress.Report(
                    "Reading IIS headers",
                    completed,
                    fileIndex.LineCount,
                    $"{completed:N0} / {fileIndex.LineCount:N0} lines");
            }
        }

        return hasFieldHeader
            ? (fields.ToArray(), dataLines.ToArray(), headers.ToArray())
            : ([], dataLinesBeforeHeader.ToArray(), []);
    }

    private static IReadOnlyList<LogColumnDefinition> BuildColumns(string[] fieldNames)
    {
        if (fieldNames.Length == 0)
        {
            return
            [
                new("Raw", 24, 4096, 24, ExpandToFill: true, WrapContent: true)
            ];
        }

        var columns = new List<LogColumnDefinition>(fieldNames.Length);

        for (int index = 0; index < fieldNames.Length; index++)
        {
            string name = fieldNames[index];
            bool isLast = index == fieldNames.Length - 1;
            bool isDate = string.Equals(name, "date", StringComparison.OrdinalIgnoreCase);
            bool isTime = string.Equals(name, "time", StringComparison.OrdinalIgnoreCase);

            if (isDate)
            {
                columns.Add(new(
                    name,
                    MinWidth: 10,
                    MaxWidth: 10,
                    MinAcceptableWidth: 10,
                    Alignment.Start,
                    LogFieldType.Timestamp,
                    LogColumnDisplayMode.DateOnly));
                continue;
            }

            if (isTime)
            {
                columns.Add(new(
                    name,
                    MinWidth: 10,
                    MaxWidth: 12,
                    MinAcceptableWidth: 8,
                    Alignment.Start,
                    LogFieldType.Timestamp,
                    LogColumnDisplayMode.TimeOnly));
                continue;
            }

            if (isLast)
            {
                int width = Math.Max(16, name.Length);
                columns.Add(new(
                    name,
                    MinWidth: Math.Clamp(width, 16, 32),
                    MaxWidth: 4096,
                    MinAcceptableWidth: 8,
                    ExpandToFill: true,
                    WrapContent: true));
                continue;
            }

            int targetWidth = Math.Max(8, name.Length);
            columns.Add(new(
                name,
                MinWidth: Math.Clamp(targetWidth, 4, 24),
                MaxWidth: Math.Clamp(Math.Max(targetWidth, 16), 16, 64),
                MinAcceptableWidth: Math.Clamp(Math.Max(name.Length, Math.Min(targetWidth, 10)), 4, 16)));
        }

        return columns;
    }
}
