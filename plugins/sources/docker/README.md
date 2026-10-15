# Docker container source

`docker.source` opens Docker container logs as a snapshot or a live stream. It is
an optional LogAce source plugin, extracted from the former built-in integration.
The timestamp parser/profile named **Docker** is the separate `docker` parser plugin, installed as a source dependency.

## Install and open

After this package is merged into the official catalog, open **Tools → Plugins…**
in LogAce, install **Docker container**, review its source and capabilities, enable
it and restart the application. **F2 → Docker container** and **Ctrl+O** then open
the configuration form.

Docker must be installed and `docker` must be on the application's `PATH`. The
selected daemon/context must be reachable and the current user must be authorized
to read that container's logs. Docker handles contexts, TLS, credentials and remote
transport; this plugin does not collect or persist credentials. The available
history depends on the container's logging driver and retention settings.

The form accepts a known container name or ID without requiring discovery. Leave
**Docker context** empty to use Docker's current context, or enter a context name
before choosing **Browse containers…**. The browser lists names, status and image
for running and stopped containers. Change its search or running filter and choose
**Refresh containers** to reload; cancelling leaves the main form unchanged.

**Last lines** accepts a non-negative count or `all` (default `2000`). **Since** is
optional and is passed to Docker, for example `30m` or an RFC3339 timestamp.
**Follow live** defaults to enabled in the form. Disable it for a finite snapshot.
These options follow the [Docker logs reference](https://docs.docker.com/reference/cli/docker/container/logs/).
Discovery uses [Docker container ls](https://docs.docker.com/reference/cli/docker/container/ls/).

The former CLI flags still work when this plugin is installed:

```sh
LogAce --docker-container api --docker-tail 2000 --docker-follow
LogAce --docker-context staging --docker-container worker --docker-since 30m
```

Without `--docker-follow`, CLI opens are snapshots. Repeat `--docker-container` to
open several sources. Without an enabled plugin, LogAce reports how to install it
before starting the TUI.

## Local development and tests

From the LogAce repository with the optional plugin submodule initialized:

```sh
dotnet run --project LogAce -- plugins dev submodules/logace-plugins/plugins/sources/docker
dotnet run --project LogAce -- --plugin-dir .local/plugins
dotnet test submodules/logace-plugins/tests/DockerSource.Tests/DockerSource.Tests.csproj
dotnet test LogAce.Tests --filter 'FullyQualifiedName~DockerSource'
```

Repeat `plugins dev` and restart the TUI after editing. It builds the manifest-listed
sources locally and registers this plugin in `.local/plugins`.
The host's ordinary source review/approval is required for catalog installation.

In a standalone catalog checkout, pass the LogAce checkout explicitly:

```sh
dotnet test tests/DockerSource.Tests/DockerSource.Tests.csproj -p:LogAceRoot=/path/to/logace
```

The plugin tests reference **only** the public API project and the exact package
sources. CLI integration tests use a temporary fake Docker executable and need no
daemon; those tests are marked skipped on Windows because the fixture is a POSIX
shell script. Configuration and reconnect cursor tests run on every platform.
LogAce's optional UI tests exercise the source picker, forms and legacy session
restore at 80×24 and 120×32 when this package is present in the submodule.
Neither test project is part of the installed package's locked source list.

## Source layout and API contract

| File | Responsibility |
| --- | --- |
| `plugin.cs` | Metadata, picker presentation, declarative forms and the public `ILogAceSourcePlugin` entry point. |
| `DockerConfiguration.cs` | Versioned neutral JSON, normalization, validation and canonical display identity. |
| `DockerCli.cs` | Argument construction, container discovery and process-tree cancellation. |
| `DockerLogSource.cs` | Shared spool, stdout/stderr readers, source lifetime and reconnect cursor. |

This is also a worked example for source plugin authors:

- `GetConfigurationUi` returns a `PluginSourceFormRequest`; no Terminal.Gui or
  private LogAce types are referenced. Nested dialogs go through
  `IPluginSourceUiHost.ShowAsync`, leaving UI-thread dispatch and focus to the host.
- Form actions receive an immutable input snapshot. Browse returns only a selected
  container; source settings such as tail and context survive the nested form.
  Errors return field diagnostics so manual entry remains available.
- Normalization and validation are shared by the form and session restore. Invalid
  JSON types are rejected rather than silently replaced with defaults.
- `OpenAsync` receives host-managed spool/state services. Its cancellation token
  controls opening; an already opened live source has a separate lifetime.
- The returned `LogSourceHandle` transfers ownership of the worker and spool to the
  host. Closing a tab kills the process tree, observes reader tasks and deletes the
  temporary file. Disposal is idempotent and waits at most two seconds on the UI
  thread; slow teardown finishes asynchronously before releasing the writer.

The plugin has no NuGet dependencies. Capabilities disclose process execution,
network access through Docker, file access and configuration UI. They do not create
a sandbox or grant permissions.

## Persistent configuration

Version `1` uses the same five fields as the former built-in source:

```json
{
  "container": "api",
  "context": null,
  "tail": "2000",
  "since": "30m",
  "follow": true
}
```

The LogAce host migrates saved `builtin.source.docker` identities to `docker.source`.
Existing Docker tab sidecars remain usable, preserving filters and bookmarks.
Sessions retain missing/disabled plugin entries and provide installation guidance.
Restoring opens a new snapshot or connection with the saved options, so relative
`since` values are evaluated again by Docker. Live offsets are intentionally not
persisted across application restarts. The temporary spool is not a durable copy of
container logs. Recent history is supported; tab duplication is disabled.

## Reading and reconnecting

Docker adds RFC3339Nano timestamps with `--timestamps`. Both container stdout and
timestamped stderr are log data; unprefixed stderr is a CLI diagnostic. Both pipes
are drained concurrently to avoid deadlock, with a lock around spool/cursor writes.
Arrival order between the pipes is not guaranteed to be chronological. The spool
uses UTF-8 without a BOM, and live writes are flushed for the host's reload watcher.

After the initial connection, failures retry every second using the same spool.
A quiet container is accepted after a short startup grace period. Immediate CLI
failures are reported when opening; the public source API currently has no separate
live connection-status channel for later daemon outages.

Reconnect requests all available logs since the latest received timestamp, keeping
its full nanosecond text. Occurrence counts at that timestamp suppress replay while
preserving genuinely repeated identical messages. Replay state survives the entire
attempt because stdout/stderr can arrive out of order. If no timestamp was received,
the next attempt starts from the original opening time. Docker retention, clock
changes or recreating a different container under the same name can still make
historical recovery incomplete; the plugin cannot reconstruct logs Docker no longer
has. Use a durable log collector when complete retention is required.

## Contributing

Submit changes through a PR to this repository. Bump the version in `plugin.json`,
runtime metadata and `index.json`. Metadata and capabilities must match in the
manifest and index; no source checksums need updating. Runtime code
must stay on the public API; add regression coverage for changes to configuration,
cancellation, cleanup or reconnect behavior. See [CONTRIBUTING](../../../CONTRIBUTING.md).
