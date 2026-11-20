# MCP server runbook

The MCP server (`src/AIEnabledRma.Mcp`) exposes read-only returns tools to AI agents. It has two
transports over the same tools and the same `ToolCallGuard`:

| Transport | Start | Caller identity | Use |
| --- | --- | --- | --- |
| stdio (default) | `dotnet run --project src/AIEnabledRma.Mcp` | `McpSecurity:LocalCallerName` (default `local-stdio`), no roles | A developer's local agent |
| Streamable HTTP | `dotnet run --project src/AIEnabledRma.Mcp -- --http` or `Mcp__Transport=http` | Client id from the access token (`azp`/`client_id`, falling back to `sub`) | Anything shared or remote |

## What every call goes through

1. **HTTP only:** Origin check (a browser `Origin` not in `McpHttp:AllowedOrigins` gets 403), JWT bearer
   validation (issuer = `McpHttp:Authority`, audience = `McpHttp:Audience`, signature, lifetime), and the
   `mcp-caller` policy: the token must carry `McpHttp:RequiredScope` (default `rma:read`) and come from a client
   in `McpHttp:AllowedClientIds`. A missing or invalid token gets 401 with a `WWW-Authenticate` header pointing
   at `/.well-known/oauth-protected-resource` (RFC 9728), which names the authorization server and the scope.
2. **`ToolCallGuard`** (both transports):
   - `reveal_customer_contact` needs an authenticated caller with the `McpSecurity:SupportRole` role or scope
     (default `rma:support`); stdio callers never qualify.
   - Budgets: `McpSecurity:CallsPerMinute` per caller, `McpSecurity:CustomerLookupsPerSession` customer-data
     calls per caller and session. The HTTP transport is stateless, so there the "session" is the caller.
     Budgets live in process memory: a scaled-out deployment needs a shared store first.
   - One `mcp_tool_calls` row per call: tool, caller, session, SHA-256 prefixes of the arguments (never the
     values), result count, outcome (`Ok`, `ToolError`, `RefusedBudget`, `RefusedPermission`, `Failed`), latency.
3. **The tool** returns masked customer data only (see the README's "What an agent can see").

## Configuring the HTTP transport

All values below are examples; supply real ones from the environment or a secret store.

```powershell
$env:Mcp__Transport = 'http'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8085'          # put TLS in front (see P11)
$env:McpHttp__Authority = 'https://idp.example.com/realms/rma'
$env:McpHttp__Audience = 'aienabledrma-mcp'
$env:McpHttp__RequiredScope = 'rma:read'
$env:McpHttp__AllowedClientIds__0 = 'support-agent'
$env:McpHttp__ResourceUrl = 'https://mcp.example.com/mcp'
$env:ConnectionStrings__Rma = 'Host=...;Database=rmadev;Username=...;Password=...'
dotnet run --project src\AIEnabledRma.Mcp
```

Startup fails if `AllowedClientIds` is empty, or if `Authority` is not an `https` URL outside Development.

### Identity provider (D2)

Any OpenID Connect provider that issues JWT access tokens works (Keycloak, Entra ID). Register:

- an API / resource with audience `aienabledrma-mcp` and scopes `rma:read` and `rma:support`;
- one confidential client per agent (client-credentials or authorization-code with PKCE), granted `rma:read`;
- `rma:support` (or a `roles` claim with that value) only for the support tooling that may unmask contacts.

Which provider to use is still the owner's decision (plan.md D2); nothing in the code depends on it.

## Checking the audit trail

```sql
SELECT at_utc, caller, tool, outcome, result_count, latency_ms
FROM mcp_tool_calls
ORDER BY at_utc DESC
LIMIT 50;
```

Repeated identical `argument_hashes` from one caller indicate probing with the same value.
