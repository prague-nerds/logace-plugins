using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Responses;
using Alignment = LogAce.PluginApi.LogColumnAlignment;
using LogAce.PluginApi;
namespace LogAce.Columns.geoip;
internal sealed partial class VirtualColumnRuntime : IVirtualColumnEvaluator
{
    private static readonly HttpClient GeoIpHttpClient = CreateGeoIpHttpClient();
    private static readonly SemaphoreSlim GeoIpLookupConcurrency = new(6, 6);
    private static readonly ConcurrentDictionary<string, Task<GeoIpLookupResult?>> GeoIpCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object GeoIpDatabaseReadersSync = new();
    private static GeoIpDatabaseReaders? _geoIpDatabaseReaders;
    private static bool _geoIpDatabaseReadersInitialized;
    private static string? _configuredGeoIpCityDatabasePath;
    private static string? _configuredGeoIpAsnDatabasePath;
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
    internal static void CloseDatabases()
    {
        lock (GeoIpDatabaseReadersSync)
        {
            DisposeGeoIpDatabaseReaders(_geoIpDatabaseReaders);
            _geoIpDatabaseReaders = null;
            _geoIpDatabaseReadersInitialized = false;
        }
        GeoIpCache.Clear();
        GeoIpHttpClient.Dispose();
    }

    public static void ConfigureGeoIpDatabasePaths(string? cityDatabasePath, string? asnDatabasePath)
    {
        string? normalizedCityPath = ColumnDataPaths.NormalizeConfiguredPath(cityDatabasePath);
        string? normalizedAsnPath = ColumnDataPaths.NormalizeConfiguredPath(asnDatabasePath);
        GeoIpDatabaseReaders? readersToDispose = null;
        bool changed;
        lock (GeoIpDatabaseReadersSync)
        {
            changed = !string.Equals(_configuredGeoIpCityDatabasePath, normalizedCityPath, StringComparison.Ordinal) || !string.Equals(_configuredGeoIpAsnDatabasePath, normalizedAsnPath, StringComparison.Ordinal);
            if (!changed)
            {
                return;
            }

            _configuredGeoIpCityDatabasePath = normalizedCityPath;
            _configuredGeoIpAsnDatabasePath = normalizedAsnPath;
            readersToDispose = _geoIpDatabaseReaders;
            _geoIpDatabaseReaders = null;
            _geoIpDatabaseReadersInitialized = false;
        }

        GeoIpCache.Clear();
        DisposeGeoIpDatabaseReaders(readersToDispose);
    }

