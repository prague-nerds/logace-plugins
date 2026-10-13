using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Buffers;

namespace LogAce.Plugins.Simctl;

/// <summary>Discovers simulator identity and boot state through xcrun.</summary>
internal static class SimctlDeviceCatalog
{
    private const string BootedSelector = "booted";

    public static bool TryListIosSimulators(
        out IReadOnlyList<SimctlDeviceListEntry> devices,
        out string? error)
    {
        devices = [];
        error = null;

        if (!OperatingSystem.IsMacOS())
        {
            error = "iOS simulator logs require macOS.";
            return false;
        }

        try
        {
            using Process process = StartSimctlListProcess();
            var (stdout, stderr) = SourceRuntime.ReadResult(process);

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? "Simulator listing failed."
                    : $"Simulator listing failed: {stderr}";
                return false;
            }

            devices = ParseDevices(stdout)
                .OrderByDescending(item => item.IsBooted)
                .ThenBy(item => item.RuntimeDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not list iOS simulators. Ensure 'xcrun simctl' is available. {ex.Message}";
            return false;
        }
    }

    public static bool TryResolveDevice(
        string selector,
        out SimctlDeviceListEntry? device,
        out string? error)
    {
        device = null;
        error = null;

        string normalizedSelector = selector.Trim();

        if (normalizedSelector.Length == 0)
        {
            error = "Simulator device selector cannot be empty.";
            return false;
        }

        if (!TryListIosSimulators(out IReadOnlyList<SimctlDeviceListEntry> devices, out error))
        {
            return false;
        }

        if (normalizedSelector.Equals(BootedSelector, StringComparison.OrdinalIgnoreCase))
        {
            SimctlDeviceListEntry[] bootedDevices = devices.Where(static item => item.IsBooted).ToArray();

            if (bootedDevices.Length == 0)
            {
                error = "No booted iOS simulator is available.";
                return false;
            }

            if (bootedDevices.Length > 1)
            {
                error = "Multiple iOS simulators are booted. Use a UDID to select one explicitly.";
                return false;
            }

            device = bootedDevices[0];
            return true;
        }

        device = devices.FirstOrDefault(item => string.Equals(item.Udid, normalizedSelector, StringComparison.OrdinalIgnoreCase));

        if (device is not null)
        {
            return true;
        }

        SimctlDeviceListEntry[] byName = devices
            .Where(item => string.Equals(item.Name, normalizedSelector, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (byName.Length == 1)
        {
            device = byName[0];
            return true;
        }

        if (byName.Length > 1)
        {
            error = $"Multiple simulators match '{selector}'. Use a UDID instead.";
            return false;
        }

        error = $"Could not find an iOS simulator matching '{selector}'.";
        return false;
    }

    private static Process StartSimctlListProcess()
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
        startInfo.ArgumentList.Add("list");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("devices");
        startInfo.ArgumentList.Add("available");

        Process? process = SourceRuntime.Start(startInfo);
        return process ?? throw new InvalidOperationException("xcrun simctl list did not start.");
    }

    private static IEnumerable<SimctlDeviceListEntry> ParseDevices(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("devices", out JsonElement devicesElement)
            || devicesElement.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (JsonProperty runtimeGroup in devicesElement.EnumerateObject())
        {
            if (!runtimeGroup.Name.StartsWith("com.apple.CoreSimulator.SimRuntime.iOS-", StringComparison.Ordinal))
            {
                continue;
            }

            string runtimeDisplayName = FormatRuntimeDisplayName(runtimeGroup.Name);

            if (runtimeGroup.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement deviceElement in runtimeGroup.Value.EnumerateArray())
            {
                if (!TryGetBoolean(deviceElement, "isAvailable"))
                {
                    continue;
                }

                string udid = GetString(deviceElement, "udid");
                string name = GetString(deviceElement, "name");
                string state = GetString(deviceElement, "state");

                if (string.IsNullOrWhiteSpace(udid) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                yield return new SimctlDeviceListEntry(
                    udid,
                    name,
                    runtimeDisplayName,
                    string.IsNullOrWhiteSpace(state) ? "Unknown" : state);
            }
        }
    }

    private static string FormatRuntimeDisplayName(string runtimeKey)
    {
        const string prefix = "com.apple.CoreSimulator.SimRuntime.";
        string raw = runtimeKey.StartsWith(prefix, StringComparison.Ordinal)
            ? runtimeKey[prefix.Length..]
            : runtimeKey;
        string[] parts = raw.Split('-', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length <= 1)
        {
            return raw;
        }

        return $"{parts[0]} {string.Join('.', parts.Skip(1))}";
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ToString();
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.True
            || (property.ValueKind == JsonValueKind.String
                && bool.TryParse(property.GetString(), out bool parsed)
                && parsed);
    }
}
