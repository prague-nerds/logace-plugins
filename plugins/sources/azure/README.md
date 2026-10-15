# Azure logs source

Optional `azure.source` plugin for LogAce, contributed through the official `prague-nerds/logace-plugins` catalog. The package uses public `LogAce.PluginApi` version 1 and declarative forms; it has no third-party NuGet dependencies or host-private references.

## Requirements

Azure CLI (`az`) on PATH, with the desired account authenticated through `az login`.

## Use

Install and enable the plugin from **Tools → Plugins…**, restart LogAce and choose **Azure logs** in **Open**. Its category is **Cloud**; empty categories are hidden when no enabled sources occupy them.

Choose App Service stream or Log Analytics query. App Service needs resource group/app name, optional subscription/slot and streams `az webapp log tail`. Log Analytics needs workspace ID, KQL query and ISO duration timespan; `az monitor log-analytics query` tables are converted to NDJSON. No credentials are stored in the form. Resource availability and permissions are checked by the CLI on open. App Service is live; Log Analytics is a finite query snapshot.

## Configuration and compatibility

`azure.source` replaces both `builtin.source.azure-appservice` and `builtin.source.azure-analytics`. The host adds `sourceMode` during migration; legacy fields/URIs stay compatible. Normalization/validation perform no external I/O. Discovery happens only through explicit Browse actions; opening performs transport/provider checks. CLI account credentials remain with the CLI and are not serialized in plugin configuration.

## Ownership and cancellation

LogAce supplies spool paths through `IPluginSourceHost`; the plugin owns acquired processes/listeners and files. Cancelled/failed startup releases acquired resources and deletes partial spools. Once open returns successfully, the live source owns its own shutdown token: cancelling the previous opening operation does not stop it. Disposing the returned `ILogSource` stops producers, closes sockets/processes and removes the temporary spool. Stderr is drained concurrently, with at most 16 KiB retained for diagnostics.

Each top-level type has its own source file. The named source factories own capture and transport; catalog and normalizer types own discovery and format conversion. `SourceRuntime.cs` isolates opening/discovery operations; `Configuration.cs` handles versioned JSON, and `TemporaryFileLifetime.cs` owns completed snapshot spools.

## Development and tests

From a LogAce checkout with this optional catalog submodule:

```sh
dotnet run --project LogAce -- plugins dev submodules/logace-plugins/plugins/sources/azure
dotnet run --project LogAce -- --plugin-dir .local/plugins
dotnet test submodules/logace-plugins/tests/AzureSource.Tests/AzureSource.Tests.csproj
```

For a standalone catalog checkout, pass `-p:LogAceRoot=/absolute/path/to/logace` to `dotnet test`. Only `LogAce.PluginApi` is referenced; the application and Terminal.Gui are not test dependencies. CLI fixtures never contact accounts or devices; listener tests use local loopback sockets. Platform-specific capture tests run on their supported OS. LogAce’s own optional integration tests verify picker/forms at 80×24 and 120×32, sessions and parser output. Real account/device access is an additional manual smoke check.

The manifest lists every compiled C# source. After edits, rerun `plugins dev`; before submitting a published update, bump the version in `plugin.json`, runtime metadata and the matching `index.json` entry. No source checksums need updating. Do not commit compiled output, credentials or local activation state.
