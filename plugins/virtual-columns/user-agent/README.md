# User-Agent virtual columns

Identify browsers, operating systems, devices and bots from User-Agent strings.

Install from the virtual-column type picker or Tools > Plugins. The package ID is `user-agent.columns`.

## Provided types

- `ua-bot`: Detect a bot from its User-Agent (yes/no).
- `ua-bot-name`: Bot name from its User-Agent.
- `ua-bot-category`: Bot category from its User-Agent.
- `ua-browser`: Browser name.
- `ua-browser-version`: Browser version.
- `ua-os`: Operating system.
- `ua-device`: Device type or name.

Existing type IDs, aliases and parameters are preserved. LogAce offers to install the package when restoring a session or profile that uses these types.

Datasets are updated from Settings > Bots & IP. Existing downloaded files are copied on first use into this plugin’s data directory. Updates validate before replacing the last usable snapshot.

Evaluators retain background lookup, materialization and cancellation behavior. The host disposes evaluators after closing their consumers.

For local development: `LogAce plugins dev plugins/virtual-columns/user-agent`. See [CONTRIBUTING](../../../CONTRIBUTING.md).
