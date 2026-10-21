using LogAce.PluginApi;
namespace LogAce.Columns.user_agent;
public sealed class ColumnsPlugin : ILogAceVirtualColumnProviderPlugin, ILogAceVirtualColumnEditor, ILogAcePluginWithHostServices, ILogAcePluginDataResources
{
    public PluginMetadata Metadata { get; } = new("user-agent.columns", "User-Agent", "Identify browsers, operating systems, devices and bots from User-Agent strings.", PluginKind.VirtualColumnProvider, Capabilities: ["ProvideVirtualColumns", "UsesNetwork", "ReadsLocalFiles", "WritesLocalFiles"]);
    public IReadOnlyList<VirtualColumnDefinition> Columns { get; } = [
        new("ua-bot", "ua-bot", "Bots | Detect a bot from its User-Agent (yes/no).", ["ua-bot", "bot"], "No extra parameters required."),
        new("ua-bot-name", "ua-bot-name", "Bots | Bot name from its User-Agent.", ["ua-bot-name", "bot-name"], "No extra parameters required."),
        new("ua-bot-category", "ua-bot-category", "Bots | Bot category from its User-Agent.", ["ua-bot-category", "bot-category"], "No extra parameters required."),
        new("ua-browser", "ua-browser", "User agent | Browser name.", ["ua-browser", "browser"], "No extra parameters required."),
        new("ua-browser-version", "ua-browser-version", "User agent | Browser version.", ["ua-browser-version", "browser-version"], "No extra parameters required."),
        new("ua-os", "ua-os", "User agent | Operating system.", ["ua-os", "os"], "No extra parameters required."),
        new("ua-device", "ua-device", "User agent | Device type or name.", ["ua-device", "device"], "No extra parameters required.")
    ];
    private static readonly IReadOnlyDictionary<string, ColumnKind> Kinds = new Dictionary<string, ColumnKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["ua-bot"] = ColumnKind.UaBot,
        ["ua-bot-name"] = ColumnKind.UaBotName,
        ["ua-bot-category"] = ColumnKind.UaBotCategory,
        ["ua-browser"] = ColumnKind.UaBrowser,
        ["ua-browser-version"] = ColumnKind.UaBrowserVersion,
        ["ua-os"] = ColumnKind.UaOs,
        ["ua-device"] = ColumnKind.UaDevice
    };
    private static readonly IReadOnlyDictionary<string, VirtualColumnEditorDefinition> Editors = new Dictionary<string, VirtualColumnEditorDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["ua-bot"] = new("Bots", "", 1, 1, []),
        ["ua-bot-name"] = new("Bots", "", 1, 1, []),
        ["ua-bot-category"] = new("Bots", "", 1, 1, []),
        ["ua-browser"] = new("User agent", "", 1, 1, []),
        ["ua-browser-version"] = new("User agent", "", 1, 1, []),
        ["ua-os"] = new("User agent", "", 1, 1, []),
        ["ua-device"] = new("User agent", "", 1, 1, [])
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
        new("UserAgents", "Bot user agents", "Bundled definitions", () => BotDatabases.UserAgents.Status, async (token, progress) => { await BotDatabases.UserAgents.UpdateAsync(token, progress).ConfigureAwait(false); InvalidateData(); })
    ];
    public void InvalidateData() { BotDatabases.UserAgents.Reload(); VirtualColumnRuntime.InvalidateBotDatabases(); }
}
