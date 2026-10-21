# LogAce plugins

The official plugin catalog for LogAce is the public repository **prague-nerds/logace-plugins**. Install from **Tools → Plugins…** in LogAce. New plugins and updates require pull requests; see [CONTRIBUTING.md](CONTRIBUTING.md).

Plugins are reviewable C# source with a separate `plugin.json` manifest. LogAce fetches one Git commit, presents the manifest, source and any pinned NuGet dependencies for approval, then compiles locally. A single hash of the approved snapshot is stored locally; no source hashes are maintained in this repository. Third-party code runs with your user privileges. This is not a sandbox.

`index.json` is schema version 5 and requires a LogAce host supporting separate plugin manifests. Packages are grouped under `plugins/sources/`, `plugins/parsers/` and `plugins/virtual-columns/`; `docker.source` and `kubectl.source` provide optional Docker and Kubernetes snapshots and live streams; `example.snapshot-source` opens entered text as a temporary log for learning the API. Each package has `plugin.cs`, `plugin.json` and helper files explicitly listed in the manifest. A NuGet-only `plugin.lock.json` is needed only for dependency packages; GeoIP and User-Agent include pinned direct and transitive dependencies. Source entries include `presentation.category` and `presentation.keywords` matching their runtime `PluginSourcePresentation`, so Open can group and search them before installation. Hosts must support catalog version 5; updated hosts also accept version 2, 3 and 4 indexes and caches. Virtual-column entries declare `virtualColumns` with stable IDs, aliases, descriptions and parameter hints so they can be found before installation. Index entries contain no external URLs or links to other source repositories.

## Available plugins

### Sources (`source`)

Source plugins connect LogAce to services, devices and live log streams. Install them from **Open → Load plugins…** or **Tools → Plugins…**. Required parser plugins are reviewed and installed alongside the source.

| Plugin | Purpose | Required parser |
| --- | --- | --- |
| [Android device (logcat)](plugins/sources/logcat/README.md) | Android logcat snapshots and live streams through adb. | [`logcat`](plugins/parsers/logcat/README.md) |
| [AWS CloudWatch Logs](plugins/sources/cloudwatch/README.md) | CloudWatch log group snapshots and live streams through AWS CLI v2. | [`cloudwatch`](plugins/parsers/cloudwatch/README.md) |
| [Azure logs](plugins/sources/azure/README.md) | Azure App Service streams and Log Analytics queries through Azure CLI. | — |
| [Coolify logs](plugins/sources/coolify/README.md) | Browse Coolify applications and read runtime or deployment logs with live polling. | [`docker`](plugins/parsers/docker/README.md) |
| [Docker container](plugins/sources/docker/README.md) | Docker container log snapshots and live streams through the Docker CLI. | [`docker`](plugins/parsers/docker/README.md) |
| [GCP Cloud Logging](plugins/sources/gcloud/README.md) | Cloud Logging snapshots and live streams through gcloud. | — |
| [iOS simulator](plugins/sources/simctl/README.md) | Unified log snapshots from booted iOS simulators through xcrun simctl. | — |
| [Journalctl service](plugins/sources/journalctl/README.md) | Local systemd journal snapshots and live streams through journalctl. | [`journalctl`](plugins/parsers/journalctl/README.md) |
| [Kubernetes pod](plugins/sources/kubectl/README.md) | Kubernetes pod log snapshots and live streams through kubectl. | [`kubectl`](plugins/parsers/kubectl/README.md) |
| [macOS unified log](plugins/sources/oslog/README.md) | Host macOS unified log snapshots and live streams through the log command. | — |
| [OpenTelemetry logs](plugins/sources/otel/README.md) | Receive OpenTelemetry logs over OTLP/HTTP as JSON or protobuf. | [`otel-logs`](plugins/parsers/otel-logs/README.md) |
| [Syslog sink](plugins/sources/syslog/README.md) | Receive syslog messages over UDP and TCP with newline or octet-counted framing. | [`syslog-sink`](plugins/parsers/syslog-sink/README.md) |
| [Text snapshot](plugins/sources/example.snapshot-source/README.md) | Open entered text as a temporary log snapshot. | — |

### Log formats (`parser`)

Parser plugins turn log text into records and columns. Install them directly from **F8 → Load plugins…** or **Tools → Plugins…**. RAW, CSV/TSV, JSONL and Ad Hoc are included in LogAce.

| Plugin | Supported format |
| --- | --- |
| [Apache / W3C Combined](plugins/parsers/apache/README.md) | Apache combined access logs. |
| [CloudWatch](plugins/parsers/cloudwatch/README.md) | CloudWatch detailed-format logs, including multiline messages. |
| [Docker](plugins/parsers/docker/README.md) | Docker logs with timestamps. |
| [IIS / W3C Extended](plugins/parsers/iis/README.md) | IIS W3C Extended logs. Columns follow the #Fields: header. |
| [Journalctl](plugins/parsers/journalctl/README.md) | systemd logs in journalctl short, short-iso or short-iso-precise format. |
| [Kubectl](plugins/parsers/kubectl/README.md) | Kubernetes logs with timestamps. |
| [Logcat](plugins/parsers/logcat/README.md) | Android logcat in threadtime format, including multiline messages. |
| [Merged](plugins/parsers/merged/README.md) | Tabs combined into a timeline, sorted by UTC timestamp. |
| [NGINX Error](plugins/parsers/nginx-error/README.md) | NGINX error logs, including multiline messages. |
| [OTel Logs](plugins/parsers/otel-logs/README.md) | OTLP logs with resource, scope, trace, span and message fields. |
| [Spring Boot](plugins/parsers/spring-boot/README.md) | Spring Boot logs, including multiline messages. |
| [Syslog (RFC 3164 / RFC 5424)](plugins/parsers/syslog/README.md) | Syslog in RFC 3164 or RFC 5424 format, detected automatically. |
| [Syslog Sink](plugins/parsers/syslog-sink/README.md) | Received syslog messages with sender, transport and severity fields. |

