# Trading 212 MCP

Read-only access to the **live Trading 212 API** from Codex, Claude Code, Claude Desktop, and other MCP clients. The C#/.NET 10 server runs on a **64-bit Raspberry Pi**, in a **Portainer Docker Standalone stack**.

**Broker keys and secrets live only on the Pi**, outside Git, and are mounted as files into the server. Clients receive only a separate MCP access token. There are no order-placement, cancellation, Pies mutation, or report-generation tools. Outbound broker requests are restricted to an allowlist of GET endpoints, even if a key has accidentally been granted write permissions.

## What is included

- Streamable HTTP endpoint at `/mcp`, authenticated with an MCP bearer token.
- Ten read tools: `get_account_summary`, `get_positions`, `get_pending_orders`, `get_pending_order`, `search_instruments`, `get_instrument`, `get_exchanges`, `get_order_history`, `get_dividend_history`, and `get_transactions`.
- Shared account/endpoint rate budgets and caches, safe history continuation, bounded results, and sanitized errors.
- ARM64-capable Dockerfile, Portainer-ready `compose.yaml`, and a TLS proxy configuration.
- A local stdio bridge for clients that cannot configure HTTP directly.

The API supports Invest and Stocks ISA, not CFD. Results identify `Live` or `Demo`. Position prices remain in instrument currency; wallet impacts remain in account currency. Numeric financial values retain their JSON precision, and broker IDs are returned as strings. This is not a general quote/candle feed.

## Requirements

The Pi needs a supported **64-bit Linux OS**, Docker Engine, Portainer with a **Docker Standalone** environment, outbound HTTPS/DNS, and a stable private IP/hostname. A 32-bit OS is not supported by the supplied ARM64 stack.

Clients need private-network access to the Pi and trust in its HTTPS certificate. Do not expose the stack or Portainer to the public internet or forward router ports. Hosted Claude/ChatGPT connectors cannot ordinarily reach a private Pi endpoint; they are not part of this deployment.

The stack uses a prebuilt image. The Pi does not need .NET, a compiler, or this repository checked out.

## 1. Build and publish the image

On a development machine with a running Docker daemon, choose a registry/repository you can push to. Example:

```powershell
docker login ghcr.io
docker buildx build --platform linux/arm64,linux/amd64 --tag ghcr.io/YOUR_ACCOUNT/trading212-mcp:v0.1.0 --push .
```

Replace `YOUR_ACCOUNT` with the lowercase registry owner. Only publish images to a registry you control. No secret files belong in the build context; `.dockerignore` allowlists server source and build configuration.

Alternatively, the included GitHub Actions workflow builds/tests on pull requests and main pushes, and publishes multi-platform GHCR images **only when you push a `v*` tag**. Configure package access in GitHub as appropriate. For a private image, add its registry credentials in Portainer and select that registry when deploying. Registry credentials are separate from Trading 212 credentials.

Set `MCP_IMAGE` in the stack to the explicit version tag or, preferably, its manifest digest. Never assume an image has already been published for this repository. Base images use maintained .NET 10 tags; rebuild regularly for runtime patches.

## 2. Provision broker credentials on the Pi

In Trading 212, create a **live** API key with these read scopes: `account`, `portfolio`, `metadata`, `orders:read`, `history:orders`, `history:dividends`, and `history:transactions`. Do not enable `orders:execute` or Pies writes. If IP restrictions are enabled, allow the Pi's **public outbound IP**, not its private Docker/LAN IP.

SSH into the Pi and use **Bash** for the commands below. These Linux paths are deployment paths on the Pi, not paths on the Windows development machine. Values entered with `read -s` are not echoed or included in shell command history.

```bash
sudo install -d -m 0700 -o 10001 -g 10001 /opt/trading212-mcp/secrets
for name in api-key api-secret mcp-token; do
  sudo install -m 0600 -o 10001 -g 10001 /dev/null "/opt/trading212-mcp/secrets/$name"
done

read -r -s -p "Live API key: " broker_key; printf '\n'
printf '%s' "$broker_key" | sudo tee /opt/trading212-mcp/secrets/api-key >/dev/null
unset broker_key

read -r -s -p "Live API secret: " broker_secret; printf '\n'
printf '%s' "$broker_secret" | sudo tee /opt/trading212-mcp/secrets/api-secret >/dev/null
unset broker_secret

openssl rand -hex 32 | sudo tee /opt/trading212-mcp/secrets/mcp-token >/dev/null
```

