using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alignment = LogAce.PluginApi.LogColumnAlignment;
using UaDetector.Abstractions.Models;
using UaDetector.Parsers;
using UaDetectorParser = UaDetector.UaDetector;
using LogAce.PluginApi;
namespace LogAce.Columns.user_agent;
internal sealed partial class VirtualColumnRuntime : IVirtualColumnEvaluator
{
    private static readonly SemaphoreSlim UserAgentLookupConcurrency = new(Math.Max(2, Environment.ProcessorCount / 2), Math.Max(2, Environment.ProcessorCount / 2));
    private static ConcurrentDictionary<string, Task<UserAgentLookupResult?>> UserAgentCache = new(StringComparer.Ordinal);
    private static readonly UaDetectorParser UserAgentParser = new();
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
    public static void InvalidateBotDatabases()
    {
        // Swap the dictionary so lookups racing with an update cannot repopulate the new cache.
        Interlocked.Exchange(ref UserAgentCache, new ConcurrentDictionary<string, Task<UserAgentLookupResult?>>(StringComparer.Ordinal));
    }

    private static LogColumnDefinition CreateVirtualColumnDefinition(ColumnAppearance column, ColumnKind kind)
    {
        return kind switch
        {
            ColumnKind.UaBot => CreateTextColumnDefinition(column, 8, 12, 6),
            ColumnKind.UaBotName => CreateTextColumnDefinition(column, 16, 64, 12),
            ColumnKind.UaBotCategory => CreateTextColumnDefinition(column, 12, 32, 10),
            ColumnKind.UaBrowser => CreateTextColumnDefinition(column, 14, 40, 10),
            ColumnKind.UaBrowserVersion => CreateTextColumnDefinition(column, 8, 20, 6),
            ColumnKind.UaOs => CreateTextColumnDefinition(column, 14, 40, 10),
            ColumnKind.UaDevice => CreateTextColumnDefinition(column, 14, 48, 10),
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
            ColumnKind.UaBot => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveUserAgentField(provider.GetSourceField(record, sourceIndices[0]), static result => result is null ? string.Empty : result.IsBot ? "yes" : "no")),
            ColumnKind.UaBotName => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveUserAgentField(provider.GetSourceField(record, sourceIndices[0]), static result => result?.BotName ?? string.Empty)),
            ColumnKind.UaBotCategory => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveUserAgentField(provider.GetSourceField(record, sourceIndices[0]), static result => result?.BotCategory ?? string.Empty)),
            ColumnKind.UaBrowser => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveUserAgentField(provider.GetSourceField(record, sourceIndices[0]), static result => result?.BrowserName ?? string.Empty)),
            ColumnKind.UaBrowserVersion => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveUserAgentField(provider.GetSourceField(record, sourceIndices[0]), static result => result?.BrowserVersion ?? string.Empty)),
            ColumnKind.UaOs => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveUserAgentField(provider.GetSourceField(record, sourceIndices[0]), static result => result?.OsName ?? string.Empty)),
            ColumnKind.UaDevice => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: true, (provider, record) => provider.ResolveUserAgentField(provider.GetSourceField(record, sourceIndices[0]), static result => result?.DeviceName ?? string.Empty)),
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

    private ResolvedVirtualField ResolveUserAgentField(LogField sourceField, Func<UserAgentLookupResult?, string> selector)
    {
        string userAgentText = NormalizeText(sourceField.RawText);
        if (userAgentText.Length == 0)
        {
            return new LogField(string.Empty);
        }

        Task<UserAgentLookupResult?> task = Volatile.Read(ref UserAgentCache).GetOrAdd(userAgentText, static lookupKey => ResolveUserAgentAsync(lookupKey));
        UserAgentLookupResult? result = TryGetTaskResult(task, $"ua:{userAgentText}", out bool pending);
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
            ColumnKind.UaBot => ResolveMaterializedUserAgentField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result is null ? string.Empty : result.IsBot ? "yes" : "no", cancellationToken),
            ColumnKind.UaBotName => ResolveMaterializedUserAgentField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result?.BotName ?? string.Empty, cancellationToken),
            ColumnKind.UaBotCategory => ResolveMaterializedUserAgentField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result?.BotCategory ?? string.Empty, cancellationToken),
            ColumnKind.UaBrowser => ResolveMaterializedUserAgentField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result?.BrowserName ?? string.Empty, cancellationToken),
            ColumnKind.UaBrowserVersion => ResolveMaterializedUserAgentField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result?.BrowserVersion ?? string.Empty, cancellationToken),
            ColumnKind.UaOs => ResolveMaterializedUserAgentField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result?.OsName ?? string.Empty, cancellationToken),
            ColumnKind.UaDevice => ResolveMaterializedUserAgentField(GetSourceField(record, plan.SourceFieldIndices[0]), static result => result?.DeviceName ?? string.Empty, cancellationToken),
            _ => ResolveVirtualField(record, plan).Field
        };
    }

    private LogField ResolveMaterializedUserAgentField(LogField sourceField, Func<UserAgentLookupResult?, string> selector, CancellationToken cancellationToken)
    {
        string userAgentText = NormalizeText(sourceField.RawText);
        if (userAgentText.Length == 0)
        {
            return new LogField(string.Empty);
        }

        Task<UserAgentLookupResult?> task = Volatile.Read(ref UserAgentCache).GetOrAdd(userAgentText, static lookupKey => ResolveUserAgentAsync(lookupKey));
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

    private static Task<UserAgentLookupResult?> ResolveUserAgentAsync(string userAgentText)
    {
        return Task.Run(async () =>
        {
            bool entered = false;
            try
            {
                await UserAgentLookupConcurrency.WaitAsync().ConfigureAwait(false);
                entered = true;
                BotUserAgentDatabase? botDatabase = BotDatabases.UserAgents.Database;
                BotUserAgentMatch? bot = botDatabase?.Match(userAgentText);
                UserAgentParser.TryParse(userAgentText, out UserAgentInfo? parsed);
                if (parsed is null && bot is null)
                {
                    return null;
                }

                bool isBot = botDatabase is null ? parsed?.Bot is not null : bot is not null;
                string botName = botDatabase is null ? parsed?.Bot?.Name?.Trim() ?? string.Empty : bot?.Name ?? string.Empty;
                string botCategory = botDatabase is null ? parsed?.Bot?.Category?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty : bot?.Category.ToLowerInvariant() ?? string.Empty;
                string browserName = parsed?.Browser?.Name?.Trim() ?? string.Empty;
                string browserVersion = parsed?.Browser?.Version?.Trim() ?? string.Empty;
                string osName = BuildOsDisplay(parsed?.Os);
                string deviceName = BuildDeviceDisplay(parsed?.Device);
                if (!isBot && botName.Length == 0 && botCategory.Length == 0 && browserName.Length == 0 && browserVersion.Length == 0 && osName.Length == 0 && deviceName.Length == 0)
                {
                    return null;
                }

                return new UserAgentLookupResult(isBot, botName, botCategory, browserName, browserVersion, osName, deviceName);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (entered)
                {
                    UserAgentLookupConcurrency.Release();
                }
            }
        });
    }

    private static string BuildOsDisplay(OsInfo? os)
    {
        if (os is null)
        {
            return string.Empty;
        }

        return string.Join(" ", new[] { os.Name?.Trim(), os.Version?.Trim() }.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildDeviceDisplay(DeviceInfo? device)
    {
        if (device is null)
        {
            return string.Empty;
        }

        string brand = device.Brand?.Name?.Trim() ?? string.Empty;
        string model = device.Model?.Trim() ?? string.Empty;
        string type = device.Type?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
        string combined = string.Join(" ", new[] { brand, model }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        if (combined.Length > 0)
        {
            return combined;
        }

        return type;
    }

    private static string NormalizeText(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private sealed record UserAgentLookupResult(bool IsBot, string BotName, string BotCategory, string BrowserName, string BrowserVersion, string OsName, string DeviceName);
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
