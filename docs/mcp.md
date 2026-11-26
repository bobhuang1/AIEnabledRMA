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

## Verify it in one command

```bash
./scripts/verify-auth.sh
```

The four gates CI runs can be run here, in the order CI runs them, without a GitHub runner and
without a token for its logs:

```bash
./scripts/verify-all.sh            # the suite, the auth flows, the images, then the restore drill
./scripts/verify-all.sh auth       # one of them, by name
```

The suite step needs docker and no host SDK: it copies the working tree into the pinned SDK image and
starts a throwaway PostgreSQL for the live fixtures. The auth step generates a master-realm admin
password for the run and hands it to compose, so the gate never logs in with the `admin`/`admin` the
compose file still defaults to for hands-on use; set `KEYCLOAK_ADMIN_PASSWORD` to reuse a stack you
started yourself.

The script boots Keycloak with the committed realm and the `mcp-http` profile, waits for both
discovery documents, then drives the two flows and asserts the answers rather than printing them:
the machine token's `iss`, `aud`, `scope` and `azp`; `200` for `initialize` with it; `401` with no
token, including the `resource_metadata` pointer; `403` from an origin that is not in the
allow-list; `204` preflights with CORS headers for the listed origin and none for an unlisted one;
the PKCE authorization-code exchange with no secret; a code refused without its verifier; and a
replayed code refused. It mints tokens from inside the compose network, where the issuer is
`http://keycloak:8080/…`, because that is the hostname the transport validates.

CI runs the same script on every pull request (`auth-flows` in `.github/workflows/ci.yml`), so the
realm's validity and both flows are a check rather than a memory: the import has already failed once
on a field Keycloak rejects, and that failure is silent until something depends on the client.
`KEEP=1` leaves the stack running; the script stops only the containers it started and never
removes a volume.

## What the realm has to provide

Six things, all committed in `infra/keycloak/realm-aienabledrma.json`:

1. **Realm** `aienabledrma` — the issuer is `{keycloak}/realms/aienabledrma`.
2. **Client scope** `rma:read` — grants the scope the tool server requires, and is a default
   scope of `mcp-agent`, so a token request does not have to name it.
3. **Audience mapper** on that scope — `oidc-audience-mapper` adding `aienabledrma-mcp` to `aud`.
   Keycloak does not put an audience for another service in a token by default, and the server
   checks `aud` exactly, so the mapper is the difference between a `401` and a working agent.

4. **Public client** `mcp-browser` — the authorization-code flow with PKCE (`S256` enforced), no
   secret, and one accepted redirect (`http://localhost:5173/*`). It exists so a page can get a
   token; without it an allow-listed origin had permission to call the transport and no way to
   obtain the credential.
5. **Client scope** `basic` — an `oidc-sub-mapper` and nothing else, a default scope of both
   clients. An imported realm does not get Keycloak's built-in scopes, so without this an access
   token carries no `sub` at all: the flow still succeeds, but every browser caller reaches the
   audit trail as `mcp-browser:unknown-subject`. It is not a token scope — `scope` stays
   `openid rma:read`.
6. **Client scope** `rma:support` — the grant that admits `reveal_customer_contact`, the one tool
   that returns a full contact record. It is separate from `rma:read` because reading masked data
   and reading one unmasked record are different permissions, and it is an *optional* scope of
   `mcp-agent`, so the everyday token does not carry it and a caller that wants it has to ask —
   which is what makes the audit row name a deliberate act rather than an agent's habit.

The realm also carries one machine client, `mcp-agent`, with `serviceAccountsEnabled: true` and
the committed secret `mcp-agent-secret`, plus a development user `browser-demo` whose password is
in the file. Every one of those is a demo value for a loopback-only container; nothing here
belongs in a deployment.

`scripts/verify-auth.sh` reads all of this back from the admin API after the import — both clients'
settings, the two scope mappers and `browser-demo`'s password credential — because the file and what
Keycloak produced are two different facts, and only the second one the flows depend on.

One more client, `mcp-negative`, exists only for the gate. It is allow-listed on purpose and carries
two audience scopes and a one-second token lifespan, so `scripts/verify-auth.sh` can mint the three
tokens the transport has to refuse — an audience it does not serve (`401`), its own audience without
`rma:read` (`403`), and the same short-lived token once it expires (`401`) — and show the gate
failing closed rather than only showing the accepted path. It is not part of the runbook; a
deployment should not have it.

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

