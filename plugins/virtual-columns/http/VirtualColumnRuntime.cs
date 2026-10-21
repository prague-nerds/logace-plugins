using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alignment = LogAce.PluginApi.LogColumnAlignment;
using LogAce.PluginApi;
namespace LogAce.Columns.http;
internal sealed partial class VirtualColumnRuntime : IVirtualColumnEvaluator
{
    private static readonly Regex RequestLineRegex = new(@"^\s*[A-Z]+\s+(?<target>\S+)\s+HTTP/\d(?:\.\d)?\s*$", RegexOptions.Compiled);
    private static readonly Regex StatusCodeRegex = new(@"\b(?<code>[1-5]\d\d)\b", RegexOptions.Compiled);
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
            ColumnKind.UrlRoute => CreateTextColumnDefinition(column, 18, 160, 12),
            ColumnKind.ResourceType => CreateTextColumnDefinition(column, 10, 16, 8),
            ColumnKind.TrafficSource => CreateTextColumnDefinition(column, 12, 20, 10),
            ColumnKind.HttpStatusClass => CreateTextColumnDefinition(column, 4, 8, 4, Alignment.Center),
            ColumnKind.UrlHost => CreateTextColumnDefinition(column, 16, 80, 12),
            ColumnKind.UrlPath => CreateTextColumnDefinition(column, 18, 160, 12),
            ColumnKind.UrlQuery => CreateTextColumnDefinition(column, 18, 160, 12),
            ColumnKind.UrlExtension => CreateTextColumnDefinition(column, 8, 20, 6),
            ColumnKind.ReferrerHost => CreateTextColumnDefinition(column, 16, 80, 12),
            ColumnKind.QueryParam => CreateTextColumnDefinition(column, 12, 128, 10),
            ColumnKind.Header => CreateTextColumnDefinition(column, 12, 128, 10),
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
            ColumnKind.HttpStatusClass => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ClassifyHttpStatus(provider.GetSourceText(record, sourceIndices[0])))),
            ColumnKind.UrlHost => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ResolveUrlHost(provider.GetSourceText(record, sourceIndices[0])))),
            ColumnKind.UrlPath => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ResolveUrlPath(provider.GetSourceText(record, sourceIndices[0])))),
            ColumnKind.UrlQuery => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ResolveUrlQuery(provider.GetSourceText(record, sourceIndices[0])))),
            ColumnKind.UrlExtension => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ResolveUrlExtension(provider.GetSourceText(record, sourceIndices[0])))),
            ColumnKind.ReferrerHost => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ResolveUrlHost(provider.GetSourceText(record, sourceIndices[0])))),
            ColumnKind.QueryParam => CreateQueryParameterPlan(kind, sourceIndices, parameters),
            ColumnKind.Header => CreateHeaderPlan(kind, sourceIndices, parameters),
            _ => new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, static (_, _) => new LogField(string.Empty))};
    }

    private static VirtualColumnPlan CreateQueryParameterPlan(ColumnKind kind, int[] sourceIndices, IReadOnlyDictionary<string, string> parameters)
    {
        string parameterName = GetRequiredParameter(parameters, "name", kind);
        return new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ResolveQueryParameter(provider.GetSourceText(record, sourceIndices[0]), parameterName)));
    }

    private static VirtualColumnPlan CreateHeaderPlan(ColumnKind kind, int[] sourceIndices, IReadOnlyDictionary<string, string> parameters)
    {
        string headerName = GetRequiredParameter(parameters, "name", kind);
        return new VirtualColumnPlan(kind, sourceIndices, UsesBackgroundLookup: false, (provider, record) => new LogField(ResolveHeaderValue(provider.GetSourceText(record, sourceIndices[0]), headerName)));
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

    private static string ClassifyHttpStatus(string rawValue)
    {
        Match match = StatusCodeRegex.Match(NormalizeText(rawValue));
        if (!match.Success || !int.TryParse(match.Groups["code"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int statusCode))
        {
            return string.Empty;
        }

        return $"{statusCode / 100}xx";
    }

    private static string ResolveUrlHost(string rawValue)
    {
        return TryExtractUri(rawValue, out Uri? uri, out bool isAbsolute) && isAbsolute && uri is not null ? uri.Host.Trim().ToLowerInvariant() : string.Empty;
    }

    private static string ResolveUrlPath(string rawValue)
    {
        if (!TryExtractUri(rawValue, out Uri? uri, out _) || uri is null)
        {
            return string.Empty;
        }

        string path = Uri.UnescapeDataString(uri.AbsolutePath).Trim();
        if (path == "/")
        {
            return string.Empty;
        }

        return path;
    }

    private static string ResolveUrlQuery(string rawValue)
    {
        return ExtractQueryString(rawValue);
    }

    private static string ResolveUrlExtension(string rawValue)
    {
        string path = ResolveUrlPath(rawValue);
        if (path.Length == 0)
        {
            return string.Empty;
        }

        return Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    }

    private static bool TryExtractUri(string rawValue, out Uri? uri, out bool isAbsolute)
    {
        uri = null;
        isAbsolute = false;
        string target = ExtractRequestTarget(rawValue);
        if (target.Length == 0)
        {
            return false;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out uri))
        {
            isAbsolute = true;
            return true;
        }

        if (target.StartsWith("/", StringComparison.Ordinal))
        {
            return Uri.TryCreate($"http://placeholder{target}", UriKind.Absolute, out uri);
        }

        if (target.StartsWith("?", StringComparison.Ordinal))
        {
            return Uri.TryCreate($"http://placeholder/{target}", UriKind.Absolute, out uri);
        }

        if (!target.Contains(' ') && target.Contains('='))
        {
            return Uri.TryCreate($"http://placeholder/?{target}", UriKind.Absolute, out uri);
        }

        return false;
    }

    private static string ExtractRequestTarget(string rawValue)
    {
        string candidate = NormalizeText(rawValue);
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        Match requestLineMatch = RequestLineRegex.Match(candidate);
        if (requestLineMatch.Success)
        {
            return requestLineMatch.Groups["target"].Value.Trim();
        }

        return candidate;
    }

    private static string ExtractQueryString(string rawValue)
    {
        string target = ExtractRequestTarget(rawValue);
        if (target.Length == 0)
        {
            return string.Empty;
        }

        if (target.StartsWith("?", StringComparison.Ordinal))
        {
            return target[1..];
        }

        int queryIndex = target.IndexOf('?');
        if (queryIndex >= 0)
        {
            string query = target[(queryIndex + 1)..];
            int hashIndex = query.IndexOf('#');
            return hashIndex >= 0 ? query[..hashIndex] : query;
        }

        return !target.Contains(' ') && target.Contains('=') ? target : string.Empty;
    }

    private static string ResolveQueryParameter(string rawValue, string parameterName)
    {
        string query = ExtractQueryString(rawValue);
        if (query.Length == 0)
        {
            return string.Empty;
        }

        foreach (string segment in query.Split(['&', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = segment.IndexOf('=');
            string rawName = separatorIndex >= 0 ? segment[..separatorIndex] : segment;
            string rawParameterValue = separatorIndex >= 0 ? segment[(separatorIndex + 1)..] : string.Empty;
            string decodedName = Uri.UnescapeDataString(rawName.Replace('+', ' ')).Trim();
            if (!string.Equals(decodedName, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Uri.UnescapeDataString(rawParameterValue.Replace('+', ' ')).Trim();
        }

        return string.Empty;
    }

    private static string ResolveHeaderValue(string rawValue, string headerName)
    {
        string normalized = rawValue.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        foreach (string segment in normalized.Split(['\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = segment.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string candidateName = segment[..separatorIndex].Trim();
            if (!string.Equals(candidateName, headerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return segment[(separatorIndex + 1)..].Trim();
        }

        return string.Empty;
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

    private static bool IsExtendedKind(ColumnKind kind) => kind is ColumnKind.UrlRoute or ColumnKind.ResourceType or ColumnKind.TrafficSource;
    private static VirtualColumnPlan CreateExtendedPlan(ColumnKind kind, int[] sources, Dictionary<string, string>? parameters, string[] sourceNames)
    {
        parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Func<LogField[], LogField> transform = VirtualColumnTransforms.Create(kind, parameters, sourceNames);
        return new VirtualColumnPlan(kind, sources, UsesBackgroundLookup: false, (provider, record) => transform(sources.Select(source => provider.GetSourceField(record, source)).ToArray()));
    }
}
