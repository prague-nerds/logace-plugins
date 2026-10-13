# macOS unified log source

Optional `oslog.source` plugin for LogAce, contributed through the official `prague-nerds/logace-plugins` catalog. The package uses public `LogAce.PluginApi` version 1 and declarative forms; it has no third-party NuGet dependencies or host-private references.

## Requirements

macOS with the system `log` command on PATH. Available events depend on the current user’s log access.

## Use

Install and enable the plugin from **Tools → Plugins…**, restart LogAce and choose **macOS unified log** in **Open**. Its category is **Devices & system**; empty categories are hidden when no enabled sources occupy them.

Open a snapshot or live stream from this Mac. Set process/PID, subsystem, predicate, minimum level and Last range. Browse processes/subsystems samples that range (at most 150,000 events); it does not enumerate every installed application. Only structured unified-log events reach the NDJSON spool. Follow reconnects and uses a best-effort timestamp replay cutoff.

## Configuration and compatibility

`oslog.source` replaces `builtin.source.oslog`; legacy `oslog://` identities and numeric enums stay compatible. Normalization/validation perform no external I/O. Discovery happens only through explicit Browse actions; opening performs transport/provider checks. CLI account credentials remain with the CLI and are not serialized in plugin configuration.

## Ownership and cancellation

LogAce supplies spool paths through `IPluginSourceHost`; the plugin owns acquired processes/listeners and files. Cancelled/failed startup releases acquired resources and deletes partial spools. Once open returns successfully, the live source owns its own shutdown token: cancelling the previous opening operation does not stop it. Disposing the returned `ILogSource` stops producers, closes sockets/processes and removes the temporary spool. Stderr is drained concurrently, with at most 16 KiB retained for diagnostics.

Each top-level type has its own source file. The named source factories own capture and transport; catalog and normalizer types own discovery and format conversion. `SourceRuntime.cs` isolates opening/discovery operations; `Configuration.cs` handles versioned JSON, and `TemporaryFileLifetime.cs` owns completed snapshot spools. `Browse.cs` supplies the nested declarative chooser without Terminal.Gui dependencies.

## Development and tests

From a LogAce checkout with this optional catalog submodule:

```sh
dotnet run --project LogAce -- plugins dev submodules/logace-plugins/plugins/oslog.source
dotnet run --project LogAce -- --plugin-dir .local/plugins
dotnet test submodules/logace-plugins/tests/OsLogSource.Tests/OsLogSource.Tests.csproj
```

For a standalone catalog checkout, pass `-p:LogAceRoot=/absolute/path/to/logace` to `dotnet test`. Only `LogAce.PluginApi` is referenced; the application and Terminal.Gui are not test dependencies. CLI fixtures never contact accounts or devices; listener tests use local loopback sockets. Platform-specific capture tests run on their supported OS. LogAce’s own optional integration tests verify picker/forms at 80×24 and 120×32, sessions and parser output. Real account/device access is an additional manual smoke check.

The manifest lists every compiled C# source. After edits, rerun `plugins dev`; before submitting a published update, bump the version in `plugin.json`, runtime metadata and the matching `index.json` entry. No source checksums need updating. Do not commit compiled output, credentials or local activation state.