These plugins retain the existing profile IDs, so saved sessions, column layouts and highlights continue to work. A missing or disabled parser can be installed or enabled when reopening a saved tab.

### Virtual columns (`virtualColumnProvider`)

Install from **Columns → Add virtual / Type → Install & Use**, the Ad Hoc type picker, or **Tools → Plugins**. One package provides a related family of types. Saved sessions and profiles retain their IDs, aliases, parameters and column dependencies; LogAce offers to install or enable missing providers before use.

| Plugin | Provided types |
| --- | --- |
| [GeoIP](plugins/virtual-columns/geoip/README.md) | `geoip`, `geoip-country`, `geoip-region`, `geoip-city`, `geoip-asn`, `geoip-org` |
| [User-Agent](plugins/virtual-columns/user-agent/README.md) | `ua-bot`, `ua-bot-name`, `ua-bot-category`, `ua-browser`, `ua-browser-version`, `ua-os`, `ua-device` |
| [Bots & IP](plugins/virtual-columns/bots-ip/README.md) | `bot-ip-name`, `bot-verification`, `ip-datacenter` |
| [HTTP & URL](plugins/virtual-columns/http/README.md) | `http-status-class`, `url-host`, `url-path`, `url-query`, `url-extension`, `referrer-host`, `query-param`, `header`, `url-route`, `resource-type`, `traffic-source` |
| [Network](plugins/virtual-columns/network/README.md) | `hostname`, `ip-private`, `ip-version`, `port-service`, `cidr` |

The app retains 14 generic types: `regex-group`, `json-path`, `email-domain`, `exception-type`, `log-level-normalized`, `coalesce`, `concat`, `expression`, `lookup`, `timestamp-floor`, `timestamp-part`, `duration-bucket`, `bytes-human`, and `bytes-bucket`.

## Package layout

```text
plugins/
  sources/
    docker/                  ID: docker.source
    cloudwatch/              ID: cloudwatch.source
    example.snapshot-source/ ID: example.snapshot-source
  parsers/
    docker/                  ID: docker
    apache/                  ID: apache
  virtual-columns/
    geoip/                   ID: geoip.columns
    http/                    ID: http.columns
```

The plugin ID in `plugin.json` is stable and independent of its directory name. Kinds map to `sources`, `parsers` and `virtual-columns`. Conventional `.source`, `.parser` and `.columns` suffixes are omitted from directory names; other IDs are used unchanged. For example, `docker.source` lives in `plugins/sources/docker` while the `docker` parser lives in `plugins/parsers/docker`.

Grouped paths were introduced with catalog v4. Catalog v5 adds virtual-column discovery metadata to matching index entries and manifests. The updated host reads both grouped packages and the legacy `plugins/<id>/` layout, including catalog versions 2, 3 and 4 and pinned cached commits. Only a missing manifest (HTTP 404) triggers a layout fallback; invalid metadata, missing source files, network failures and cancellation never mix files from different directories. Every package is reviewed and downloaded from one directory in one commit.

Old local `plugins inspect`, `install`, `update` and `dev` paths also continue to work with the updated host: `plugins/docker.source` resolves to `plugins/sources/docker` after the move. Grouped paths also resolve against older flat checkouts. Resolution verifies the manifest ID and kind and never overrides an existing manifest. Installed artifacts, approved snapshots, sessions, settings and credentials remain keyed by the existing plugin ID; they are not moved or renamed. Hosts without grouped-layout support must update before downloading from the reorganized repository.

## Local development

From the LogAce checkout, optionally initialize this repository with `./scripts/setup-plugin-development.sh`, create a branch inside `submodules/logace-plugins`, then build and run:

```sh
dotnet run --project LogAce -- plugins dev submodules/logace-plugins/plugins/sources/example.snapshot-source
dotnet run --project LogAce -- --plugin-dir .local/plugins
```

For a source with a required parser, build the parser first in the same isolated registry.

The development command builds manifest-listed sources into an isolated local registry and refreshes only the native UI host binding when required. Repeat it and restart LogAce after edits. Dependency pins and API/identity metadata remain explicit. Before submitting a PR, bump the plugin version in its manifest, runtime metadata and `index.json`, and keep catalog metadata aligned. Keep compiled artifacts and local state out of the repository.

Require pull requests and maintainer approval on `main` using GitHub rulesets/branch protection.

## AWS CloudWatch source

[`cloudwatch.source`](plugins/sources/cloudwatch/README.md) provides AWS CLI v2 log group discovery, snapshots and live follow through the public source API. Its standalone tests use fake CLI fixtures; credentials stay with the AWS CLI.
