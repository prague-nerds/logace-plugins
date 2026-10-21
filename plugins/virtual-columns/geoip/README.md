# GeoIP virtual columns

Enrich IP addresses with location, ASN and organization.

Install from the virtual-column type picker or Tools > Plugins. The package ID is `geoip.columns`.

## Provided types

- `geoip`: Country, region and city for an IP address.
- `geoip-country`: Country for an IP address.
- `geoip-region`: Region for an IP address.
- `geoip-city`: City for an IP address.
- `geoip-asn`: Autonomous system number for an IP address.
- `geoip-org`: Network organization for an IP address.

Existing type IDs, aliases and parameters are preserved. LogAce offers to install the package when restoring a session or profile that uses these types.

GeoIP uses the configured MMDB paths and existing `LOGACE_GEOIP_CITY_DB_PATH` / `LOGACE_GEOIP_ASN_DB_PATH` overrides. Without a local database it retains the existing ipwho.is lookup behavior.

Evaluators retain background lookup, materialization and cancellation behavior. The host disposes evaluators after closing their consumers.

For local development: `LogAce plugins dev plugins/virtual-columns/geoip`. See [CONTRIBUTING](../../../CONTRIBUTING.md).
