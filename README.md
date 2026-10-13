# LogAce plugins

The official source catalog for LogAce is the public repository **prague-nerds/logace-plugins**. Install from **Tools → Plugins…** in LogAce. New plugins and updates require pull requests; see [CONTRIBUTING.md](CONTRIBUTING.md).

Plugins are reviewable C# source with a separate `plugin.json` manifest. LogAce fetches one Git commit, presents the manifest, source and any pinned NuGet dependencies for approval, then compiles locally. A single hash of the approved snapshot is stored locally; no source hashes are maintained in this repository. Third-party code runs with your user privileges. This is not a sandbox.

`index.json` is schema version 2 and requires a LogAce host supporting separate plugin manifests. Packages live in `plugins/<id>/`; `docker.source` and `kubectl.source` provide optional Docker and Kubernetes snapshots and live streams; `example.snapshot-source` opens entered text as a temporary log for learning the API. Each package has `plugin.cs`, `plugin.json` and helper files explicitly listed in the manifest. A NuGet-only `plugin.lock.json` is needed only for dependency packages; the current plugins use none. Index entries contain no external URLs or links to other source repositories.

## Available plugins

| Plugin | Purpose |
| --- | --- |
| [Docker container](plugins/docker.source/README.md) | Browse containers, open snapshots and follow logs through the Docker CLI. |
| [Kubernetes pod](plugins/kubectl.source/README.md) | Browse pods and containers, open snapshots and follow logs through kubectl. |
| [GCP Cloud Logging](plugins/gcloud.source/README.md) | Cloud Logging snapshots and live streams through gcloud. |
| [Azure logs](plugins/azure.source/README.md) | Azure App Service streams and Log Analytics queries through Azure CLI. |
| [Journalctl service](plugins/journalctl.source/README.md) | Local systemd journal snapshots and live streams through journalctl. |
| [iOS simulator](plugins/simctl.source/README.md) | Unified log snapshots from booted iOS simulators through xcrun simctl. |
| [Android device (logcat)](plugins/logcat.source/README.md) | Android logcat snapshots and live streams through adb. |
| [macOS unified log](plugins/oslog.source/README.md) | Host macOS unified log snapshots and live streams through the log command. |
| [OpenTelemetry logs](plugins/otel.source/README.md) | Receive OpenTelemetry logs over OTLP/HTTP as JSON or protobuf. |
| [Syslog sink](plugins/syslog.source/README.md) | Receive syslog messages over UDP and TCP with newline or octet-counted framing. |
| Text snapshot | Minimal declarative form example for plugin authors. |

## Local development

From the LogAce checkout, optionally initialize this repository with `./scripts/setup-plugin-development.sh`, create a branch inside `submodules/logace-plugins`, then build and run:

```sh
dotnet run --project LogAce -- plugins dev submodules/logace-plugins/plugins/example.snapshot-source
dotnet run --project LogAce -- --plugin-dir .local/plugins
```

The development command builds manifest-listed sources into an isolated local registry and refreshes only the native UI host binding when required. Repeat it and restart LogAce after edits. Dependency pins and API/identity metadata remain explicit. Before submitting a PR, bump the plugin version in its manifest, runtime metadata and `index.json`, and keep catalog metadata aligned. Keep compiled artifacts and local state out of the repository.

Require pull requests and maintainer approval on `main` using GitHub rulesets/branch protection.

## AWS CloudWatch source

[`cloudwatch.source`](plugins/cloudwatch.source/README.md) provides AWS CLI v2 log group discovery, snapshots and live follow through the public source API. Its standalone tests use fake CLI fixtures; credentials stay with the AWS CLI.
