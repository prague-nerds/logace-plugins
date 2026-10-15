using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Buffers;
using LogAce.PluginApi;

namespace LogAce.Plugins.Logcat;

/// <summary>Creates provider captures and canonical identities; the public plugin owns validation and operation scope.</summary>
internal static class LogcatSourceFactory
{
    private const string LogcatProfileId = "logcat";

    public static string GetDisplayPath(LogcatSourceOptions options)
    {
        return BuildDisplayPath(NormalizeOptions(options));
    }

    public static bool TryParseDisplayPath(
        string displayPath,
        out LogcatSourceOptions? options,
        out string? error)
    {
        options = null;
        error = null;

        if (!Uri.TryCreate(displayPath, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "adb", StringComparison.OrdinalIgnoreCase))
        {
            error = "Display path is not an Android logcat source.";
            return false;
        }

        string serial = Uri.UnescapeDataString(uri.Host);

        if (string.IsNullOrWhiteSpace(serial))
        {
            error = "Android logcat display path is missing the device serial.";
            return false;
        }

        Dictionary<string, string> query = ParseQuery(uri.Query);

        IReadOnlyList<LogcatBuffer> buffers = ParseBuffers(query.GetValueOrDefault("buffers"));
        LogcatLevel level = LogcatOptionTokens.ParseLevel(query.GetValueOrDefault("level"));

        IReadOnlyList<string> tags = query.TryGetValue("tag", out string? tagValue) && !string.IsNullOrWhiteSpace(tagValue)
            ? tagValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        LogcatOpenMode openMode = IsEnabled(query, "follow")
            || string.Equals(query.GetValueOrDefault("mode"), "follow", StringComparison.OrdinalIgnoreCase)
                ? LogcatOpenMode.Follow
                : LogcatOpenMode.Snapshot;

        int? processId = null;

        if (query.TryGetValue("pid", out string? pidValue)
            && int.TryParse(pidValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPid)
            && parsedPid > 0)
        {
            processId = parsedPid;
        }

        options = NormalizeOptions(new LogcatSourceOptions(
            DeviceSelector: serial,
            DeviceSerial: serial,
            DeviceModel: query.GetValueOrDefault("model"),
            Buffers: buffers,
            MinimumLevel: level,
            Tags: tags,
            Package: query.GetValueOrDefault("package"),
            ProcessId: processId,
            OpenMode: openMode,
            Tail: query.TryGetValue("tail", out string? tail) && !string.IsNullOrWhiteSpace(tail) ? tail : "2000",
            ClearBufferFirst: IsEnabled(query, "clear")));
        return true;
    }

    public static bool TryCreateCore(
        LogcatSourceOptions options,
        out LogSourceHandle? source,
        out string? error)
    {
        source = null;
        error = null;

        LogcatSourceOptions normalizedOptions = NormalizeOptions(options);

        if (!AdbDeviceCatalog.TryResolveDevice(
                normalizedOptions.DeviceSerial ?? normalizedOptions.DeviceSelector,
                out AdbDeviceListEntry? device,
                out error)
            || device is null)
        {
            return false;
        }

        normalizedOptions = normalizedOptions with
        {
            DeviceSerial = device.Serial,
            DeviceModel = device.Model ?? normalizedOptions.DeviceModel
        };

        if (normalizedOptions.ProcessId is null && !string.IsNullOrWhiteSpace(normalizedOptions.Package))
        {
            if (!AdbPackageCatalog.TryResolvePid(device.Serial, normalizedOptions.Package!, out int? resolvedPid, out error))
            {
                return false;
            }

            if (resolvedPid is null)
            {
                error = $"Package '{normalizedOptions.Package}' is not running on the device, so no PID filter could be applied.";
                return false;
            }

            normalizedOptions = normalizedOptions with
            {
                ProcessId = resolvedPid
            };
        }

        string spoolPath = BuildSpoolPath(normalizedOptions);
        string displayPath = BuildDisplayPath(normalizedOptions);

        try
        {
            if (normalizedOptions.OpenMode == LogcatOpenMode.Follow)
            {
                if (!LogcatFollowSession.TryStart(normalizedOptions, spoolPath, out LogcatFollowSession? session, out error))
                {
                    return false;
                }

                source = new LogSourceHandle(
                    spoolPath,
                    displayPath,
                    BuildShortName(normalizedOptions),

                    preferredProfileId: LogcatProfileId,
                    supportsRecentHistory: true,
                    supportsDuplicate: true,
                    autoReloadOnExternalChange: true,
                    isFileSystemSource: false,
                    lifetime: session);
                return true;
            }

            if (!TryCaptureSnapshot(normalizedOptions, spoolPath, out error))
            {
                TryDeleteFile(spoolPath);
                return false;
            }

            source = new LogSourceHandle(
                spoolPath,
                displayPath,
                BuildShortName(normalizedOptions),

                preferredProfileId: LogcatProfileId,
                supportsRecentHistory: true,
                supportsDuplicate: true,
                autoReloadOnExternalChange: false,
                isFileSystemSource: false,
                lifetime: new TemporaryFileLifetime(spoolPath));
            return true;
        }
        catch (Exception ex)
        {
            TryDeleteFile(spoolPath);
            error = $"Could not open Android logcat for '{displayPath}': {ex.Message}";
            return false;
        }
    }

