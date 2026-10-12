# LogAce plugins

The official source catalog for LogAce is the public repository **prague-nerds/logace-plugins**. Install from **Tools → Plugins…** in LogAce. New plugins and updates require pull requests; see [CONTRIBUTING.md](CONTRIBUTING.md).

Plugins are reviewable C# source with a pinned lock file. LogAce fetches one commit, verifies hashes, presents source and dependencies for approval, then compiles locally. Third-party code runs with your user privileges. This is not a sandbox.

`index.json` is schema version 1. Packages live in `plugins/<id>/`; `docker.source` and `kubectl.source` provide optional Docker and Kubernetes snapshots and live streams; `example.snapshot-source` opens entered text as a temporary log for learning the API. Each package has `plugin.cs`, `plugin.lock.json` and optional explicitly locked helper files. Index entries contain no external URLs or links to other source repositories.

## Available plugins

| Plugin | Purpose |
| --- | --- |
| [Docker container](plugins/docker.source/README.md) | Browse containers, open snapshots and follow logs through the Docker CLI. |
| [Kubernetes pod](plugins/kubectl.source/README.md) | Browse pods and containers, open snapshots and follow logs through kubectl. |
| Text snapshot | Minimal declarative form example for plugin authors. |

## Local development

From the LogAce checkout, optionally initialize this repository with `./scripts/setup-plugin-development.sh`, create a branch inside `submodules/logace-plugins`, then build and run:

```sh
dotnet run --project LogAce -- plugins dev submodules/logace-plugins/plugins/example.snapshot-source
dotnet run --project LogAce -- --plugin-dir .local/plugins
```

The development command refreshes declared source hashes and the native UI host binding, then builds into an isolated local registry. Repeat it and restart LogAce after edits. Dependency pins and API/identity metadata remain explicit. Before submitting a PR, update `index.json` with the final raw lock's SHA-256 and matching metadata. Keep compiled artifacts and local state out of the repository.

Require pull requests and maintainer approval on `main` using GitHub rulesets/branch protection.

## AWS CloudWatch source

[`cloudwatch.source`](plugins/cloudwatch.source/README.md) provides AWS CLI v2 log group discovery, snapshots and live follow through the public source API. Its standalone tests use fake CLI fixtures; credentials stay with the AWS CLI.