    private static LogColumnDefinition CreateVirtualColumnDefinition(ColumnAppearance column, ColumnKind kind)
    {
        return kind switch
        {
            ColumnKind.GeoIp => CreateTextColumnDefinition(column, 20, 128, 14),
            ColumnKind.GeoIpCountry => CreateTextColumnDefinition(column, 12, 32, 10),
            ColumnKind.GeoIpRegion => CreateTextColumnDefinition(column, 14, 40, 10),
            ColumnKind.GeoIpCity => CreateTextColumnDefinition(column, 14, 40, 10),
            ColumnKind.GeoIpAsn => CreateTextColumnDefinition(column, 8, 16, 6, Alignment.End),
            ColumnKind.GeoIpOrg => CreateTextColumnDefinition(column, 18, 96, 12),
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
            ColumnKind.GeoIp => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveGeoIpField(provider.GetSourceField(record, sourceIndices[0]), static result => FormatGeoIpSummary(result))),
            ColumnKind.GeoIpCountry => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveGeoIpField(provider.GetSourceField(record, sourceIndices[0]), static result => result?.Country ?? string.Empty)),
            ColumnKind.GeoIpRegion => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveGeoIpField(provider.GetSourceField(record, sourceIndices[0]), static result => result?.Region ?? string.Empty)),
            ColumnKind.GeoIpCity => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveGeoIpField(provider.GetSourceField(record, sourceIndices[0]), static result => result?.City ?? string.Empty)),
            ColumnKind.GeoIpAsn => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveGeoIpField(provider.GetSourceField(record, sourceIndices[0]), static result => result?.Asn ?? string.Empty)),
            ColumnKind.GeoIpOrg => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveGeoIpField(provider.GetSourceField(record, sourceIndices[0]), static result => result?.Org ?? string.Empty)),
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

    private ResolvedVirtualField ResolveGeoIpField(LogField sourceField, Func<GeoIpLookupResult?, string> selector)
    {
        if (!TryExtractIpAddress(sourceField.RawText ?? string.Empty, out IPAddress? address) || address is null)
        {
            return new LogField(string.Empty);
        }

        string cacheKey = address.ToString();
        Task<GeoIpLookupResult?> task = GeoIpCache.GetOrAdd(cacheKey, lookupKey => ResolveGeoIpAsync(lookupKey, address));
        GeoIpLookupResult? result = TryGetTaskResult(task, $"geoip:{cacheKey}", out bool pending);
        return new ResolvedVirtualField(new LogField(selector(result) ?? string.Empty), pending);
    }

    private LogField ResolveMaterializedVirtualField(LogRecord record, VirtualColumnPlan plan, CancellationToken cancellationToken)
    {
        if (!plan.UsesBackgroundLookup)
        {
            return ResolveVirtualField(record, plan).Field;
        }

        return plan.Kind switch
        {
            ColumnKind.GeoIp => ResolveMaterializedGeoIpField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => FormatGeoIpSummary(result), cancellationToken),
            ColumnKind.GeoIpCountry => ResolveMaterializedGeoIpField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result?.Country ?? string.Empty, cancellationToken),
            ColumnKind.GeoIpRegion => ResolveMaterializedGeoIpField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result?.Region ?? string.Empty, cancellationToken),
            ColumnKind.GeoIpCity => ResolveMaterializedGeoIpField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result?.City ?? string.Empty, cancellationToken),
            ColumnKind.GeoIpAsn => ResolveMaterializedGeoIpField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result?.Asn ?? string.Empty, cancellationToken),
            ColumnKind.GeoIpOrg => ResolveMaterializedGeoIpField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result?.Org ?? string.Empty, cancellationToken),
            _ => ResolveVirtualField(record, plan).Field
        };
    }

    private LogField ResolveMaterializedGeoIpField(LogField sourceField, Func<GeoIpLookupResult?, string> selector, CancellationToken cancellationToken)
    {
        if (!TryExtractIpAddress(sourceField.RawText ?? string.Empty, out IPAddress? address) || address is null)
        {
            return new LogField(string.Empty);
        }

        string cacheKey = address.ToString();
        Task<GeoIpLookupResult?> task = GeoIpCache.GetOrAdd(cacheKey, lookupKey => ResolveGeoIpAsync(lookupKey, address));
        return new LogField(selector(WaitForTaskResult(task, cancellationToken)) ?? string.Empty);
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

    private static async Task<GeoIpLookupResult?> ResolveGeoIpAsync(string ipText, IPAddress address)
    {
        if (!IsGeoIpEligible(address))
        {
            return null;
        }

        GeoIpLookupResult? localResult = ResolveGeoIpFromLocalDatabase(address);
        if (localResult is not null)
        {
            return localResult;
        }

        bool entered = false;
        try
        {
            await GeoIpLookupConcurrency.WaitAsync().ConfigureAwait(false);
            entered = true;
            string payload = await GeoIpHttpClient.GetStringAsync($"https://ipwho.is/{Uri.EscapeDataString(ipText)}").ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("success", out JsonElement successElement) && successElement.ValueKind == JsonValueKind.False)
            {
                return null;
            }

            string country = ReadString(root, "country");
            string region = ReadString(root, "region");
            string city = ReadString(root, "city");
            string asn = string.Empty;
            string org = string.Empty;
            if (root.TryGetProperty("connection", out JsonElement connection) && connection.ValueKind == JsonValueKind.Object)
            {
                asn = ReadNumericOrString(connection, "asn");
                org = ReadString(connection, "org");
            }

            if (country.Length == 0 && region.Length == 0 && city.Length == 0 && asn.Length == 0 && org.Length == 0)
            {
                return null;
            }

            return new GeoIpLookupResult(country, region, city, asn, org);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (entered)
            {
                GeoIpLookupConcurrency.Release();
            }
        }
    }

    private static HttpClient CreateGeoIpHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LogAce", "1.0"));
        return client;
    }

    private static GeoIpLookupResult? ResolveGeoIpFromLocalDatabase(IPAddress address)
    {
        lock (GeoIpDatabaseReadersSync)
        {
            GeoIpDatabaseReaders? readers = GetGeoIpDatabaseReadersCore();
            if (readers is null)
            {
                return null;
            }

            string country = string.Empty;
            string region = string.Empty;
            string city = string.Empty;
            string asn = string.Empty;
            string org = string.Empty;
            if (readers.CityReader is not null)
            {
                try
                {
                    CityResponse cityResponse = readers.CityReader.City(address);
                    country = cityResponse.Country?.Name?.Trim() ?? string.Empty;
                    region = cityResponse.MostSpecificSubdivision?.Name?.Trim() ?? string.Empty;
                    city = cityResponse.City?.Name?.Trim() ?? string.Empty;
                }
                catch
                {
                }
            }

            if (readers.AsnReader is not null)
            {
                try
                {
                    AsnResponse asnResponse = readers.AsnReader.Asn(address);
                    asn = asnResponse.AutonomousSystemNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                    org = asnResponse.AutonomousSystemOrganization?.Trim() ?? string.Empty;
                }
                catch
                {
                }
            }

            if (country.Length == 0 && region.Length == 0 && city.Length == 0 && asn.Length == 0 && org.Length == 0)
            {
                return null;
            }

            return new GeoIpLookupResult(country, region, city, asn, org);
        }
    }

    private static GeoIpDatabaseReaders? GetGeoIpDatabaseReadersCore()
    {
        if (_geoIpDatabaseReadersInitialized)
        {
            return _geoIpDatabaseReaders;
        }

        _geoIpDatabaseReaders = TryCreateGeoIpDatabaseReaders();
        _geoIpDatabaseReadersInitialized = true;
        return _geoIpDatabaseReaders;
    }

    private static GeoIpDatabaseReaders? TryCreateGeoIpDatabaseReaders()
    {
        DatabaseReader? cityReader = TryOpenGeoIpDatabaseReader(ColumnDataPaths.ResolveGeoIpCityDatabaseReadPath(_configuredGeoIpCityDatabasePath));
        DatabaseReader? asnReader = TryOpenGeoIpDatabaseReader(ColumnDataPaths.ResolveGeoIpAsnDatabaseReadPath(_configuredGeoIpAsnDatabasePath));
        if (cityReader is null && asnReader is null)
        {
            return null;
        }

        return new GeoIpDatabaseReaders(cityReader, asnReader);
    }

    private static DatabaseReader? TryOpenGeoIpDatabaseReader(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return new DatabaseReader(path, FileAccessMode.MemoryMapped);
        }
        catch
        {
            return null;
        }
    }

    private static void DisposeGeoIpDatabaseReaders(GeoIpDatabaseReaders? readers)
    {
        try
        {
            readers?.CityReader?.Dispose();
        }
        catch
        {
        }

        try
        {
            readers?.AsnReader?.Dispose();
        }
        catch
        {
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

    private static string FormatGeoIpSummary(GeoIpLookupResult? result)
    {
        if (result is null)
        {
            return string.Empty;
        }

        string[] parts = [result.Country, result.Region, result.City, result.Org];
        return string.Join(" / ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : string.Empty;
    }

    private static string ReadNumericOrString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            _ => string.Empty
        };
    }

    private sealed record GeoIpLookupResult(string Country, string Region, string City, string Asn, string Org);
    private sealed record GeoIpDatabaseReaders(DatabaseReader? CityReader, DatabaseReader? AsnReader);
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