    public static string BuildShortName(LogcatSourceOptions options)
    {
        string device = options.DeviceModel ?? options.DeviceSerial ?? options.DeviceSelector;

        if (!string.IsNullOrWhiteSpace(options.Package))
        {
            return $"{device}/{options.Package}";
        }

        if (options.ProcessId is int pid)
        {
            return $"{device}/pid:{pid.ToString(CultureInfo.InvariantCulture)}";
        }

        return device;
    }

    internal static List<string> BuildLogcatArguments(LogcatSourceOptions options, bool follow, string? sinceTimestamp)
    {
        var arguments = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.DeviceSerial))
        {
            arguments.Add("-s");
            arguments.Add(options.DeviceSerial!);
        }

        arguments.Add("logcat");
        arguments.Add("-v");
        arguments.Add("threadtime");
        arguments.Add("-v");
        arguments.Add("year");

        foreach (LogcatBuffer buffer in options.Buffers)
        {
            arguments.Add("-b");
            arguments.Add(LogcatOptionTokens.ToBufferArgument(buffer));
        }

        if (options.ProcessId is int pid)
        {
            arguments.Add($"--pid={pid.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(sinceTimestamp))
        {
            // Reconnect: resume from the last written timestamp.
            arguments.Add("-T");
            arguments.Add(sinceTimestamp!);
        }
        else if (!IsAllTail(options.Tail))
        {
            arguments.Add(follow ? "-T" : "-t");
            arguments.Add(options.Tail);
        }

        if (!follow)
        {
            // Dump the buffer and exit instead of streaming.
            arguments.Add("-d");
        }

        AppendFilterSpec(arguments, options);
        return arguments;
    }

    private static void AppendFilterSpec(List<string> arguments, LogcatSourceOptions options)
    {
        char level = LogcatOptionTokens.ToLevelLetter(options.MinimumLevel);

        if (options.Tags.Count == 0)
        {
            arguments.Add($"*:{level}");
            return;
        }

        foreach (string tag in options.Tags)
        {
            arguments.Add($"{tag}:{level}");
        }

        arguments.Add("*:S");
    }

    private static bool TryCaptureSnapshot(
        LogcatSourceOptions options,
        string spoolPath,
        out string? error)
    {
        error = null;

        using Process process = AdbDeviceCatalog.StartAdbProcess(BuildLogcatArguments(options, follow: false, sinceTimestamp: null));
        Task<string> stderrTask = SourceRuntime.ReadDiagnosticsAsync(process.StandardError);

        using (var output = new FileStream(
                   spoolPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            CopyOutput(process.StandardOutput.BaseStream, output, SourceRuntime.OpeningToken);
        }

        process.WaitForExit();
        string stderr = stderrTask.GetAwaiter().GetResult().Trim();

        if (process.ExitCode == 0)
        {
            return true;
        }

        error = string.IsNullOrWhiteSpace(stderr)
            ? $"Android logcat command failed for '{BuildDisplayPath(options)}'."
            : $"Android logcat command failed for '{BuildDisplayPath(options)}': {stderr}";
        return false;
    }

    internal static string? TryExtractTimestamp(string line)
    {
        if (line.Length < 23
            || line[4] != '-'
            || line[7] != '-'
            || line[10] != ' '
            || line[13] != ':'
            || line[16] != ':'
            || line[19] != '.')
        {
            return null;
        }

        return line[..23];
    }

    private static LogcatSourceOptions NormalizeOptions(LogcatSourceOptions options)
    {
        IReadOnlyList<LogcatBuffer> buffers = options.Buffers.Count == 0
            ? [LogcatBuffer.Main, LogcatBuffer.System, LogcatBuffer.Crash]
            : options.Buffers.Contains(LogcatBuffer.All)
                ? [LogcatBuffer.All]
                : options.Buffers.Distinct().ToArray();

        string[] tags = options.Tags
            .Select(static tag => tag.Trim())
            .Where(static tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string tail = string.IsNullOrWhiteSpace(options.Tail) ? "2000" : options.Tail.Trim();

        return options with
        {
            DeviceSelector = options.DeviceSelector.Trim(),
            Buffers = buffers,
            Tags = tags,
            Package = NormalizeText(options.Package),
            ProcessId = options.ProcessId is > 0 ? options.ProcessId : null,
            Tail = tail
        };
    }

    private static string BuildDisplayPath(LogcatSourceOptions options)
    {
        string serial = Uri.EscapeDataString(options.DeviceSerial ?? options.DeviceSelector);
        var queryParts = new List<string>
        {
            $"buffers={string.Join(",", options.Buffers.Select(LogcatOptionTokens.ToBufferArgument))}",
            $"level={LogcatOptionTokens.ToLevelLetter(options.MinimumLevel)}"
        };

        if (options.Tags.Count > 0)
        {
            queryParts.Add($"tag={Uri.EscapeDataString(string.Join(",", options.Tags))}");
        }

        if (options.ProcessId is int pid)
        {
            queryParts.Add($"pid={pid.ToString(CultureInfo.InvariantCulture)}");
        }
        else if (!string.IsNullOrWhiteSpace(options.Package))
        {
            queryParts.Add($"package={Uri.EscapeDataString(options.Package!)}");
        }

        if (!string.IsNullOrWhiteSpace(options.DeviceModel))
        {
            queryParts.Add($"model={Uri.EscapeDataString(options.DeviceModel!)}");
        }

        queryParts.Add($"mode={(options.OpenMode == LogcatOpenMode.Follow ? "follow" : "snapshot")}");
        queryParts.Add($"tail={Uri.EscapeDataString(options.Tail)}");

        if (options.OpenMode == LogcatOpenMode.Follow)
        {
            queryParts.Add("follow=1");
        }

        if (options.ClearBufferFirst)
        {
            queryParts.Add("clear=1");
        }

        return $"adb://{serial}?{string.Join("&", queryParts)}";
    }

    private static IReadOnlyList<LogcatBuffer> ParseBuffers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [LogcatBuffer.Main, LogcatBuffer.System, LogcatBuffer.Crash];
        }

        var buffers = new List<LogcatBuffer>();

        foreach (string token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (LogcatOptionTokens.TryParseBuffer(token, out LogcatBuffer buffer))
            {
                buffers.Add(buffer);
            }
        }

        return buffers.Count == 0
            ? [LogcatBuffer.Main, LogcatBuffer.System, LogcatBuffer.Crash]
            : buffers;
    }

    private static bool IsAllTail(string tail)
    {
        return string.Equals(tail.Trim(), "all", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSpoolPath(LogcatSourceOptions options) => SourceRuntime.CreateSpool(".logcat");

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        string trimmed = query.StartsWith('?') ? query[1..] : query;

        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = pair.IndexOf('=');

            if (separatorIndex < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            string key = Uri.UnescapeDataString(pair[..separatorIndex]);
            string value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static bool IsEnabled(IReadOnlyDictionary<string, string> query, string key)
    {
        return query.TryGetValue(key, out string? value)
            && (value.Length == 0
                || string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
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
            return "logcat";
        }

        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
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

    private static void CopyOutput(Stream input, Stream output, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);

                if (read == 0)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                output.Flush();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
