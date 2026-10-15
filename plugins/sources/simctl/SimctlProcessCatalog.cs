using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Buffers;

namespace LogAce.Plugins.Simctl;

/// <summary>Discovers active simulator processes for optional PID filtering.</summary>
internal static class SimctlProcessCatalog
{
    public static bool TryListProcesses(
        string deviceUdid,
        out IReadOnlyList<SimctlProcessListEntry> processes,
        out string? error)
    {
        processes = [];
        error = null;

        if (!OperatingSystem.IsMacOS())
        {
            error = "Simulator process browsing requires macOS.";
            return false;
        }

        try
        {
            Dictionary<string, SimctlAppListEntry> appsByBundleId = TryBuildAppLookup(deviceUdid);

            using Process process = StartProcessListProcess(deviceUdid);
            var (stdout, stderr) = SourceRuntime.ReadResult(process);

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? $"Process listing failed for simulator '{deviceUdid}'."
                    : $"Process listing failed for simulator '{deviceUdid}': {stderr}";
                return false;
            }

            processes = ParseProcesses(stdout, appsByBundleId)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Pid)
                .ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not list processes for simulator '{deviceUdid}'. Ensure the simulator is booted. {ex.Message}";
            return false;
        }
    }

    private static Process StartProcessListProcess(string deviceUdid)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "xcrun",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("simctl");
        startInfo.ArgumentList.Add("spawn");
        startInfo.ArgumentList.Add(deviceUdid);
        startInfo.ArgumentList.Add("launchctl");
        startInfo.ArgumentList.Add("list");

        Process? process = SourceRuntime.Start(startInfo);
        return process ?? throw new InvalidOperationException("xcrun simctl spawn launchctl list did not start.");
    }

    private static IEnumerable<SimctlProcessListEntry> ParseProcesses(
        string output,
        IReadOnlyDictionary<string, SimctlAppListEntry> appsByBundleId)
    {
        foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith("PID\tStatus\tLabel", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split('\t');

            if (parts.Length < 3)
            {
                continue;
            }

            string pidText = parts[0].Trim();
            string label = parts[2].Trim();

            if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
                || pid <= 0
                || label.Length == 0)
            {
                continue;
            }

            string processName = BuildProcessDisplayName(label, appsByBundleId);

            yield return new SimctlProcessListEntry(pid, processName, label);
        }
    }

    private static Dictionary<string, SimctlAppListEntry> TryBuildAppLookup(string deviceUdid)
    {
        if (!SimctlAppCatalog.TryListApps(deviceUdid, out IReadOnlyList<SimctlAppListEntry> apps, out _))
        {
            return [];
        }

        return apps.ToDictionary(item => item.BundleId, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildProcessDisplayName(
        string label,
        IReadOnlyDictionary<string, SimctlAppListEntry> appsByBundleId)
    {
        const string uiKitPrefix = "UIKitApplication:";

        if (label.StartsWith(uiKitPrefix, StringComparison.Ordinal))
        {
            string bundleId = ExtractBundleIdFromUIKitLabel(label);

            if (bundleId.Length > 0 && appsByBundleId.TryGetValue(bundleId, out SimctlAppListEntry? app))
            {
                return app.DisplayName;
            }

            return bundleId.Length > 0 ? bundleId : label;
        }

        int lastDot = label.LastIndexOf('.');

        if (lastDot >= 0 && lastDot + 1 < label.Length)
        {
            return label[(lastDot + 1)..];
        }

        return label;
    }

    private static string ExtractBundleIdFromUIKitLabel(string label)
    {
        const string uiKitPrefix = "UIKitApplication:";

        if (!label.StartsWith(uiKitPrefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        int startIndex = uiKitPrefix.Length;
        int endIndex = label.IndexOf('[', startIndex);

        if (endIndex < 0)
        {
            endIndex = label.Length;
        }

        return label[startIndex..endIndex].Trim();
    }
}
