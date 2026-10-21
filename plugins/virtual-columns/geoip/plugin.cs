using LogAce.PluginApi;
namespace LogAce.Columns.geoip;
public sealed class ColumnsPlugin : ILogAceVirtualColumnProviderPlugin, ILogAceVirtualColumnEditor, ILogAcePluginWithHostServices, ILogAcePluginSettingsConsumer, IDisposable
{
    public PluginMetadata Metadata { get; } = new("geoip.columns", "GeoIP", "Enrich IP addresses with location, ASN and organization.", PluginKind.VirtualColumnProvider, Capabilities: ["ProvideVirtualColumns", "UsesNetwork", "ReadsLocalFiles"]);
    public IReadOnlyList<VirtualColumnDefinition> Columns { get; } = [
        new("geoip", "geoip", "Network | Country, region and city for an IP address.", ["geoip", "geo"], "No extra parameters required."),
        new("geoip-country", "geoip-country", "Network | Country for an IP address.", ["geoip-country", "country"], "No extra parameters required."),
        new("geoip-region", "geoip-region", "Network | Region for an IP address.", ["geoip-region", "region"], "No extra parameters required."),
        new("geoip-city", "geoip-city", "Network | City for an IP address.", ["geoip-city", "city"], "No extra parameters required."),
        new("geoip-asn", "geoip-asn", "Network | Autonomous system number for an IP address.", ["geoip-asn", "asn"], "No extra parameters required."),
        new("geoip-org", "geoip-org", "Network | Network organization for an IP address.", ["geoip-org", "org", "organization"], "No extra parameters required.")
    ];
    private static readonly IReadOnlyDictionary<string, ColumnKind> Kinds = new Dictionary<string, ColumnKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["geoip"] = ColumnKind.GeoIp,
        ["geoip-country"] = ColumnKind.GeoIpCountry,
        ["geoip-region"] = ColumnKind.GeoIpRegion,
        ["geoip-city"] = ColumnKind.GeoIpCity,
        ["geoip-asn"] = ColumnKind.GeoIpAsn,
        ["geoip-org"] = ColumnKind.GeoIpOrg
    };
    private static readonly IReadOnlyDictionary<string, VirtualColumnEditorDefinition> Editors = new Dictionary<string, VirtualColumnEditorDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["geoip"] = new("Network", "", 1, 1, []),
        ["geoip-country"] = new("Network", "", 1, 1, []),
        ["geoip-region"] = new("Network", "", 1, 1, []),
        ["geoip-city"] = new("Network", "", 1, 1, []),
        ["geoip-asn"] = new("Network", "", 1, 1, []),
        ["geoip-org"] = new("Network", "", 1, 1, [])
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
    public ValueTask InitializeAsync(PluginHostServices services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ColumnDataPaths.Initialize(services);
        return ValueTask.CompletedTask;
    }
    public void ApplySettings(IReadOnlyDictionary<string, string> settings) => VirtualColumnRuntime.ConfigureGeoIpDatabasePaths(settings.GetValueOrDefault("cityDatabasePath"), settings.GetValueOrDefault("asnDatabasePath"));
    public void Dispose() => VirtualColumnRuntime.CloseDatabases();
}
