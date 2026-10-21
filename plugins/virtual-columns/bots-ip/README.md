# Bots & IP virtual columns

Identify crawlers and datacenters and verify crawler identity using downloaded IP ranges.

Install from the virtual-column type picker or Tools > Plugins. The package ID is `bots-ip.columns`.

## Provided types

- `bot-ip-name`: Crawler name from downloaded IP ranges.
- `bot-verification`: Check a User-Agent against its crawler IP ranges.
- `ip-datacenter`: Hosting provider from downloaded datacenter IP ranges.

Existing type IDs, aliases and parameters are preserved. LogAce offers to install the package when restoring a session or profile that uses these types.

Datasets are updated from Settings > Bots & IP. Existing downloaded files are copied on first use into this plugin’s data directory. Updates validate before replacing the last usable snapshot.

Evaluators retain background lookup, materialization and cancellation behavior. The host disposes evaluators after closing their consumers.

For local development: `LogAce plugins dev plugins/virtual-columns/bots-ip`. See [CONTRIBUTING](../../../CONTRIBUTING.md).
