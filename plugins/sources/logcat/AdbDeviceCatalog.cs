using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Buffers;

namespace LogAce.Plugins.Logcat;

/// <summary>Discovers online devices; adb retains authentication ownership.</summary>
internal static class AdbDeviceCatalog
{
    public static bool TryListDevices(
        out IReadOnlyList<AdbDeviceListEntry> devices,
        out string? error)
    {
        devices = [];
        error = null;

        try
        {
            using Process process = StartAdbProcess(["devices", "-l"]);
            var (stdout, stderr) = SourceRuntime.ReadResult(process);

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? "Android device listing failed."
                    : $"Android device listing failed: {stderr}";
                return false;
            }

            devices = ParseDevices(stdout)
                .OrderByDescending(item => item.IsOnline)
                .ThenBy(item => item.Model ?? item.Serial, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not list Android devices. Ensure Android platform-tools are installed and 'adb' is on PATH. {ex.Message}";
            return false;
        }
    }

    public static bool TryResolveDevice(
        string selector,
        out AdbDeviceListEntry? device,
        out string? error)
    {
        device = null;
        error = null;

        string normalizedSelector = selector.Trim();

        if (normalizedSelector.Length == 0)
        {
            error = "Android device selector cannot be empty.";
            return false;
        }

        if (!TryListDevices(out IReadOnlyList<AdbDeviceListEntry> devices, out error))
        {
            return false;
        }

        AdbDeviceListEntry[] online = devices.Where(item => item.IsOnline).ToArray();

        if (normalizedSelector.Equals(LogcatOptionTokens.AnyDeviceSelector, StringComparison.OrdinalIgnoreCase))
        {
            if (online.Length == 0)
            {
                error = "No Android devices connected.";
                return false;
            }

            if (online.Length > 1)
            {
                error = "Multiple Android devices are connected. Select a device explicitly.";
                return false;
            }

            device = online[0];
            return true;
        }

        AdbDeviceListEntry? bySerial = devices.FirstOrDefault(
            item => string.Equals(item.Serial, normalizedSelector, StringComparison.OrdinalIgnoreCase));

        if (bySerial is not null)
        {
            return ValidateOnline(bySerial, out device, out error);
        }

        AdbDeviceListEntry[] byModel = devices
            .Where(item => string.Equals(item.Model, normalizedSelector, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (byModel.Length == 1)
        {
            return ValidateOnline(byModel[0], out device, out error);
        }

        if (byModel.Length > 1)
        {
            error = $"Multiple Android devices match '{selector}'. Use a serial instead.";
            return false;
        }

        error = $"Could not find an Android device matching '{selector}'.";
        return false;
    }

    private static bool ValidateOnline(AdbDeviceListEntry candidate, out AdbDeviceListEntry? device, out string? error)
    {
        device = null;
        error = null;

        if (candidate.IsOnline)
        {
            device = candidate;
            return true;
        }

        error = string.Equals(candidate.State, "unauthorized", StringComparison.OrdinalIgnoreCase)
            ? $"Device '{candidate.Serial}' is unauthorized. Accept the USB debugging prompt on the device."
            : $"Device '{candidate.Serial}' is {candidate.State}.";
        return false;
    }

    internal static IEnumerable<AdbDeviceListEntry> ParseDevices(string output)
    {
        foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();

            if (line.Length == 0
                || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("* daemon", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
            {
                continue;
            }

            string serial = parts[0];
            string state = parts[1];
            string? model = null;
            string? product = null;
            string? transportId = null;

            for (int index = 2; index < parts.Length; index++)
            {
                string token = parts[index];
                int separatorIndex = token.IndexOf(':');

                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = token[..separatorIndex];
                string value = token[(separatorIndex + 1)..];

                switch (key)
                {
                    case "model":
                        model = value;
                        break;
                    case "product":
                        product = value;
                        break;
                    case "transport_id":
                        transportId = value;
                        break;
                }
            }

            yield return new AdbDeviceListEntry(serial, state, model, product, transportId);
        }
    }

    internal static Process StartAdbProcess(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "adb",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = SourceRuntime.Start(startInfo);
        return process ?? throw new InvalidOperationException("adb process did not start.");
    }
}