**Run the initialization loop only once**: it empties existing files. Credential rotation is described below. Do not paste keys into Portainer, client settings, prompts, `.env` files, or the repository. The container runs as UID/GID `10001:10001`; the files must be readable by that identity. Rootless Docker or user-namespace remapping may require mapped host ownership instead; verify this before deploying.

All file locations passed to the server must be absolute. Missing, unreadable, empty, or invalid files stop startup; on Linux, group/other file permissions are also rejected. There is no fallback to broker credentials from environment variables or clients.

## 3. Configure private HTTPS

Choose a hostname resolvable by your clients, for example `trading212-pi.home.arpa`, and reserve the Pi's LAN IP in your router/DNS.

The supplied stack includes Caddy and requires:

| File on the Pi | Purpose |
| --- | --- |
| `/opt/trading212-mcp/config/Caddyfile` | Copy the repository's `deploy\Caddyfile` here; it contains no secrets. |
| `/opt/trading212-mcp/tls/server.crt` | PEM certificate/full chain with the chosen hostname in its subject alternative names. |
| `/opt/trading212-mcp/tls/server.key` | Matching PEM private key, without a password prompt. |

Create the directories with restrictive permissions, then install the files:

```bash
sudo install -d -m 0700 /opt/trading212-mcp/config /opt/trading212-mcp/tls
# After privately transferring the configuration and certificate files to the Pi:
sudo install -m 0644 Caddyfile /opt/trading212-mcp/config/Caddyfile
sudo install -m 0644 server.crt /opt/trading212-mcp/tls/server.crt
sudo install -m 0600 server.key /opt/trading212-mcp/tls/server.key
```

Use a certificate already trusted by your clients, or issue one with your private CA and install **only its public CA certificate** in each client/runtime trust store. Never copy the CA private key to a client or the proxy, and never disable certificate validation.

If you do not already have a private CA, this optional Pi-terminal example creates a CA and a 90-day server certificate. Replace the hostname before running it; keep the CA directory private and renew the server certificate before expiry:

```bash
sudo install -d -m 0700 /opt/trading212-mcp/ca
sudo bash -c 'umask 077; openssl req -x509 -newkey rsa:3072 -nodes \
  -keyout /opt/trading212-mcp/ca/ca.key -out /opt/trading212-mcp/ca/ca.crt \
  -days 3650 -subj "/CN=Trading212 private CA"'
sudo bash -c 'umask 077; openssl req -new -newkey rsa:3072 -nodes \
  -keyout /opt/trading212-mcp/tls/server.key -out /opt/trading212-mcp/tls/server.csr \
  -subj "/CN=trading212-pi.home.arpa"'
printf 'subjectAltName=DNS:trading212-pi.home.arpa\nextendedKeyUsage=serverAuth\n' \
  | sudo tee /opt/trading212-mcp/tls/server.ext >/dev/null
sudo openssl x509 -req -in /opt/trading212-mcp/tls/server.csr \
  -CA /opt/trading212-mcp/ca/ca.crt -CAkey /opt/trading212-mcp/ca/ca.key \
  -CAcreateserial -out /opt/trading212-mcp/tls/server.crt \
  -days 90 -sha256 -extfile /opt/trading212-mcp/tls/server.ext
```

For Windows, import the public `ca.crt` into the current user's **Trusted Root Certification Authorities** using the certificate manager. For Linux clients, install it using the distribution's CA trust tools. Some apps maintain their own certificate store; configure those separately.

The proxy exposes only `/mcp`; health endpoints stay internal. It mounts TLS files, **not broker credentials**. The application has no host port; only the proxy publishes HTTPS. HTTP/1.1 and HTTP/2 are enabled; HTTP/3 is disabled because this stack publishes TCP only and the external port can differ from the proxy's port. An existing proxy can be used instead, but it must join the backend network, preserve Authorization/MCP headers, support streaming without buffering, and route only `/mcp`.

## 4. Deploy the Portainer stack

In Portainer, select the **Pi's Docker Standalone environment**, then **Stacks > Add stack**. Name it `trading212-mcp`. Choose **Web editor** and paste the full `compose.yaml`, or choose **Upload** and upload that file.

