// LogAce-Plugin:
// Id: kubectl.source
// Version: 1.0.0
// DisplayName: Kubernetes pod
// Kind: Source
// EntryPoint: LogAce.Plugins.Kubernetes.KubernetesSourcePlugin
// ApiVersion: 1
// Capabilities: ProvideSourceConfigurationUi, RunsProcesses, UsesNetwork, ReadsLocalFiles, WritesLocalFiles
// Description: Kubernetes pod log snapshots and live streams through kubectl.
// Packages:

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Kubernetes;

/// <summary>
/// Optional Kubernetes source using only the public LogAce API and the system
/// kubectl CLI. LogAce owns forms, threading, source registration and sessions.
/// </summary>
public sealed class KubernetesSourcePlugin : ILogAceSourcePlugin
{
    /// <summary>Capabilities describe reviewable behavior, not sandbox permissions.</summary>
    public PluginMetadata Metadata { get; } = new("kubectl.source", "Kubernetes pod",
        "Kubernetes pod log snapshots and live streams through kubectl.", PluginKind.Source,
        Capabilities: ["ProvideSourceConfigurationUi", "RunsProcesses", "UsesNetwork", "ReadsLocalFiles", "WritesLocalFiles"]);

    /// <summary>The host creates the Containers category when a source occupies it.</summary>
    public PluginSourcePresentation Presentation { get; } = new("Containers", ["k8s", "kubectl", "kubernetes", "pod", "snapshot", "follow"]);

    /// <summary>Interactive opens follow by default; CLI and session settings override this.</summary>
    public PluginSourceConfiguration CreateDefaultConfiguration() =>
        KubernetesConfiguration.Create(new("", null, null, null, false, "2000", null, true));

    /// <summary>Discovery is optional: users with restricted list access can enter a known pod.</summary>
    public IPluginSourceUiRequest GetConfigurationUi(PluginSourceConfigurationContext context) =>
        new PluginSourceFormRequest(new("Open Kubernetes Log",
        [
            new("pod", "Pod name or namespace/pod") { Required = true },
            new("context", "Context") { HelpText = "Empty uses kubectl's current context." },
            new("namespace", "Namespace") { HelpText = "Empty uses the current namespace; Browse lists all namespaces." },
            new("container", "Container") { HelpText = "Empty uses kubectl's default container." },
            new("allContainers", "All containers", PluginSourceFieldKind.Boolean),
            new("tail", "Last lines") { Required = true, HelpText = "A non-negative count, or 'all'." },
            new("since", "Since") { HelpText = "Optional duration, for example 30m." },
            new("sinceTime", "Since time") { HelpText = "Optional RFC3339 timestamp. Takes precedence over Since." },
            new("follow", "Follow live", PluginSourceFieldKind.Boolean)
        ])
        {
            Description = "Enter a known pod, or set Context and Namespace and browse.",
            Actions =
            [
                new("browse-pods", "_Browse pods...", BrowsePodsAsync, "pod"),
                new("browse-containers", "Choose _container...", BrowseContainersAsync, "container")
            ]
        }, context.InitialConfiguration);

    /// <summary>Keep only source settings and canonicalize optional/qualified names.</summary>
    public PluginSourceConfiguration NormalizeConfiguration(PluginSourceConfiguration configuration) => KubernetesConfiguration.Normalize(configuration);

    /// <summary>Validate restored configurations too: they may never pass through a form.</summary>
    public PluginValidationResult ValidateConfiguration(PluginSourceConfiguration configuration) => KubernetesConfiguration.Validate(configuration);

    /// <summary>The open token covers startup; an opened follow stream owns its own lifetime.</summary>
    public async ValueTask<ILogSource> OpenAsync(PluginSourceConfiguration configuration, PluginSourceOpenContext context)
    {
        configuration = NormalizeConfiguration(configuration);
        PluginValidationResult validation = ValidateConfiguration(configuration);
        if (!validation.IsValid) throw new ArgumentException(validation.Errors[0].Message);
        return await KubernetesLogSource.OpenAsync(KubernetesConfiguration.Read(configuration), context).ConfigureAwait(false);
    }

