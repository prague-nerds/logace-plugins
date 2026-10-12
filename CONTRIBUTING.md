# Contributing a plugin

Submit a pull request to **prague-nerds/logace-plugins**, LogAce's sole public plugin source. Maintainers review the exact source and pins users will install.

1. Choose a unique lowercase ID (`[a-z0-9][a-z0-9._-]{0,127}`); `builtin.` is reserved. Add `plugins/<id>/plugin.cs` and `plugin.lock.json`. Use public `LogAce.PluginApi`; native dialogs also use its optional Terminal.Gui adapter.
2. Include the source metadata header, exact version, canonical kind (`parser`, `source`, `virtualColumnProvider`), API, entry point, displayName, description and capabilities. Document usage, configuration, credentials, cancellation and cleanup in README.
3. Pin each C# file's SHA-256, and all direct/transitive NuGet versions and nupkg SHA-512 hashes in the lock. Portable managed dependencies are supported. Do not include opaque plugin DLLs, native/RID assets, build scripts or generated outputs.
4. For source UI, declare `declarativeForm` and/or `terminalGuiDialog`. Native dialogs require the exact UI API and fingerprint from `LogAce --plugin-host-info` in both lock and index.
5. For your own working copy, run `LogAce plugins dev <folder>` to refresh declared source hashes/native UI binding and build into `.local/plugins`, then `LogAce --plugin-dir .local/plugins`. Repeat dev and restart after edits; there is no hot reload. Package pins and API/identity fields remain explicit. Alternatively use `plugins inspect`, then `plugins install/update --approve <reviewHash>` with an explicit development `--plugin-dir`. Test parser/source UI/session/column integration as applicable. Keep compiled artifacts and local activation state out of the PR.
6. Add a matching `index.json` entry with ID, version, displayName, description, canonical kind, API, capabilities and optional UI metadata. `lockSha256` is SHA-256 of the final raw lock bytes. Paths are implicit under `plugins/<id>/`; alternate source URLs and Git references are unsupported.
7. Updates bump the version and recalculate source/lock hashes. Summarize source, capability and dependency changes in the PR. Preserve stable IDs and migrate saved source configuration where needed.

Maintainers check source/dependencies, test integration and approve before merge to `main`. Hashes provide integrity and repeatable review; they do not establish trust or limit plugin privileges.

## C# layout and readability

Use four spaces and the repository's `.editorconfig`. Put braces around control-flow bodies and expand statement blocks onto separate lines. Separate methods, constructors and documented members with one blank line, placing that line before their XML documentation. Inside methods, separate guards, loops and other logical sections with a blank line.

Keep one top-level class, interface, record or enum per file, named after the type. The package entry point may stay in the required `plugin.cs` metadata file. Small private nested types can remain with their owner when they describe an implementation detail of that type. The compiler does not require combined provider files: it compiles all source files explicitly listed in `plugin.lock.json` together. When splitting or adding files, update that list and every source hash, then update the catalog's raw lock hash.