Add these **non-secret** stack environment variables:

| Variable | Example | Meaning |
| --- | --- | --- |
| `MCP_IMAGE` | `ghcr.io/your_account/trading212-mcp:v0.1.0` | Published ARM64 image/version or digest. |
| `MCP_HOST` | `trading212-pi.home.arpa` | DNS hostname only; no scheme, port, or path. Must match the certificate. |
| `PI_BIND_IP` | `192.168.1.50` | Actual Pi LAN/VPN IP to publish HTTPS on; not `0.0.0.0`. |
| `HTTPS_PORT` | `8443` | Client-facing HTTPS port. Default 8443. |
| `PI_SECRET_DIR` | `/opt/trading212-mcp/secrets` | Absolute Pi directory containing the three credential files. |
| `PI_CONFIG_DIR` | `/opt/trading212-mcp/config` | Absolute Pi directory containing `Caddyfile`. |
| `PI_TLS_DIR` | `/opt/trading212-mcp/tls` | Absolute Pi directory containing server TLS files. |

**Do not add API key/secret values to Portainer variables or `stack.env`.** The variables above specify locations/settings only. Bind mounts reference files on the **Pi Docker host**, not your laptop or Portainer's Git checkout. Missing source files must fail deployment, not be created as empty directories; use a current Portainer/Compose version supporting `bind.create_host_path: false`.

Select your image registry if authentication is needed, then click **Deploy the stack**. Inspect the server's startup logs and health status in Portainer. The stack explicitly selects `Live`; no demo key is required. Do not add a trading flag: none exists.

Access the endpoint as `https://trading212-pi.home.arpa:8443/mcp`. Confirm DNS, certificate trust, and the intended interface binding. Restrict Docker-published traffic through firewall/network rules that actually cover Docker's forwarding; do not assume a host firewall alone covers published ports. Test from permitted and non-permitted networks. Do not add router port forwards.

For a TLS/connectivity check from a client, this should return **401 Unauthorized**, without any broker request:

```powershell
curl.exe --cacert "C:\secure\trading212-ca.crt" --head https://trading212-pi.home.arpa:8443/mcp
```

The health check runs `dotnet Trading212Mcp.Server.dll --health-check` inside the server container; no curl install is needed. A Docker restart policy restarts an exited process, **not** an unhealthy process still running. Investigate unhealthy status in Portainer rather than assuming automatic recovery.

## 5. Configure consuming clients

Privately copy **only** the Pi's `mcp-token` value to a secure location on the consuming device. Never copy `api-key` or `api-secret`. The MCP token grants read access to the account through this server, so protect it like a password. Keep it out of Git, shell history, screenshots, and prompts.

### Codex: HTTP

Set `TRADING212_MCP_TOKEN` in the Codex process environment from that private local MCP token file. Example PowerShell (the file must be outside the repository and protected by owner-only ACLs):

```powershell
$env:TRADING212_MCP_TOKEN = (Get-Content -Raw "C:\secure\trading212-mcp-token").Trim()
```

Add to your private Codex `config.toml`:

```toml
[mcp_servers.trading212]
url = "https://trading212-pi.home.arpa:8443/mcp"
bearer_token_env_var = "TRADING212_MCP_TOKEN"
```

### Claude Code: HTTP

Launch Claude Code with the same privately loaded MCP token environment variable. Add this server entry to its MCP configuration:

```json
{
  "mcpServers": {
    "trading212": {
      "type": "http",
      "url": "https://trading212-pi.home.arpa:8443/mcp",
      "headers": { "Authorization": "Bearer ${TRADING212_MCP_TOKEN}" }
    }
  }
}
```

Only the variable name belongs in configuration, not its value. Check the connected-server/tool list in the consuming app.

### Claude Desktop or other stdio clients

Publish the bridge on the development machine with .NET 10:

```powershell
dotnet publish .\src\Trading212Mcp.Bridge -c Release -o .\artifacts\bridge
```

Copy the published output to a private installation directory on the consuming device. It needs the .NET 10 runtime there; alternatively publish a self-contained build for that device. Example Windows local MCP configuration:

