using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LogAce.Plugins.Kubernetes;

internal sealed record KubernetesPod(string Namespace, string Name, string Phase,
    int Ready, int RegularContainers, int Restarts, string[] Containers)
{
    internal string Id => Namespace + "/" + Name;
    internal string Label => Id + " — " + Phase + " — " + Ready + "/" + RegularContainers
        + " ready — " + Restarts + " restarts";
}

/// <summary>Read-only discovery; no kubectl configuration or cluster resources are changed.</summary>
internal static class KubernetesPodCatalog
{
    internal static async Task<IReadOnlyList<KubernetesPod>> ListAsync(string? context,
        string? namespaceName, CancellationToken token)
    {
        var arguments = new List<string> { "get", "pods", "-o", "json" };
        if (string.IsNullOrWhiteSpace(namespaceName)) arguments.Add("--all-namespaces");
        else KubernetesCli.AddNamespace(arguments, namespaceName);
        string json = await ReadJsonAsync(context, arguments, token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new IOException("kubectl returned an invalid pod list. Refresh or enter a known pod name.");
        return items.EnumerateArray().Select(Parse).Where(pod => pod.Name.Length > 0)
            .OrderBy(pod => pod.Namespace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pod => pod.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static async Task<KubernetesPod> GetAsync(KubernetesOptions options, CancellationToken token)
    {
        var arguments = new List<string> { "get", "pod", "-o", "json" };
        KubernetesCli.AddNamespace(arguments, options.Namespace);
        arguments.Add("--");
        arguments.Add(options.Pod);
        using JsonDocument document = JsonDocument.Parse(await ReadJsonAsync(options.Context, arguments, token).ConfigureAwait(false));
        KubernetesPod pod = Parse(document.RootElement);
        if (pod.Name.Length == 0) throw new IOException("kubectl returned an invalid pod. Check Pod and Namespace.");
        return pod;
    }

    private static KubernetesPod Parse(JsonElement pod)
    {
        if (pod.ValueKind != JsonValueKind.Object) throw new IOException("kubectl returned an invalid pod object. Check access and Refresh.");
        string Text(string parent, string field) => pod.TryGetProperty(parent, out var group)
            && group.ValueKind == JsonValueKind.Object && group.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        var names = new List<string>();
        int regularContainers = 0;
        if (pod.TryGetProperty("spec", out var spec) && spec.ValueKind == JsonValueKind.Object)
            foreach (string kind in new[] { "containers", "initContainers", "ephemeralContainers" })
                if (spec.TryGetProperty(kind, out var containers) && containers.ValueKind == JsonValueKind.Array)
                    foreach (var container in containers.EnumerateArray())
                        if (container.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        {
                            names.Add(name.GetString()!);
                            if (kind == "containers") regularContainers++;
                        }
        int ready = 0, restarts = 0;
        if (pod.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("containerStatuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
            foreach (var item in statuses.EnumerateArray())
            {
                if (item.TryGetProperty("ready", out var flag) && flag.ValueKind == JsonValueKind.True) ready++;
                if (item.TryGetProperty("restartCount", out var count) && count.TryGetInt32(out int value)) restarts += value;
            }
        return new(Text("metadata", "namespace"), Text("metadata", "name"), Text("status", "phase"),
            ready, regularContainers, restarts, names.Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static async Task<string> ReadJsonAsync(string? context, IEnumerable<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using Process process = KubernetesCli.Start(context, arguments);
        using var cancellation = token.Register(() => KubernetesCli.Kill(process));
        Task<string> errors = KubernetesCli.ReadDiagnosticsAsync(process.StandardError, token);
        try
        {
            // Bound discovery memory without ceasing to drain stderr. A very large
            // cluster can be scoped by namespace or opened directly by known name.
            var output = new StringBuilder();
            char[] buffer = new char[4096];
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                output.Append(buffer, 0, read);
                if (output.Length > 16 * 1024 * 1024)
                    throw new IOException("Pod discovery is too large. Set a Namespace or enter a known pod.");
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            string diagnostic = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException(diagnostic.Length == 0 ? "Could not discover pods. Check Context, Namespace and kubectl access." : diagnostic);
            return output.ToString();
        }
        finally
        {
            KubernetesCli.Kill(process);
            try { await errors.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
}
