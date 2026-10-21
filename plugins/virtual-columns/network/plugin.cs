using LogAce.PluginApi;
namespace LogAce.Columns.network;
public sealed class ColumnsPlugin : ILogAceVirtualColumnProviderPlugin, ILogAceVirtualColumnEditor
{
    public PluginMetadata Metadata { get; } = new("network.columns", "Network", "Inspect IP addresses, networks and ports and resolve hostnames.", PluginKind.VirtualColumnProvider, Capabilities: ["ProvideVirtualColumns", "UsesNetwork"]);
    public IReadOnlyList<VirtualColumnDefinition> Columns { get; } = [
        new("hostname", "hostname", "Network | Resolve an IP address to a hostname using reverse DNS.", ["hostname", "host", "rdns"], "No extra parameters required."),
        new("ip-private", "ip-private", "Network | Identify private IP addresses.", ["ip-private"], "No extra parameters required."),
        new("ip-version", "ip-version", "Network | IPv4 or IPv6.", ["ip-version"], "No extra parameters required."),
        new("port-service", "port-service", "Network | Common service for a port number.", ["port-service"], "No extra parameters required."),
        new("cidr", "cidr", "Network | Group IP addresses by a network prefix.", ["cidr"], "Required: prefix=24")
    ];
    private static readonly IReadOnlyDictionary<string, ColumnKind> Kinds = new Dictionary<string, ColumnKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["hostname"] = ColumnKind.Hostname,
        ["ip-private"] = ColumnKind.IpPrivate,
        ["ip-version"] = ColumnKind.IpVersion,
        ["port-service"] = ColumnKind.PortService,
        ["cidr"] = ColumnKind.Cidr
    };
    private static readonly IReadOnlyDictionary<string, VirtualColumnEditorDefinition> Editors = new Dictionary<string, VirtualColumnEditorDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["hostname"] = new("Network", "", 1, 1, []),
        ["ip-private"] = new("Network", "", 1, 1, []),
        ["ip-version"] = new("Network", "", 1, 1, []),
        ["port-service"] = new("Network", "", 1, 1, []),
        ["cidr"] = new("Network", "", 1, 1, [new("prefix", "Network prefix length", "Example: 24", true)])
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
