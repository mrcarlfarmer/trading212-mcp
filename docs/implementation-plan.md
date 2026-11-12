# Trading 212 MCP server implementation plan

Research date: 12 November 2026. This document records the implementation plan and the current implementation/deployment boundary.

Implementation update: the repository now contains the .NET 10 read-only server, ten MCP tools, HTTPS stdio bridge, synthetic unit/HTTP protocol tests, ARM64 Dockerfile, Portainer Standalone stack, and TLS proxy configuration. Follow `README.md` for deployment. ARM64/AMD64 image builds and the real AMD64 HTTPS stack have passed local container checks with synthetic credentials, Linux bind-mount permissions, and isolated broker networking. This host cannot execute ARM64 binaries. Native Pi startup, real client certificate trust, and live-account acceptance remain deployment checks. Broker JSON is shape-validated and preserved without floating-point conversion rather than mapped into a large generated DTO model.

## Recommendation

Build a C#/.NET 10 LTS MCP server using the official MCP C# SDK and ASP.NET Core. Run a single Linux container on a **Raspberry Pi**, deployed as a **Portainer Docker Standalone stack**. Expose Streamable HTTP through authenticated HTTPS on the private LAN or a private VPN, not the public internet. The first release must support read-only access to the **live Trading 212 API**, using live credentials stored only on the Pi. Demo access remains optional for testing, not a prerequisite for live reads.

Use one shared server for Codex, Claude Code, and other HTTP-capable clients. Add a thin stdio-to-HTTP bridge for clients such as Claude Desktop's local MCP configuration. The bridge should not call Trading 212 directly: keeping broker access in one process gives all clients a shared rate limiter and cache.

Public internet hosting, hosted Claude/ChatGPT connectors, report generation, trading, and deprecated Pies endpoints are outside the first release. Private-network HTTPS is part of the initial deployment. Trading is a possible future extension, not a feature enabled by a configuration flag in the initial server.

**Required credential boundary:** Trading 212 API keys and secrets must be stored on the Raspberry Pi outside the repository and supplied only to the server at runtime. Consuming AI clients and the stdio bridge must never receive them. A separate MCP access token authenticates clients to the server; it is not a Trading 212 credential. This replaces the earlier WSL hosting and credential-storage requirement.

### Confirmed first-release scope

Use the live base URL `https://live.trading212.com/api/v0` for the intended deployment. Require an explicit startup environment setting (`Live` or `Demo`), with the documented deployment example selecting `Live`. Reject missing or invalid settings; never silently fall back to demo after a live authentication or connection failure. Include the selected environment in tool results.

Expose only the MVP read tools in the inventory below. Do not implement order submission, cancellation, Pies mutations, or CSV report generation in this release. Enforce an allowlist of GET methods and supported read paths in the **broker client**, so even a key accidentally granted write permissions cannot be used to mutate the account through this server. MCP itself still uses HTTP POST to carry protocol messages; this restriction applies to outbound Trading 212 requests, not the MCP transport.

Create a live API key with only the required read scopes. Account authentication and real portfolio access must work without requiring demo credentials or giving any broker credential to an AI app.

Implementation began from a repository containing only Git metadata and a .NET `.gitignore`; there was no application to migrate.

## 1. Findings from the Trading 212 documentation

