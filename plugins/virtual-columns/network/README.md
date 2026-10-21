# Network virtual columns

Inspect IP addresses, networks and ports and resolve hostnames.

Install from the virtual-column type picker or Tools > Plugins. The package ID is `network.columns`.

## Provided types

- `hostname`: Resolve an IP address to a hostname using reverse DNS.
- `ip-private`: Identify private IP addresses.
- `ip-version`: IPv4 or IPv6.
- `port-service`: Common service for a port number.
- `cidr`: Group IP addresses by a network prefix.

Existing type IDs, aliases and parameters are preserved. LogAce offers to install the package when restoring a session or profile that uses these types.

Evaluators retain background lookup, materialization and cancellation behavior. The host disposes evaluators after closing their consumers.

For local development: `LogAce plugins dev plugins/virtual-columns/network`. See [CONTRIBUTING](../../../CONTRIBUTING.md).
