using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Buffers;
using LogAce.PluginApi;

namespace LogAce.Plugins.Simctl;

/// <summary>Creates provider captures and canonical identities; the public plugin owns validation and operation scope.</summary>
internal static class SimctlLogSourceFactory
{
    private const string JsonLinesProfileId = "jsonl";

    public static string GetDisplayPath(SimctlLogSourceOptions options)
    {
        SimctlLogSourceOptions normalized = NormalizeOptions(options, out _, allowCatalogErrors: false);
        return BuildDisplayPath(normalized);
    }

    public static bool TryCreateCore(
        SimctlLogSourceOptions options,
        out LogSourceHandle? source,
        out string? error)
    {
        source = null;
        error = null;

        if (!OperatingSystem.IsMacOS())
        {
            error = "iOS simulator logs require macOS.";
            return false;
        }

        SimctlLogSourceOptions normalizedOptions = NormalizeOptions(options, out error, allowCatalogErrors: true);

        if (error is not null)
        {
            return false;
        }

        if (!SimctlDeviceCatalog.TryResolveDevice(
                normalizedOptions.DeviceUdid ?? normalizedOptions.DeviceSelector,
                out SimctlDeviceListEntry? resolvedDevice,
                out error))
        {
            return false;
        }

        if (resolvedDevice is null || !resolvedDevice.IsBooted)
        {
            error = $"Simulator '{normalizedOptions.DeviceName ?? normalizedOptions.DeviceSelector}' must be booted before importing logs.";
            return false;
        }

        string spoolPath = BuildSpoolPath(normalizedOptions);
        string displayPath = BuildDisplayPath(normalizedOptions);

        try
        {
            if (!TryCaptureSnapshot(normalizedOptions, spoolPath, out error))
            {
                TryDeleteFile(spoolPath);
                return false;
            }

            source = new LogSourceHandle(
                spoolPath,
                displayPath,
                BuildShortName(normalizedOptions),

                preferredProfileId: JsonLinesProfileId,
                supportsRecentHistory: false,
                supportsDuplicate: false,
                autoReloadOnExternalChange: false,
                isFileSystemSource: false,
                lifetime: new TemporaryFileLifetime(spoolPath));
            return true;
        }
        catch (Exception ex)
        {
            TryDeleteFile(spoolPath);
            error = $"Could not open simulator logs for '{displayPath}': {ex.Message}";
            return false;
        }
    }

    private static SimctlLogSourceOptions NormalizeOptions(
        SimctlLogSourceOptions options,
        out string? error,
        bool allowCatalogErrors)
    {
        error = null;
        string deviceSelector = options.DeviceSelector.Trim();
        string? deviceUdid = NormalizeText(options.DeviceUdid);
        string? deviceName = NormalizeText(options.DeviceName);
        string? runtimeDisplayName = NormalizeText(options.RuntimeDisplayName);
        string? appBundleId = NormalizeText(options.AppBundleId);
        string? appDisplayName = NormalizeText(options.AppDisplayName);
        string? appExecutable = NormalizeText(options.AppExecutable);
        string? process = NormalizeText(options.Process);
        int? processId = options.ProcessId is > 0 ? options.ProcessId : null;
        string last = string.IsNullOrWhiteSpace(options.Last) ? "15m" : options.Last.Trim();

        bool needsResolvedDevice = deviceUdid is null || deviceName is null || runtimeDisplayName is null;
        string? deviceError = null;

        if (needsResolvedDevice
            && SimctlDeviceCatalog.TryResolveDevice(deviceUdid ?? deviceSelector, out SimctlDeviceListEntry? device, out deviceError))
        {
            deviceUdid = device!.Udid;
            deviceName = device.Name;
            runtimeDisplayName = device.RuntimeDisplayName;
        }
        else if (allowCatalogErrors && needsResolvedDevice)
        {
            error = deviceError;
            return options;
        }

        if (appBundleId is not null && (appDisplayName is null || appExecutable is null))
        {
            if (deviceUdid is null)
            {
                error = "Resolve the simulator device before filtering by app bundle identifier.";
                return options;
            }

            if (SimctlAppCatalog.TryGetApp(deviceUdid, appBundleId, out SimctlAppListEntry? app, out string? appError))
            {
                SimctlAppListEntry resolvedApp = app!;
                appDisplayName ??= resolvedApp.DisplayName;
                appExecutable ??= resolvedApp.Executable;
            }
            else if (allowCatalogErrors)
            {
                error = appError;
                return options;
            }
        }

        return new SimctlLogSourceOptions(
            deviceSelector,
            deviceUdid,
            deviceName,
            runtimeDisplayName,
            appBundleId,
            appDisplayName,
            appExecutable,
            process,
            processId,
            last,
            options.IncludeInfo,
            options.IncludeDebug);
    }

