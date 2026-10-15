# Coolify logs source

`coolify.source` browses Coolify applications and opens their runtime or deployment
logs as a snapshot or a growing tab. Live mode polls the REST API; Docker, SSH and
a Coolify CLI are not required. The package uses only `LogAce.PluginApi` and .NET
libraries, with no NuGet dependencies or private host/UI references.

## Install and open

Install **Coolify logs** from **Tools → Plugins…**, enable it and restart LogAce.
Open **F2 → Cloud → Coolify logs**. For development before catalog publication,
use the commands below.

1. Enter your Coolify instance URL, for example `https://coolify.example.com`.
   A URL ending in `/api/v1` is accepted too. Prefer HTTPS; HTTP is supported for
   installations on trusted networks. API access must be enabled on self-hosted
   Coolify, and any API IP allowlist must permit your machine.
2. Create a team API token with **read:sensitive**. Ordinary `read` does not allow
   runtime logs and can omit deployment logs. The plugin only sends GET requests;
   it does not need `write`, `deploy` or `root`.
3. Give the connection a name such as **Production** or **Staging**. Either enter the token in the masked **API token** field, or set
   `COOLIFY_API_TOKEN` in the environment inherited by LogAce. The environment
   variable name is configurable for multiple instances. A typed token takes
   precedence; then the environment is checked, followed by the saved token for
   this named connection and instance URL. Tokens are never stored in
   sessions, recent history, settings JSON or the source identity. Enable
   **Remember token** to store it in the system credential vault after a successful
   open. Leave the option off to use a temporary token. Launching LogAce from the
   desktop does not necessarily inherit variables from a terminal shell.
4. Enter a known **Application UUID**, or choose **Browse applications…**. The
   browser lists names, status, domains and UUIDs from the token's team. Search
   and Refresh reload the list. Selecting another application clears the old
   deployment selection. Cancelling leaves the main form unchanged.
5. Choose **Runtime logs** or **Deployment / build logs**.
6. Leave **Follow live** enabled to poll, or disable it for a finite snapshot.
   The default interval is three seconds; supported intervals are 1–300 seconds.

### Saved connections and credentials

**Saved connections…** loads a named profile's nonsecret settings. Switching
profiles clears any token typed for the previous connection. Connection names and
URLs identify credentials together: changing the instance URL does not reuse the
old instance's token. Use separate names for different teams/accounts on one
instance. Settings profiles are remembered after a successful open even when the
token is temporary. The host also pre-fills the last successful configuration after
restart.

**Remember token** uses the host's SecretsBag: macOS Keychain, Windows Credential
Manager, or Linux Secret Service through libsecret. There is no plaintext fallback.
If the vault is locked, denied or unavailable, unlock it or turn Remember token off
and use a temporary/environment token. **Forget saved token** removes the local
credential and clears the Remember token option; it does not revoke the API token
in Coolify. A temporary override does not replace a previously saved vault token.
Reopening the named profile uses its saved token unless another token/environment
value is provided.

Version 1.1.0 requires a LogAce host with the optional plugin storage/resolver
contracts. Plugin API version remains 1. Hosts without initialized services can
still use temporary inputs, but cannot save or select connections.

### Runtime logs

**Last lines** accepts `1`–`10000`, or `all`, and defaults to `2000`. The same
window is requested on every poll, with timestamps enabled. Increase the window
or shorten the interval if an application produces many logs. `all` repeatedly
downloads all retained output and is unsuitable for a large, busy container.

For a Docker Compose application, **Compose service** optionally selects a
`service_name`. Empty uses the first container chosen by Coolify's API; it does
not aggregate every container. Older Coolify versions may not support the
selector. This source targets Coolify applications, including Compose applications;
standalone databases and one-click service resources use different endpoints and
are not included in the application browser.

### Deployment logs

Choose **Browse deployments…** to list deployments by time, status, commit and
UUID. Each page contains 50 entries. Set **Skip newest deployments** to `50`,
`100`, etc., then Refresh to browse older history. Choosing a deployment switches
the main form to deployment mode.

A known deployment UUID can be entered directly. Leave it empty to resolve the
latest deployment when the tab opens. Follow remains pinned to that deployment;
it does not switch to a new deployment automatically. Restoring a saved configuration
with an empty UUID resolves the latest deployment again. A saved explicit UUID
always reopens the same deployment.