If the realm file changed and the stack is already running, recreate Keycloak (`docker compose up -d
--force-recreate keycloak`) **and restart the tool server**. Keycloak mints new signing keys on every
import, and the transport caches the old ones, so a machine token answered before the restart gets a
`401` that reads as a broken realm. `scripts/verify-auth.sh` does both when it reuses a running stack.

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

The script above performs this request and asserts the four claims; the curl is here so you can see
the request the assertions are about. The access token's payload must carry all four of these, or
the transport will refuse it:

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

## Discovery (RFC 9728)

A client given only the endpoint URL can find the issuer without being configured with it. The
transport publishes a protected-resource metadata document at both addresses an MCP client tries:

- `http://localhost:5080/.well-known/oauth-protected-resource/mcp` — the path-specific form. RFC
  9728 puts the well-known segment *between* the host and the endpoint's path, and this is the one
  the `401` names;
- `http://localhost:5080/.well-known/oauth-protected-resource` — the root form, for a client that
  ignores the header and falls back to trying both.

Neither requires a token, because the document describes how to obtain one:

```json
{
  "resource": "http://localhost:5080/mcp",
  "authorization_servers": ["http://localhost:8080/realms/aienabledrma"],
  "scopes_supported": ["rma:read"],
  "bearer_methods_supported": ["header"],
  "resource_name": "AIEnabledRma MCP tools"
}
```

And a request with no token answers with the pointer rather than a bare challenge:

```text
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer resource_metadata="http://localhost:5080/.well-known/oauth-protected-resource/mcp", scope="rma:read"
```

Two values are worth reading carefully. `resource` is the URL a client connects to, not the
well-known URL: RFC 9728 §3.3 has the client compare it with its own notion of the resource and
refuse a document that describes something else. `authorization_servers` is `Mcp:Http:Issuer`
verbatim, so the same hostname rule as the token's `iss` applies — in compose it names the service
name, which is right for a client on that network and wrong for a browser on the host. `scope` in
the challenge is the scope the policy requires, so a client asks for exactly that.

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

`200`. The script above checks this answer, the `401` below, the `403` for an unlisted origin and
the preflight that precedes either, so these curls are for reading rather than for running the gate.
The body arrives as one server-sent event (`event: message` followed by a `data:` line
carrying the JSON-RPC result), which is why the `Accept` header names both types; a request
without it is refused by the SDK rather than by any check here. The transport is stateless, so no
session id is issued and each request stands alone — `tools/list` can be sent the same way without
an `initialize` first, and answers with the ten tools.

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
| `401` with `WWW-Authenticate: Bearer resource_metadata="…", scope="rma:read"` | no token, expired token, `iss` that is not `Mcp:Http:Issuer`, `aud` that is not `aienabledrma-mcp`, or a signature the realm's keys do not verify. The pointer names the RFC 9728 document below, so a client can find the realm instead of being configured with it |
| `403` | a valid token without the `rma:read` scope, or from a `client_id`/`azp` not in `AllowedClientIds` |
| `403` before any token is read | an `Origin` header that is not in `AllowedOrigins`. A server-side agent sends no `Origin` and is unaffected |
| `500` at startup | `EnsureUsable` refused the configuration: an `http` issuer outside Development, an empty client allow-list, a wildcard or empty origin, an empty scope, a path that does not start with `/` |
| connection refused | the transport is not listening: check `Mcp:Transport` is `http`, not the default `stdio` |

### Every token is a 401 right after the realm was re-imported

Keycloak mints a fresh signing key every time it imports the realm, and the compose service imports
on every boot. The transport caches the realm's key set with its discovery document, so a token
minted after the re-import names a `kid` the cache has never seen, and every machine request answers
`401` while the realm itself is fine — `curl` against the token endpoint still succeeds, which is
what makes this read as a broken token check rather than a stale cache.