    private static bool TryCaptureSnapshot(
        SimctlLogSourceOptions options,
        string spoolPath,
        out string? error)
    {
        error = null;

        using Process process = StartLogShowProcess(options);
        Task<string> stderrTask = SourceRuntime.ReadDiagnosticsAsync(process.StandardError);
        int importedEventCount;

        using (var output = new FileStream(
                   spoolPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            importedEventCount = CopyNdjsonEvents(process.StandardOutput.BaseStream, output, SourceRuntime.OpeningToken);
        }

        process.WaitForExit();
        string stderr = stderrTask.GetAwaiter().GetResult().Trim();

        if (process.ExitCode == 0 && importedEventCount > 0)
        {
            return true;
        }

        if (process.ExitCode == 0 && importedEventCount == 0)
        {
            error =
                "No matching simulator unified-log events were found. Try a larger 'Last' range, clear the app/process filter, or note that plain stdout/print output is not imported by this source.";
            return false;
        }

        error = string.IsNullOrWhiteSpace(stderr)
            ? $"Simulator log import failed for '{BuildDisplayPath(options)}'."
            : $"Simulator log import failed for '{BuildDisplayPath(options)}': {stderr}";
        return false;
    }

    private static Process StartLogShowProcess(SimctlLogSourceOptions options)
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
        startInfo.ArgumentList.Add(options.DeviceUdid ?? options.DeviceSelector);
        startInfo.ArgumentList.Add("log");
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add("--style");
        startInfo.ArgumentList.Add("ndjson");
        startInfo.ArgumentList.Add("--last");
        startInfo.ArgumentList.Add(options.Last);
        startInfo.ArgumentList.Add("--no-backtrace");

        if (options.IncludeInfo)
        {
            startInfo.ArgumentList.Add("--info");
        }

        if (options.IncludeDebug)
        {
            startInfo.ArgumentList.Add("--debug");
        }

        string? appPredicate = BuildAppPredicate(options);

        if (!string.IsNullOrWhiteSpace(appPredicate))
        {
            startInfo.ArgumentList.Add("--predicate");
            startInfo.ArgumentList.Add(appPredicate);
        }

        if (options.ProcessId is int pid)
        {
            startInfo.ArgumentList.Add("--process");
            startInfo.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        }
        else if (!string.IsNullOrWhiteSpace(options.Process))
        {
            startInfo.ArgumentList.Add("--process");
            startInfo.ArgumentList.Add(options.Process);
        }

        try
        {
            Process? process = SourceRuntime.Start(startInfo);
            return process ?? throw new InvalidOperationException("xcrun simctl spawn log show did not start.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start simulator log import. Ensure 'xcrun simctl' is available. {ex.Message}",
                ex);
        }
    }

    private static string? BuildAppPredicate(SimctlLogSourceOptions options)
    {
        var clauses = new HashSet<string>(StringComparer.Ordinal);

        AddEqualityPredicateClause(clauses, "subsystem", options.AppBundleId);
        AddContainsPredicateClause(clauses, "eventMessage", options.AppBundleId);
        AddEqualityPredicateClause(clauses, "process", options.AppExecutable);
        AddEndsWithPredicateClause(clauses, "processImagePath", options.AppExecutable);
        AddEndsWithPredicateClause(clauses, "senderImagePath", options.AppExecutable);

        return clauses.Count == 0 ? null : string.Join(" OR ", clauses);
    }

