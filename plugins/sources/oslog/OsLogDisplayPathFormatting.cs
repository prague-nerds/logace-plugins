using System.Collections.Generic;

namespace LogAce.Plugins.OsLog;

/// <summary>Formats stable source identities for saved state and readable recent-history labels.</summary>
internal static class OsLogDisplayPathFormatting
{
    public static string GetShortLabel(string displayPath)
    {
        return OsLogSourceFactory.TryParseDisplayPath(displayPath, out OsLogSourceOptions? options, out _) && options is not null
            ? OsLogSourceFactory.BuildShortName(options)
            : "unified log";
    }

    public static string GetRecentHistoryLabel(string displayPath)
    {
        if (!OsLogSourceFactory.TryParseDisplayPath(displayPath, out OsLogSourceOptions? options, out _) || options is null)
        {
            return displayPath;
        }

        var parts = new List<string> { "macOS unified log" };

        if (!string.IsNullOrWhiteSpace(options.Process))
        {
            parts.Add(options.Process!);
        }

        if (!string.IsNullOrWhiteSpace(options.Subsystem))
        {
            parts.Add(options.Subsystem!);
        }

        parts.Add(options.OpenMode == OsLogOpenMode.Follow ? "follow" : $"last {options.Last}");
        return string.Join(" | ", parts);
    }
}
