using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LogAce.Plugins.CloudWatch;

/// <summary>
/// Best-effort replay across AWS CLI disconnects. AWS does not guarantee timestamp
/// ordering, so retain a bounded overlap instead of discarding all older events.
/// No durable event ID is exposed by `logs tail`; this cannot promise exactly once.
/// </summary>
internal sealed class CloudWatchFollowCursor
{
    private static readonly Regex TimestampPattern = new(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant);
    private readonly SortedDictionary<DateTimeOffset, Dictionary<string, int>> _seen = new();
    private Dictionary<string, int> _replay = new(StringComparer.Ordinal);
    private bool _initial = true;
    private bool _skipContinuation;
    private DateTimeOffset? _latest;
    private int _keys;

    internal CloudWatchOptions NextAttempt(CloudWatchOptions options)
    {
        _skipContinuation = false;
        if (_initial) { _initial = false; return options; }
        _replay = new(StringComparer.Ordinal);
        foreach (var bucket in _seen.Values)
            foreach (var pair in bucket) _replay[pair.Key] = pair.Value;
        // If no timestamp has arrived yet, retry the original range. Advancing to
        // local wall clock could lose delayed events from an otherwise quiet group.
        return _seen.Count == 0 ? options : options with
        { Since = _seen.First().Key.AddMilliseconds(-1).ToString("O", CultureInfo.InvariantCulture) };
    }

    internal bool ShouldSkipReplay(string line)
    {
        if (Timestamp(line) is null) return _skipContinuation;
        _skipContinuation = _replay.TryGetValue(line, out int remaining) && remaining > 0;
        if (_skipContinuation) _replay[line] = remaining - 1;
        return _skipContinuation;
    }

    internal void Record(string line)
    {
        if (Timestamp(line) is not { } timestamp) return;
        _latest = _latest is null || timestamp > _latest ? timestamp : _latest;
        if (!_seen.TryGetValue(timestamp, out var bucket)) _seen[timestamp] = bucket = new(StringComparer.Ordinal);
        if (!bucket.ContainsKey(line)) _keys++;
        bucket[line] = bucket.GetValueOrDefault(line) + 1;
        // Bound long-running memory use. Out-of-order events older than two
        // minutes are written but may replay on reconnect; do not silently drop them.
        while (_seen.Count > 0 && (_seen.First().Key < _latest.Value.AddMinutes(-2) || _keys > 20000))
        { var first = _seen.First(); _keys -= first.Value.Count; _seen.Remove(first.Key); }
    }

    internal static DateTimeOffset? Timestamp(string line)
    {
        int separator = line.IndexOf(' ');
        if (separator <= 0) return null;
        string value = line[..separator];
        return TimestampPattern.IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
            ? timestamp : null;
    }
}
