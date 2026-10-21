# Virtual-column plugins

Each package implements `ILogAceVirtualColumnProviderPlugin` and lives under `plugins/virtual-columns/<name>/`. The manifest carries the stable `<name>.columns` ID; individual column IDs and aliases are preserved across extraction from the host.

| Plugin | Provided types |
| --- | --- |
| [GeoIP](geoip/README.md) | `geoip`, `geoip-country`, `geoip-region`, `geoip-city`, `geoip-asn`, `geoip-org` |
| [User-Agent](user-agent/README.md) | `ua-bot`, `ua-bot-name`, `ua-bot-category`, `ua-browser`, `ua-browser-version`, `ua-os`, `ua-device` |
| [Bots & IP](bots-ip/README.md) | `bot-ip-name`, `bot-verification`, `ip-datacenter` |
| [HTTP & URL](http/README.md) | `http-status-class`, `url-host`, `url-path`, `url-query`, `url-extension`, `referrer-host`, `query-param`, `header`, `url-route`, `resource-type`, `traffic-source` |
| [Network](network/README.md) | `hostname`, `ip-private`, `ip-version`, `port-service`, `cidr` |

Install directly from the virtual-column type picker. The host keeps generic transforms available and offers to install missing providers when restoring saved sessions and profiles. See [CONTRIBUTING](../../CONTRIBUTING.md).
