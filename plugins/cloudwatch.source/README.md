# AWS CloudWatch Logs source

`cloudwatch.source` opens log group snapshots and live streams through **AWS CLI v2**.
The package uses only `LogAce.PluginApi` and the .NET standard libraries. It does
not reference private LogAce classes, Terminal.Gui or an AWS SDK.

## Install and open

Install **AWS CloudWatch Logs** from **Tools → Plugins…** in LogAce, enable it and
restart. This repository is the sole catalog; new integrations are contributed
through pull requests. The source then appears in **F2 → Cloud** and File → Open
AWS CloudWatch Logs. Without the plugin, the `--cloudwatch-*` options explain how
to install it before opening the TUI. The separate `cloudwatch` parser is installed as a dependency for
ordinary log files.

Install AWS CLI v2 and configure its normal credential chain, profile and region.
For an SSO profile, authenticate with `aws sso login --profile <profile>` before
opening the source. The plugin inherits the CLI environment and credential helpers;
it never asks for or persists access keys or tokens. Local CLI/profile configuration
is trusted in the same way as when running `aws` directly.

Enter a known log group, or set **AWS profile** and **Region** and use **Browse log
groups…**. Browsing requires `logs:DescribeLogGroups`; manual entry remains available
when listing is denied. Reading requires the AWS permissions used by `logs tail`,
including `logs:FilterLogEvents`. The browser shows names from the selected account
and region, supports case-insensitive search and explicit Refresh, and retains all
main-form settings when a group is selected. Empty results and CLI errors explain
how to recover. AWS CLI handles pagination; the plugin does not limit listing to
its first page.

The initial mode is a **one-hour snapshot**. Set Stream prefix and Filter pattern
to narrow events, and enable **Follow live** for a growing tab. Since accepts a
single duration such as `30m`, `1h` or `2d`, or an ISO 8601 timestamp. A trailing
`*` in Stream prefix is accepted as a convenience and removed before invoking AWS.

```sh
LogAce --cloudwatch-group /aws/lambda/api \
  --cloudwatch-profile prod --cloudwatch-region eu-central-1 \
  --cloudwatch-stream 'app-*' --cloudwatch-filter ERROR \
  --cloudwatch-since 30m --cloudwatch-follow
```

`--cloudwatch-group` may be repeated. All existing CloudWatch CLI options are
translated into the same portable configuration as the form. Recent history,
named sessions and independent duplicate tabs are supported. The plugin must be
installed and enabled when restoring those sources.

## Public API and ownership

- `plugin.cs` declares metadata, picker presentation and neutral forms. Async
  discovery actions request nested forms through `IPluginSourceUiHost`; LogAce
  owns UI dispatch, cancellation, modal focus and configuration confirmation.
- `CloudWatchConfiguration.cs` normalizes and validates version 1 JSON. Persistent
  fields are `logGroup`, `profile`, `region`, `streamFilter`, `filterPattern`,
  `since` and numeric `openMode` (`0` snapshot, `1` follow). This matches the former
  built-in schema. `follow` is a form-only checkbox; search is not persisted.
- `CloudWatchGroupCatalog.cs` parses JSON group names and drains both process
  pipes concurrently. Profile and region scope the CLI discovery call.
- `CloudWatchCli.cs` constructs separate `ProcessStartInfo.ArgumentList` values,
  disables shell execution, pager and automatic prompts, and bounds retained
  diagnostics while continuing to drain the pipe. CLI stderr is diagnostic data;
  only stdout becomes log content. Authentication errors include a recovery hint.
- `CloudWatchLogSource.cs` owns the temporary spool and follow worker. LogAce
  supplies the spool location through `IPluginSourceHost`. A failed/cancelled open
  deletes partial output. Disposing a source stops its process tree, waits for
  readers with bounded UI shutdown and deletes the spool. The opening token covers
  startup; cancelling it after a successful open does not stop follow.
- `CloudWatchFollowCursor.cs` retains occurrence counts within a two-minute overlap,
  capped at 20,000 distinct timestamped lines. A reconnect uses the oldest retained
  timestamp minus one millisecond. Matching replayed headers and their multiline
  continuations are suppressed; repeated headers beyond their old occurrence count
  remain visible. A quiet source retries its original Since range.

The command uses `logs tail --format detailed`, supplying timestamp, stream and
message columns to the optional `cloudwatch` parser. The former built-in command requested
`short`, which omits the stream and full timestamp; this extraction corrects that
mismatch while keeping the existing `cloudwatch://` identity and state files.
Old `builtin.source.cloudwatch` and URI-only CloudWatch sessions are migrated by
the host. Existing `.las` filters/bookmarks remain usable; a newer plugin state
file takes precedence.

Follow is best effort. AWS does not guarantee event timestamp ordering. Retention,
late ingestion, events outside the overlap and reconnect delays can cause repeats
or missing events. CLI output has no durable event IDs; equal headers with different
continuations are ambiguous. This source does not promise exactly-once delivery.
Failures during initial startup are reported; later disconnects retry every second
and may remain quiet until AWS access returns. Spools grow for the tab's lifetime.

Capabilities in the metadata describe behavior for review; they are not sandbox
permissions. Installing this plugin trusts its code and the system `aws` executable
to run with the user's access. One locally computed snapshot hash binds the manifest and source to the approved package.
No compiled binaries, credentials or local activation state belong in this catalog.

## Develop and test

From a LogAce checkout with the optional catalog submodule:

```sh
dotnet run --project LogAce -- plugins dev submodules/logace-plugins/plugins/cloudwatch.source --plugin-dir .local/plugins
dotnet run --project LogAce -- --plugin-dir .local/plugins
dotnet test submodules/logace-plugins/tests/CloudWatchSource.Tests/CloudWatchSource.Tests.csproj
```

From a standalone catalog checkout, supply the public API checkout:

```sh
dotnet test tests/CloudWatchSource.Tests/CloudWatchSource.Tests.csproj -p:LogAceRoot=/path/to/logace
```

Tests link the package sources and public API only. POSIX fake-CLI fixtures never
contact AWS: they cover arguments, schema validation, discovery/search/errors,
empty snapshots, pipe draining, cancellation, startup failures, reconnect replay,
independent follow lifetime and disposal. PATH fixtures are serialized; those CLI
tests are skipped on Windows. Host tests separately exercise package compilation,
loading, legacy sessions, state migration, alias search and real Terminal.Gui
selection/focus at 80×24 and 120×32. A real AWS account smoke test is still required
for environment-specific credentials and IAM policies.

Before submitting a PR, run `plugins dev` and tests, bump the version in `plugin.json`,
runtime metadata and `index.json`, and keep their metadata aligned. See
[CONTRIBUTING](../../CONTRIBUTING.md) for the catalog review workflow.

## AWS references

- [AWS CLI logs tail](https://docs.aws.amazon.com/cli/latest/reference/logs/tail.html):
  formats, Since, stream prefix, filter syntax and ordering limits.
- [AWS CLI describe-log-groups](https://docs.aws.amazon.com/cli/latest/reference/logs/describe-log-groups.html):
  listing and automatic pagination.
