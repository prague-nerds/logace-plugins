# Text snapshot source plugin

Choose Text snapshot in LogAce's Tools > Plugins catalog, review and approve it, then restart LogAce and open it from the source picker. Entered text becomes a temporary log snapshot; its file is deleted on tab close. Sessions retain the text and restore it using the same public API. No NuGet dependencies are used.

Changes require a pull request to prague-nerds/logace-plugins with a version bump and matching manifest/runtime/index metadata. No source checksums are stored in the repository. See the repository's CONTRIBUTING.md.
