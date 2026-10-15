# Kubernetes pod source

`kubectl.source` is an optional LogAce source plugin for snapshots and live streams
from Kubernetes pods. It replaces the former built-in kubectl source and uses only
`LogAce.PluginApi` and .NET libraries. The timestamp parser is the separate `kubectl` parser plugin, installed as a source dependency.

## Requirements and installation

- LogAce with plugin API version 1 and a .NET 10 runtime.
- `kubectl` on `PATH`, with a working kubeconfig and access to the selected cluster.
- Permission to read the pod's logs. Browsing additionally needs permission to list
  pods; container discovery needs permission to get the selected pod.

Install **Kubernetes pod** from **Tools → Plugins…**, enable it, and restart LogAce.
The sole catalog is `prague-nerds/logace-plugins`; additions and updates go through
pull requests. LogAce reviews source and dependencies before compiling locally.
This plugin runs with your user privileges, as does the kubectl CLI it launches.

## Opening logs

In **F2 → Containers → Kubernetes pod** or **File → Open Kubernetes Pod…** (`Ctrl+K`):

1. Enter a pod name or `namespace/pod`. Discovery is optional.
2. Set Context and Namespace if needed. Empty Context uses kubectl's current
   context. Empty Namespace uses kubectl's configured namespace when opening logs,
   but **Browse pods** lists all namespaces.
3. Browse pods to see phase, readiness and restart counts. Search matches namespace,
   pod and container names; change filters and press **Refresh pods** to apply them.
4. Leave Container empty for kubectl's default, choose a specific container, or
   select All containers. Choosing a container clears All containers. Selecting a
   different pod clears the old container selection. The container chooser includes
   ordinary, init and ephemeral containers; availability of their logs depends on
   the cluster and the container's lifecycle.
5. Set Last lines (`2000` by default, or `all`), an optional Since duration such as
   `30m`, or Since time as an RFC3339 timestamp. Since time takes precedence.
6. Leave Follow live enabled for streaming, or clear it for a snapshot, then Open.

Forms scroll to the focused field on small terminals. Tab/Shift+Tab reaches every
setting and action; the common host form keeps confirm/cancel available. Discovery
errors return field diagnostics and leave manual entry available. Cancel stops an
in-progress discovery/startup without affecting previously opened sources.

Existing CLI options use this plugin too:

```sh
dotnet run --project LogAce -- --kubectl-pod api --kubectl-namespace default
dotnet run --project LogAce -- --kubectl-context staging --kubectl-pod worker \
  --kubectl-namespace jobs --kubectl-container app --kubectl-follow
dotnet run --project LogAce -- --kubectl-pod api --kubectl-all-containers --kubectl-tail all
```

The CLI defaults to a snapshot. Without an enabled plugin it reports installation
instructions before starting the TUI. `--plugin-dir` chooses an isolated plugin
registry/cache; it does not disable installation from the official catalog.

## Source layout and public API

| File | Responsibility |
| --- | --- |
| `plugin.cs` | Metadata, searchable presentation, declarative forms and nested discovery actions. |
| `KubernetesConfiguration.cs` | Versioned JSON normalization, validation and source identity. |
| `KubernetesCli.cs` | Shell-free arguments, bounded stderr and process cancellation. |
| `KubernetesPodCatalog.cs` | Cancellable JSON discovery, namespace scoping and container lists. |
| `KubernetesLogSource.cs` | Snapshot capture, source-owned follow worker and spool cleanup. |
| `KubernetesFollowCursor.cs` | Nanosecond-preserving reconnect and boundary occurrence counts. |
| `plugin.json` | Explicit source paths, metadata and declarative UI/API compatibility. |

The entry point implements `ILogAceSourcePlugin`. `Presentation` supplies the picker
category and search aliases; the host omits empty categories automatically.
`GetConfigurationUi` returns neutral `PluginSourceFormRequest` data, so this plugin
does not depend on Terminal.Gui or private LogAce classes.

