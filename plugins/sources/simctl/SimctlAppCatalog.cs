using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Buffers;

namespace LogAce.Plugins.Simctl;

/// <summary>Discovers installed applications without storing credentials.</summary>
internal static class SimctlAppCatalog
{
    public static bool TryListApps(
        string deviceUdid,
        out IReadOnlyList<SimctlAppListEntry> apps,
        out string? error)
    {
        apps = [];
        error = null;

        if (!OperatingSystem.IsMacOS())
        {
            error = "Simulator app browsing requires macOS.";
            return false;
        }

        try
        {
            using Process process = StartSimctlListAppsProcess(deviceUdid);
            var (stdout, stderr) = SourceRuntime.ReadResult(process);

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? $"App listing failed for simulator '{deviceUdid}'."
                    : $"App listing failed for simulator '{deviceUdid}': {stderr}";
                return false;
            }

            string json = ConvertPlistLikeOutputToJson(stdout, out error);

            if (error is not null)
            {
                return false;
            }

            apps = ParseApps(json)
                .OrderBy(item => item.ApplicationType.Equals("User", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.BundleId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not list apps for simulator '{deviceUdid}'. Ensure the simulator is booted. {ex.Message}";
            return false;
        }
    }

    public static bool TryGetApp(
        string deviceUdid,
        string bundleId,
        out SimctlAppListEntry? app,
        out string? error)
    {
        app = null;
        error = null;

        if (!TryListApps(deviceUdid, out IReadOnlyList<SimctlAppListEntry> apps, out error))
        {
            return false;
        }

        app = apps.FirstOrDefault(item => string.Equals(item.BundleId, bundleId, StringComparison.OrdinalIgnoreCase));

        if (app is null)
        {
            error = $"Could not find app '{bundleId}' on simulator '{deviceUdid}'.";
            return false;
        }

        return true;
    }

    private static Process StartSimctlListAppsProcess(string deviceUdid)
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
        startInfo.ArgumentList.Add("listapps");
        startInfo.ArgumentList.Add(deviceUdid);

        Process? process = SourceRuntime.Start(startInfo);
        return process ?? throw new InvalidOperationException("xcrun simctl listapps did not start.");
    }

    private static string ConvertPlistLikeOutputToJson(string plistText, out string? error)
    {
        error = null;
        string tempPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(tempPath, plistText, Encoding.UTF8);

            using Process process = StartPlutilConversionProcess(tempPath);
            var (stdout, stderr) = SourceRuntime.ReadResult(process);

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? "plutil could not convert simulator app metadata."
                    : $"plutil could not convert simulator app metadata: {stderr}";
                return string.Empty;
            }

            return stdout;
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    private static Process StartPlutilConversionProcess(string sourcePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/plutil",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-convert");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("-");
        startInfo.ArgumentList.Add(sourcePath);

        Process? process = SourceRuntime.Start(startInfo);
        return process ?? throw new InvalidOperationException("plutil did not start.");
    }

    private static IEnumerable<SimctlAppListEntry> ParseApps(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            JsonElement appElement = property.Value;
            string bundleId = property.Name;
            string executable = GetString(appElement, "CFBundleExecutable");
            string displayName = GetPreferredDisplayName(appElement, bundleId);
            string applicationType = GetString(appElement, "ApplicationType");

            yield return new SimctlAppListEntry(
                bundleId,
                displayName,
                string.IsNullOrWhiteSpace(executable) ? bundleId : executable,
                string.IsNullOrWhiteSpace(applicationType) ? "Unknown" : applicationType);
        }
    }

    private static string GetPreferredDisplayName(JsonElement appElement, string fallback)
    {
        string displayName = GetString(appElement, "CFBundleDisplayName");

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        string bundleName = GetString(appElement, "CFBundleName");
        return string.IsNullOrWhiteSpace(bundleName) ? fallback : bundleName;
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

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
