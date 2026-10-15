using System.Collections.Generic;
using System.Linq;

namespace LogAce.Plugins.Logcat;

/// <summary>Formats stable source identities for saved state and readable recent-history labels.</summary>
internal static class LogcatDisplayPathFormatting
{
    public static string GetShortLabel(string displayPath)
    {
        return LogcatSourceFactory.TryParseDisplayPath(displayPath, out LogcatSourceOptions? options, out _) && options is not null
            ? LogcatSourceFactory.BuildShortName(options)
            : "logcat";
    }

    public static string GetRecentHistoryLabel(string displayPath)
    {
        if (!LogcatSourceFactory.TryParseDisplayPath(displayPath, out LogcatSourceOptions? options, out _) || options is null)
        {
            return displayPath;
        }

        var parts = new List<string>
        {
            options.DeviceModel ?? options.DeviceSerial ?? options.DeviceSelector
        };

        parts.Add(string.Join(",", options.Buffers.Select(LogcatOptionTokens.ToBufferArgument)));

        if (!string.IsNullOrWhiteSpace(options.Package))
        {
            parts.Add(options.Package!);
        }
        else if (options.ProcessId is int pid)
        {
            parts.Add($"pid:{pid}");
        }

        parts.Add(options.OpenMode == LogcatOpenMode.Follow ? "follow" : "snapshot");
        return string.Join(" | ", parts);
    }
}