    private static void AddEqualityPredicateClause(ISet<string> clauses, string fieldName, string? value)
    {
        string? normalized = NormalizeText(value);

        if (normalized is null)
        {
            return;
        }

        clauses.Add($"{fieldName} == {ToPredicateStringLiteral(normalized)}");
    }

    private static void AddContainsPredicateClause(ISet<string> clauses, string fieldName, string? value)
    {
        string? normalized = NormalizeText(value);

        if (normalized is null)
        {
            return;
        }

        clauses.Add($"{fieldName} CONTAINS[c] {ToPredicateStringLiteral(normalized)}");
    }

    private static void AddEndsWithPredicateClause(ISet<string> clauses, string fieldName, string? fileName)
    {
        string? normalized = NormalizeText(fileName);

        if (normalized is null)
        {
            return;
        }

        clauses.Add($"{fieldName} ENDSWITH {ToPredicateStringLiteral($"/{normalized}")}");
    }

    private static string ToPredicateStringLiteral(string value)
    {
        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string BuildSpoolPath(SimctlLogSourceOptions options) => SourceRuntime.CreateSpool(".ndjson");

    private static string BuildDisplayPath(SimctlLogSourceOptions options)
    {
        string device = Uri.EscapeDataString(options.DeviceUdid ?? options.DeviceSelector);
        var queryParts = new List<string>
        {
            $"last={Uri.EscapeDataString(options.Last)}"
        };

        if (!string.IsNullOrWhiteSpace(options.DeviceName))
        {
            queryParts.Add($"name={Uri.EscapeDataString(options.DeviceName)}");
        }

        if (!string.IsNullOrWhiteSpace(options.RuntimeDisplayName))
        {
            queryParts.Add($"runtime={Uri.EscapeDataString(options.RuntimeDisplayName)}");
        }

        if (!string.IsNullOrWhiteSpace(options.AppBundleId))
        {
            queryParts.Add($"app={Uri.EscapeDataString(options.AppBundleId)}");
        }

        if (options.ProcessId is int pid)
        {
            queryParts.Add($"pid={pid.ToString(CultureInfo.InvariantCulture)}");
        }
        else if (!string.IsNullOrWhiteSpace(options.Process))
        {
            queryParts.Add($"process={Uri.EscapeDataString(options.Process)}");
        }

        if (options.IncludeInfo)
        {
            queryParts.Add("info=1");
        }

        if (options.IncludeDebug)
        {
            queryParts.Add("debug=1");
        }

        return $"simctl://{device}?{string.Join("&", queryParts)}";
    }

    private static string BuildShortName(SimctlLogSourceOptions options)
    {
        string deviceName = options.DeviceName ?? options.DeviceSelector;

        if (!string.IsNullOrWhiteSpace(options.AppDisplayName))
        {
            return $"{deviceName}/{options.AppDisplayName}";
        }

        if (!string.IsNullOrWhiteSpace(options.AppBundleId))
        {
            return $"{deviceName}/{options.AppBundleId}";
        }

        if (options.ProcessId is int pid)
        {
            return $"{deviceName}/pid:{pid.ToString(CultureInfo.InvariantCulture)}";
        }

        if (!string.IsNullOrWhiteSpace(options.Process))
        {
            return $"{deviceName}/{options.Process}";
        }

        return deviceName;
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(
            value
                .Trim()
                .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                .ToArray());

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "simctl-log";
        }

        return sanitized.Length <= 64
            ? sanitized
            : sanitized[..64];
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static int CopyNdjsonEvents(Stream input, Stream output, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        int writtenEvents = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = reader.ReadLine();

            if (line is null)
            {
                break;
            }

            if (!IsLogEventLine(line))
            {
                continue;
            }

            writer.WriteLine(line);
            writtenEvents++;
        }

        writer.Flush();
        return writtenEvents;
    }

    private static bool IsLogEventLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("eventType", out JsonElement eventType)
                && eventType.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(eventType.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
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