The transport refetches the key set when it is handed an unknown `kid`, so the first request after a
rotation succeeds without a restart. Refetches are rate-limited to one every
`RefreshingSigningKeys.MinimumRefetchInterval` (30 s): inside that window an unknown `kid` is refused
without a discovery request, so a flood of invented key ids cannot be turned into a flood of calls
to the identity provider. If the binding was built before this behaviour — or the identity provider
changed its metadata endpoint rather than only its keys — the fallback is the manual restart:

```bash
docker compose --profile mcp-http restart mcp-http
```

That costs seconds and is safe to repeat; `scripts/verify-auth.sh` does it for you when it reuses a
stack that is already running, because a gate that silently depended on a warm cache would pass on
a fresh boot and fail on the second run. The browser flow is not a diagnostic here: it usually
passes, because a refetch triggered by an earlier machine request has already landed.

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
- The compose service lists `http://localhost:5173` and the `mcp-browser` client, so the flow below
  works against it as checked out. A different page origin needs both
  `Mcp__Http__AllowedOrigins__0=<your origin>` and, if it signs in as itself, an entry in
  `AllowedClientIds`.

### Signing in from a browser (PKCE)

`mcp-browser` is public: there is no secret, so PKCE is what binds the code to the page that asked
for it, and the realm rejects an authorize request without a `code_challenge`.

1. Generate a verifier and its SHA-256 challenge:

   ```bash
   VERIFIER=$(openssl rand -hex 32)
   CHALLENGE=$(printf '%s' "$VERIFIER" | openssl dgst -sha256 -binary \
     | openssl base64 -A | tr '+/' '-_' | tr -d '=')
   ```

2. Send the browser to the authorization endpoint. `scope` must include `openid` for the flow and
   `rma:read` for the tools:

   ```text
   http://localhost:8080/realms/aienabledrma/protocol/openid-connect/auth
     ?client_id=mcp-browser
     &response_type=code
     &scope=openid%20rma%3Aread
     &redirect_uri=http%3A%2F%2Flocalhost%3A5173%2Fcallback
     &code_challenge=$CHALLENGE
     &code_challenge_method=S256
   ```

   Sign in as `browser-demo` / `browser-demo`. The account exists only in the development realm
   import; it holds no roles, so what it can do is exactly what the client's scopes grant.

3. Keycloak redirects to `http://localhost:5173/callback?code=...&session_state=...`. The page trades
   the code, with no secret and with the verifier:

   ```bash
   curl -s -X POST \
     http://localhost:8080/realms/aienabledrma/protocol/openid-connect/token \
     -d 'grant_type=authorization_code' \
     -d 'client_id=mcp-browser' \
     -d "code=$CODE" \
     -d 'redirect_uri=http://localhost:5173/callback' \
     -d "code_verifier=$VERIFIER"
   ```

   A replayed code, a missing verifier, or a verifier that does not hash to the challenge is a
   `400 invalid_grant`. That is the whole protection, so the page must not keep the verifier in
   `localStorage` where a script can read it later.

4. The access token carries the same four claims as the machine client's, with `azp: mcp-browser`.
   Send it as `Authorization: Bearer`, from the page whose origin is listed, and the transport
   answers like it does for an agent:

   ```bash
   curl -sS -o /dev/null -w '%{http_code}\n' \
     -X POST http://localhost:5080/mcp \
     -H "Authorization: Bearer $TOKEN" \
     -H 'Origin: http://localhost:5173' \
     -H 'Content-Type: application/json' \
     -H 'Accept: application/json, text/event-stream' \
     -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"runbook-browser","version":"1"}}}'
   ```

   `200`, with `Access-Control-Allow-Origin: http://localhost:5173` on the response. The token lasts
   five minutes and Keycloak issues a refresh token for the flow; a page that keeps a refresh token
   for hours has undone the short lifetime, so refresh on demand and drop it.

   The browser flow's `scope` claim reads `openid rma:read`, not `rma:read`: `openid` is part of the
   authorization-code request, and the transport checks for its required scope among the
   space-separated values rather than comparing the whole claim, so the extra scope is expected.
   The claim set measured from a real run: `iss=http://keycloak:8080/realms/aienabledrma`,
   `aud=aienabledrma-mcp`, `scope="openid rma:read"`, `azp=mcp-browser`, `typ=Bearer`, and
   `sub=<the browser-demo user id>` (a UUID, so not reproduced here). The response body carried
   `expires_in: 300`, a refresh token and an id token; the transport answered `200`
   `text/event-stream` with the `initialize` result for `AIEnabledRma.Mcp`.

   `sub` is the claim the audit row needs: `HttpContextCallerIdentity` records `client:subject`, so a
   browser call is `mcp-browser:<user id>` and an agent call is `mcp-agent:<service account id>`. It
   is not in a token minted by the stock realm, because Keycloak's built-in `basic`/`profile` scopes
   are not created for an imported realm that does not list them — without `basic` the token is
   otherwise valid and carries no `sub` at all, and every caller looked the same to the audit trail.
   `basic` is therefore committed in the realm and is a default scope of both clients; its only job
   is the subject mapper, and it adds nothing to the `scope` claim.

   A browser page is not needed to prove any of this: the authorize request and the sign-in form can
   be driven with curl and a cookie jar, which is how the check in the `200` above was made. What
   the browser adds is `Origin` on the request and the preflight that precedes it.

