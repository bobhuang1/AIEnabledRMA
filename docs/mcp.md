# Running the MCP tool server

`src/AIEnabledRma.Mcp` is one binary with two transports.

| Transport | Selected by | Who can reach it | What guards it |
|---|---|---|---|
| stdio | nothing — it is the default | whoever launches the process | the process boundary, the per-caller budget and the audit row |
| streamable HTTP | `Mcp:Transport=http` | anything that can reach the port | Keycloak-issued JWT (issuer, audience, scope), a client allow-list, an origin allow-list |

The HTTP transport has no switch that turns authentication off, and it refuses to start without
an issuer and a client allow-list. That is deliberate: the failure worth designing out is
`Mcp:Transport=http` shipped without an identity provider, which would serve the customer and
device tools to the network.

This runbook is the recipe that produced a `200` from the transport against a real Keycloak,
not a signing key held by a test.

## What the realm has to provide

Three things, all committed in `infra/keycloak/realm-aienabledrma.json`:

1. **Realm** `aienabledrma` — the issuer is `{keycloak}/realms/aienabledrma`.
2. **Client scope** `rma:read` — grants the scope the tool server requires, and is a default
   scope of `mcp-agent`, so a token request does not have to name it.
3. **Audience mapper** on that scope — `oidc-audience-mapper` adding `aienabledrma-mcp` to `aud`.
   Keycloak does not put an audience for another service in a token by default, and the server
   checks `aud` exactly, so the mapper is the difference between a `401` and a working agent.

The realm also carries one machine client, `mcp-agent`, with `serviceAccountsEnabled: true` and
the committed secret `mcp-agent-secret`. Both the secret and the admin account are demo values for
a loopback-only container; nothing here belongs in a deployment.

## Start the identity provider

```bash
docker compose up -d keycloak
```

Keycloak imports the realm on every start — the container has no volume, because the import only
runs when the realm is absent and a kept volume would silently pin an old copy of the JSON. Wait
for the log line rather than for the port:

```bash
docker compose logs keycloak | grep "Realm 'aienabledrma' imported"
```

Discovery is then available at
`http://localhost:8080/realms/aienabledrma/.well-known/openid-configuration`.

## Get a token

```bash
curl -s -X POST \
  http://localhost:8080/realms/aienabledrma/protocol/openid-connect/token \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  -d 'grant_type=client_credentials' \
  -d 'client_id=mcp-agent' \
  -d 'client_secret=mcp-agent-secret' \
  -d 'scope=rma:read'
```

The access token's payload must carry all four of these, or the transport will refuse it:

```json
{
  "iss": "http://localhost:8080/realms/aienabledrma",
  "aud": "aienabledrma-mcp",
  "scope": "rma:read",
  "azp": "mcp-agent"
}
```

If `aud` is missing, the mapper is not on the scope the client was granted. If `scope` is missing
`rma:read`, the client does not hold the scope. Both are realm mistakes, not server mistakes, and
both produce a `401`/`403` that says nothing about which.

## Run the server

**On this machine, against the compose Keycloak:**

```bash
dotnet run --project src/AIEnabledRma.Mcp -- \
  --Mcp:Transport=http \
  --Mcp:Http:Issuer=http://localhost:8080/realms/aienabledrma \
  --Mcp:Http:AllowedClientIds:0=mcp-agent
```

`DOTNET_ENVIRONMENT` must be `Development`: an `http` issuer is refused anywhere else, because a
token fetched over plain http can be replaced in flight and every check after it is then
decoration. `dotnet run` is Development by default; a container is not.

**In compose, beside Keycloak:**

```bash
docker compose --profile mcp-http up -d mcp-http
```

That service sets `Mcp__Http__Issuer=http://keycloak:8080/realms/aienabledrma`, because inside the
compose network Keycloak answers to its service name. **The issuer is the hostname you reached
Keycloak by**, and the token's `iss` has to equal it exactly: a server validating
`http://keycloak:8080/...` rejects a token minted through `http://localhost:8080/...` even though
both are the same realm. Either use one hostname on both sides, or configure
`KC_HOSTNAME` to pin the issuer for a real deployment.

## Prove it answers

With a token in `$TOKEN`:

```bash
curl -sS -o /dev/null -w '%{http_code}\n' \
  -X POST http://localhost:5080/mcp \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"runbook","version":"1"}}}'
```

`200`. The body arrives as one server-sent event (`event: message` followed by a `data:` line
carrying the JSON-RPC result), which is why the `Accept` header names both types; a request
without it is refused by the SDK rather than by any check here. The transport is stateless, so no
session id is issued and each request stands alone — `tools/list` can be sent the same way without
an `initialize` first, and answers with the nine tools.

