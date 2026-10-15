using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.CloudWatch;

/// <summary>
/// Optional CloudWatch source built solely on LogAce's public API. AWS CLI owns
/// transport and authentication; the host owns UI, spool paths and tab state.
/// </summary>
public sealed class CloudWatchSourcePlugin : ILogAceSourcePlugin
{
    /// <summary>Capabilities describe behavior for review, not sandbox permissions.</summary>
    public PluginMetadata Metadata { get; } = new("cloudwatch.source", "AWS CloudWatch Logs",
        "CloudWatch log group snapshots and live streams through AWS CLI v2.", PluginKind.Source,
        Version: "1.0.2", Capabilities: ["ProvideSourceConfigurationUi", "RunsProcesses", "UsesNetwork", "ReadsLocalFiles", "WritesLocalFiles"]);
    /// <summary>The shared picker supplies category navigation and alias search.</summary>
    public PluginSourcePresentation Presentation { get; } = new("Cloud", ["amazon", "aws", "cloudwatch", "logs", "snapshot", "follow"]);
    /// <summary>Keep the former interactive default: a one-hour snapshot.</summary>
    public PluginSourceConfiguration CreateDefaultConfiguration() => CloudWatchConfiguration.Create(new("", null, null, null, null, false, "1h"));
    /// <summary>Users may type a known group even without list permissions.</summary>
    public IPluginSourceUiRequest GetConfigurationUi(PluginSourceConfigurationContext context) => new PluginSourceFormRequest(new("Open AWS CloudWatch Logs",
    [
        new("logGroup", "Log group") { Required = true },
        new("profile", "AWS profile") { HelpText = "Empty uses the AWS CLI credential chain." },
        new("region", "Region") { HelpText = "Empty uses the AWS CLI default region." },
        new("streamFilter", "Stream prefix") { HelpText = "Optional prefix; a trailing * is accepted." },
        new("filterPattern", "Filter pattern") { HelpText = "Optional AWS CloudWatch filter expression." },
        new("since", "Since") { Required = true, HelpText = "One duration (30m, 1h) or an ISO 8601 timestamp." },
        new("follow", "Follow live", PluginSourceFieldKind.Boolean)
    ])
    {
        Description = "Enter a known group, or set AWS profile and Region and browse.",
        Actions = [new("browse-groups", "_Browse log groups...", BrowseAsync, "logGroup")]
    }, CloudWatchConfiguration.ForForm(context.InitialConfiguration));
    /// <summary>Canonicalize source settings; omit discovery filters from sessions.</summary>
    public PluginSourceConfiguration NormalizeConfiguration(PluginSourceConfiguration configuration) => CloudWatchConfiguration.Normalize(configuration);
    /// <summary>Validate restored payloads too, including schema and JSON types.</summary>
    public PluginValidationResult ValidateConfiguration(PluginSourceConfiguration configuration) => CloudWatchConfiguration.Validate(configuration);
    /// <summary>Opening cancellation covers startup; follow owns a separate lifetime.</summary>
    public async ValueTask<ILogSource> OpenAsync(PluginSourceConfiguration configuration, PluginSourceOpenContext context)
    {
        configuration = NormalizeConfiguration(configuration);
        var validation = ValidateConfiguration(configuration);
        if (!validation.IsValid) throw new ArgumentException(validation.Errors[0].Message);
        return await CloudWatchLogSource.OpenAsync(CloudWatchConfiguration.Read(configuration), context).ConfigureAwait(false);
    }

    private static async ValueTask<PluginSourceFormUpdate> BrowseAsync(PluginSourceFormActionContext context)
    {
        var loaded = await RefreshAsync(context).ConfigureAwait(false);
        if (!loaded.Choices.TryGetValue("logGroup", out var choices)) return loaded;
        var values = new Dictionary<string, JsonElement>(context.Values.Values);
        values["logGroup"] = loaded.Values["logGroup"];
        var request = new PluginSourceFormRequest(new("Choose CloudWatch Log Group",
        [
            new("logGroup", "Log group", PluginSourceFieldKind.Choice) { Required = true, Choices = choices },
            new("search", "Search group name")
        ])
        {
            ConfirmLabel = "_Use group",
            Description = choices.Count == 0 ? "No groups found. Cancel to change AWS profile or Region, or enter a known group."
                : "Choose a group. Change Search, then Refresh to reload.",
            Actions = [new("refresh", "_Refresh groups", RefreshAsync, "logGroup")]
        }, new(1, PluginSourceJson.Object(values)));
        // The nested form retains all settings because the owning source plugin
        // validates confirmation. The host marshals UI work and owns modal focus.
        var result = await context.Host.ShowAsync(request, context.CancellationToken).ConfigureAwait(false);
        return result.Status == PluginSourceConfigurationStatus.Confirmed && result.Configuration is { } selected
            ? new() { Values = new Dictionary<string, JsonElement> { ["logGroup"] = selected.Values.GetProperty("logGroup") } } : new();
    }

    private static async ValueTask<PluginSourceFormUpdate> RefreshAsync(PluginSourceFormActionContext context)
    {
        IReadOnlyList<string> names;
        try { names = await CloudWatchGroupCatalog.ListAsync(context.Values.GetString("profile"), context.Values.GetString("region"), context.CancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or JsonException)
        { return new() { Diagnostics = [new(error is JsonException ? "AWS CLI returned invalid group JSON. Check Profile and Region, then Refresh." : error.Message, "logGroup")] }; }
        string search = context.Values.GetString("search")?.Trim() ?? "";
        var choices = names.Where(name => name.Contains(search, StringComparison.OrdinalIgnoreCase)).Select(name => new PluginSourceChoice(name, name)).ToArray();
        return new()
        {
            Choices = new Dictionary<string, IReadOnlyList<PluginSourceChoice>> { ["logGroup"] = choices },
            Values = new Dictionary<string, JsonElement> { ["logGroup"] = PluginSourceJson.Value(choices.Any(choice => choice.Id == context.Values.GetString("logGroup")) ? context.Values.GetString("logGroup") : "") },
            Diagnostics = choices.Length == 0 ? [new(names.Count == 0 ? "No log groups in this account and region. Enter a known group or change Profile and Region."
                : "No matching groups. Change Search and Refresh.", "logGroup")] : []
        };
    }
}
