using System;

namespace LogAce.Plugins.Coolify;

/// <summary>Match the longest old suffix against the new prefix, without collapsing
/// legitimate duplicate lines. Linear time even for a large repetitive snapshot.</summary>
internal sealed class CoolifyPollCursor
{
    private string[] _previous = [];

    internal string[] Append(string[] snapshot, out bool gap)
    {
        gap = false;
        if (snapshot.Length == 0)
        {
            // Empty polls or a temporarily missing container must not erase the cursor.
            return [];
        }

        int[] prefix = new int[snapshot.Length];
        for (int i = 1, matched = 0; i < snapshot.Length; i++)
        {
            while (matched > 0 && snapshot[i] != snapshot[matched])
            {
                matched = prefix[matched - 1];
            }
            if (snapshot[i] == snapshot[matched])
            {
                matched++;
            }
            prefix[i] = matched;
        }

        int overlap = 0;
        foreach (string line in _previous)
        {
            while (overlap > 0 && (overlap == snapshot.Length || line != snapshot[overlap]))
            {
                overlap = prefix[overlap - 1];
            }
            if (line == snapshot[overlap])
            {
                overlap++;
            }
        }
        gap = _previous.Length > 0 && overlap == 0;
        _previous = snapshot;
        return snapshot[overlap..];
    }
}
