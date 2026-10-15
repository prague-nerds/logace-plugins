# Contributing a plugin

Submit a pull request to **prague-nerds/logace-plugins**, LogAce's sole public plugin source. Maintainers review the source and NuGet pins users will install.

1. Choose a unique lowercase ID (`[a-z0-9][a-z0-9._-]{0,127}`); `builtin.` is reserved. Add `plugins/<id>/plugin.json` and `plugin.cs`. Use public `LogAce.PluginApi`; native dialogs also use its optional Terminal.Gui adapter.
2. Put metadata in `plugin.json`: `manifestVersion: 1`, ID, exact version, canonical kind (`parser`, `source`, `virtualColumnProvider`), entry point, API, `targetFramework: "net10.0"`, display name, description, capabilities, source paths and direct NuGet dependencies. The C# entry point's runtime identity/version must match. Document usage, configuration, credentials, cancellation and cleanup in README.
3. List every compiled C# file as a relative path in the manifest's `sources` array. Source hashes are not stored in the repository. If there are NuGet dependencies, list direct `PackageId@version` references in the manifest's `packages` array and add `plugin.lock.json` with `lockVersion: 2` and all direct/transitive package IDs, exact versions and nupkg SHA-512 hashes. Omit the lock when `packages` is empty. Portable managed dependencies are supported; opaque DLLs, native/RID assets, build scripts and generated outputs are unsupported.
4. For source UI, declare `ui.modes` in the manifest: `declarativeForm` and/or `terminalGuiDialog`. Native dialogs require the exact UI API and host fingerprint from `LogAce --plugin-host-info` in both manifest and index.
5. For your working copy, run `LogAce plugins dev <folder>` to build into `.local/plugins`, then `LogAce --plugin-dir .local/plugins`. Dev refreshes only the native UI binding in the manifest when necessary; source edits need no checksum update. Package pins and API/identity fields remain explicit. Alternatively use `plugins inspect`, then `plugins install/update --approve <reviewHash>` with an explicit development `--plugin-dir`. Test parser/source UI/session/column integration as applicable. Keep compiled artifacts and local activation state out of the PR.
6. Add a matching schema-version-4 `index.json` entry with ID, version, display name, description, canonical kind, API, capabilities and optional UI metadata. For source plugins, include `presentation.category` and `presentation.keywords` matching the runtime `PluginSourcePresentation`; these index-only fields allow discovery before installation. No source, manifest or lock hashes belong in the index. Paths are implicit under `plugins/<id>/`; alternate URLs and Git references are unsupported.
7. Bump the version for published changes, in the manifest, runtime metadata and index. Updates are offered by version. Summarize source, capability and dependency changes in the PR. Preserve stable IDs and migrate saved source configuration where needed.

LogAce fetches the index, manifest, optional NuGet lock and sources from one Git commit. Approval binds the exact captured files; successful installation stores one locally computed SHA-256 for the whole snapshot. This hash includes the raw manifest, optional lock, source paths and bytes. It detects changes before automatic recompilation, without requiring per-file checksums in this repository. Third-party code runs with the user's privileges.

## Package format

A plugin without NuGet dependencies needs only a manifest and its listed C# files:

```json
{
  "manifestVersion": 1,
  "id": "example.source",
  "version": "1.0.1",
  "displayName": "Example source",
  "description": "Read a service's logs.",
  "kind": "source",
  "entryPoint": "Example.SourcePlugin",
  "apiVersion": 1,
  "targetFramework": "net10.0",
  "sources": ["plugin.cs", "helpers/Extra.cs"],
  "capabilities": ["UsesNetwork"],
  "packages": [],
  "ui": { "modes": ["declarativeForm"] }
}
```

With direct dependencies such as `"packages": ["Example.Client@1.0.0"]`, add this NuGet-only lock:

```json
{
  "lockVersion": 2,
  "packages": [
    { "id": "Example.Client", "version": "1.0.0", "sha512": "<base64 SHA-512 of exact .nupkg bytes>" }
  ]
}
```

The previous source-header/combined-lock format and schema-version-1 index are replaced. These packages require a LogAce host supporting separate manifests; older hosts must be updated before using this catalog.

## C# layout and readability

Use four spaces and the repository's `.editorconfig`. Put braces around control-flow bodies and expand statement blocks onto separate lines. Separate methods, constructors and documented members with one blank line, placing that line before their XML documentation. Inside methods, separate guards, loops and other logical sections with a blank line.

Keep one top-level class, interface, record or enum per file, named after the type. The package entry point stays in `plugin.cs`. Small private nested types can remain with their owner when they describe an implementation detail. The compiler compiles all source files explicitly listed in `plugin.json` together. When splitting or adding files, update that list.

Sources that need a specialized format must declare `requiredParsers` in both `plugin.json` and their catalog entry. Use stable parser IDs, not source IDs or assembly names. The host reviews parser packages from the same pinned catalog commit before installing the source. Keep `apiVersion`, source metadata version and manifest/catalog version aligned; test parser packages through the API-only source compiler.