The official [API reference](https://docs.trading212.com/api) and [OpenAPI document](https://docs.trading212.com/_bundle/api.yaml) describe a beta, version `v0` REST API.

| Finding | Implementation consequence |
| --- | --- |
| Only Invest and Stocks ISA accounts are supported | Do not promise CFD integration or general access to every Trading 212 account type. |
| Demo base URL: `https://demo.trading212.com/api/v0` | Use paper trading for integration testing. |
| Live base URL: `https://live.trading212.com/api/v0` | Select environment at startup; never let an individual tool silently switch environments. |
| HTTP Basic authentication using API key and API secret | Set the broker Authorization header inside the server. Never put broker credentials in tool arguments or results. |
| Keys have permission scopes and optional IP restrictions | Use least-privilege keys. An IP allowlist concerns the broker-facing public egress IP, not the container's private IP. |
| Rate limits apply per account regardless of key or originating IP | Share limits across MCP sessions; additional keys do not increase capacity. Other integrations can still consume the same allowance. |
| History uses cursor pagination; default 20 items, maximum 50 | Return one bounded page per tool call, with continuation information. |
| Instrument/exchange metadata refreshes every 10 minutes | Cache metadata and perform searches locally rather than returning the entire catalogue. |
| Orders use positive quantities for buys and negative quantities for sells | Expose an explicit `side` and positive quantity to AI clients; convert the sign in the broker adapter. |
| All four order-placement endpoints explicitly warn they are not idempotent | Never automatically retry order placement after timeout, disconnection, or an ambiguous response. |
| Maximum 50 pending orders per ticker per account | Validate where possible, but treat the broker as authoritative because state can change. |
| Pies endpoints are marked deprecated | Exclude them from the initial tool catalogue. |

### Capabilities and important gaps

The API covers account summary, positions, instruments, exchange schedules, pending orders, historical orders/dividends/transactions, CSV exports, and order submission/cancellation.

The documented API does **not** provide a general quote feed, candles, an order book, broker event streaming, or order placement by cash value. A current price on an owned position is not a general-purpose quote service.

The overview says multi-currency accounts are unsupported and account values use the primary account currency. However, the detailed position schema explicitly describes `averagePricePaid` and `currentPrice` as instrument-currency values, with account-currency amounts under `walletImpact`. Preserve these distinct currencies rather than applying a blanket currency assumption.

`GET /equity/orders/{id}` retrieves a **pending** order, not a guaranteed permanent order record. A subsequent 404 is not proof of cancellation or non-execution; historical orders may be needed for reconciliation.

Cancellation acceptance is not confirmation that an order cannot fill. Report the accepted cancellation request and subsequently observe the actual state.

### Endpoint inventory and tool mapping

Paths below are relative to `/api/v0`. Limits are documented values, not desired polling frequencies.

| Proposed tool | Upstream method/path | Limit | Release |
| --- | --- | --- | --- |
| `get_account_summary` | GET `/equity/account/summary` | 1 / 5 seconds | MVP |
| `get_positions` | GET `/equity/positions`, optional `ticker` | 1 / second | MVP |
| `search_instruments`, `get_instrument` | GET `/equity/metadata/instruments`, then local filtering | 1 / 50 seconds | MVP |
| `get_exchanges` | GET `/equity/metadata/exchanges` | 1 / 30 seconds | MVP |
| `get_pending_orders` | GET `/equity/orders` | 1 / 5 seconds | MVP |
| `get_pending_order` | GET `/equity/orders/{id}` | 1 / second | MVP |
| `get_order_history` | GET `/equity/history/orders` | 20 / minute | MVP |
| `get_dividend_history` | GET `/equity/history/dividends` | 20 / minute | MVP |
| `get_transactions` | GET `/equity/history/transactions` | 20 / minute | MVP |
| `list_reports` | GET `/equity/history/exports` | 1 / minute | Later |
| `request_report` | POST `/equity/history/exports` | 1 / 30 seconds | Later |
| Approved market submission | POST `/equity/orders/market` | 50 / minute | Optional trading |
| Approved limit submission | POST `/equity/orders/limit` | 1 / 2 seconds | Optional trading |
| Approved stop submission | POST `/equity/orders/stop` | 1 / 2 seconds | Optional trading |
| Approved stop-limit submission | POST `/equity/orders/stop_limit` | 1 / 2 seconds | Optional trading |
| Approved cancellation | DELETE `/equity/orders/{id}` | 50 / minute | Optional trading |

Read-only scopes documented for the MVP are `account`, `portfolio`, `metadata`, `orders:read`, `history:orders`, `history:dividends`, and `history:transactions`. Trading requires `orders:execute`. Export permission names are not specified in these endpoint descriptions; verify them in the key-management UI rather than inventing a scope.

Historical order/dividend endpoints support a ticker filter. Transactions support a starting `time` filter, not the same ticker filter. Do not expose unsupported date-range filters as if the broker implements them; any local filtering must disclose which pages were examined.

## 2. Architecture and C# implementation

```text
Codex / Claude Code / other local HTTP clients
                  |
      HTTPS Streamable HTTP + MCP access token
                  |
         private-network TLS reverse proxy
                 |
      ASP.NET Core MCP server in Pi container
                  |
         tool handlers and input validation
                  |
      shared cache + per-account rate scheduler
                  |
         typed Trading 212 HTTP client
                  |
            demo or live REST API

Local stdio-only client -> thin stdio bridge -> same HTTP MCP server
```

Use .NET 10 LTS, the latest supported patch, and pinned stable SDK package versions selected when implementation begins. Microsoft's [support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) identifies .NET 10 as an active LTS release.

Use `ModelContextProtocol.AspNetCore` for HTTP hosting and `ModelContextProtocol` for the optional bridge/client APIs. Follow the [official C# SDK guide](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/getting-started.html): `AddMcpServer`, `WithHttpTransport`, and `MapMcp`. Prefer stateless HTTP because these tools do not need sampling, elicitation, or server-to-client requests. Stateless transport does not mean separate caches or limiters per request.

Suggested initial layout:

```text
Trading212Mcp.sln
src\Trading212Mcp.Server\
  Program.cs
  Configuration\
  Trading212\       (typed client, DTOs, broker errors, rate scheduler)
  Tools\            (account, portfolio, metadata, order reads, history)
tests\Trading212Mcp.Tests\
Dockerfile
compose.yaml
.dockerignore
README.md
docs\implementation-plan.md
```

Keep the first version to one application and one test project. Add a bridge project only when the compatibility milestone requires it. Use an existing TLS reverse proxy or a small proxy service in the stack for private-network HTTPS. Do not introduce a database, message bus, frontend, or multi-account tenancy for the read-only MVP.

### Broker client

Use `IHttpClientFactory`, `System.Text.Json`, cancellation tokens, and validated configuration. Use `decimal` for money, prices, quantities, and FX rates; `DateTimeOffset` for timestamps; and `long` for broker identifiers. Consider serializing IDs as decimal strings at the MCP boundary to avoid loss of precision in JavaScript clients.

Start with handwritten DTOs for the selected endpoints, using OpenAPI as a reference. The published schema includes sparse required-field declarations, legacy authentication metadata, and nullable syntax that merits review before using a generator. Do not generate an unrestricted MCP tool for every OpenAPI operation.

Validate required fields explicitly. Preserve optionality and handle newly introduced enum values without silently converting them into a known value. Distinguish a missing field from a genuine zero balance or quantity.

Centralize URL construction against the explicitly selected broker origin and enforce the first-release outbound read-method/path allowlist before sending any request. Disable automatic redirects for authenticated broker requests, preventing redirects from bypassing the allowlist.

### Rate limits, caching, and resilience

Implement one scheduler keyed by environment, account identity/configuration, and endpoint bucket; parameter variants and order IDs must not bypass the same endpoint limit. Initially operate one broker account per server and only one server replica.

Seed conservative budgets from the documented limits, then read `x-ratelimit-limit`, `x-ratelimit-period`, `x-ratelimit-remaining`, `x-ratelimit-reset`, and `x-ratelimit-used`. Synchronize admission and budget updates across concurrent calls. Do not assume separate paths necessarily have independent buckets if observed headers indicate shared limits.

On 429, honor the broker reset time and `Retry-After` if present. Bound waiting and retries within the tool timeout; otherwise return a rate-limit error and the next permissible attempt time. Account for other applications using the account: local counters alone cannot guarantee the broker allowance.

The initial implementation surfaces transient read failures without automatic retries, so repeat attempts cannot hide failures or unexpectedly consume the shared allowance. Any future retry policy must be limited to safe reads with bounded backoff and a tool-timeout budget. Never retry validation or permission failures; writes remain outside the first release.

Cache instrument and exchange metadata for a proposed 10-minute TTL. Use small, explicit TTLs for account/position/order reads, coalesce identical concurrent requests, and return retrieval time and cache status. Never silently substitute stale data after a failed refresh. Trading preflight, if added, must explicitly obtain sufficiently fresh data.

Avoid background polling in the MVP. Multiple clients should request data on demand, not multiply idle traffic.

### MCP tool contracts

Tools should be intent-oriented and discoverable, with accurate descriptions, input schemas, and read-only/destructive annotations. Annotations help clients but are not authorization controls.

Return structured results and a text JSON representation for broad compatibility. Include `environment`, retrieval timestamp, relevant currency fields, and pagination/cache information. Bound search results and large position/order lists with explicit local paging or truncation metadata; never hide omitted records.

History tools accept `limit` in the range 1-50 and the appropriate filter. Preserve the complete broker `nextPagePath` behind a validated continuation token. Allow only the expected relative history path on the configured broker origin; reject absolute URLs, alternate hosts, path traversal, and endpoint changes. Do not accept arbitrary fetch URLs from an AI client.

Return MCP tool errors explicitly (`isError` with a stable error code), including invalid input, unauthorized credentials, missing permission, not found, rate limiting, upstream outage, and malformed responses. Sanitized errors must not contain credentials or full financial payloads.

MVP success examples: "Show my portfolio", "Find the Trading 212 identifier for an ETF", and "Show the next page of dividends". Do not fabricate data the API cannot supply.

## 3. Raspberry Pi, Docker, Portainer, and client compatibility

### Platform and image

Target a Raspberry Pi running a supported **64-bit Linux OS** with Docker Engine and a Portainer-managed Docker Standalone environment. Use `linux/arm64` images. Confirm the Pi model, OS architecture, and compatibility of the chosen .NET runtime image before implementation; a 64-bit CPU with a 32-bit OS is not an ARM64 deployment. A 32-bit Pi deployment needs a separate runtime/image decision and is not assumed to work with the ARM64 image.

Build a multi-stage image from official .NET SDK and ASP.NET runtime images. Run as a non-root user, use a read-only root filesystem where practical, drop unnecessary capabilities, and never mount the Docker socket into the MCP server.

Build the image on the development machine or CI with Docker Buildx and publish an ARM64 image to a chosen registry, such as GHCR. Optionally publish an AMD64 variant for development. Use an explicit version or digest in the stack, not an untracked `latest` tag. Portainer should pull the prebuilt image; the Pi does not need the .NET SDK or a repository checkout. Verify the image manifest and actually start/test the image on the Pi; an AMD64 build passing in CI is not ARM64 runtime validation.

### Private-network access and TLS

Clients connect to the Pi's stable DNS name or reserved LAN address, not their own `localhost`. A representative URL is `https://trading212-pi.example.internal:8443/mcp`; replace this illustrative hostname with the actual private hostname and matching certificate.

Terminate TLS at an existing reverse proxy or a proxy service included in the Portainer stack. Keep Kestrel on container port 8080 on a dedicated Docker bridge network, with no direct host port published for the MCP application. Only the TLS proxy publishes its HTTPS port on the intended Pi LAN/VPN interface. Keep broker egress available for Trading 212 HTTPS and DNS; do not mark the application's sole network as isolated from outbound traffic.

Use a trusted certificate for the selected hostname. With a private CA, install its trust chain in each client/runtime trust store; never disable certificate validation to make a client connect. Do not send MCP bearer tokens or portfolio data over plain LAN HTTP. Keep proxy buffering/timeouts compatible with Streamable HTTP, preserve Authorization and MCP headers, and disable sensitive request/response logging.

Restrict ASP.NET `AllowedHosts` to the intended hostname, validate supplied Origin headers, and do not enable permissive CORS. If forwarded headers are needed, trust only the actual proxy. Authenticate every MCP endpoint request with the separate server access token; private networking and TLS are not substitutes for authorization.

Restrict the published Docker port through network controls that actually cover Docker-published traffic; do not assume ordinary host firewall rules automatically do so. Do not configure router port forwarding or expose Portainer publicly. Verify reachability from allowed clients and non-reachability from unintended interfaces. Access away from home can use a private VPN, provided the consuming client runs on a device that can join it.

### Pi-only broker credentials

Store the Trading 212 API key and secret in a private directory on the **Pi's host filesystem**, outside every repository, checkout, and Docker build context. Do not use a repository `.env` file, even if Git ignores it, or store the broker credentials in Windows/client settings. Keep demo and live credentials in separate files.

Create the directory with owner-only permissions (0700) and credential files with owner-only read/write permissions (0600). Provision values interactively without echoing them or placing them in command history. Never put real credentials in documentation or ask an AI client to supply them.

For the initial Docker Standalone stack, use explicit read-only file bind mounts to expose only the selected environment's key and secret to the server container. Use absolute source paths on the **Pi Docker host**, not paths on the development PC or relative to Portainer's Git checkout. Configure mounts to fail when source files are absent rather than automatically creating source directories. Stack configuration contains file locations, not credential values.

Provision credentials privately on the Pi via its terminal or SSH. Do not paste broker keys/secrets into the Portainer stack editor, stack environment variables, uploaded `.env` files, Git integration, or client configuration. Portainer may store non-secret settings such as the external secret directory, image version, hostname, and environment selection.

The C# server reads these files at startup, validates that both values exist and are non-empty, and constructs Basic authentication only for requests to the configured Trading 212 origin. Missing, unreadable, or invalid secret files fail startup clearly without printing their contents. Do not fall back to credentials in tool arguments, client configuration, or broker-credential environment variables.

Ensure the non-root container UID/GID can read the mounted files without making them world-readable; arrange host file ownership for the actual container identity and verify effective access on the Pi. Read-only bind mounts are not encrypted storage or Docker Swarm secrets. Mount no broker secret into the TLS proxy, stdio bridge, or other client-side process. A switch to Swarm would require a separate stack/secrets design, not an assumption that Standalone behavior carries over.

Rotate credentials by replacing the external Pi files and **recreating the server container through Portainer** to remount/reload them; a restart alone may retain an old inode after atomic replacement of a file bind mount. Keep any backups outside the repository with equally restrictive access; revoke the old key in Trading 212. Never bake credentials into image layers, examples, command-line arguments, logs, health checks, tool results, or exception text.

The client needs only the server URL and a separate MCP access token. Store the server's token in a protected Pi file, mounted read-only, separately from broker credentials; clients and the bridge may hold only this MCP credential. File permissions and read-only mounts reduce accidental exposure but do not protect against a compromised host administrator, Docker administrator, or sufficiently privileged Portainer user. Limit Portainer access accordingly.

### Portainer stack deployment and operations

Provide a `compose.yaml` compatible with the Portainer Docker Standalone stack editor/upload workflow. Define one MCP server service, its dedicated network, non-root identity, external read-only credential mounts, read-only root filesystem where supported, graceful stop timeout, restart policy, and bounded log rotation. Add a TLS proxy service only if an existing proxy is unavailable. Exclude broker values from the YAML and all interpolation variables.

Deployment sequence:

1. Confirm ARM64 OS/Docker, network hostname, client certificate trust, and the chosen registry's access from the Pi.
2. Provision the live read-only broker key/secret and separate MCP token files privately on the Pi; verify ownership against the container UID/GID.
3. In Portainer, select the Pi's Docker Standalone environment, choose **Stacks > Add stack**, and use the web editor or upload the supplied Compose file. Set only non-secret deployment variables and select registry credentials if the image is private.
4. Deploy the pinned image, inspect startup/health without exposing secrets, and complete the live-read/client acceptance checks.
5. For updates, publish a new version, explicitly redeploy the stack, and verify health/tools. Roll back by redeploying the prior image version; do not delete host credential files when removing/replacing a stack.

Provide lightweight liveness/readiness endpoints that do not call Trading 212 or return account information, and a container health check with tooling actually present in the image. Keep health endpoints internal to the stack. A Docker restart policy does not itself restart an unhealthy-but-running process; document how health failures are surfaced and handled in Portainer.

Use `restart: unless-stopped`, enable Docker startup on Pi boot, and verify recovery after a Pi reboot. Bound container logs to reduce storage/SD-card wear, avoid persistent financial payloads, and check memory usage on the actual device before selecting resource limits. No application data volume is required for the initial in-memory cache; protected host secrets must persist independently of container replacement.

| Client surface | Connection strategy |
| --- | --- |
| Codex CLI and local IDE/desktop clients using Codex MCP configuration | Streamable HTTP URL plus `bearer_token_env_var`; stdio is also supported. |
| Claude Code | HTTP MCP URL with authenticated headers; keep secrets in private/environment-backed configuration. |
| Claude Desktop local server configuration | A locally launched stdio bridge connects over HTTPS to the shared Pi server; no Pi broker credentials are copied to the client. |
| Other MCP apps | Use supported Streamable HTTP configuration, or the stdio bridge; client configuration formats are not universal. |
| Hosted Claude or ChatGPT integrations | Cannot reach a private Pi LAN/VPN endpoint unless that platform provides compatible private connectivity. Require separately planned hosted reachability and supported authentication/distribution setup. |

For the stdio bridge, use the official C# MCP client to forward the supported tool catalogue and tool calls, including errors and cancellation. Reserve stdout exclusively for protocol messages; logs go to stderr. It holds only the MCP access credential, not broker credentials. Verify local process arguments, Pi DNS resolution, TLS trust, startup failures, network loss, and disconnect behavior. WSL is not required for hosting or client access.

Example future Codex configuration, not something to apply before the server exists:

```toml
[mcp_servers.trading212]
url = "https://trading212-pi.example.internal:8443/mcp"
bearer_token_env_var = "TRADING212_MCP_TOKEN"
```

Set that environment variable privately in the client process environment. For Claude Code, use an HTTP configuration with an environment-expanded Authorization header, following its current documentation. Do not commit a literal bearer token.

If hosted connectors are later required, design externally reachable hosting and MCP-compatible OAuth before public exposure. Broker Basic authentication is distinct from client-to-MCP authorization; Trading 212 does not document an OAuth delegation flow here. Do not assume a private-network tunnel is reachable by a hosted client, or that every hosted connector supports arbitrary bearer headers.

## 4. Future extensions, outside the first release

These are research notes for a later scope decision. They must not add write tools, execution code, or write-enabling configuration to the initial read-only server.

### CSV exports

Add `request_report` and `list_reports` after the read-only MVP. Request generation once, return `reportId`, and poll separately at the documented 1/minute allowance. Do not keep a tool call open for the entire generation process.

Validate UTC date ranges and selected report sections. Expose queued, running, finished, canceled, and failed states accurately. Treat download URLs as sensitive. If downloading is implemented, use a separate unauthenticated client with validated HTTPS destinations/redirects and bounded download size; never forward the broker Basic header to a report host.

### Trading, disabled by default

If trading is separately requested later, require a deliberate opt-in and a key with `orders:execute`. Replace the first-release GET-only broker policy only as part of that reviewed extension. Register no execution tools when trading is disabled, and enforce the same policy in the underlying service. Live trading needs an additional explicit live-trading opt-in; selecting the live environment for read-only access must never enable trading.

Use a prepare/approve/execute workflow, not an unrestricted `place_order` tool:

1. Prepare an immutable draft with environment, account, exact ticker, side, positive quantity, order type, applicable prices, time validity, extended-hours choice, and a human-readable summary.
2. Validate required prices, instrument identity, available quantity/cash where meaningful, and configured exposure limits. Explain that estimates and market-order notional cannot guarantee the final fill price.
3. Require approval through a trusted human channel, such as a local administrative CLI. A confirmation token returned to the model, a `confirmed=true` argument, or two consecutive model tool calls is not evidence of human approval.
4. Bind approval to the exact draft, account, environment, and expiry. Execute at most once through an atomic operation ledger; persist the ledger in a protected volume before enabling trading.
5. If submission times out or the server crashes around transmission, mark the result unknown and block resubmission. Reconcile using pending and historical orders, with manual review when no definitive match exists. The API has no documented client idempotency key, so exactly-once broker execution cannot be guaranteed.

Use `DAY` or `GOOD_TILL_CANCEL` for the applicable order types and keep extended-hours trading off by default. Cancellation also needs explicit approval and must not be reported as a guaranteed final cancellation.

Define configured trade limits and acceptable approval UX before implementing this milestone. Do not invent a universal financial limit or imply that a server safety check makes a trade suitable.

## 5. Delivery milestones and acceptance criteria

| Milestone | Work | Exit criteria |
| --- | --- | --- |
| 1. Foundation | Solution/test project, pinned .NET and NuGet versions, explicit environment, typed client, sanitized logging | Live secret files select the live origin without demo credentials; invalid settings fail clearly; no live-to-demo fallback; mocked requests use correct Basic authentication. |
| 2. Read-only MCP | MVP tools, GET-only broker allowlist, shared scheduler/cache, continuation validation, structured errors | Catalogue contains only read tools; broker writes and unsupported paths are blocked before network access even with a write-capable key; pagination/currencies/budgets are correct. |
| 3. Raspberry Pi and Portainer stack | ARM64 image, Standalone Compose stack, Pi secret mounts, private-network TLS, health/operations documentation | Image runs on the Pi; non-root secret access and recreation-based rotation work; no broker credentials in repository/image/Portainer settings/logs; TLS/auth/network restrictions and reboot recovery are verified. |
| 4. Client interoperability and live acceptance | Codex/Claude Code examples, stdio bridge, Claude Desktop example, opt-in live reads | Each named local client can read the live account using only an MCP access credential; no demo key required; unauthenticated MCP calls fail; no broker credentials in client settings/results or bridge mounts. |
| Future, only if requested: reports | Asynchronous export tools and bounded status retrieval | Report generation, failure, and rate-limited polling are tested without assuming immediate completion. |
| Future, only if requested: trading | Trusted approval channel, persistent ledger, policy gates, demo order/cancel workflows | No write is possible by default; forged/replayed approval fails; an ambiguous submission is never automatically repeated. |

Use xUnit and a fake HTTP handler for focused unit tests, plus ASP.NET integration tests and the official MCP C# client for protocol tests. Use MCP Inspector as an optional manual diagnostic, not a runtime dependency.

Test authentication/permission failures, malformed upstream JSON, fractional quantities, currency separation, int64 IDs, pagination termination and invalid continuation URLs, concurrent cache misses, per-account 429 handling, and cancellation. Introduce write-specific tests only when writes exist.

Use synthetic credential canaries to verify that configuration errors, HTTP/proxy logging, tool results, and bridge output never reveal broker keys, secrets, or their Base64 Basic-auth representation. Exercise startup with missing/empty/unreadable files and recreation-based credential rotation in the Pi/Portainer acceptance checks. Verify missing bind sources fail deployment rather than becoming empty directories.

Use synthetic fixtures; never check real portfolio snapshots into Git. Automated tests use mocks by default and verify that non-GET broker requests and unsupported read paths cannot reach the network. Optional demo checks use separate demo credentials.

Complete a manually initiated live smoke check using the Pi-mounted live read-only key: discover tools, retrieve account summary and positions, and retrieve one bounded history page. Verify the live environment marker and compare results locally with the Trading 212 app, without persisting real financial payloads in the repository or test output. Verify that no write tool is available. No live credentials or real-account tests are required in CI, and no live write tests are permitted. Add GitHub Actions for restore, build, test, and ARM64 container build when implementation starts.

## 6. Open decisions and boundaries

The confirmed first release is one user, one broker account, a Raspberry Pi Docker container managed as a Portainer stack, private-network HTTPS access, and read-only tools with live API support. Broker credentials remain solely on the Pi/server runtime, never in the repository or consuming clients. Separate account instances must keep credentials/cache isolation; multiple instances for the same account would need shared rate coordination.

Before implementation, confirm Pi model/64-bit OS, Docker Standalone rather than Swarm, registry, private hostname, and existing proxy/certificate setup. The plan recommends ARM64 and Standalone; it does not assume unverified device compatibility. Hosted connectors, report generation, and trading require a separate future request; any trading extension must settle human approval first.

The API is beta. Recheck endpoint schemas, scopes, rates, and SDK/client compatibility at implementation time. Review Trading 212's API terms before public distribution or offering the server to other users; no legal assessment was performed here.

## Sources

- [Trading 212 API reference](https://docs.trading212.com/api) and [OpenAPI YAML](https://docs.trading212.com/_bundle/api.yaml): endpoints, schemas, limits, permissions, environments, and deprecations.
- [Market order documentation](https://docs.trading212.com/api/orders/placemarketorder.md): quantity sign, extended hours, slippage, and non-idempotency.
- [CSV report request](https://docs.trading212.com/api/historical-events/requestreport.md): report-generation workflow.
- [Official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) and [getting started](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/getting-started.html): packages, transports, hosting, and local HTTP safeguards.
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core): supported runtime choice.
- [Codex MCP documentation](https://developers.openai.com/codex/mcp/): local stdio/HTTP support and bearer-token environment configuration; this URL currently redirects to the shared ChatGPT/Codex MCP guide.
- [Claude Code MCP documentation](https://code.claude.com/docs/en/mcp): HTTP/stdio setup and transport compatibility.
- [Local MCP server guide](https://modelcontextprotocol.io/docs/develop/connect-local-servers): Claude Desktop local process configuration.
- [Building connectors for Claude](https://claude.com/docs/connectors/building): hosted reachability, authentication, and client capability boundaries.
- [Deploy .NET apps to ARM single-board computers](https://learn.microsoft.com/en-us/dotnet/iot/deployment): ARM64 versus 32-bit runtime considerations.
- [Portainer: add a stack](https://docs.portainer.io/user/docker/stacks/add): Standalone web editor/upload/Git workflows, non-secret environment settings, and registry selection.
- [Docker multi-platform builds](https://docs.docker.com/build/building/multi-platform/): ARM64 image variants, manifests, and Buildx.
- [Compose service reference](https://docs.docker.com/reference/compose-file/services/): service configuration, bind mounts, and restart policies.
