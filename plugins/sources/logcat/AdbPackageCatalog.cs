using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Buffers;

namespace LogAce.Plugins.Logcat;

/// <summary>Discovers installed packages and resolves changing process IDs.</summary>
internal static class AdbPackageCatalog
{
    public static bool TryListPackages(
        string serial,
        bool thirdPartyOnly,
        out IReadOnlyList<string> packages,
        out string? error)
    {
        packages = [];
        error = null;

        var arguments = new List<string> { "-s", serial, "shell", "pm", "list", "packages" };

        if (thirdPartyOnly)
        {
            arguments.Add("-3");
        }

        try
        {
            using Process process = AdbDeviceCatalog.StartAdbProcess(arguments);
            var (stdout, stderr) = SourceRuntime.ReadResult(process);

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? $"Package listing failed for device '{serial}'."
                    : $"Package listing failed for device '{serial}': {stderr}";
                return false;
            }

            packages = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .Where(static line => line.StartsWith("package:", StringComparison.Ordinal))
                .Select(static line => line["package:".Length..].Trim())
                .Where(static name => name.Length > 0)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not list packages for device '{serial}'. {ex.Message}";
            return false;
        }
    }

    public static bool TryResolvePid(string serial, string package, out int? processId, out string? error)
    {
        processId = null;
        error = null;

        try
        {
            using Process process = AdbDeviceCatalog.StartAdbProcess(["-s", serial, "shell", "pidof", "-s", package]);
            var (output, _) = SourceRuntime.ReadResult(process);
            string stdout = output.Trim();

            if (stdout.Length == 0)
            {
                return true;
            }

            string firstToken = stdout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

            if (int.TryParse(firstToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) && pid > 0)
            {
                processId = pid;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not resolve a PID for package '{package}'. {ex.Message}";
            return false;
        }
    }
}