From inside the compose network, ask the same question by service name:

```bash
docker compose --profile mcp-http exec mcp-http \
  sh -c 'curl -sS -o /dev/null -w "%{http_code}\n" -X POST http://localhost:8080/mcp \
    -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
    -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"runbook\",\"version\":\"1\"}}}"'
```

`401` — that request carried no token, which is the other half of the proof.

## When it says no

| Response | Cause |
|---|---|
| `401` with `WWW-Authenticate: Bearer` | no token, expired token, `iss` that is not `Mcp:Http:Issuer`, `aud` that is not `aienabledrma-mcp`, or a signature the realm's keys do not verify. A wrong issuer is named in the header: `The issuer 'http://localhost:8080/realms/aienabledrma' is invalid` — that is what a host-minted token gets when the server validates the compose service name |
| `403` | a valid token without the `rma:read` scope, or from a `client_id`/`azp` not in `AllowedClientIds` |
| `403` before any token is read | an `Origin` header that is not in `AllowedOrigins`. A server-side agent sends no `Origin` and is unaffected |
| `500` at startup | `EnsureUsable` refused the configuration: an `http` issuer outside Development, an empty client allow-list, a wildcard or empty origin, an empty scope, a path that does not start with `/` |
| connection refused | the transport is not listening: check `Mcp:Transport` is `http`, not the default `stdio` |

## Browser clients

The origin allow-list is also the CORS policy. A page served from an origin in
`Mcp:Http:AllowedOrigins` may call the endpoint; one that is not gets no
`Access-Control-Allow-Origin`, and if it sends the request anyway the origin check answers `403`.

- The policy is applied to the whole host rather than to the endpoint, because a preflight
  `OPTIONS` matches no route — endpoint-scoped CORS would never see it.
- A preflight is answered `204` with `Access-Control-Allow-Headers: authorization, content-type`
  and the caller's origin echoed back. A disallowed origin gets `204` from the framework with **no**
  CORS headers, which is what makes the browser refuse to send the real request; the server-side
  refusal is the `403` above.
- `Mcp-Session-Id` is exposed, so a client can read a session id if the transport issues one.
- The compose service leaves `AllowedOrigins` empty, so the browser path is off in the quick start
  above and its preflight comes back with no allow-origin header. Add
  `Mcp__Http__AllowedOrigins__0=<your origin>` to the `mcp-http` service to try it.
- The token still has to come from somewhere. This realm defines no public client, so a browser
  client is only practical once one exists; that is a realm change, not a transport change.

## Configuration

Bound from `Mcp:Http`; environment variables use `__` as the separator.

| Key | Default | Meaning |
|---|---|---|
| `Mcp:Transport` | `stdio` | `stdio` or `http`; anything else stops the host |
| `Mcp:Http:Issuer` | `http://localhost:8080/realms/aienabledrma` | OIDC issuer, and the required `iss` |
| `Mcp:Http:Audience` | `aienabledrma-mcp` | required `aud` |
| `Mcp:Http:RequiredScope` | `rma:read` | scope every caller must hold |
| `Mcp:Http:AllowedClientIds` | empty | clients allowed to call; empty is a startup failure |
| `Mcp:Http:AllowedOrigins` | empty | browser origins allowed, and the CORS policy; empty means no browser client |
| `Mcp:Http:Path` | `/mcp` | endpoint path |

The JWT checks read `scope` or `scp` for the scope claim and accept `client_id` or `azp` for the
client, because issuers differ in which they mint.

## What this does not cover

- **Protected-resource metadata (RFC 9728) is not implemented.** A `401` carries
  `WWW-Authenticate: Bearer` without the `resource_metadata` parameter, so a client cannot discover
  the authorization server from the response and has to be configured with the realm.
- **A browser client holds its token where page scripts can reach it.** The transport now admits
  allow-listed origins (see below), but a token in a browser is a token any script on that page can
  read; keep its lifetime short and list only origins you would trust with it.
- **The realm's client is a service account, not a browser flow.** `mcp-agent` is confidential and
  uses `client_credentials`. A real browser client needs a public client with PKCE, which this realm
  does not define.
- **The realm is a development realm.** `sslRequired: none`, a committed client secret, admin
  `admin`/`admin`, and a five-minute token lifetime. A deployment needs its own realm, a secret from
  a secret store, https, and an issuer that is not a loopback name.
- **Audit and budgets are per process.** Two replicas of the transport each hold their own budget,
  so the configured limit is per replica. The audit rows are shared, because they are in PostgreSQL.