    private static async ValueTask<PluginSourceFormUpdate> BrowsePodsAsync(PluginSourceFormActionContext context)
    {
        PluginSourceFormUpdate loaded = await RefreshPodsAsync(context).ConfigureAwait(false);
        if (!loaded.Choices.TryGetValue("pod", out var choices)) return loaded;
        var values = new Dictionary<string, JsonElement>(context.Values.Values);
        string pod = context.Values.GetString("pod") ?? "";
        string? namespaceName = context.Values.GetString("namespace");
        string qualified = pod.Contains('/') || string.IsNullOrWhiteSpace(namespaceName) ? pod : namespaceName + "/" + pod;
        if (!choices.Any(choice => choice.Id == qualified) && !pod.Contains('/') && string.IsNullOrWhiteSpace(namespaceName))
        {
            // A bare pod can be preselected when discovery contains one matching
            // namespace. Ambiguous names remain unselected for an explicit choice.
            var matches = choices.Where(choice => choice.Id.EndsWith("/" + pod, StringComparison.Ordinal)).ToArray();
            qualified = matches.Length == 1 ? matches[0].Id : "";
        }
        values["pod"] = PluginSourceJson.Value(qualified);
        var request = new PluginSourceFormRequest(new("Choose Kubernetes Pod",
        [
            // Put the list and its Refresh action first: choices must be visible
            // on entry even when a small terminal scrolls the lower filter fields.
            new("pod", "Pod", PluginSourceFieldKind.Choice) { Required = true, Choices = choices },
            new("search", "Search namespace, pod or container"),
            new("runningOnly", "Running only", PluginSourceFieldKind.Boolean)
        ])
        {
            ConfirmLabel = "_Use pod",
            Description = choices.Count == 0 ? "No pods found. Cancel to change Namespace or enter a known pod."
                : "Choose a pod. Change filters, then Refresh to reload.",
            Actions = [new("refresh", "_Refresh pods", RefreshPodsAsync, "pod")]
        }, new(1, PluginSourceJson.Object(values)));

        // Workers ask the host for nested UI. This keeps native focus, dispatch,
        // cancellation and the owning plugin's validation in one place.
        PluginSourceConfigurationResult result = await context.Host.ShowAsync(request, context.CancellationToken).ConfigureAwait(false);
        if (result.Status != PluginSourceConfigurationStatus.Confirmed || result.Configuration is not { } selected) return new();
        return new()
        {
            Values = new Dictionary<string, JsonElement>
            {
                ["pod"] = selected.Values.GetProperty("pod"),
                ["namespace"] = selected.Values.GetProperty("namespace"),
                // A container from another pod may not exist in the new selection.
                ["container"] = PluginSourceJson.Value(null as string)
            }
        };
    }

    private static async ValueTask<PluginSourceFormUpdate> RefreshPodsAsync(PluginSourceFormActionContext context)
    {
        IReadOnlyList<KubernetesPod> pods;
        try
        {
            pods = await KubernetesPodCatalog.ListAsync(context.Values.GetString("context"),
                context.Values.GetString("namespace"), context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return new() { Diagnostics = [new(error is JsonException ? "kubectl returned invalid JSON. Check access and Refresh." : error.Message, "pod")] };
        }
        string search = context.Values.GetString("search")?.Trim() ?? "";
        bool running = context.Values.GetBoolean("runningOnly");
        var choices = pods.Where(pod => (!running || pod.Phase.Equals("Running", StringComparison.OrdinalIgnoreCase))
            && (search.Length == 0 || pod.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                || pod.Containers.Any(name => name.Contains(search, StringComparison.OrdinalIgnoreCase))))
            .Select(pod => new PluginSourceChoice(pod.Id, pod.Label)).ToArray();
        return new()
        {
            Choices = new Dictionary<string, IReadOnlyList<PluginSourceChoice>> { ["pod"] = choices },
            Values = new Dictionary<string, JsonElement>
            {
                ["pod"] = PluginSourceJson.Value(choices.Any(choice => choice.Id == context.Values.GetString("pod"))
                    ? context.Values.GetString("pod") : "")
            },
            Diagnostics = choices.Length == 0 ? [new(pods.Count == 0 ? "No pods in this namespace or context."
                : "No matching pods. Change the filters and Refresh.", "pod")] : []
        };
    }

    private static async ValueTask<PluginSourceFormUpdate> BrowseContainersAsync(PluginSourceFormActionContext context)
    {
        // Container discovery needs a pod but does not require a valid tail or a
        // cleared all-containers setting while the user is still editing the form.
        var configuration = KubernetesConfiguration.Normalize(new(1, PluginSourceJson.Object(context.Values.Values)));
        KubernetesOptions options = KubernetesConfiguration.Read(configuration);
        if (string.IsNullOrWhiteSpace(options.Pod) || options.Pod.StartsWith('-') || options.Pod.Contains('/'))
            return new() { Diagnostics = [new("Enter or browse a pod before choosing its container.", "pod")] };
        KubernetesPod pod;
        try { pod = await KubernetesPodCatalog.GetAsync(options, context.CancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return new() { Diagnostics = [new(error is JsonException ? "kubectl returned invalid pod JSON. Check Pod and Namespace." : error.Message, "container")] };
        }
        var values = configuration.Values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        values["allContainers"] = PluginSourceJson.Value(false);
        if (options.Container is null) values["container"] = PluginSourceJson.Value(KubernetesConfiguration.DefaultContainerChoiceId);
        var choices = new[] { new PluginSourceChoice(KubernetesConfiguration.DefaultContainerChoiceId, "Default container (kubectl)") }
            .Concat(pod.Containers.Select(name => new PluginSourceChoice(name, name))).ToArray();
        var request = new PluginSourceFormRequest(new("Choose Kubernetes Container",
        [new("container", "Container", PluginSourceFieldKind.Choice) { Choices = choices }])
        {
            Description = pod.Id,
            ConfirmLabel = "_Use container"
        }, new(1, PluginSourceJson.Object(values)));
        PluginSourceConfigurationResult result = await context.Host.ShowAsync(request, context.CancellationToken).ConfigureAwait(false);
        return result.Status == PluginSourceConfigurationStatus.Confirmed && result.Configuration is { } selected
            ? new() { Values = new Dictionary<string, JsonElement>
                { ["container"] = selected.Values.GetProperty("container"), ["allContainers"] = PluginSourceJson.Value(false) } }
            : new();
    }
}
