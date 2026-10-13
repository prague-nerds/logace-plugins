using System;
using System.Collections.Generic;

namespace LogAce.Plugins.Journalctl;

/// <summary>Provider-specific helpers shared by discovery, capture and identity handling within this package.</summary>
internal static class JournalctlSinceArgumentBuilder
{
    public static void Append(ICollection<string> arguments, string? value)
    {
        string? normalized = NormalizeText(value);

        if (string.IsNullOrWhiteSpace(normalized)
            || string.Equals(normalized, "all", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(normalized, "boot", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-b");
            return;
        }

        arguments.Add("--since");
        arguments.Add(NormalizeSinceValue(normalized));
    }

    public static string NormalizeSinceValue(string value)
    {
        string normalized = NormalizeText(value) ?? string.Empty;

        if (TryConvertCompactRelativeValue(normalized, out string? converted))
        {
            return converted!;
        }

        return normalized;
    }

    private static bool TryConvertCompactRelativeValue(string value, out string? converted)
    {
        converted = null;

        if (value.Length < 2)
        {
            return false;
        }

        string magnitudePart = value[..^1];
        char unit = char.ToLowerInvariant(value[^1]);

        if (!int.TryParse(magnitudePart, out int magnitude) || magnitude < 0)
        {
            return false;
        }

        string? noun = unit switch
        {
            's' => magnitude == 1 ? "second" : "seconds",
            'm' => magnitude == 1 ? "minute" : "minutes",
            'h' => magnitude == 1 ? "hour" : "hours",
            'd' => magnitude == 1 ? "day" : "days",
            'w' => magnitude == 1 ? "week" : "weeks",
            _ => null
        };

        if (noun is null)
        {
            return false;
        }

        converted = magnitude == 0
            ? "now"
            : $"{magnitude} {noun} ago";
        return true;
    }

    private static string? NormalizeText(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }
}
