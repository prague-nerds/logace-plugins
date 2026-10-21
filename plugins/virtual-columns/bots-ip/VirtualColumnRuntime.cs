using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alignment = LogAce.PluginApi.LogColumnAlignment;
using LogAce.PluginApi;
namespace LogAce.Columns.bots_ip;
internal sealed partial class VirtualColumnRuntime : IVirtualColumnEvaluator
{
    private readonly ConcurrentDictionary<string, byte> _pendingRefreshNotifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly VirtualColumnEvaluationContext _context;
    private readonly VirtualColumnPlan _plan;
    private int _disposed;
    public VirtualColumnRuntime(ColumnKind kind, VirtualColumnConfiguration configuration, VirtualColumnEvaluationContext context)
    {
        _context = context;
        int[] sources = Enumerable.Range(0, configuration.SourceColumns.Count).ToArray();
        var parameters = configuration.Parameters.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        _plan = IsExtendedKind(kind) ? CreateExtendedPlan(kind, sources, parameters, configuration.SourceColumns.ToArray()) : CreateVirtualColumnPlan(kind, sources, parameters);
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
            ColumnKind.BotVerification => CreateTextColumnDefinition(column, 10, 16, 10),
            ColumnKind.BotIpName => CreateTextColumnDefinition(column, 16, 64, 12),
            ColumnKind.IpDatacenter => CreateTextColumnDefinition(column, 18, 96, 12),
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
            ColumnKind.BotIpName => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => ResolveIpDatabaseField(provider.GetSourceField(record, sourceIndices[0]), BotDatabases.IpRanges.Database)),
            ColumnKind.IpDatacenter => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => ResolveIpDatabaseField(provider.GetSourceField(record, sourceIndices[0]), BotDatabases.Datacenters.Database)),
            _ => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, static (_, _) => new LogField(string.Empty))};
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

    private LogField ResolveMaterializedVirtualField(LogRecord record, VirtualColumnPlan plan, CancellationToken cancellationToken)
    {
        if (!plan.UsesBackgroundLookup)
        {
            return ResolveVirtualField(record, plan).Field;
        }

        return plan.Kind switch
        {
            _ => ResolveVirtualField(record, plan).Field
        };
    }

    internal static LogField ResolveIpDatabaseField(LogField sourceField, BotIpDatabase? database)
    {
        if (!TryExtractIpAddress(sourceField.RawText ?? string.Empty, out IPAddress? address) || address is null)
        {
            return new LogField(string.Empty);
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        // Some provider feeds contain internal ranges; those cannot identify an owner globally.
        return new LogField(IsGeoIpEligible(address) ? database?.Match(address) ?? string.Empty : string.Empty);
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

    private static bool IsGeoIpEligible(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return !IsPrivateOrReservedIpv4(bytes);
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
        {
            return false;
        }

        return !IsPrivateOrReservedIpv6(bytes);
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

    private static string NormalizeText(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

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

    private static bool IsExtendedKind(ColumnKind kind) => kind is ColumnKind.BotVerification;
    private static VirtualColumnPlan CreateExtendedPlan(ColumnKind kind, int[] sources, Dictionary<string, string>? parameters, string[] sourceNames)
    {
        parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (kind == ColumnKind.BotVerification)
        {
            return new VirtualColumnPlan(kind, sources, UsesBackgroundLookup: true, (provider, record) =>
            {
                string userAgent = provider.GetSourceText(record, sources[0]);
                string rawIp = provider.GetSourceText(record, sources[1]);
                if (userAgent.Length == 0 || !TryExtractIpAddress(rawIp, out System.Net.IPAddress? ip) || ip is null)
                    return new LogField("unknown");
                if (ip.IsIPv4MappedToIPv6)
                    ip = ip.MapToIPv4();
                if (!IsGeoIpEligible(ip))
                    return new LogField("unknown");
                return new LogField(BotDatabases.IpRanges.Database?.Verify(userAgent, ip) ?? "unknown");
            });
        }

        throw new InvalidOperationException("Unsupported column kind.");
    }
}
