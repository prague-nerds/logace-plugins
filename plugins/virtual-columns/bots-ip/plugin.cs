using LogAce.PluginApi;
namespace LogAce.Columns.bots_ip;
public sealed class ColumnsPlugin : ILogAceVirtualColumnProviderPlugin, ILogAceVirtualColumnEditor, ILogAcePluginWithHostServices, ILogAcePluginDataResources
{
    public PluginMetadata Metadata { get; } = new("bots-ip.columns", "Bots & IP", "Identify crawlers and datacenters and verify crawler identity using downloaded IP ranges.", PluginKind.VirtualColumnProvider, Capabilities: ["ProvideVirtualColumns", "UsesNetwork", "ReadsLocalFiles", "WritesLocalFiles"]);
    public IReadOnlyList<VirtualColumnDefinition> Columns { get; } = [
        new("bot-ip-name", "bot-ip-name", "Bots | Crawler name from downloaded IP ranges.", ["bot-ip-name"], "No extra parameters required."),
        new("bot-verification", "bot-verification", "Bots | Check a User-Agent against its crawler IP ranges.", ["bot-verification"], "Sources: User-Agent, IP (in that order). Uses downloaded crawler ranges."),
        new("ip-datacenter", "ip-datacenter", "Network | Hosting provider from downloaded datacenter IP ranges.", ["ip-datacenter"], "No extra parameters required.")
    ];
    private static readonly IReadOnlyDictionary<string, ColumnKind> Kinds = new Dictionary<string, ColumnKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["bot-ip-name"] = ColumnKind.BotIpName,
        ["bot-verification"] = ColumnKind.BotVerification,
        ["ip-datacenter"] = ColumnKind.IpDatacenter
    };
    private static readonly IReadOnlyDictionary<string, VirtualColumnEditorDefinition> Editors = new Dictionary<string, VirtualColumnEditorDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["bot-ip-name"] = new("Bots", "", 1, 1, []),
        ["bot-verification"] = new("Bots", "Sources: User-Agent, then IP.", 2, 2, []),
        ["ip-datacenter"] = new("Network", "", 1, 1, [])
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
    public IReadOnlyList<PluginDataResource> DataResources => [
        new("IpRanges", "Crawler IP ranges", "Not downloaded", () => BotDatabases.IpRanges.Status, async (token, progress) => { await BotDatabases.IpRanges.UpdateAsync(token, progress).ConfigureAwait(false); InvalidateData(); }),
        new("Datacenters", "Datacenter IP ranges", "Not downloaded", () => BotDatabases.Datacenters.Status, async (token, progress) => { await BotDatabases.Datacenters.UpdateAsync(token, progress).ConfigureAwait(false); InvalidateData(); })
    ];
    public void InvalidateData() { BotDatabases.IpRanges.Reload(); BotDatabases.Datacenters.Reload();  }
}
