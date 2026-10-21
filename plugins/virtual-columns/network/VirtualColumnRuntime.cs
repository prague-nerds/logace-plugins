using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alignment = LogAce.PluginApi.LogColumnAlignment;
using LogAce.PluginApi;
namespace LogAce.Columns.network;
internal sealed partial class VirtualColumnRuntime : IVirtualColumnEvaluator
{
    private static readonly SemaphoreSlim HostnameLookupConcurrency = new(8, 8);
    private static readonly ConcurrentDictionary<string, Task<string?>> HostnameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingRefreshNotifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly VirtualColumnEvaluationContext _context;
    private readonly VirtualColumnPlan _plan;
    private int _disposed;
    public VirtualColumnRuntime(ColumnKind kind, VirtualColumnConfiguration configuration, VirtualColumnEvaluationContext context)
    {
        _context = context;
        int[] sources = Enumerable.Range(0, configuration.SourceColumns.Count).ToArray();
        var parameters = configuration.Parameters.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        _plan = CreateVirtualColumnPlan(kind, sources, parameters);
    }

    public bool UsesBackgroundLookup => _plan.UsesBackgroundLookup;

    public VirtualColumnEvaluation Evaluate(IReadOnlyList<LogField> sources, bool materialize = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        _context.CancellationToken.ThrowIfCancellationRequested();
        var record = new LogRecord(sources as LogField[] ?? sources.ToArray());
        if (materialize)
        {
            using CancellationTokenSource? linked = cancellationToken.CanBeCanceled && _context.CancellationToken.CanBeCanceled && cancellationToken != _context.CancellationToken ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _context.CancellationToken) : null;
            CancellationToken token = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : _context.CancellationToken);
            return new(ResolveMaterializedVirtualField(record, _plan, token));
        }

        ResolvedVirtualField result = ResolveVirtualField(record, _plan);
        return new(result.Field, result.Pending);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _pendingRefreshNotifications.Clear();
    }

    internal static LogColumnDefinition Describe(ColumnKind kind, VirtualColumnConfiguration configuration) => CreateVirtualColumnDefinition(new ColumnAppearance { ColumnName = configuration.ColumnName }, kind);
    private static LogColumnDefinition CreateVirtualColumnDefinition(ColumnAppearance column, ColumnKind kind)
    {
        return kind switch
        {
            ColumnKind.Hostname => CreateTextColumnDefinition(column, 16, 96, 12),
            ColumnKind.IpPrivate => CreateTextColumnDefinition(column, 8, 12, 6),
            ColumnKind.IpVersion => CreateTextColumnDefinition(column, 6, 8, 6),
            ColumnKind.PortService => CreateTextColumnDefinition(column, 10, 24, 8),
            ColumnKind.Cidr => CreateTextColumnDefinition(column, 12, 48, 10),
            _ => CreateTextColumnDefinition(column, 16, 80, 12)};
    }

    private static LogColumnDefinition CreateTextColumnDefinition(ColumnAppearance column, int minWidth, int maxWidth, int minAcceptableWidth, Alignment alignment = Alignment.Start)
    {
        return new LogColumnDefinition(column.ColumnName, MinWidth: minWidth, MaxWidth: maxWidth, MinAcceptableWidth: minAcceptableWidth, alignment, LogFieldType.Text, LogColumnDisplayMode.Text, ExpandToFill: column.ExpandToFill, WrapContent: column.WrapContent);
    }

    private static VirtualColumnPlan CreateVirtualColumnPlan(ColumnKind kind, int[] sourceIndices, Dictionary<string, string>? parameters)
    {
        parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return kind switch
        {
            ColumnKind.Hostname => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveHostnameField(provider.GetSourceField(record, sourceIndices[0]))),
            ColumnKind.IpPrivate => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ResolveIpPrivate(provider.GetSourceText(record, sourceIndices[0])))),
            ColumnKind.IpVersion => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ResolveIpVersion(provider.GetSourceText(record, sourceIndices[0])))),
            ColumnKind.PortService => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ResolvePortService(provider.GetSourceText(record, sourceIndices[0])))),
            ColumnKind.Cidr => CreateCidrPlan(kind, sourceIndices, parameters),
            _ => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, static (_, _) => new LogField(string.Empty))};
    }

    private static VirtualColumnPlan CreateCidrPlan(ColumnKind kind, int[] sourceIndices, IReadOnlyDictionary<string, string> parameters)
    {
        string prefixText = GetRequiredParameter(parameters, "prefix", kind);
        if (!int.TryParse(prefixText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int prefix) || prefix < 0)
        {
            throw new InvalidOperationException($"Invalid CIDR prefix '{prefixText}'.");
        }

        return new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ResolveCidr(provider.GetSourceText(record, sourceIndices[0]), prefix)));
    }

    private static string GetRequiredParameter(IReadOnlyDictionary<string, string> parameters, string key, ColumnKind kind)
    {
        if (!parameters.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Virtual kind '{kind.ToString()}' requires parameter '{key}'.");
        }

        return value.Trim();
    }

    private ResolvedVirtualField ResolveVirtualField(LogRecord record, VirtualColumnPlan plan)
    {
        return plan.Resolve(this, record);
    }

    private LogField GetSourceField(LogRecord record, int sourceFieldIndex)
    {
        return sourceFieldIndex >= 0 && sourceFieldIndex < record.Fields.Length ? record.Fields[sourceFieldIndex] : new LogField(string.Empty);
    }

    private string GetSourceText(LogRecord record, int sourceFieldIndex)
    {
        return NormalizeText(GetSourceField(record, sourceFieldIndex).RawText);
    }

    private ResolvedVirtualField ResolveHostnameField(LogField sourceField)
    {
        if (!TryExtractIpAddress(sourceField.RawText ?? string.Empty, out IPAddress? address) || address is null)
        {
            return new LogField(string.Empty);
        }

        string cacheKey = address.ToString();
        Task<string?> task = HostnameCache.GetOrAdd(cacheKey, static lookupKey => ResolveHostnameAsync(lookupKey));
        string? result = TryGetTaskResult(task, $"hostname:{cacheKey}", out bool pending);
        return new ResolvedVirtualField(new LogField(result ?? string.Empty), pending);
    }

    private LogField ResolveMaterializedVirtualField(LogRecord record, VirtualColumnPlan plan, CancellationToken cancellationToken)
    {
        if (!plan.UsesBackgroundLookup)
        {
            return ResolveVirtualField(record, plan).Field;
        }

        return plan.Kind switch
        {
            ColumnKind.Hostname => ResolveMaterializedHostnameField(GetSourceField(record, plan.SourceFieldIndices[0]), cancellationToken),
            _ => ResolveVirtualField(record, plan).Field
        };
    }

    private LogField ResolveMaterializedHostnameField(LogField sourceField, CancellationToken cancellationToken)
    {
        if (!TryExtractIpAddress(sourceField.RawText ?? string.Empty, out IPAddress? address) || address is null)
        {
            return new LogField(string.Empty);
        }

        string cacheKey = address.ToString();
        Task<string?> task = HostnameCache.GetOrAdd(cacheKey, static lookupKey => ResolveHostnameAsync(lookupKey));
        return new LogField(WaitForTaskResult(task, cancellationToken) ?? string.Empty);
    }

    private TResult? TryGetTaskResult<TResult>(Task<TResult> task, string notificationKey, out bool pending)
    {
        pending = !task.IsCompleted;
        if (pending)
        {
            TrackPendingRefresh(notificationKey, task);
            return default;
        }

        if (task.IsCompletedSuccessfully)
        {
            return task.Result;
        }

        return default;
    }

    private void TrackPendingRefresh<TResult>(string notificationKey, Task<TResult> task)
    {
        if (!_pendingRefreshNotifications.TryAdd(notificationKey, 0))
        {
            return;
        }

        _ = task.ContinueWith(_completedTask =>
        {
            _pendingRefreshNotifications.TryRemove(notificationKey, out _);
            if (Volatile.Read(ref _disposed) == 0)
                _context.Invalidate();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static TResult? WaitForTaskResult<TResult>(Task<TResult> task, CancellationToken cancellationToken)
    {
        try
        {
            return task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return default;
        }
    }

    private static async Task<string?> ResolveHostnameAsync(string ipText)
    {
        if (!IPAddress.TryParse(ipText, out IPAddress? parsed))
        {
            return null;
        }

        bool entered = false;
        try
        {
            await HostnameLookupConcurrency.WaitAsync().ConfigureAwait(false);
            entered = true;
            IPHostEntry hostEntry = await Dns.GetHostEntryAsync(parsed).WaitAsync(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
            string hostName = hostEntry.HostName?.Trim().TrimEnd('.') ?? string.Empty;
            return hostName.Length == 0 ? null : hostName;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (entered)
            {
                HostnameLookupConcurrency.Release();
            }
        }
    }

    private static bool TryExtractIpAddress(string rawValue, out IPAddress? address)
    {
        address = null;
        string candidate = NormalizeText(rawValue).TrimEnd(',', ';');
        if (candidate.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(candidate, out address))
        {
            return true;
        }

        if (candidate.StartsWith("[", StringComparison.Ordinal))
        {
            int closingBracket = candidate.IndexOf(']');
            if (closingBracket > 1)
            {
                string bracketedAddress = candidate[1..closingBracket];
                if (IPAddress.TryParse(bracketedAddress, out address))
                {
                    return true;
                }
            }
        }

        int separatorIndex = candidate.LastIndexOf(':');
        if (separatorIndex > 0 && separatorIndex < candidate.Length - 1 && candidate.Count(static character => character == ':') == 1)
        {
            string withoutPort = candidate[..separatorIndex];
            if (IPAddress.TryParse(withoutPort, out address))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractPort(string rawValue, out int port)
    {
        port = 0;
        string candidate = NormalizeText(rawValue);
        if (candidate.Length == 0)
        {
            return false;
        }

        if (int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is >= 0 and <= 65535)
        {
            return true;
        }

        if (candidate.StartsWith("[", StringComparison.Ordinal))
        {
            int closingBracket = candidate.LastIndexOf(']');
            if (closingBracket > 0 && closingBracket < candidate.Length - 2 && candidate[closingBracket + 1] == ':')
            {
                string suffix = candidate[(closingBracket + 2)..];
                return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is >= 0 and <= 65535;
            }
        }

        int separatorIndex = candidate.LastIndexOf(':');
        if (separatorIndex > 0 && separatorIndex < candidate.Length - 1)
        {
            string suffix = candidate[(separatorIndex + 1)..];
            return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is >= 0 and <= 65535;
        }

        return false;
    }

    private static bool IsPrivateOrReservedIpv4(byte[] bytes)
    {
        if (bytes.Length != 4)
        {
            return true;
        }

        return bytes[0] switch
        {
            0 => true,
            10 => true,
            127 => true,
            >= 224 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            192 when bytes[1] == 168 => true,
            100 when bytes[1] >= 64 && bytes[1] <= 127 => true,
            _ => false
        };
    }

    private static bool IsPrivateOrReservedIpv6(byte[] bytes)
    {
        if (bytes.Length != 16)
        {
            return true;
        }

        return (bytes[0] & 0xfe) == 0xfc || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80);
    }

    private static string ResolveIpPrivate(string rawValue)
    {
        if (!TryExtractIpAddress(rawValue, out IPAddress? address) || address is null)
        {
            return string.Empty;
        }

        byte[] bytes = address.GetAddressBytes();
        bool isPrivate = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IsPrivateOrReservedIpv4(bytes) || IPAddress.IsLoopback(address) : IsPrivateOrReservedIpv6(bytes) || IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal;
        return isPrivate ? "yes" : "no";
    }

    private static string ResolveIpVersion(string rawValue)
    {
        if (!TryExtractIpAddress(rawValue, out IPAddress? address) || address is null)
        {
            return string.Empty;
        }

        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "ipv4" : "ipv6";
    }

    private static string ResolvePortService(string rawValue)
    {
        return TryExtractPort(rawValue, out int port) ? PortServiceNames.TryGetValue(port, out string? serviceName) ? serviceName : "other" : string.Empty;
    }

    private static string ResolveCidr(string rawValue, int prefix)
    {
        if (!TryExtractIpAddress(rawValue, out IPAddress? address) || address is null)
        {
            return string.Empty;
        }

        byte[] bytes = address.GetAddressBytes();
        int maxPrefix = bytes.Length * 8;
        if (prefix < 0 || prefix > maxPrefix)
        {
            return string.Empty;
        }

        byte[] networkBytes = (byte[])bytes.Clone();
        int fullBytes = prefix / 8;
        int remainingBits = prefix % 8;
        if (remainingBits > 0 && fullBytes < networkBytes.Length)
        {
            networkBytes[fullBytes] &= (byte)(0xff << (8 - remainingBits));
            fullBytes++;
        }

        for (int index = fullBytes; index < networkBytes.Length; index++)
        {
            networkBytes[index] = 0;
        }

        return $"{new IPAddress(networkBytes)}/{prefix}";
    }

    private static string NormalizeText(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static readonly IReadOnlyDictionary<int, string> PortServiceNames = new Dictionary<int, string>
    {
        [20] = "ftp-data",
        [21] = "ftp",
        [22] = "ssh",
        [23] = "telnet",
        [25] = "smtp",
        [53] = "dns",
        [67] = "dhcp-server",
        [68] = "dhcp-client",
        [80] = "http",
        [110] = "pop3",
        [123] = "ntp",
        [143] = "imap",
        [161] = "snmp",
        [389] = "ldap",
        [443] = "https",
        [465] = "smtps",
        [514] = "syslog",
        [587] = "submission",
        [631] = "ipp",
        [993] = "imaps",
        [995] = "pop3s",
        [1433] = "mssql",
        [1521] = "oracle",
        [2049] = "nfs",
        [2375] = "docker",
        [2376] = "docker-tls",
        [3306] = "mysql",
        [3389] = "rdp",
        [5432] = "postgres",
        [5672] = "amqp",
        [6379] = "redis",
        [6443] = "kubernetes-api",
        [8080] = "http-alt",
        [8443] = "https-alt",
        [9200] = "elasticsearch",
        [9300] = "elasticsearch-transport",
        [11211] = "memcached",
        [27017] = "mongodb"
    };
    private readonly record struct ResolvedVirtualField(LogField Field, bool Pending = false)
    {
        public static implicit operator ResolvedVirtualField(LogField field) => new(field);
    }

    private sealed class VirtualColumnPlan
    {
        public VirtualColumnPlan(ColumnKind kind, IReadOnlyList<int> sourceFieldIndices, bool UsesBackgroundLookup, Func<VirtualColumnRuntime, LogRecord, ResolvedVirtualField> resolve)
        {
            Kind = kind;
            SourceFieldIndices = sourceFieldIndices.ToArray();
            this.UsesBackgroundLookup = UsesBackgroundLookup;
            Resolve = resolve;
        }

        public ColumnKind Kind { get; }
        public int[] SourceFieldIndices { get; }
        public bool UsesBackgroundLookup { get; set; }
        public Func<VirtualColumnRuntime, LogRecord, ResolvedVirtualField> Resolve { get; }
    }
}
