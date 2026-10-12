using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LogAce.PluginApi;

namespace LogAce.Plugins.Syslog;

/// <summary>Creates provider captures and canonical identities; the public plugin owns validation and operation scope.</summary>
internal static class SyslogSinkSourceFactory
{
    public const string DefaultBindAddress = "0.0.0.0";
    public const int DefaultPort = 5514;
    private const int MaxTcpFrameLength = 1024 * 1024;

    public static string NormalizeBindAddress(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized) ? DefaultBindAddress : normalized;
    }

    public static string GetDisplayPath(SyslogSinkSourceOptions options)
    {
        return BuildDisplayPath(options);
    }

    public static bool TryCreateCore(
        SyslogSinkSourceOptions options,
        out LogSourceHandle? source,
        out string? error)
    {
        source = null;
        error = null;

        if (!ValidateOptions(options, out error))
        {
            return false;
        }

        string spoolPath = BuildSpoolPath(options);
        string displayPath = BuildDisplayPath(options);
        string shortName = BuildShortName(options);

        try
        {
            if (!SyslogSinkSession.TryStart(options, spoolPath, out SyslogSinkSession? session, out error))
            {
                TryDeleteFile(spoolPath);
                return false;
            }

            source = new LogSourceHandle(
                spoolPath,
                displayPath,
                shortName,

                preferredProfileId: "syslog-sink",
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
            error = $"Could not open syslog sink '{displayPath}': {ex.Message}";
            return false;
        }
    }

    public static bool ValidateOptions(SyslogSinkSourceOptions options, out string? error)
    {
        error = null;

        if (!options.UseUdp && !options.UseTcp)
        {
            error = "Enable at least one syslog transport by providing a UDP or TCP port.";
            return false;
        }

        if (options.UdpPort is int udpPort && !IsValidPort(udpPort))
        {
            error = $"UDP port '{udpPort}' must be between 1 and 65535.";
            return false;
        }

        if (options.TcpPort is int tcpPort && !IsValidPort(tcpPort))
        {
            error = $"TCP port '{tcpPort}' must be between 1 and 65535.";
            return false;
        }

        try
        {
            _ = ResolveBindAddresses(options.BindAddress);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not resolve bind address '{options.BindAddress}': {ex.Message}";
            return false;
        }
    }

    private static bool IsValidPort(int port)
    {
        return port is >= 1 and <= 65535;
    }

    private static string BuildSpoolPath(SyslogSinkSourceOptions options) => SourceRuntime.CreateSpool(".log");

    private static string BuildDisplayPath(SyslogSinkSourceOptions options)
    {
        string host = FormatAddressForDisplay(options.BindAddress);

        if (options.UdpPort is int udpPort
            && options.TcpPort is int tcpPort
            && udpPort == tcpPort)
        {
            return $"syslog://{host}:{udpPort}?transport=udp,tcp";
        }

        var parameters = new List<string>(2);

        if (options.UdpPort is int udpOnlyPort)
        {
            parameters.Add($"udp={udpOnlyPort}");
        }

        if (options.TcpPort is int tcpOnlyPort)
        {
            parameters.Add($"tcp={tcpOnlyPort}");
        }

        return $"syslog://{host}?{string.Join("&", parameters)}";
    }

    private static string BuildShortName(SyslogSinkSourceOptions options)
    {
        bool usesDefaultBind = string.Equals(
            NormalizeBindAddress(options.BindAddress),
            DefaultBindAddress,
            StringComparison.OrdinalIgnoreCase);
        string bindSuffix = usesDefaultBind ? string.Empty : $"@{options.BindAddress}";

        if (options.UdpPort is int udpPort
            && options.TcpPort is int tcpPort
            && udpPort == tcpPort)
        {
            return $"syslog{bindSuffix}:{udpPort}";
        }

        if (options.UdpPort is int udpOnlyPort && options.TcpPort is null)
        {
            return $"syslog{bindSuffix} udp:{udpOnlyPort}";
        }

        if (options.TcpPort is int tcpOnlyPort && options.UdpPort is null)
        {
            return $"syslog{bindSuffix} tcp:{tcpOnlyPort}";
        }

        return $"syslog{bindSuffix} u:{options.UdpPort?.ToString(CultureInfo.InvariantCulture) ?? "-"} t:{options.TcpPort?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
    }

    private static string FormatPorts(SyslogSinkSourceOptions options)
    {
        if (options.UdpPort is int udpPort
            && options.TcpPort is int tcpPort
            && udpPort == tcpPort)
        {
            return udpPort.ToString(CultureInfo.InvariantCulture);
        }

        string udp = options.UdpPort?.ToString(CultureInfo.InvariantCulture) ?? "off";
        string tcp = options.TcpPort?.ToString(CultureInfo.InvariantCulture) ?? "off";
        return $"udp-{udp}_tcp-{tcp}";
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
            return "any";
        }

        return sanitized.Length <= 64
            ? sanitized
            : sanitized[..64];
    }

    private static string FormatAddressForDisplay(string value)
    {
        if (value.Contains(':', StringComparison.Ordinal) && !value.StartsWith("[", StringComparison.Ordinal))
        {
            return $"[{value}]";
        }

        return value;
    }

    private static IReadOnlyList<IPAddress> ResolveBindAddresses(string bindAddress)
    {
        string normalized = NormalizeBindAddress(bindAddress);

        if (string.Equals(normalized, "*", StringComparison.Ordinal))
        {
            return [IPAddress.Any];
        }

        if (IPAddress.TryParse(normalized, out IPAddress? parsed))
        {
            return [parsed];
        }

        IPAddress[] resolved = Dns.GetHostAddresses(normalized)
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct()
            .ToArray();

        if (resolved.Length == 0)
        {
            throw new InvalidOperationException("No IPv4 or IPv6 addresses were resolved.");
        }

        return resolved;
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

    private sealed class SyslogSinkSession : IDisposable
    {
        private readonly string _spoolPath;
        private readonly FileStream _output;
        private readonly StreamWriter _writer;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly List<Socket> _udpSockets;
        private readonly List<TcpListener> _tcpListeners;
        private readonly List<Task> _backgroundTasks;
        private readonly object _writeSync = new();
        private readonly object _taskSync = new();
        private bool _disposed;

        private SyslogSinkSession(
            string spoolPath,
            FileStream output,
            CancellationTokenSource cancellationTokenSource,
            List<Socket> udpSockets,
            List<TcpListener> tcpListeners,
            List<Task> backgroundTasks)
        {
            _spoolPath = spoolPath;
            _output = output;
            _writer = new StreamWriter(output, new UTF8Encoding(false))
            {
                NewLine = "\n",
                AutoFlush = true
            };
            _cancellationTokenSource = cancellationTokenSource;
            _udpSockets = udpSockets;
            _tcpListeners = tcpListeners;
            _backgroundTasks = backgroundTasks;
            _writer.WriteLine("#syslog-sink-v2\tTime\tHost\tApp\tPID\tMsgId\tPRI\tFacility\tSeverity\tFormat\tTransport\tRemoteIp\tListenerPort\tStructuredData\tMessage");
        }

        public static bool TryStart(
            SyslogSinkSourceOptions options,
            string spoolPath,
            out SyslogSinkSession? createdSession,
            out string? error)
        {
            createdSession = null;
            error = null;

            var udpSockets = new List<Socket>();
            var tcpListeners = new List<TcpListener>();
            var backgroundTasks = new List<Task>();
            var cancellationTokenSource = new CancellationTokenSource();
            FileStream? output = null;

            try
            {
                output = new FileStream(
                    spoolPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);

                createdSession = new SyslogSinkSession(
                    spoolPath,
                    output,
                    cancellationTokenSource,
                    udpSockets,
                    tcpListeners,
                    backgroundTasks);

                IReadOnlyList<IPAddress> bindAddresses = ResolveBindAddresses(options.BindAddress);

                if (options.UdpPort is int udpPort)
                {
                    foreach (IPAddress address in bindAddresses)
                    {
                        Socket socket = CreateUdpSocket(address, udpPort);
                        udpSockets.Add(socket);
                        backgroundTasks.Add(createdSession.RunUdpListenerAsync(socket, cancellationTokenSource.Token));
                    }
                }

                if (options.TcpPort is int tcpPort)
                {
                    foreach (IPAddress address in bindAddresses)
                    {
                        TcpListener listener = CreateTcpListener(address, tcpPort);
                        tcpListeners.Add(listener);
                        backgroundTasks.Add(createdSession.RunTcpAcceptLoopAsync(listener, cancellationTokenSource.Token));
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                cancellationTokenSource.Cancel();

                foreach (Socket socket in udpSockets)
                {
                    try
                    {
                        socket.Dispose();
                    }
                    catch
                    {
                    }
                }

                foreach (TcpListener listener in tcpListeners)
                {
                    try
                    {
                        listener.Stop();
                    }
                    catch
                    {
                    }
                }

                output?.Dispose();
                cancellationTokenSource.Dispose();
                TryDeleteFile(spoolPath);
                createdSession = null;
                error = $"Could not start syslog listener: {ex.Message}";
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

            foreach (Socket socket in _udpSockets)
            {
                try
                {
                    socket.Dispose();
                }
                catch
                {
                }
            }

            foreach (TcpListener listener in _tcpListeners)
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                }
            }

            Task[] tasks;

            lock (_taskSync)
            {
                tasks = _backgroundTasks.ToArray();
            }

            try
            {
                Task.WaitAll(tasks, TimeSpan.FromSeconds(2));
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

        private static Socket CreateUdpSocket(IPAddress address, int port)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(address, port));
            return socket;
        }

        private static TcpListener CreateTcpListener(IPAddress address, int port)
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            return listener;
        }

        private async Task RunUdpListenerAsync(Socket socket, CancellationToken cancellationToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            EndPoint remoteTemplate = CreateReceiveEndpointTemplate(socket.AddressFamily);
            int listenerPort = ((IPEndPoint?)socket.LocalEndPoint)?.Port ?? 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    SocketReceiveFromResult result;

                    try
                    {
                        result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteTemplate, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (result.ReceivedBytes <= 0)
                    {
                        continue;
                    }

                    AppendMessage(
                        transport: "udp",
                        remoteIp: FormatRemoteIp(result.RemoteEndPoint),
                        listenerPort: listenerPort,
                        message: Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task RunTcpAcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                int listenerPort = ((IPEndPoint?)listener.LocalEndpoint)?.Port ?? 0;
                Task clientTask = RunTcpClientAsync(client, listenerPort, cancellationToken);

                lock (_taskSync)
                {
                    _backgroundTasks.Add(clientTask);
                }

                _ = clientTask.ContinueWith(
                    completedTask =>
                    {
                        lock (_taskSync)
                        {
                            _backgroundTasks.Remove(completedTask);
                        }
                    },
                    SourceRuntime.OpeningToken,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        private async Task RunTcpClientAsync(TcpClient client, int listenerPort, CancellationToken cancellationToken)
        {
            using (client)
            {
                await using (NetworkStream stream = client.GetStream())
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
                    var parser = new SyslogTcpMessageParser();
                    string remoteIp = FormatRemoteIp(client.Client.RemoteEndPoint);

                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            int read;

                            try
                            {
                                read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch (IOException) when (cancellationToken.IsCancellationRequested)
                            {
                                break;
                            }

                            if (read <= 0)
                            {
                                break;
                            }

                            parser.Append(buffer.AsSpan(0, read));

                            foreach (string message in parser.DrainMessages(final: false))
                            {
                                AppendMessage("tcp", remoteIp, listenerPort, message);
                            }
                        }

                        foreach (string message in parser.DrainMessages(final: true))
                        {
                            AppendMessage("tcp", remoteIp, listenerPort, message);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
        }

        private void AppendMessage(string transport, string remoteIp, int listenerPort, string message)
        {
            string normalized = NormalizeMessage(message);

            if (normalized.Length == 0)
            {
                return;
            }

            string rawReceivedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
            SplitMessage(normalized, out string firstLine, out string continuation);
            SyslogParsedMessage? parsed = SyslogParsing.TryParse(firstLine, continuation);
            string rawTime = parsed?.RawTime ?? string.Empty;
            string host = parsed?.Host ?? string.Empty;
            string app = parsed?.App ?? string.Empty;
            string procId = parsed?.ProcId ?? string.Empty;
            string msgId = parsed?.MsgId ?? string.Empty;
            string priority = parsed?.Priority ?? string.Empty;
            string facility = parsed?.Facility ?? string.Empty;
            string severity = parsed?.Severity ?? string.Empty;
            string format = parsed?.Format ?? string.Empty;
            string structuredData = parsed?.StructuredData ?? string.Empty;
            string payloadMessage = parsed?.Message ?? normalized;

            lock (_writeSync)
            {
                WriteRecord(
                    rawReceivedAt,
                    rawTime,
                    host,
                    app,
                    procId,
                    msgId,
                    priority,
                    facility,
                    severity,
                    format,
                    transport,
                    remoteIp,
                    listenerPort.ToString(CultureInfo.InvariantCulture),
                    structuredData,
                    payloadMessage);
                _writer.Flush();
            }
        }

        private void WriteRecord(
            string rawReceivedAt,
            string rawTime,
            string host,
            string app,
            string procId,
            string msgId,
            string priority,
            string facility,
            string severity,
            string format,
            string transport,
            string remoteIp,
            string listenerPort,
            string structuredData,
            string message)
        {
            string normalizedMessage = NormalizeMessage(message);
            string[] messageLines = normalizedMessage.Split('\n');
            string firstMessageLine = messageLines.Length > 0 ? messageLines[0] : string.Empty;

            _writer.Write(rawReceivedAt);
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(rawTime));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(host));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(app));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(procId));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(msgId));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(priority));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(facility));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(severity));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(format));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(transport));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(remoteIp));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(listenerPort));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(structuredData));
            _writer.Write('\t');
            _writer.Write(SyslogParsing.SanitizeInlineField(firstMessageLine));
            _writer.Write('\n');

            for (int index = 1; index < messageLines.Length; index++)
            {
                _writer.WriteLine(SyslogParsing.SanitizeInlineField(messageLines[index]));
            }
        }

        private static string NormalizeMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            return message
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .TrimEnd('\n');
        }

        private static void SplitMessage(string message, out string firstLine, out string continuation)
        {
            int newlineIndex = message.IndexOf('\n');

            if (newlineIndex < 0)
            {
                firstLine = message;
                continuation = string.Empty;
                return;
            }

            firstLine = message[..newlineIndex];
            continuation = newlineIndex + 1 < message.Length
                ? message[(newlineIndex + 1)..]
                : string.Empty;
        }

        private static EndPoint CreateReceiveEndpointTemplate(AddressFamily addressFamily)
        {
            IPAddress address = addressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Any
                : IPAddress.Any;
            return new IPEndPoint(address, 0);
        }

        private static string FormatRemoteIp(EndPoint? endPoint)
        {
            return endPoint is IPEndPoint ipEndPoint
                ? ipEndPoint.Address.ToString()
                : string.Empty;
        }
    }

    private sealed class SyslogTcpMessageParser
    {
        private readonly List<byte> _buffer = [];

        public void Append(ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
            {
                _buffer.Add(value);
            }
        }

        public IEnumerable<string> DrainMessages(bool final)
        {
            while (TryReadNextMessage(final, out string? message))
            {
                if (!string.IsNullOrEmpty(message))
                {
                    yield return message;
                }
            }
        }

        private bool TryReadNextMessage(bool final, out string? message)
        {
            message = null;

            if (_buffer.Count == 0)
            {
                return false;
            }

            if (TryReadOctetCountedMessage(out message))
            {
                return true;
            }

            int delimiterIndex = FindDelimiterIndex();

            if (delimiterIndex >= 0)
            {
                message = ReadUtf8(0, delimiterIndex);
                RemoveConsumed(delimiterIndex + 1);
                message = message.TrimEnd('\r');
                return true;
            }

            if (!final)
            {
                return false;
            }

            message = ReadUtf8(0, _buffer.Count).TrimEnd('\r');
            _buffer.Clear();
            return true;
        }

        private bool TryReadOctetCountedMessage(out string? message)
        {
            message = null;

            int spaceIndex = -1;
            int inspectedDigits = 0;

            for (int index = 0; index < _buffer.Count && index < 10; index++)
            {
                byte current = _buffer[index];

                if (current == (byte)' ')
                {
                    spaceIndex = index;
                    break;
                }

                if (current is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }

                inspectedDigits++;
            }

            if (spaceIndex <= 0 || inspectedDigits == 0)
            {
                return false;
            }

            ReadOnlySpan<byte> span = CollectionsMarshal.AsSpan(_buffer);

            if (!int.TryParse(
                    Encoding.ASCII.GetString(span[..spaceIndex]),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int frameLength))
            {
                return false;
            }

            if (frameLength <= 0 || frameLength > MaxTcpFrameLength)
            {
                return false;
            }

            int requiredLength = spaceIndex + 1 + frameLength;

            if (_buffer.Count < requiredLength)
            {
                return false;
            }

            message = ReadUtf8(spaceIndex + 1, frameLength);
            RemoveConsumed(requiredLength);
            return true;
        }

        private int FindDelimiterIndex()
        {
            for (int index = 0; index < _buffer.Count; index++)
            {
                byte current = _buffer[index];

                if (current == (byte)'\n' || current == 0)
                {
                    return index;
                }
            }

            return -1;
        }

        private string ReadUtf8(int offset, int length)
        {
            return Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_buffer).Slice(offset, length));
        }

        private void RemoveConsumed(int count)
        {
            if (count >= _buffer.Count)
            {
                _buffer.Clear();
                return;
            }

            _buffer.RemoveRange(0, count);
        }
    }
}