## Configuration

Bound from `Mcp:Http`; environment variables use `__` as the separator.

| Key | Default | Meaning |
|---|---|---|
| `Mcp:Transport` | `stdio` | `stdio` or `http`; anything else stops the host |
| `Mcp:Http:Issuer` | `http://localhost:8080/realms/aienabledrma` | OIDC issuer, and the required `iss` |
| `Mcp:Http:Audience` | `aienabledrma-mcp` | required `aud` |
| `Mcp:Http:RequiredScope` | `rma:read` | scope every caller must hold |
| `Mcp:Http:SupportScope` | `rma:support` | scope that admits `reveal_customer_contact`; empty stops the host |
| `Mcp:Http:AllowedClientIds` | empty | clients allowed to call; empty is a startup failure |
| `Mcp:Http:AllowedOrigins` | empty | browser origins allowed, and the CORS policy; empty means no browser client |
| `Mcp:Http:Path` | `/mcp` | endpoint path |

The JWT checks read `scope` or `scp` for the scope claim and accept `client_id` or `azp` for the
client, because issuers differ in which they mint.

## What this does not cover

- **The metadata names the issuer as the host reached it.** `authorization_servers` is
  `Mcp:Http:Issuer` verbatim, so the compose setup advertises `http://keycloak:8080/...`: correct for a
  client inside that network, unreachable from a browser on the host. The same hostname rule as the
  token's `iss` applies, and a real deployment sets both to one public name.
- **The example grants the unmask scope to a machine client.** `mcp-agent` may request
  `rma:support`, which is what lets the gate mint one token that may reveal and one that may not.
  A deployment should attach that scope to a support role held by a person; the tool checks the
  scope, and the audit row names whichever caller the token represented.
- **A browser client holds its token where page scripts can reach it.** The transport now admits
  allow-listed origins (see below), but a token in a browser is a token any script on that page can
  read; keep its lifetime short and list only origins you would trust with it.
- **The browser client is a runbook client, not a shipped page.** `mcp-browser` and the
  `browser-demo` user exist so the flow can be exercised; there is no page in this repository that
  performs the sign-in, and the committed password is a development value.
- **The realm is a development realm.** `sslRequired: none`, a committed client secret, admin
  `admin`/`admin`, and a five-minute token lifetime. A deployment needs its own realm, a secret from
  a secret store, https, and an issuer that is not a loopback name.
- **Budgets are shared, but they cost a database round trip.** The counters are rows in
  `mcp_call_stamps`, written inside a transaction that takes a per-caller advisory lock, so any
  number of replicas enforce one allowance and a concurrent pair of calls for the same caller cannot
  both take the last slot. The cost is a transaction per tool call. The counter fails closed: if the
  database cannot be reached the call is refused, because a budget that cannot count is not a budget.
- **The customer-lookup cap is now a day, not a session.** "Session" meant a process, and a process
  is the thing a deployment duplicates, so a per-session cap reset whenever a caller landed on a
  fresh replica. `Mcp:Budget:CustomerLookupsPerDay` (default 200) is a rolling 24 hours per caller;
  `CallsPerMinute` (default 60) is a rolling minute. Neither is reset by a restart.
