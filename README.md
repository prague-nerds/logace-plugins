# LogAce plugins

The official source catalog for LogAce is the public repository **prague-nerds/logace-plugins**. Install from **Tools → Plugins…** in LogAce. New plugins and updates require pull requests; see [CONTRIBUTING.md](CONTRIBUTING.md).

Plugins are reviewable C# source with a pinned lock file. LogAce fetches one commit, verifies hashes, presents source and dependencies for approval, then compiles locally. Third-party code runs with your user privileges. This is not a sandbox.

`index.json` is schema version 1. Packages live in `plugins/<id>/`; the initial `example.snapshot-source` opens entered text as a temporary log. Each package has `plugin.cs`, `plugin.lock.json` and optional explicitly locked helper files. Index entries contain no external URLs or links to other source repositories.

## Local development

From the LogAce checkout, optionally initialize this repository with `./scripts/setup-plugin-development.sh`, create a branch inside `submodules/logace-plugins`, then build and run:

```sh
dotnet run --project LogAce -- plugins dev submodules/logace-plugins/plugins/example.snapshot-source
dotnet run --project LogAce -- --plugin-dir .local/plugins
```

The development command refreshes declared source hashes and the native UI host binding, then builds into an isolated local registry. Repeat it and restart LogAce after edits. Dependency pins and API/identity metadata remain explicit. Before submitting a PR, update `index.json` with the final raw lock's SHA-256 and matching metadata. Keep compiled artifacts and local state out of the repository.

Require pull requests and maintainer approval on `main` using GitHub rulesets/branch protection.