Coolify commonly returns deployment logs as a JSON-encoded array. The plugin
renders each record's timestamp, stdout/stderr type and output, preserving
multiline messages, repeated messages and debug records marked `hidden`. Decoded
arrays and plain text are also supported. Runtime and deployment logs use
the optional `docker` timestamp parser, installed as a source dependency.

## Polling, failures and lifetime

The first request must succeed before a tab opens. Authentication, permissions,
invalid JSON, missing log fields and incompatible API responses produce opening
errors and leave no temporary spool behind. HTTP errors never expose response
bodies or the supplied token. Redirects are not followed; TLS uses normal system
certificate validation. Requests have a 30-second deadline and a 32 MiB response
limit, including responses without a Content-Length header.

Subsequent polls compare the longest suffix of the previous snapshot with the
prefix of the current snapshot, including complete timestamps. Only the new
suffix is appended. Identical messages occurring several times in one snapshot
remain separate records. Empty polls preserve the last cursor. The cursor is in
memory and is not a durable delivery offset.

If two nonempty snapshots have no overlap, the plugin appends a timestamped
`[Coolify]` warning and the available new snapshot. Logs may have rotated, the
container may have been replaced, or more lines may have arrived than the polling
window retained. Repetitive identical windows are inherently ambiguous. Polling
does not guarantee lossless or exactly-once delivery, historical recovery after
container removal, or durable retention.

After opening, API failures retain the same spool and retry with backoff up to
60 seconds, or the configured interval if it is longer. Retry-After headers are
respected up to 300 seconds. A changed error
and recovery are recorded as `[Coolify]` lines; repeated identical errors are not
printed on every poll. Local spool write failures end the producer. The spool
grows for the tab's lifetime, so close captures that are no longer needed.

Closing the tab cancels in-flight HTTP requests and the poller, disposes the HTTP
client/writer, and removes the spool. Cancellation used to open the source does
not control an already opened source. Disposal is idempotent and bounds UI
shutdown to two seconds, with asynchronous cleanup if needed.

## Persistent configuration

Version `1` stores the following nonsecret fields; browsing filters and API tokens
are excluded. The optional application name is a display label only.

```json
{
  "instanceUrl": "https://coolify.example.com",
  "connectionName": "Production",
  "rememberToken": true,
  "tokenEnvironment": "COOLIFY_API_TOKEN",
  "applicationUuid": "application-uuid",
  "applicationName": "API",
  "mode": "runtime",
  "deploymentUuid": "",
  "serviceName": "",
  "tail": "2000",
  "follow": true,
  "pollSeconds": 3
}
```

Sessions and recent history are supported. Environment tokens can restore when
their variable remains available. Vault-backed named connections can restore
without a form when the system vault is available. Temporary typed tokens without
a saved credential request that input again. Tab duplication is disabled.

## Development and tests

From the LogAce checkout with the plugin catalog submodule initialized:

```sh
dotnet run --project LogAce -- plugins dev submodules/logace-plugins/plugins/sources/coolify --plugin-dir .local/plugins
dotnet run --project LogAce -- --plugin-dir .local/plugins
dotnet test submodules/logace-plugins/tests/CoolifySource.Tests/CoolifySource.Tests.csproj
```

From a standalone catalog checkout, pass `-p:LogAceRoot=/path/to/logace` to the
test command. Tests compile the exact package sources against the public API and
use a fake HTTP transport. They cover discovery, deployment pagination, token
handling, request construction, response parsing, replay, rate limits, retry,
cancellation and cleanup without contacting a real Coolify installation. Validate
against your installed Coolify version and team permissions for a live smoke test.

The manifest lists all compiled C# files, the catalog index mirrors its metadata,
and no dependency lock is needed. See [CONTRIBUTING](../../../CONTRIBUTING.md).

## API references

- [Applications](https://coolify.io/docs/api/endpoints/applications/list-applications)
- [Runtime logs](https://coolify.io/docs/api/endpoints/applications/get-application-logs-by-uuid)
- [Application deployment history](https://coolify.io/docs/api/endpoints/deployments/list-deployments-by-app-uuid)
- [Deployment detail and logs](https://coolify.io/docs/api/endpoints/deployments/get-deployment-by-uuid)
- [API permissions](https://coolify.io/docs/api/permissions)