```json
{
  "mcpServers": {
    "trading212": {
      "command": "dotnet",
      "args": ["C:\\tools\\trading212-bridge\\Trading212Mcp.Bridge.dll"],
      "env": {
        "MCP_SERVER_URL": "https://trading212-pi.home.arpa:8443/mcp",
        "MCP_ACCESS_TOKEN_FILE": "C:\\secure\\trading212-mcp-token"
      }
    }
  }
}
```

The bridge accepts HTTPS only, trusts the platform certificate store, forwards only the known read-tool catalogue/calls, and logs to stderr. Neither WSL nor broker credentials are needed on the consuming device.

## Reading data and limits

Start with "Show my portfolio" or "Find the Trading 212 identifier for this ETF". Confirm the `Live` result marker against the Trading 212 app privately. Do not commit real account snapshots.

Array tools support local `limit` (1-50) and `offset`; results report `total` and `nextOffset`. History tools return one broker page with `continuationToken` and `hasMore`. For the next history page, supply **only** the continuation token; do not repeat first-page filters or a non-default limit.

Metadata is cached for 10 minutes. Summary/pending-order lists use 5-second caches; positions/single pending orders use 1 second. `retrievedAt` and `cached` disclose freshness. All sessions share account endpoint budgets. A `rate_limited` error includes `retryAt`; wait until then rather than looping. Broker reads are not automatically retried, and stale data is never substituted after a failed refresh.

Transactions accept a starting UTC ISO-8601 `time` ending in `Z`. Orders/dividends support a ticker filter. No fabricated broker date-range or quote capabilities are provided. A pending-order 404 is not evidence of cancellation or non-execution.

## Updates, rotation, and troubleshooting

For image updates, set `MCP_IMAGE` to the new version/digest in Portainer and redeploy. Roll back to the previous image if needed. Do not remove the host secret/TLS files when removing a stack.

Rotate broker credentials privately on the Pi, maintaining owner `10001:10001` and mode 0600. **Recreate the server container through Portainer**, not just restart it: atomic replacement of file bind mounts can otherwise leave the old inode mounted. Verify live reads, then revoke the old broker key. Rotate the MCP token the same way and update only that token on clients. Recreate the proxy after replacing TLS files.

Enable Docker at Pi boot and verify the stack after a reboot. Logs are capped at two 5 MB files per container. Health probes do not call the broker, and caches are in memory; no financial-data volume is required.

| Symptom | Check |
| --- | --- |
| Stack fails before startup | ARM64 image exists, bind sources are actual Pi files, variables are set, IP belongs to Pi. |
| Server exits on startup | Explicit `Live`/`Demo`, absolute secret-file paths, owner/mode, token length 32-512. Logs identify the setting without its contents. |
| TLS connection fails | DNS and hostname SAN, certificate chain/expiry, client trust store, proxy logs. Do not disable TLS checks. |
| MCP 401 | Separate MCP token and Authorization header; do not use a broker API key as the bearer token. |
| MCP 403 | Supplied Origin must match configured HTTPS origin. CORS is deliberately not enabled for cross-origin browser clients. |
| `broker_unauthorized` / `broker_forbidden` | Live key/secret, read scopes, broker public-IP allowlist. No automatic demo fallback occurs. |
| `rate_limited` | Shared per-account allowance, including other applications; wait for `retryAt`. |

## Development and validation

Install the .NET 10 SDK:

```powershell
dotnet test .\Trading212Mcp.sln -c Release
```

Tests use synthetic credentials and a fake broker, including an actual HTTP MCP handshake via the official C# client. No live credentials or Docker daemon are needed for these tests. Local native server development still requires external secret files and explicit configuration; clients never provide broker credentials.

Validate Compose with the non-secret variables above using `docker compose config --quiet`. Build/run the ARM64 image on the Pi and complete the live-read smoke checks separately: these cannot be established by a Windows mock test or an image manifest.

Local container acceptance has verified ARM64 and AMD64 image builds, a healthy non-root/read-only AMD64 server, actual Linux secret permissions and failure cases, certificate-validated HTTPS through Caddy, MCP initialization/read-tool discovery, and access controls. Synthetic credentials and an isolated broker network were used. This Docker host cannot execute ARM64 binaries, so native Pi startup, real client trust configuration, and live-account reads still need verification on your deployment.

See [the implementation plan](docs/implementation-plan.md) for API research and future scope. The API is beta; verify current limits/terms before public distribution.