Form actions receive immutable values and run on workers. They return values,
choices and diagnostics; nested pickers go through `context.Host.ShowAsync`, which
owns UI dispatch, focus and modal lifetime. The owning plugin validates nested
confirmation too, so the browser carries the whole source configuration and uses
qualified pod choices that normalize into separate pod/namespace settings.

`NormalizeConfiguration` removes discovery-only filters, trims text and supplies
missing version-1 defaults without disguising malformed types. `ValidateConfiguration`
also protects session and CLI opens, not just form confirmation. No credential is
stored in this configuration: kubectl inherits the normal kubeconfig/environment
and credential helper behavior. Configuration stores `pod`, `context`, `namespace`,
`container`, `allContainers`, `tail`, `since`, `follow` and optional `sinceTime`.

`OpenAsync` requests its temporary file from `IPluginSourceHost.CreateSpoolPath` and
returns a `LogSourceHandle`. The host adapts the handle, saves the configuration in
recent history/sessions, and provides the state path. Old `builtin.source.kubectl`
sessions migrate to `kubectl.source`; existing `kubectl://` identities and `.las`
state are retained by LogAce's compatibility bridge. Closing the handle stops its
worker/process tree and removes its spool. The opening token covers only startup;
the active follow worker has a separate lifetime.

## Process behavior and limitations

Arguments use `ProcessStartInfo.ArgumentList`, never a shell command assembled from
input. stdout and stderr drain concurrently. kubectl emits pod log records on
stdout; stderr stays a diagnostic and never enters the log. Discovery output is
bounded to 16 MiB and retained diagnostics to 8 KiB, while stderr continues draining.

Follow retries after stream exit with a one-second delay. Reconnect requests all
available records since the last raw timestamp instead of `--tail 0`, so available
logs produced during the outage can be recovered. The cursor compares padded UTC
timestamp keys without rounding nanoseconds and suppresses only previously seen
occurrences at the replay boundary.

Kubernetes log retention, pod deletion/replacement, container restarts, clock
ordering and interleaved all-container output prevent a lossless delivery guarantee.
Reconnect keeps targeting the same pod name; it does not select a replacement pod
from a Deployment or replay the previous container instance. A quiet running stream
is accepted after a short startup grace period; later failures are retried. The
plugin does not provide a durable offset, TLS bypass, cluster mutation, or a bounded
live spool size. Close the tab when a capture is no longer needed.

See the official [kubectl logs reference](https://kubernetes.io/docs/reference/kubectl/generated/kubectl_logs/)
for timestamp, tail, since, container and follow semantics.

## Development and tests

From a LogAce checkout with this catalog initialized as its optional submodule:

```sh
dotnet run --project LogAce -- plugins dev submodules/logace-plugins/plugins/kubectl.source
dotnet run --project LogAce -- --plugin-dir .local/plugins
dotnet test submodules/logace-plugins/tests/KubernetesSource.Tests/KubernetesSource.Tests.csproj
dotnet test LogAce.Tests/LogAce.Tests.csproj --filter FullyQualifiedName~KubernetesSource
```

From a standalone catalog checkout, pass the API checkout explicitly:

```sh
dotnet test tests/KubernetesSource.Tests/KubernetesSource.Tests.csproj -p:LogAceRoot=/path/to/logace
```

The package tests compile against the public API only. POSIX fake CLI fixtures
exercise arguments, snapshots, diagnostics, cancellation, reconnect, filters and
container selection without contacting a cluster; those fixtures are skipped on
Windows. Configuration/cursor tests are platform independent. LogAce's optional
integration tests compile/load the actual package, restore a legacy session and
exercise host forms at 80×24 and 120×32. A real cluster smoke test remains necessary
for your authentication setup and kubectl/server combination.

Repeat `plugins dev` and restart after edits. Before submitting a PR, bump the
version in `plugin.json`, runtime metadata and `index.json`; keep their metadata aligned. No dependency packages,
build scripts, precompiled binaries, local registry or generated state ship here.
