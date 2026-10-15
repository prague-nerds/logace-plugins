using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;
using System.Buffers;
using System.IO.Compression;
using System.Net;
using LogAce.PluginApi;

namespace LogAce.Plugins.Otel;

/// <summary>Creates provider captures and canonical identities; the public plugin owns validation and operation scope.</summary>
internal static class OtelLogSourceFactory
{
    public const string DefaultBindAddress = "127.0.0.1";
    public const int DefaultPort = 4318;
    public const string DefaultPath = "/v1/logs";

    public static string NormalizeBindAddress(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized) ? DefaultBindAddress : normalized;
    }

    public static string NormalizePath(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return DefaultPath;
        }

        return normalized.StartsWith("/", StringComparison.Ordinal)
            ? normalized
            : $"/{normalized}";
    }

    public static string GetDisplayPath(OtelLogSourceOptions options)
    {
        return BuildDisplayPath(NormalizeOptions(options));
    }

    public static bool TryParseDisplayPath(
        string displayPath,
        out OtelLogSourceOptions? options,
        out string? error)
    {
        options = null;
        error = null;

        if (!Uri.TryCreate(displayPath, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "otlp-http", StringComparison.OrdinalIgnoreCase))
        {
            error = "OpenTelemetry display path must start with otlp-http://.";
            return false;
        }

        int port = uri.IsDefaultPort ? DefaultPort : uri.Port;
        string path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? DefaultPath : uri.AbsolutePath;
        options = NormalizeOptions(new OtelLogSourceOptions(uri.Host, port, path));
        return ValidateOptions(options, out error);
    }

    public static bool TryCreateCore(
        OtelLogSourceOptions options,
        out LogSourceHandle? source,
        out string? error)
    {
        source = null;
        OtelLogSourceOptions normalized = NormalizeOptions(options);

        if (!ValidateOptions(normalized, out error))
        {
            return false;
        }

        string spoolPath = BuildSpoolPath(normalized);
        string displayPath = BuildDisplayPath(normalized);

        try
        {
            if (!OtelLogSinkSession.TryStart(normalized, spoolPath, out OtelLogSinkSession? session, out error))
            {
                TryDeleteFile(spoolPath);
                return false;
            }

            source = new LogSourceHandle(
                spoolPath,
                displayPath,
                BuildShortName(normalized),

                preferredProfileId: "otel-logs",
                supportsRecentHistory: false,
                supportsDuplicate: false,
                autoReloadOnExternalChange: true,
                isFileSystemSource: false,
                lifetime: session);
            return true;
        }
        catch (Exception ex)
        {
            TryDeleteFile(spoolPath);
            error = $"Could not open OpenTelemetry log sink '{displayPath}': {ex.Message}";
            return false;
        }
    }

    public static bool ValidateOptions(OtelLogSourceOptions options, out string? error)
    {
        error = null;

        if (options.Port is < 1 or > 65535)
        {
            error = $"OTLP HTTP port '{options.Port}' must be between 1 and 65535.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.BindAddress))
        {
            error = "OTLP HTTP bind address cannot be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.Path) || !options.Path.StartsWith("/", StringComparison.Ordinal))
        {
            error = "OTLP HTTP path must start with '/'.";
            return false;
        }

        try
        {
            _ = BuildListenerPrefix(options);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not use OTLP HTTP bind address '{options.BindAddress}': {ex.Message}";
            return false;
        }
    }

    public static string BuildShortName(OtelLogSourceOptions options)
    {
        string bind = NormalizeBindAddress(options.BindAddress);
        return string.Equals(bind, DefaultBindAddress, StringComparison.OrdinalIgnoreCase)
            ? $"otel-logs:{options.Port.ToString(CultureInfo.InvariantCulture)}"
            : $"otel-logs@{bind}:{options.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    internal static string BuildListenerPrefix(OtelLogSourceOptions options)
    {
        string host = FormatHostForListener(NormalizeBindAddress(options.BindAddress));
        return $"http://{host}:{options.Port.ToString(CultureInfo.InvariantCulture)}/";
    }

    private static OtelLogSourceOptions NormalizeOptions(OtelLogSourceOptions options)
    {
        return new OtelLogSourceOptions(
            NormalizeBindAddress(options.BindAddress),
            options.Port <= 0 ? DefaultPort : options.Port,
            NormalizePath(options.Path));
    }

    private static string BuildDisplayPath(OtelLogSourceOptions options)
    {
        string host = FormatHostForDisplay(options.BindAddress);
        return $"otlp-http://{host}:{options.Port.ToString(CultureInfo.InvariantCulture)}{options.Path}";
    }

    private static string BuildSpoolPath(OtelLogSourceOptions options) => SourceRuntime.CreateSpool(".ndjson");

    private static string FormatHostForListener(string value)
    {
        string normalized = NormalizeBindAddress(value);

        if (normalized is "*" or "+" || string.Equals(normalized, "0.0.0.0", StringComparison.Ordinal) || string.Equals(normalized, "::", StringComparison.Ordinal))
        {
            return "*";
        }

        string trimmed = normalized.Trim('[', ']');
        return trimmed.Contains(':', StringComparison.Ordinal)
            ? $"[{trimmed}]"
            : trimmed;
    }

    private static string FormatHostForDisplay(string value)
    {
        string normalized = NormalizeBindAddress(value);
        string trimmed = normalized.Trim('[', ']');
        return trimmed.Contains(':', StringComparison.Ordinal)
            ? $"[{trimmed}]"
            : trimmed;
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
            return "default";
        }

        return sanitized.Length <= 64
            ? sanitized
            : sanitized[..64];
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

    private sealed class OtelLogSinkSession : IDisposable
    {
        private const int MaxRequestBytes = 64 * 1024 * 1024;

        private readonly string _spoolPath;
        private readonly OtelLogSourceOptions _options;
        private readonly HttpListener _listener;
        private readonly FileStream _output;
        private readonly StreamWriter _writer;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _listenerTask;
        private readonly object _writeSync = new();
        private bool _disposed;

        private OtelLogSinkSession(
            string spoolPath,
            OtelLogSourceOptions options,
            HttpListener listener,
            FileStream output,
            CancellationTokenSource cancellationTokenSource)
        {
            _spoolPath = spoolPath;
            _options = options;
            _listener = listener;
            _output = output;
            _writer = new StreamWriter(output, new UTF8Encoding(false))
            {
                NewLine = "\n",
                AutoFlush = true
            };
            _cancellationTokenSource = cancellationTokenSource;
            _listenerTask = RunListenerAsync(cancellationTokenSource.Token);
        }

        public static bool TryStart(
            OtelLogSourceOptions options,
            string spoolPath,
            out OtelLogSinkSession? createdSession,
            out string? error)
        {
            createdSession = null;
            error = null;
            FileStream? output = null;
            HttpListener? listener = null;
            var cancellationTokenSource = new CancellationTokenSource();

            try
            {
                output = new FileStream(
                    spoolPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);

                listener = new HttpListener();
                listener.Prefixes.Add(BuildListenerPrefix(options));
                listener.Start();

                createdSession = new OtelLogSinkSession(
                    spoolPath,
                    options,
                    listener,
                    output,
                    cancellationTokenSource);
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    listener?.Close();
                }
                catch
                {
                }

                output?.Dispose();
                cancellationTokenSource.Dispose();
                TryDeleteFile(spoolPath);
                error = $"Could not start OTLP HTTP listener: {ex.Message}";
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch
            {
            }

            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
            }

            try
            {
                _listenerTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            try
            {
                _writer.Dispose();
            }
            catch
            {
            }

            try
            {
                _output.Dispose();
            }
            catch
            {
            }

            _cancellationTokenSource.Dispose();
            TryDeleteFile(_spoolPath);
        }

        private async Task RunListenerAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                await HandleContextAsync(context, cancellationToken);
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            try
            {
                if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextResponseAsync(context.Response, 405, "OTLP log sink only accepts POST.", cancellationToken);
                    return;
                }

                string requestPath = context.Request.Url?.AbsolutePath ?? string.Empty;

                if (!string.Equals(requestPath, _options.Path, StringComparison.Ordinal))
                {
                    await WriteTextResponseAsync(context.Response, 404, $"OTLP log endpoint is {_options.Path}.", cancellationToken);
                    return;
                }

                byte[] body;

                try
                {
                    body = await ReadRequestBodyAsync(context.Request, cancellationToken);
                }
                catch (InvalidDataException ex)
                {
                    await WriteTextResponseAsync(context.Response, 413, ex.Message, cancellationToken);
                    return;
                }

                if (body.Length == 0)
                {
                    await WriteOtlpResponseAsync(context.Request, context.Response, cancellationToken);
                    return;
                }

                bool isJson = LooksLikeJson(context.Request.ContentType, body);
                bool ok = isJson
                    ? OtelLogBatchNormalizer.TryNormalizeJson(body, AppendLine, out int recordCount, out string? error)
                    : OtelLogBatchNormalizer.TryNormalizeProtobuf(body, AppendLine, out recordCount, out error);

                if (!ok)
                {
                    await WriteTextResponseAsync(context.Response, 400, error ?? "Could not parse OTLP log request.", cancellationToken);
                    return;
                }

                _ = recordCount;
                await WriteOtlpResponseAsync(context.Request, context.Response, cancellationToken);
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteTextResponseAsync(context.Response, 500, $"OTLP log ingest failed: {ex.Message}", cancellationToken);
                }
                catch
                {
                }
            }
        }

        private void AppendLine(string line)
        {
            lock (_writeSync)
            {
                _writer.WriteLine(line);
            }
        }

        private static async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
        {
            Stream input = request.InputStream;
            Stream? decompressor = null;

            if (string.Equals(request.Headers["Content-Encoding"], "gzip", StringComparison.OrdinalIgnoreCase))
            {
                decompressor = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
                input = decompressor;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);

            try
            {
                using var memory = new MemoryStream();

                while (true)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

                    if (read <= 0)
                    {
                        break;
                    }

                    if (memory.Length + read > MaxRequestBytes)
                    {
                        throw new InvalidDataException($"OTLP request body exceeds {MaxRequestBytes / (1024 * 1024)} MiB.");
                    }

                    memory.Write(buffer, 0, read);
                }

                return memory.ToArray();
            }
            finally
            {
                decompressor?.Dispose();
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static bool LooksLikeJson(string? contentType, byte[] body)
        {
            if (!string.IsNullOrWhiteSpace(contentType)
                && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (byte value in body)
            {
                if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    continue;
                }

                return value is (byte)'{' or (byte)'[';
            }

            return false;
        }

        private static async Task WriteOtlpResponseAsync(
            HttpListenerRequest request,
            HttpListenerResponse response,
            CancellationToken cancellationToken)
        {
            bool wantsJson = request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
            byte[] payload = wantsJson ? Encoding.UTF8.GetBytes("{}") : [];
            response.StatusCode = 200;
            response.ContentType = wantsJson ? "application/json" : "application/x-protobuf";
            response.ContentLength64 = payload.Length;

            if (payload.Length > 0)
            {
                await response.OutputStream.WriteAsync(payload, cancellationToken);
            }

            response.Close();
        }

        private static async Task WriteTextResponseAsync(
            HttpListenerResponse response,
            int statusCode,
            string message,
            CancellationToken cancellationToken)
        {
            byte[] payload = Encoding.UTF8.GetBytes(message);
            response.StatusCode = statusCode;
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = payload.Length;
            await response.OutputStream.WriteAsync(payload, cancellationToken);
            response.Close();
        }
    }
}
