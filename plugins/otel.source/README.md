# OpenTelemetry logs source

Optional `otel.source` plugin for LogAce, contributed through the official `prague-nerds/logace-plugins` catalog. The package uses public `LogAce.PluginApi` version 1 and declarative forms; it has no third-party NuGet dependencies or host-private references.

## Requirements

A free local TCP port and OTLP/HTTP senders. No external CLI or SDK is needed.

## Use

Install and enable the plugin from **Tools → Plugins…**, restart LogAce and choose **OpenTelemetry logs** in **Open**. Its category is **Listeners**; empty categories are hidden when no enabled sources occupy them.

Set bind address, HTTP port and path (defaults: 127.0.0.1:4318/v1/logs). The form displays `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT`, using loopback for wildcard binds. Send POST requests with OTLP JSON or protobuf; gzip is supported. Wrong methods/routes and malformed bodies return HTTP errors. Decompressed requests are limited to 64 MiB. Incoming resource/scope/log fields are normalized to the `otel-logs` parser plugin’s NDJSON schema. The listener provides neither TLS nor authentication; use a trusted interface/network or an appropriate proxy. It is a live session, not a durable collector.

## Configuration and compatibility

`otel.source` replaces `builtin.source.otel`; legacy `otlp-http://` URIs and version 1 bind/port/path settings remain compatible. Normalization/validation perform no external I/O. Discovery happens only through explicit Browse actions; opening performs transport/provider checks. CLI account credentials remain with the CLI and are not serialized in plugin configuration.

## Ownership and cancellation

LogAce supplies spool paths through `IPluginSourceHost`; the plugin owns acquired processes/listeners and files. Cancelled/failed startup releases acquired resources and deletes partial spools. Once open returns successfully, the live source owns its own shutdown token: cancelling the previous opening operation does not stop it. Disposing the returned `ILogSource` stops producers, closes sockets/processes and removes the temporary spool. Stderr is drained concurrently, with at most 16 KiB retained for diagnostics.

Each top-level type has its own source file. The named source factories own capture and transport; catalog and normalizer types own discovery and format conversion. `SourceRuntime.cs` isolates opening/discovery operations; `Configuration.cs` handles versioned JSON, and `TemporaryFileLifetime.cs` owns completed snapshot spools.

## Development and tests

From a LogAce checkout with this optional catalog submodule:

```sh
dotnet run --project LogAce -- plugins dev submodules/logace-plugins/plugins/otel.source
dotnet run --project LogAce -- --plugin-dir .local/plugins
dotnet test submodules/logace-plugins/tests/OtelSource.Tests/OtelSource.Tests.csproj
```

For a standalone catalog checkout, pass `-p:LogAceRoot=/absolute/path/to/logace` to `dotnet test`. Only `LogAce.PluginApi` is referenced; the application and Terminal.Gui are not test dependencies. CLI fixtures never contact accounts or devices; listener tests use local loopback sockets. Platform-specific capture tests run on their supported OS. LogAce’s own optional integration tests verify picker/forms at 80×24 and 120×32, sessions and parser output. Real account/device access is an additional manual smoke check.

The manifest lists every compiled C# source. After edits, rerun `plugins dev`; before submitting a published update, bump the version in `plugin.json`, runtime metadata and the matching `index.json` entry. No source checksums need updating. Do not commit compiled output, credentials or local activation state.
