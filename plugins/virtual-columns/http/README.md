# HTTP & URL virtual columns

Extract and classify HTTP requests, URLs, headers and traffic.

Install from the virtual-column type picker or Tools > Plugins. The package ID is `http.columns`.

## Provided types

- `http-status-class`: Group status codes into 2xx, 3xx, 4xx and 5xx.
- `url-host`: Host from a URL.
- `url-path`: URL path without the query string.
- `url-query`: Query string from a URL.
- `url-extension`: File extension from a URL.
- `referrer-host`: Host from a referrer URL.
- `query-param`: Read one named query parameter.
- `header`: Read one named HTTP header.
- `url-route`: Group URL paths by replacing IDs, UUIDs and hashes.
- `resource-type`: Classify resources by URL and Content-Type.
- `traffic-source`: Internal, search, social or external traffic from a referrer.

Existing type IDs, aliases and parameters are preserved. LogAce offers to install the package when restoring a session or profile that uses these types.

Evaluators retain background lookup, materialization and cancellation behavior. The host disposes evaluators after closing their consumers.

For local development: `LogAce plugins dev plugins/virtual-columns/http`. See [CONTRIBUTING](../../../CONTRIBUTING.md).
