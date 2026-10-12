// LogAce-Plugin:
// Id: docker.source
// Version: 1.0.0
// DisplayName: Docker container
// Kind: Source
// EntryPoint: LogAce.Plugins.Docker.DockerSourcePlugin
// ApiVersion: 1
// Capabilities: ProvideSourceConfigurationUi, RunsProcesses, UsesNetwork, ReadsLocalFiles, WritesLocalFiles
// Description: Docker container log snapshots and live streams through the Docker CLI.
// Packages:

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Docker;

/// <summary>
/// Optional Docker integration. Configuration is described with neutral API types;
/// LogAce owns the form, threading, source registration and session persistence.
/// No application implementation or Terminal.Gui assembly is referenced here.
/// </summary>
public sealed class DockerSourcePlugin : ILogAceSourcePlugin
{
    /// <summary>Capabilities are review disclosures, not runtime permission grants.</summary>
    public PluginMetadata Metadata { get; } = new("docker.source", "Docker container",
        "Docker container log snapshots and live streams through the Docker CLI.", PluginKind.Source,
        Capabilities: ["ProvideSourceConfigurationUi", "RunsProcesses", "UsesNetwork", "ReadsLocalFiles", "WritesLocalFiles"]);

    /// <summary>The host places this source in its searchable source picker.</summary>
    public PluginSourcePresentation Presentation { get; } = new("Containers", ["docker", "container", "snapshot", "follow"]);

    /// <summary>Interactive opens follow by default; CLI callers can request a snapshot.</summary>
    public PluginSourceConfiguration CreateDefaultConfiguration() => DockerConfiguration.Create("", null, "2000", null, true);

    /// <summary>Known container names work offline from discovery; Browse is optional.</summary>
    public IPluginSourceUiRequest GetConfigurationUi(PluginSourceConfigurationContext context) =>
        new PluginSourceFormRequest(new("Open Docker Log",
        [
            new("context", "Docker context") { HelpText = "Leave empty to use Docker's current context." },
            new("container", "Container name or ID") { Required = true },
            new("tail", "Last lines") { Required = true, HelpText = "Enter a non-negative count, or 'all' for the complete available history." },
            new("since", "Since") { HelpText = "Optional Docker time value, for example 30m or an RFC3339 timestamp." },
            new("follow", "Follow live", PluginSourceFieldKind.Boolean)
        ])
        {
            Description = "Open a known container, or browse containers in the selected context.",
            Actions = [new("browse", "_Browse containers...", BrowseAsync, "container")]
        }, context.InitialConfiguration);

    /// <summary>Canonicalize persistent inputs; remove browser-only search/filter state.</summary>
    public PluginSourceConfiguration NormalizeConfiguration(PluginSourceConfiguration configuration) => DockerConfiguration.Normalize(configuration);
    /// <summary>Validate persisted data too, since session restore can bypass a form.</summary>
    public PluginValidationResult ValidateConfiguration(PluginSourceConfiguration configuration) => DockerConfiguration.Validate(configuration);

    /// <summary>
    /// The open token covers connection/startup only. A live source owns a separate
    /// lifetime so cancelling another dialog cannot stop an already opened stream.
    /// </summary>
    public async ValueTask<ILogSource> OpenAsync(PluginSourceConfiguration configuration, PluginSourceOpenContext context)
    {
        configuration = NormalizeConfiguration(configuration);
        PluginValidationResult validation = ValidateConfiguration(configuration);
        if (!validation.IsValid) throw new ArgumentException(validation.Errors[0].Message);
        return await DockerLogSource.OpenAsync(DockerConfiguration.Read(configuration), context).ConfigureAwait(false);
    }

    private static async ValueTask<PluginSourceFormUpdate> BrowseAsync(PluginSourceFormActionContext context)
    {
        // Actions run on workers. Nested UI must go through the host so focus,
        // cancellation and native hosted modal bookkeeping remain host-owned.
        PluginSourceFormUpdate loaded = await RefreshContainersAsync(context).ConfigureAwait(false);
        if (!loaded.Choices.TryGetValue("container", out var choices)) return loaded;
        var initial = new PluginSourceConfiguration(1, PluginSourceJson.Object(context.Values.Values));
        var request = new PluginSourceFormRequest(new("Choose Docker Container",
        [
            // The common form renderer scrolls long forms. Put the primary choice
            // first so even a small terminal opens with actual containers visible.
            new("container", "Container", PluginSourceFieldKind.Choice)
            {
                Required = true, Choices = choices,
            },
            new("search", "Search name, ID or image"),
            new("runningOnly", "Running only", PluginSourceFieldKind.Boolean)
        ])
        {
            ConfirmLabel = "_Use container",
            Description = choices.Count == 0 ? "No containers in this context. Cancel to enter a known name manually." : "Choose a container. Change the filters, then Refresh to reload the list.",
            Actions = [new("refresh", "_Refresh containers", RefreshContainersAsync, "container")]
        }, initial);
        PluginSourceConfigurationResult result = await context.Host.ShowAsync(request, context.CancellationToken).ConfigureAwait(false);
        return result.Status == PluginSourceConfigurationStatus.Confirmed && result.Configuration is { } selected
            ? new() { Values = new Dictionary<string, JsonElement> { ["container"] = selected.Values.GetProperty("container") } }
            : new();
    }

    private static async ValueTask<PluginSourceFormUpdate> RefreshContainersAsync(PluginSourceFormActionContext context)
    {
        IReadOnlyList<DockerContainer> containers;
        try { containers = await DockerCli.ListAsync(context.Values.GetString("context"), context.CancellationToken).ConfigureAwait(false); }
        catch (IOException error) { return new() { Diagnostics = [new(error.Message, "container")] }; }
        string search = context.Values.GetString("search")?.Trim() ?? "";
        bool running = context.Values.GetBoolean("runningOnly");
        var choices = containers.Where(container => (!running || container.State.Equals("running", StringComparison.OrdinalIgnoreCase))
            && (search.Length == 0 || container.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || container.Id.Contains(search, StringComparison.OrdinalIgnoreCase) || container.Image.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Select(container => new PluginSourceChoice(container.Name, $"{container.Name} — {container.Status} — {container.Image}"))
            .ToArray();
        // Returning an empty list keeps the picker usable and produces an explicit
        // recovery message; it must never select a stale container after a refresh.
        return new()
        {
            Choices = new Dictionary<string, IReadOnlyList<PluginSourceChoice>> { ["container"] = choices },
            Values = new Dictionary<string, JsonElement> { ["container"] = PluginSourceJson.Value(
                choices.Any(choice => choice.Id == context.Values.GetString("container")) ? context.Values.GetString("container") : "") },
            Diagnostics = choices.Length == 0 ? [new(containers.Count == 0 ? "No containers in this context."
                : "No matching containers. Change the filters and Refresh.", "container")] : []
        };
    }
}
