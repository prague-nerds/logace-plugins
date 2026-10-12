using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LogAce.Plugins.Kubernetes;

/// <summary>
/// Replays available logs from the last timestamp after a CLI disconnect. Counts
/// of complete lines preserve legitimate repeated messages at the same instant.
/// This is a best-effort cursor: Kubernetes does not expose a durable log offset.
/// </summary>
internal sealed class KubernetesFollowCursor
{
    private static readonly Regex TimestampPattern = new(
        @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?Z\z", RegexOptions.CultureInvariant);
    private readonly string _baseline = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private Dictionary<string, int> _replay = new(StringComparer.Ordinal);
    private bool _initial = true;
    private string? _lastRaw;
    private string? _lastKey;
    private string? _replayKey;

    internal KubernetesOptions NextAttempt(KubernetesOptions options)
    {
        if (_initial)
        {
            _initial = false;
            return options;
        }
        _replayKey = _lastKey;
        _replay = new(_counts, StringComparer.Ordinal);
        // --tail 0 loses logs written during the outage. Remove the initial tail
        // cap, use the exact timestamp (including nanoseconds), and deduplicate
        // records at the boundary locally. Never send both since flags.
        return options with { Tail = "all", Since = null, SinceTime = _lastRaw ?? _baseline, Follow = true };
    }

    internal bool ShouldSkipReplay(string line)
    {
        string? timestamp = Timestamp(line);
        if (timestamp is null || _replayKey is null) return false;
        int comparison = string.CompareOrdinal(Key(timestamp), _replayKey);
        if (comparison < 0) return true;
        if (comparison != 0 || !_replay.TryGetValue(line, out int remaining) || remaining == 0) return false;
        _replay[line] = remaining - 1;
        // Keep the boundary for the entire attempt: all-container streams can
        // interleave, so a newer line does not prove the boundary has finished.
        return true;
    }

    internal void Record(string line)
    {
        string? timestamp = Timestamp(line);
        if (timestamp is null) return;
        string key = Key(timestamp);
        int comparison = _lastKey is null ? 1 : string.CompareOrdinal(key, _lastKey);
        if (comparison > 0)
        {
            _lastRaw = timestamp;
            _lastKey = key;
            _counts.Clear();
        }
        if (comparison >= 0) _counts[line] = _counts.GetValueOrDefault(line) + 1;
    }

    internal static string? Timestamp(string line)
    {
        int separator = line.IndexOf(' ');
        if (separator <= 0) return null;
        string value = line[..separator];
        return TimestampPattern.IsMatch(value) ? value : null;
    }

    private static string Key(string timestamp)
    {
        // RFC3339Nano omits trailing zeros. Pad only the comparison key; parsing
        // through DateTime would truncate Kubernetes' ninth fractional digit.
        int fraction = timestamp.IndexOf('.');
        return timestamp[..19] + "." + (fraction < 0 ? "" : timestamp[20..^1]).PadRight(9, '0') + "Z";
    }
}
