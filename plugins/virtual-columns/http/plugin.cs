using LogAce.PluginApi;
namespace LogAce.Columns.http;
public sealed class ColumnsPlugin : ILogAceVirtualColumnProviderPlugin, ILogAceVirtualColumnEditor
{
    public PluginMetadata Metadata { get; } = new("http.columns", "HTTP & URL", "Extract and classify HTTP requests, URLs, headers and traffic.", PluginKind.VirtualColumnProvider, Capabilities: ["ProvideVirtualColumns"]);
    public IReadOnlyList<VirtualColumnDefinition> Columns { get; } = [
        new("http-status-class", "http-status-class", "HTTP | Group status codes into 2xx, 3xx, 4xx and 5xx.", ["http-status-class", "status-class"], "No extra parameters required."),
        new("url-host", "url-host", "HTTP | Host from a URL.", ["url-host"], "No extra parameters required."),
        new("url-path", "url-path", "HTTP | URL path without the query string.", ["url-path"], "No extra parameters required."),
        new("url-query", "url-query", "HTTP | Query string from a URL.", ["url-query"], "No extra parameters required."),
        new("url-extension", "url-extension", "HTTP | File extension from a URL.", ["url-extension"], "No extra parameters required."),
        new("referrer-host", "referrer-host", "HTTP | Host from a referrer URL.", ["referrer-host", "referer-host"], "No extra parameters required."),
        new("query-param", "query-param", "HTTP | Read one named query parameter.", ["query-param"], "Required: name=sessionId"),
        new("header", "header", "HTTP | Read one named HTTP header.", ["header"], "Required: name=X-Forwarded-For"),
        new("url-route", "url-route", "HTTP | Group URL paths by replacing IDs, UUIDs and hashes.", ["url-route"], "Optional: pattern=^/orders/[0-9]+$ ; replacement=/orders/{id}"),
        new("resource-type", "resource-type", "HTTP | Classify resources by URL and Content-Type.", ["resource-type"], "Sources: URL[, Content-Type]. Optional: api-prefixes=/api/,/graphql"),
        new("traffic-source", "traffic-source", "HTTP | Internal, search, social or external traffic from a referrer.", ["traffic-source"], "Source: referrer. Required: internal=example.com ; Optional: search=google.com,bing.com ; social=x.com")
    ];
    private static readonly IReadOnlyDictionary<string, ColumnKind> Kinds = new Dictionary<string, ColumnKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["http-status-class"] = ColumnKind.HttpStatusClass,
        ["url-host"] = ColumnKind.UrlHost,
        ["url-path"] = ColumnKind.UrlPath,
        ["url-query"] = ColumnKind.UrlQuery,
        ["url-extension"] = ColumnKind.UrlExtension,
        ["referrer-host"] = ColumnKind.ReferrerHost,
        ["query-param"] = ColumnKind.QueryParam,
        ["header"] = ColumnKind.Header,
        ["url-route"] = ColumnKind.UrlRoute,
        ["resource-type"] = ColumnKind.ResourceType,
        ["traffic-source"] = ColumnKind.TrafficSource
    };
    private static readonly IReadOnlyDictionary<string, VirtualColumnEditorDefinition> Editors = new Dictionary<string, VirtualColumnEditorDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["http-status-class"] = new("HTTP", "", 1, 1, []),
        ["url-host"] = new("HTTP", "", 1, 1, []),
        ["url-path"] = new("HTTP", "", 1, 1, []),
        ["url-query"] = new("HTTP", "", 1, 1, []),
        ["url-extension"] = new("HTTP", "", 1, 1, []),
        ["referrer-host"] = new("HTTP", "", 1, 1, []),
        ["query-param"] = new("HTTP", "", 1, 1, [new("name", "Parameter / header name", "Example: sessionId", true)]),
        ["header"] = new("HTTP", "", 1, 1, [new("name", "Parameter / header name", "Example: X-Forwarded-For", true)]),
        ["url-route"] = new("HTTP", "", 1, 1, [new("pattern", "Regex pattern", "Example: ^/orders/[0-9]+$", false), new("replacement", "Replacement", "Example: /orders/{id}", false)]),
        ["resource-type"] = new("HTTP", "Sources: URL, then optional Content-Type.", 1, 2, [new("api-prefixes", "API path prefixes", "Example: /api/,/graphql", false)]),
        ["traffic-source"] = new("HTTP", "", 1, 1, [new("internal", "Your domains", "Example: example.com,example.org", true), new("search", "Search engine domains", "Example: google.com,bing.com", false), new("social", "Social network domains", "Example: x.com", false)])
    };
    public VirtualColumnEditorDefinition GetEditor(string kindId) => Editors[kindId];

    public PluginValidationResult Validate(VirtualColumnConfiguration configuration)
    {
        if (!Editors.TryGetValue(configuration.KindId, out var editor))
        {
            return PluginValidationResult.Error("Unknown virtual column kind '" + configuration.KindId + "'.");
        }
        if (configuration.SourceColumns.Count < editor.MinimumSources || configuration.SourceColumns.Count > editor.MaximumSources)
        {
            return PluginValidationResult.Error("Virtual column '" + configuration.ColumnName + "' requires " + editor.MinimumSources + (editor.MinimumSources == editor.MaximumSources ? "" : " to " + editor.MaximumSources) + " source columns.");
        }
        foreach (string key in configuration.Parameters.Keys)
        {
            if (!editor.Parameters.Any(parameter => parameter.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                return PluginValidationResult.Error("Unsupported parameter '" + key + "'.");
            }
        }
        foreach (var parameter in editor.Parameters.Where(parameter => parameter.Required))
        {
            if (!configuration.Parameters.TryGetValue(parameter.Key, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                return PluginValidationResult.Error("Required parameter '" + parameter.Key + "'.", parameter.Key);
            }
        }
        return PluginValidationResult.Success;
    }
    public LogColumnDefinition Describe(VirtualColumnConfiguration configuration) => VirtualColumnRuntime.Describe(Kinds[configuration.KindId], configuration);

    public IVirtualColumnEvaluator CreateEvaluator(VirtualColumnConfiguration configuration, VirtualColumnEvaluationContext context) => new VirtualColumnRuntime(Kinds[configuration.KindId], configuration, context);
}
