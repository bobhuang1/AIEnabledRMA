# AIEnabledRMA — plan to finish the project

Written 2026-11-14 from a code review of `main` (`e96156b`).

**Baseline:** `dotnet test` gives 190 passed and 9 skipped. The 9 live-PostgreSQL tests skip when
`RMA_TEST_CONNECTION` is unset.

**Updated 2026-11-15** with what has shipped since the review. The suite is at 335 passed, 0 skipped with
`RMA_TEST_CONNECTION` set, and the same commands are what `.github/workflows/ci.yml` runs; the workstream status
lines below say what each item is proven by, and an item is only marked done where a test would fail if the
behaviour regressed.

## 1. Where the project stands

**Built and working:**
- **The return wizard** (`src/AIEnabledRma.Web`): start, triage, problems, shipping, payment and confirm.
  - It is anonymous, and the request GUID acts as the capability.
  - Customer details are masked on fuzzy matches.
  - The currency comes from the price table.
- **The deterministic policy** (`RmaPolicyEvaluator`): eligibility, warranty tiers, paid repair from the
  per-product price table (`Policy:RepairPrices`), mixed batches with persisted exclusions, and a race-free RMA
  number sequence.
- **AI triage** (`TriagePipeline`):
  - The AI is advisory only, and any model failure becomes human review.
  - Citations are intersected with the retrieved set.
  - `ScopeGuard` refuses questions outside the configured scope.
  - The customer's free text is redacted before it is sent to a provider.
  - Two providers ship: an OpenAI-compatible client and an offline `example-local` model.
- **Knowledge base:** 19 markdown articles under `knowledge-base/`, loaded into an in-memory BM25-style index
  (`AIEnabledRma.Rag`), plus a small RAG host with `/diagnostics`, `/retrieve` and `/rag/reload`.
- **MCP server** (`src/AIEnabledRma.Mcp`): a stdio server with 9 read-only tools: `find_customer`,
  `get_customer_addresses`, `get_customer_returns`, `resolve_shipping_address`, `find_device`, `search_devices`,
  `get_open_returns_for_device`, `check_warranty_coverage` and `get_fee_quote`. Every customer tool returns a
  masked projection, every call writes an audit row and spends a budget, and with `Mcp:Transport=http` the same
  tools are served over streamable HTTP behind Keycloak-issued tokens.
- **Payments:** only `FakePaymentGateway` exists (accept-all, in memory).
- **Ops:** Dockerfiles for Web and MCP (non-root) and `tools/DbAdmin`.
- **CI** (`.github/workflows/ci.yml`): pull requests and manual runs only — restore in `--locked-mode`, build
  with `-warnaserror`, the EF model-and-migration comparison, the suite against a `postgres:16` service, a
  refusal when any test was skipped, and a vulnerability audit; actions pinned by commit, `contents: read`.

**Missing:**
- An authenticated *user* anywhere: the wizard is anonymous by design, the RAG admin endpoints take a shared key
  or loopback, and only the MCP HTTP transport validates a token.
- An operator or back-office view.
- Any CRM or support-case data.
- Vector search or chunking: articles are indexed whole.
- A real payment provider.
- A production configuration.

## 2. Review findings

Severity is for a deployment that faces real customers. None of these bite in the local demo as it is today.

| # | Severity | Finding | Where |
|---|---|---|---|
| F1 | High | **The MCP server hands full PII to any agent.** `find_customer` returns full name, email, phone, external id and default address unmasked, unlike the wizard. Fuzzy matching on partial email or phone makes it an enumeration oracle. There is no caller identity, no audit record of tool calls and no rate or budget limit. Stdio limits it to whoever can launch the process, but every MCP client that launches it sends this data into the model's transcript. | `Mcp/Tools/CustomerLookupTools.cs:212`, `Mcp/Program.cs` |
| F2 | High | **The RAG host's endpoints have no authentication.** `POST /rag/reload` lets anyone trigger a disk reload. `/diagnostics` discloses the server's knowledge-base path and the full article inventory. `/retrieve` is open. | `Rag/Program.cs` |
| F3 | High | **Customer free text reaches the AI provider raw.** The customer statement and conversation turns are sent as written, and the system prompt only *asks* the model not to repeat personal information. With `Provider=openai`, anything a customer types (an address, a phone number, a card number) leaves the company. | `Domain/Triage/TriagePipeline.cs:165-232` |
| F4 | Medium | **Knowledge-base ingestion trusts its input.** It loads every `*.md` under the root, recursively, with no file size or count cap, no provenance (author, review, hash), and no check for instructions planted in article bodies, which go straight into the prompt. A reload publishes whatever is on disk if it parses. | `Rag/Knowledge/KnowledgeBaseLoader.cs:86`, `TriagePipeline` reference block |
| F5 | Medium | **The wizard's Start step can confirm whether a customer exists by email or phone.** It has no rate limiting, and capability GUIDs never expire, so a leaked step URL drives that return until it is confirmed. | `Web/Controllers/RmaWizardController.cs`, `Domain/Rma/RmaRequest.cs` |
| F6 | Medium | **`FakePaymentGateway` is registered unconditionally,** so a Production build would "authorise" every payment. | `Web/Program.cs` |
| F7 | Low | **Resolved.** The knowledge-base filesystem path is gone, the unused `RmaDbContext` injection is gone, and the README no longer describes a payments diagnostics endpoint that does not exist. `/health` now answers an anonymous caller with `{"status":"ok"}` only, and the counts and the provider name sit behind the operator key the RAG host uses; the host refuses to start outside Development without that key, so the remaining question is answered at startup rather than in review. | `Web/Program.cs:173` |
| F8 | Low | **`docker-compose.yml` publishes PostgreSQL on all interfaces with default credentials.** Fine for a demo, but it should bind to `127.0.0.1`. | `docker-compose.yml` |
| F9 | Low | **No CI, no dependency audit, and no lock files.** The live suites skip silently, so a green run can contain 0 database tests. | repo root |

## 3. Goals for "finished"

1. **A secure MCP server.**
   - Authenticated callers, minimal and masked data, audited calls, budgets.
   - A remote transport (streamable HTTP with OAuth) for agents that are not local.
2. **Secure knowledge-base ingestion:** reviewed, size-bounded, provenance-stamped and sanitised before it is
   indexed, with prompt-injection containment.
3. **Support cases from the CRM, with personal information removed, ingested as troubleshooting knowledge.** The
   AI then learns from real resolved cases without ever seeing a customer's identity.
4. **A vector database with chunking:** pgvector in the existing PostgreSQL, structure-aware chunks, and hybrid
   (BM25 + vector) retrieval. The current lexical index remains the offline/CI fallback.
5. **PII never reaches an external AI provider:** customer text is redacted before every model call.
6. **Production-ready:**
   - CI, a real payment gateway behind the interface, refusal of unsafe configuration, rate limits;
   - an operator view for human-review cases.

## 4. Decisions needed from the owner

| # | Decision | Blocks | Proposed default |
|---|---|---|---|
| D1 | Which CRM holds the support cases (Dynamics 365/Dataverse assumed, as for Docketglass), and which case fields may be used | P6 | Dynamics incidents: title, description, resolution, notes; synthetic data until the tenant is configured by the owner |
| D2 | How agents will reach the MCP server: local stdio only, or remote HTTP with an identity provider (which one?) | P2 | **Decided:** both. stdio stays the default for a developer; anything shared is streamable HTTP behind Keycloak, so the realm is one container beside PostgreSQL instead of a tenant someone has to be granted access to. See the P2 status for what that means in configuration. |
| D3 | Whether an agent may see unmasked customer contact data at all | P1 | No. Masked by default, and an unmask tool only for an authenticated support role, audited |
| D4 | The embedding model: a local model (BGE-M3 / nomic via Ollama) or a hosted API | P5 | Local, so no case text leaves the company |
| D5 | The real payment provider | P9 | Stripe, test mode first |
| D6 | Whether the wizard stays anonymous or gets customer sign-in | P8 | Stay anonymous; add expiry and rate limits |

## 5. Workstreams

Each one is a branch and a PR. Every PR runs the build plus tests. Sizes: S is under a day, M is 1–3 days, L is
about a week.

**Status lines** record what exists in the tree today and what proves it. Three levels are used, and they
are not interchangeable: *live* means a test boots the host or talks to PostgreSQL; *loader-level* or
*pipeline-level* means the seam is exercised with a fixture and a fake; *untested* means the code exists and
nothing fails if it is deleted. A workstream is only "shipped" where the bullets say so — the rest of the
bullets stay as requirements.

### P0. Baseline and CI (S) — do first

- Add `.github/workflows/ci.yml`:
  - restore, build, and test with a `postgres:16` service container so the live suites run;
  - **fail if any test is skipped in CI**;
  - pin actions by SHA, with least-privilege `permissions`.
- Enable NuGet lock files (`RestorePackagesWithLockFile`) and run `dotnet list package --vulnerable` in CI.
- Bind compose PostgreSQL to `127.0.0.1` (F8). Make the README match the code (F7).
- **Done when** CI is green with 199 run and 0 skipped.

**Status: shipped, except that no run of the workflow has been observed.**
- `.github/workflows/ci.yml` runs on pull requests and manual dispatch, ignores root markdown and `docs/` but
  not `knowledge-base/` (its markdown is data the retrieval tests read), cancels a superseded run, and has a
  25-minute timeout. Actions are pinned by commit, `permissions` is `contents: read`, and the NuGet cache is
  keyed on the checked-in lock files.
- Restore is `--locked-mode` and the build is `-warnaserror`. The job starts a `postgres:16` service so the live
  suites run, and the step after the tests fails the job if the log shows `Skipped: N` *or* no tests at all — a
  run that quietly skipped the database is red, not green.
- `dotnet list package --vulnerable --include-transitive` runs in the job and fails it on a vulnerable package.
  Lock files are enabled (`RestorePackagesWithLockFile`) and every `packages.lock.json` is committed.
- F8 is fixed: compose publishes PostgreSQL on `127.0.0.1` only.
- Added beyond the bullets above: the EF model/migration comparison (`39fb628`), with the CLI pinned to the
  runtime's own version in `.config/dotnet-tools.json` and `has-pending-model-changes` running before the tests,
  so a widened column fails by name rather than only failing the live suites through
  `PendingModelChangesWarning`.
- Evidence: the same commands in an SDK 10 container against a throwaway PostgreSQL give 335 passed, 0 skipped
  and "No changes have been made to the model since the last migration". The bullet's "199 run" is the
  baseline's count and is now stale.
- **Unproven:** the workflow has never executed on a runner — the tree is not pushed — so what is evidenced is
  the command list and the file's structure, not a green check on a pull request.

### P1. Lock down the MCP data surface (M) — F1

- New `CustomerProjection` shared with the wizard's masking: masked name, email and phone, and city/postcode
  prefix only. Masked is the default for every tool.
- Remove `externalCustomerId` from output. Refuse `find_customer` searches shorter than a minimum length, and
  allow exact matches only on email or phone (no fuzzy contact matching through MCP).
- An `IToolAuditSink` that writes one row per call to a new `mcp_tool_calls` table: tool, caller, argument
  hashes (not values), result count, latency.
- Per-session budgets: calls per minute, and customer lookups per session.
- An optional `reveal_customer_contact` tool, gated by D3: only for an authenticated support role, it requires a
  reason and is audited.
- Tests:
  - projection snapshot tests;
  - an enumeration test (partial email returns nothing);
  - an audit row written per call;
  - a budget refusal.

**Status: shipped, except the unmask tool.** Commits `aa6531c`, `f308981`, `25ca72a`, `cb62e82`.
- **The budget is in PostgreSQL, because a process is the thing a deployment duplicates.** The first version
  counted in a singleton dictionary. That is right on stdio, where one agent launches one process, and wrong on
  the HTTP transport: two replicas each hand out the full allowance, so the configured limit multiplies by the
  replica count while every replica's own numbers look compliant. The counter is now `mcp_call_stamps` — one row
  per counted call, deleted as it ages out — and `PostgresCallMeter` takes a place in a transaction that holds a
  per-caller advisory lock, counts, inserts, and commits only if there was room. Two replicas answering the same
  caller at the same instant cannot both take the last slot, and a refusal rolls back rather than lengthening
  the window that follows it. Both transports register the same meter (`McpServices`), so stdio and HTTP cannot
  drift. `McpSharedBudgetTests` runs two service providers with their own contexts against one real PostgreSQL —
  the closest thing to two replicas a test can build — and asserts the third call is refused on either replica,
  a second caller keeps its own window, the lookup cap is shared, and a lookup does not spend the minute window.
  Putting the counter back in the process (the meter replaced by an instance-level dictionary) fails those sharing
  tests with `Expected: False, Actual: True` while the per-caller split test still passes — which is precisely how
  the old budget looked correct on one replica.
- **The lookup cap is a day, not a session.** "Session" was the process, which is exactly what the replica
  multiplies, so `Mcp:Budget:CustomerLookupsPerSession` is now `CustomerLookupsPerDay` (default 200, rolling 24
  hours per caller). A restart no longer refunds it. The minute window stays a rolling minute. Both windows are
  rows that age out, so no background job is needed for them to move.
- **The counter fails closed.** A call whose place cannot be counted is refused, not allowed: the budget exists
  to bound disclosure, and a meter that silently stops metering is worse than a broken tool call. The cost is one
  transaction and one advisory lock per call, which is the price of a bound that survives replication.
- Masking through `CustomerProjection`, shared with the wizard; `externalCustomerId` is gone from output and
  `find_customer` refuses short terms and matches email or phone exactly. Pinned by `McpCustomerToolTests`,
  including the enumeration case (a partial email returns nothing).
- The audit sink and its `mcp_tool_calls` migration, with `EfToolAuditSinkTests` running both against a real
  PostgreSQL — the live level, not a stub.
- Budgets enforced in `ToolCallGuard` at the MCP `tools/call` filter rather than inside each of the four
  tools, so a tool added later cannot skip them. `McpToolGuardTests` drives the real server over the loopback
  transport. These are in-process tests, not a networked deployment.
- Both counters are keyed on `ICallerIdentity.Describe()`, so one caller cannot spend another's allowance.
  `Two_callers_spend_their_own_budgets_through_one_budget_object` pins that split for calls and for lookups.
- **Outstanding:** `reveal_customer_contact`. D3's default was "masked by default, with an audited unmask
  tool for an authenticated support role"; only the masked half exists, so no role can see a full contact
  record. Nothing is exposed that should not be — the decision is simply not implemented.

### P2. Remote MCP transport with OAuth (M) — D2

- Add a streamable HTTP transport (`ModelContextProtocol.AspNetCore`) beside stdio.
- Protected-resource metadata (RFC 9728), JWT validation (issuer, audience `aienabledrma-mcp`, required scope
  `rma:read`), an Origin check and an allow-list of client ids.
- Caller identity flows into P1's audit and budgets. Stdio keeps working for local development, as a named
  local caller.
- Tests: missing token, wrong audience, missing scope and disallowed Origin each give 401/403. A valid token
  reaches the tools.
- Add a `docs/mcp.md` runbook.

**Status: transport, its checks, RFC 9728 discovery, the committed realm (both clients) and the runbook shipped.**
- `McpTransportMode` picks the transport from `Mcp:Transport` (default `stdio`) and refuses any other value,
  so a deployment that mistypes `http` fails to start instead of coming up as a process that never listens.
- `McpHttpHost` serves `MapMcp` at `Mcp:Http:Path` from the same `McpServices` registrations stdio uses, so a
  tool cannot be guarded on one transport and not the other.
- JWT validation: `iss` must equal `Mcp:Http:Issuer`, `aud` must be `aienabledrma-mcp`, lifetime and signature
  are checked against the realm's discovery document, and one authorization policy requires the `rma:read`
  scope together with a listed `client_id`/`azp`. The Origin allow-list is compared exactly and runs before
  authentication, so a page from an origin nobody allowed is refused without its token being processed; a
  request with no `Origin` header (a server-side agent) is unaffected.
- `McpHttpOptions.EnsureUsable` stops the host on the configurations that would serve the tools wider than
  intended: an http issuer outside Development, an empty client allow-list, a wildcard origin, an empty scope.
  There is no switch that disables authentication, so `Mcp:Transport=http` without an identity provider cannot
  ship.
- The caller recorded in P1's audit is now asked for rather than assumed: the validated token's
  `client_id:sub` on HTTP, `stdio:local` when there is no request. `CallerIdentityTests` pins both.
- Pinned by `McpHttpTransportTests` on a `TestServer` (the real pipeline: routing, origin check, authentication,
  authorization, MCP endpoint), asserting 401 for no token, a token minted for another audience and one from
  another issuer; 403 for a token without `rma:read`, for an unlisted client, for a disallowed origin and for an
  origin that merely starts with an allowed one; and 200 with `check_warranty_coverage` in `tools/list` for a
  valid token at the allowed origin. `McpHttpOptionsTests` covers the startup guard. 13 facts, no database.
- Keycloak is the default identity provider: `Mcp:Http:Issuer` defaults to
  `http://localhost:8080/realms/aienabledrma`, which is accepted in Development only, and the realm needs an
  audience mapper adding `aienabledrma-mcp` to `aud` and a client scope granting `rma:read`. Without those two
  the checks fail closed — a 401 rather than an agent that silently reaches nothing.
- The realm is committed and started by compose: `infra/keycloak/realm-aienabledrma.json` imports realm
  `aienabledrma`, the `rma:read` client scope carrying the `oidc-audience-mapper` that adds `aienabledrma-mcp` to
  `aud`, and the `mcp-agent` service-account client whose default scope is `rma:read`. `docker compose up -d
  keycloak` imports it on every start — the container has no volume, so the file is the source of truth and an
  edit cannot be pinned by an old copy — and the `mcp-http` compose profile runs the transport beside it with
  `Mcp__Http__Issuer=http://keycloak:8080/realms/aienabledrma`. The image is the ASP.NET runtime now, because
  this mode listens.
- The realm also defines the public client `mcp-browser` (PKCE with `S256` enforced, no secret, one accepted
  loopback redirect) and a `browser-demo` user, because an allow-listed origin with no way to obtain a token is
  not a browser client. The compose service lists both clients and the page origin, so the browser flow works
  as checked out.
- The realm file is now pinned by tests rather than by that verification alone: `KeycloakBrowserClientTests` asserts
  `mcp-browser` exists, is public, carries no secret, requires `S256`, enables only the authorization-code flow,
  accepts exactly one loopback redirect and lists only loopback origins, and that the `mcp-http` compose service
  allow-lists that client id and that origin. The live check proved it once; the JSON and YAML are what a future
  edit can silently break, so they are what the test reads. Mutations checked: adding a `secret` to the client and
  replacing the compose client id each fail it.
- Verified end to end through the browser flow, not just the machine one: authorize `200`, sign-in `302` to
  `http://localhost:5173/callback?code=...`, code exchanged with the verifier and no secret, and the resulting
  token carries `iss=http://keycloak:8080/realms/aienabledrma`, `aud=aienabledrma-mcp`,
  `scope="openid rma:read"` and `azp=mcp-browser`. The preflight from `http://localhost:5173` answers `204` with
  that origin echoed, `POST /mcp` with the token and that `Origin` answers `200`, and the same token from an
  unlisted origin answers `403`. Code replay and a missing verifier were not separately exercised, so
  `400 invalid_grant` is Keycloak's documented behaviour rather than something measured here.
- Verified against that realm rather than against a test-held key: the imported client's token carries
  `iss=http://localhost:8080/realms/aienabledrma`, `aud=aienabledrma-mcp`, `scope=rma:read` and `azp=mcp-agent`;
  `POST /mcp` with it answers `200` with the `initialize` result as one server-sent event, `tools/list` answers
  `200` twice with the nine tools, and the same request without a token answers `401`. A token minted through
  `localhost` presented to a server validating the compose service name answers `401` naming the issuer, which
  is the runbook's "both sides use the same hostname" rule observed rather than assumed.
- `docs/mcp.md` is the runbook: what the realm must provide, start-up, the token request, both ways to run the
  server, the 200 and 401 checks, a failure table carrying the exact issuer error, the configuration keys, and
  what the path does not cover yet.
- The flows are a **gate, not a recipe**: `scripts/verify-auth.sh` boots the committed realm and the `mcp-http`
  profile with compose, then asserts the answers — the machine token's `iss`, `aud`, `scope` and `azp`, `200` for
  `initialize`, `401` with no token including the `resource_metadata` pointer, `403` from an unlisted origin, `204`
  preflights with and without CORS, and the PKCE flow with the code refused without its verifier and a replay
  refused. The `auth-flows` job in `.github/workflows/ci.yml` runs that script on every pull request, so a realm
  import that Keycloak rejects — which has already happened once, silently — fails the build. The runbook names the
  same script as its one-command check, and `The_runbook_and_ci_name_the_same_script` asserts the three references
  still match, so renaming it fails a test instead of leaving a runbook that describes a file nobody has. Verified
  end to end on this workstation: all checks passed, exit 0.
- `main` itself was unchecked: `ci.yml` triggered on pull requests and manual dispatch only, and the one run recorded
  on the repository was a failure from days earlier, so twenty commits reached `origin/main` without the gate ever
  seeing them. Pushes to `main` now start the same workflow (a documentation-only merge is still skipped by the path
  filter), and `CiWorkflowTests` asserts that the trigger block names `main` and that `auth-flows` and
  `runtime-images` are still jobs — deleting the trigger or a job fails a test instead of quietly removing the check.
  Mutation: deleting the `push:` block leaves only `pull_request`, `Main_is_checked_on_push` fails, the other two
  pass. Suite: 379 passed, 0 skipped.
- A re-imported realm used to strand the transport: Keycloak mints new signing keys on every import, the host cached
  the old set, and every machine token was a 401 until someone ran `docker compose restart mcp-http`. The transport now
  refetches when a token names an unknown key id (`RefreshingSigningKeys`, with a 30 s floor per refetch so an invented
  key id cannot be turned into a discovery request against the identity provider), and `docs/mcp.md` keeps the
  restart as the documented fallback for a build that predates this or a metadata endpoint that moved.
  The first attempt refetched with `ConfigurationManager.RequestRefresh` on the cached manager, and that served the
  **previous** document: measured against the stack, the resolver was reached and returned the key id of the realm that
  had just been replaced, so the rotated token stayed a 401. Each refetch therefore builds a fresh manager with no
  cache to serve. Measured end to end: warm token `200`, Keycloak recreated with the transport left running, a token
  minted from the new keys `200`, and the next request `200` — the sequence that used to need the restart. Mutation:
  returning the cached set instead of refetching fails four of the six resolver tests, and restoring
  `RequestRefresh` fails the loopback key-set test with `Expected: "key-b" / Actual: "key-a"`. Suite: 386 passed,
  0 skipped.
- The gate showed the accepted paths and three refusals that needed no token (none, unlisted origin, no verifier), but
  it could not show a **token** being refused: every token it could mint had the accepted shape. The realm now carries
  `mcp-negative` — allow-listed, one-second lifespan, two audience scopes — and `verify-auth.sh` asserts a token minted
  for `aienabledrma-not-mcp` is `401`, a token with the right audience and no `rma:read` is `403`, and the same
  short-lived token is `200` when fresh and `401` after 65 s (the transport's one-minute skew plus the one-second
  lifespan). Each case asserts the token's claims before the status, so a realm mistake fails as "the token is not the
  shape this check assumes" instead of a status that happens to match. Measured on the running stack: `401`, `403`,
  `200` then `401`; the whole script passes 41 checks. The first import was rejected — Keycloak stores a client
  description in `VARCHAR(255)` and this one was 365 characters — which is the same class of failure the import check
  exists for. `RealmGateClientTests` pins the client, its scopes, its lifespan, the allow-list entry and the script's
  three assertions. Mutation: deleting the client fails the realm test. Suite: 390 passed, 0 skipped.
- The hosts never checked the corpus they loaded: `REVIEWED.sha256` mattered only to the Dockerfile, the csproj and
  the loader's Production-only review path, so a drifted or empty corpus answered silently everywhere else. `AddRag`
  now registers `KnowledgeBaseStartupCheck`, which every RAG-loading host (Web and RAG) runs at startup: it compares
  what is on disk with the manifest — missing manifest, article without a line, line without an article, hash
  mismatch, and a loader that indexed fewer files than exist — logs
  `Knowledge base at {root}: {articles} article(s), {lines} line(s) in REVIEWED.sha256, {problems} problem(s)` in both
  environments, and throws outside Development when anything disagrees. In Development it logs the same facts and
  starts anyway, so a developer can inspect the corpus rather than crash-loop. Registered in `AddRag` rather than per
  host because a host that forgets the call is the failure being guarded against. Mutation: returning before verifying
  fails the three startup tests (the drift refusal, the Development log, the count log). Suite: 400 passed, 0 skipped.
- The `mcp-browser` authorization-code flow was then **driven by hand, once, outside the script**, to read what it
  actually mints: authorize `200`, sign-in `302` with a code, exchange `200` with `expires_in: 300`, a refresh token
  and an id token, and the transport answering `200 text/event-stream` with the `initialize` result and
  `Access-Control-Allow-Origin: http://localhost:5173`. The claims were `iss`, `aud=aienabledrma-mcp`,
  `azp=mcp-browser`, `scope="openid rma:read"`, `typ=Bearer`, `sid`, `jti`, `exp`, `iat` — and **no `sub`**. That
  last absence was the defect the runbook could not see: Keycloak creates its built-in `basic`/`profile` scopes for
  a new realm but not for an imported one that omits them, so the token was valid and anonymous, and
  `HttpContextCallerIdentity` recorded `mcp-browser:unknown-subject` for every browser caller. The realm now carries
  a `basic` scope with one `oidc-sub-mapper` as a default scope of both clients; the re-run shows
  `sub=<user id>` for the browser flow and a service-account id for the machine flow, and the gate asserts both are
  non-empty.
- Re-importing the realm also exposed a trap worth knowing: Keycloak mints new signing keys, the transport caches
  the old ones, and the next machine token answers `401` — which reads as a broken realm and is not one. The causes
  are separate (a restart of the transport fixes it), so `verify-auth.sh` restarts `mcp-http` whenever it reuses a
  running stack, and `docs/mcp.md` says to recreate Keycloak **and** restart the tool server after editing the realm.
- The origin allow-list is also the CORS policy, so a listed page can drive the endpoint instead of only being
  refused by it. The policy is applied to the whole host rather than as endpoint metadata because a preflight
  `OPTIONS` matches no route. `An_allowed_origin_can_preflight_the_endpoint` asserts the 204 with
  `Access-Control-Allow-Origin` and `authorization` among the allowed headers,
  `An_allowed_origin_can_read_the_headers_the_transport_returns` asserts `Mcp-Session-Id` is exposed, and
  `A_preflight_from_a_disallowed_origin_grants_nothing` asserts an unlisted origin gets no CORS headers — the
  framework answers every preflight 204, so it is the missing header that makes the browser refuse, and the
  real request from that origin is the existing 403. Removing `UseCors` fails the first two.
- RFC 9728 discovery is served, so a client that was handed a URL no longer has to be configured with the
  realm. `McpHttpHost` maps `/.well-known/oauth-protected-resource/mcp` (the path-specific form, RFC 9728 §3.1)
  and the root `/.well-known/oauth-protected-resource`, both unauthenticated — the document describes how to get
  a token — and both built from the request's own scheme and host, so the value is the address the caller used.
  The document carries `resource` (the endpoint URL, which a client compares with its own),
  `authorization_servers` (the issuer the host validates), `scopes_supported` (`rma:read`),
  `bearer_methods_supported` (`["header"]`) and a name. The 401 is written by a JWT `OnChallenge` handler rather
  than the framework default, and carries
  `WWW-Authenticate: Bearer resource_metadata="…/oauth-protected-resource/mcp", scope="rma:read"` — the scope
  included so a client asks its authorization server for the right one. `A_401_names_the_metadata_document_and_the_document_answers`
  follows the header to the document and asserts every field; `The_metadata_is_also_at_the_root_well_known_path`
  pins the fallback. Replacing the challenge with the framework default fails the first on the missing pointer.
- **Outstanding:** none.
- The audit trail's identity claim is asserted against the table, not inferred from a unit or from the sink:
  `A_tool_call_over_http_writes_the_token_s_client_and_subject_to_the_audit_table` sends a real `tools/call`
  over the HTTP transport with a token carrying `client_id` and `sub`, then reads the row back from
  `mcp_tool_calls` and asserts `rma-agent:agent-1`, not `stdio:local`. Only the device catalogue and the call
  counter are replaced — routing, token validation, the tools/call filter, the guard, the identity and
  `EfToolAuditSink` are the real ones, against a real PostgreSQL — because the two halves can break
  independently: a correct caller name written by a sink that never gets reached leaves the same empty trail.
  Making `HttpContextCallerIdentity` answer with the local name — the exact failure a transport change could
  introduce unnoticed — fails it with `Expected: "rma-agent:agent-1", Actual: "stdio:local"`.
- That test, in its earlier sink-level form, found a real defect the identity unit test could not see: JWT inbound claim mapping
  renames `sub` to a schema URI, so every HTTP audit row recorded `unknown-subject` for a perfectly valid token
  (the sink-level version of this test asserted `rma-agent:unknown-subject`; only the row read from the table
  would have been caught either way).
  The host now sets `MapInboundClaims = false` and reads the token's own claim names, which is what the identity
  class and `NameClaimType` already assumed.

### P3. Redact before every model call (M) — F3

- New `AIEnabledRma.Privacy` library: a `PiiRedactor` with pattern recognisers for email, phone, card (Luhn),
  postal address, serial-like and order numbers, and names of the matched customer (known from context), plus a
  name/NER pass when a local model is configured.
- `TriagePipeline` sends redacted text only. Placeholders are stable within a conversation (`[EMAIL_1]`).
  Device serial and product stay, because policy needs them.
- Model output is scanned before display or storage. A reply that contains a raw identifier is replaced, and
  the case goes to human review.
- Log entity counts only, never values.
- Tests:
  - a synthetic corpus of fault statements with planted PII;
  - a fake `IChatModel` that records what it received;
  - the gate: zero planted values reach the model.

**Status: shipped; proven against a fake, not a provider.** Commit `de4bde3`.
- `AIEnabledRma.Privacy` recognises email, phone, card numbers (Luhn-checked), postal addresses, serial-like
  and order-like numbers, and the customer names supplied with the request. Serial, SKU and product are
  protected values rather than redacted, because the policy needs them. Placeholders are stable within a
  session.
- `TriagePipeline` redacts the statement and every conversation turn before retrieval and before the model
  call, scans the returned summary and steps for a surviving identifier and routes the case to human review
  if one is found, and logs entity counts only.
- Evidence is at the pipeline seam: `PiiRedactorTests` for the recognisers and the planted corpus,
  `TriagePipelineTests` with a fake `IChatModel` that records what it received. **No test runs a real model
  or provider**, so "zero planted values reached the model" is proven against the fake only.
- **Outstanding:** the name/NER pass. No local model is configured, so the redactor knows no names except
  those the caller hands it; a third party's name typed into a fault description is not recognised.

### P4. Secure knowledge-base ingestion (M) — F2, F4

- Authenticate the RAG host's operator endpoints (API key or JWT with an `rma:kb-admin` scope).
  - `/rag/reload` becomes admin-only and audited.
  - `/diagnostics` drops the filesystem path for anonymous callers.
  - `/retrieve` requires auth or is turned off outside Development.
- Loader limits: maximum file size, article count and front-matter size; UTF-8 only; no symlinks out of the
  root.
- Provenance on every article: front matter gains `reviewed_by`, `reviewed_at` and a content hash. A reload
  refuses unreviewed articles in Production.
- Sanitise article bodies before they go into the prompt:
  - strip HTML and link targets;
  - flag instruction-like text ("ignore previous", role markers);
  - wrap each article in delimiters the system prompt declares as data.
- Tests: oversize, symlink, unreviewed and injected-instruction fixtures are each refused or neutralised. An
  injection eval runs against triage (the model must not obey article text).

**Status: shipped except the review itself.** Commits
`6577f78`, `f072698`, `f3ef418`, `6ad56da`, `8946532`, `2432487`, `15897b1`, `f80d363`.
- The operator endpoints sit behind `RagAdminGate`: a key in `X-Rag-Admin-Key` compared in fixed time, or
  loopback when no key is configured, and a refusal is a 404. A keyless host refuses to start outside
  Development (`f3ef418`) and `6ad56da` boots Production and expects the throw; `RagAdminGateTests` covers
  the decisions, `RagAdminEndpointTests` the wiring.
- Loader limits: file size, article count, front-matter size, strict UTF-8, symlinks and paths that escape the
  root. `KnowledgeBaseLoaderTests` and `KnowledgeBaseHardeningTests` cover each refusal.
- Provenance: `reviewed_by` and `reviewed_at` are required where the host requires review (Production), and
  the checked-in `REVIEWED.sha256` pins the bytes a reviewer read, so an article edited after review is
  refused rather than cited. `ShippedKnowledgeBaseTests` loads the real corpus with review on, and `15897b1`
  makes the RAG host refuse to start on a corpus with load errors or no articles.
- Bodies, titles and keywords are sanitised at load, after normalising numeric entities, invisible format
  characters and compatibility forms; the triage prompt wraps the reference block in delimiters the system
  prompt declares as data.
- Title sanitisation is proven end to end by `A_title_borne_injection_never_reaches_the_prompt`: an article
  whose *title* is an instruction is loaded through the real loader and index, retrieved through
  `KnowledgeIndexRetriever` — the implementation the host registers — and the prompt's `TITLE:` line is
  asserted to be `[instruction-like text removed]`, with the instruction absent from the prompt entirely.
  Putting the raw title back in the loader makes the test fail on that assertion. It had existed since
  `f072698`, but fed the pipeline from a stub retriever and looked for the marker anywhere in the prompt, so
  what it proved was weaker than what it was cited for; both halves are now the real thing.
- `POST /rag/reload` is audited. Each attempt records the credential that admitted it (`admin-key` or
  `loopback`), the remote address, the time, the article counts before and after, the duration and, when it
  was refused, the reason — through `IRagReloadAudit`, which is a structured log line by default and a seam a
  deployment can replace with a durable sink. A refusal is recorded as carefully as a success, because after a
  bad deploy the question is who asked and what the corpus looked like at the time.
  `A_reload_records_who_asked_and_what_it_did` and `A_refused_reload_is_recorded_as_a_refusal` pin both.
  The record names a credential rather than a person: the key is shared, so the honest answer is which kind of
  credential was presented and from where.
- **Outstanding:**
  - **The provenance is synthetic.** `reviewed_by: docs-team` and `reviewed_at: 2026-09-29` were written by
    the implementer, and `REVIEWED.sha256` was generated from the same files, so it proves the articles have
    not changed since that commit — not that anyone reviewed them. A deployment must re-review the corpus
    and regenerate the manifest.

### P5. Vector store and chunking (L) — D4

- Add pgvector to the existing PostgreSQL (`pgvector/pgvector:pg16` image). A migration adds `kb_documents`
  (source, kind = `article`/`crm_case`, provenance, hash, policy version) and `kb_chunks` (document id, ordinal,
  heading path, text, token count, `embedding vector(N)`, `tsvector`).
- Add an HNSW index (`vector_cosine_ops`) and a GIN index on the tsvector.
- **The chunker:**
  - markdown-aware: split on headings, keep the article title and heading path as a prefix;
  - 300–500 tokens with about 15% overlap, measured with the embedding model's tokenizer;
  - troubleshooting steps are kept whole;
  - CRM cases are chunked as symptom / diagnosis / resolution.
- An `IEmbeddingModel` abstraction (OpenAI-compatible `/embeddings`, local by default), batched with retry.
  Failed chunks are marked and retried, and never silently dropped.
- Hybrid retrieval in `IKnowledgeRetriever`:
  - BM25 (tsvector) plus vector search, fused by reciprocal rank;
  - the product-scope filter applied in SQL *before* ranking (keeping today's rule);
  - citations point to (document, chunk).
- The current in-memory index stays as the fallback when no database or embedding model is configured, so the
  offline demo and the unit tests keep working.
- An ingestion command, `DbAdmin kb-ingest`: idempotent by content hash, re-embeds only changed chunks, and swaps
  a generation atomically.
- Tests:
  - chunk-boundary fixtures;
  - hybrid retrieval against a golden question set (recall@3 not worse than today's lexical index on the 19
    articles);
  - live pgvector tests in CI (P0's service container).

**Status: not started; its acceptance test now exists, so the first line of it is measurable.**
- `eval/retrieval-golden.json` holds 40 questions written in a customer's or a bench technician's wording, each
  naming every article that would be a correct citation, plus the recall@3 floor. All nineteen articles are
  covered. Many of the cases are near-miss pairs — trigger vs firmware, decode vs window damage, freeze vs
  blank screen, cradle vs accessory, who decides vs what evidence — so a scorer cannot pass by matching a
  category; both product-scoped articles have cases in the customer's own description rather than the article's
  title. It also owns two coverage
  rules the scorer enforces: a case may not name an article the corpus has dropped, and every article must
  appear in at least one case, so the number cannot quietly measure a shrinking share of the corpus.
- `RetrievalGoldenSetTests` composes the host's own `AddRag` with review on and the product families the
  scoped cases name, then scores whatever `IKnowledgeRetriever` that resolves to: recall@3 and MRR, printed per
  case, so a P5 change shows which question it broke rather than only that the average moved. The scorer itself
  (`RetrievalGoldenSet` in the RAG project) takes the retriever interface, so a vector store is measured by the
  same cases, top-three rule and floor by being registered where `KnowledgeIndexRetriever` is — no edit to the
  test, the data or the floor. `The_scorer_measures_the_retriever_it_is_given` pins that with two stubs, and
  registering an empty retriever in `AddRag` fails the acceptance test with recall 0.000 without any change to
  the cases, which is the proof that the number now follows the implementation rather than the class.
- History now, five appended entries rather than one block: **0.714 (15/21) MRR 0.627** on the set before it was
  widened, **0.775 (31/40) MRR 0.646** on the unstemmed tokeniser, **0.850 (34/40) MRR 0.750** after it, the same
  two numbers with the five unanswerable questions and the citation metrics first recorded, then **0.925 (37/40)
  MRR 0.825** once the curated synonym map became the default. The numbers read 0.714 → 0.775 → 0.850 → 0.925,
  each step a different cause (more questions, then a better tokeniser, then a lexicon).
- The tokeniser (`TextTokens`) now does what the first baseline said was missing: a stopword list, so `the`,
  `and` and `now` are no longer scored terms; a shallow suffix stemmer (`-s`, `-es`, `-ies`, `-ed`, `-ing`, the
  doubled consonant an inflection leaves, and the final `e`), so `spilled` reaches `spill`, `scratched` reaches
  `scratch` and `updated` still reaches `update`; and the surface form indexed beside its stem, because stemming
  alone *lost* "it restarts by itself every few minutes" — the article's own keyword is `restarts`, and collapsing
  it to `restart` let a shorter article win on its other words. `TextTokensTests` pins each rule, including the
  ones that must not fire (`spill` must not become `spil`).
- The stemmer was reviewed against the corpus's own vocabulary rather than by reading it, and one **over-stemming
  collision** turned up: dropping the final `e` turned `note` into `not`, and both words are in use — an article says
  "Note which of the four steps produced a response", and questions say "it will not switch on". The fix is a
  collision guard rather than a dictionary: a stem is kept whole when shortening it would produce a word the corpus
  already uses differently. Two pieces of dead weight went with it — a `Complete` helper whose added `e` was removed
  again by the final-`e` rule on the next line, and a dedicated `sses` branch that the `-es` sibilant rule already
  covered. The measurement did not move (0.925 / 0.825), which is the point of a simplification: no new entry, and
  the per-case ranks still match the recorded ones.
- Net effect on the forty cases, per case rather than netted: four recovered (`rq-010` 0→1, `rq-012` 0→1,
  `rq-022` 0→2, `rq-028` 0→1), four moved up without changing whether they hit (`rq-009` 2→1, `rq-026` 3→1,
  `rq-027` 3→1, `rq-006` 3→2), **two previously passing questions slipped a rank** (`rq-004` 1→2, `rq-005`
  1→2) and one was lost (`rq-038` 3→miss, where the question now matches the cradle article's vocabulary more
  strongly than the evidence article's), so recall +0.075 and MRR +0.104. The demotions are the price of the
  stopword list and are named rather than averaged away; both still sit inside the top three, so neither costs a
  citation yet. Three still miss after the map and they are **not vocabulary gaps**: `rq-017` (`evidence`, `faulty`),
  `rq-033` (`photos` → `documentation`) and `rq-038` (`write down` → `record`) all name words that `kb-0017`'s own
  title and keywords contain, so a synonym cannot bridge them — the long article loses on BM25 length normalisation
  and coverage against shorter ones. Those need a ranking change (or an embedding), measured on its own, and the
  latest entry's `misses` lists all three.
- **The retriever seam now has a second implementation, measured by the same cases.** `SynonymExpandingRetriever`
  wraps the index, expands the customer's words with the corpus's (`switch`→`turn`, `photos`→`documentation`,
  `halfway`→`truncated`, `faulty`→`fault`, `write`→`record`, `pull`→`press`, `nothing`→`dead`), and is selected by
  `Rag:Retrieval:SynonymExpansion`, **which is now the default**: the map fixes three of the six misses the
  tokeniser could not (`rq-002` switch→turn, `rq-008` pull/nothing→press/dead, `rq-013` halfway→truncated), taking
  recall@3 from 0.850 to **0.925 (37/40)** and MRR from 0.750 to 0.825, with no other case moving in either
  direction. The floor moved with it, to **0.9** — the measurement rounded down, as the file's rules require — so the
  plain index no longer clears the bar. `Disabling_the_synonym_map_measures_the_lexical_index` is the ablation that
  keeps that honest: it pins the fallback at exactly 0.850/0.750, so the 0.925 is the map's contribution rather than
  the default having changed. It also keeps the seam honest: the acceptance test scores whatever
  `IKnowledgeRetriever` the host registers, which is how this implementation was measured before it became the one
  the host registers by default.
- The recorded measurement is **append-only history**, not one block that gets overwritten: `measurements` holds
  every run, oldest first, and the acceptance test reproduces the latest entry — recall@3, MRR, case count and all
  forty per-case ranks — while asserting the dates never go backwards. Overwriting the last entry was the failure
  mode worth removing: it keeps a number that cannot be reproduced and destroys the one that could. A corrected
  measurement is a new entry whose notes name the one it supersedes.
- Each entry records the **corpus fingerprint** it was measured on (`RetrievalGoldenSet.CorpusFingerprint`:
  sha256 over each article's relative path and bytes, sorted so enumeration order cannot change it). The test
  computes it and fails when it differs from the latest entry, with a message that says the corpus moved under the
  measurement: a number that changed with the corpus is the articles' doing, not the retriever's. The two older
  entries carry `null` rather than a backfilled value nobody measured, which is stated in their notes.
- Each entry also records the **reviewed corpus hash** (`RetrievalGoldenSet.ReviewedCorpusHash`): sha256 over the
  sorted entries of `knowledge-base/REVIEWED.sha256`, normalised so line order, CRLF and hash case cannot move it.
  The fingerprint above answers "what are the bytes on disk"; this one answers "which bytes did a reviewer sign
  off", and the eval loads with `RequireReview` on, so the second is the corpus the retriever actually searched.
  Recording both is what attributes a moved number instead of merely noticing one: fingerprint moved with the
  reviewed hash held still is an article edited without a re-review, which the loader then refuses, so the served
  corpus changed even though no reviewer acted; both moving is an edit that was re-reviewed. With one hash alone
  neither case can be told from the retriever behaving differently, which was the gap this closes. The check is in
  the acceptance test beside the fingerprint check, and `The_reviewed_corpus_hash_ignores_order_and_line_endings`
  pins the normalisation with a manifest rewritten in reverse order and CRLF.
- **The floor-moving rules are in the data**, in `floorRules`, so a change to them is a reviewable diff beside the
  numbers: never lower a floor in the same commit that changes scoring code (land and measure the change, append
  its entry, move the floor in a later commit citing it); never lower one to turn a red run green; raise one only
  in the commit that appends a measurement meeting it; keep `measurements` append-only; record a fingerprint every
  time; add cases but never weaken one. The acceptance test asserts the rules are present and that the
  same-commit prohibition and the append-only rule are both stated, so deleting the sentence fails the suite
  rather than degrading the file to convention.
- **The set has eight questions whose correct answer is no article at all** (`rq-041`…`rq-048`: currency and
  invoicing, a mobile app, a courier, a tablet version, an account e-mail, and then refunds, shipping and billing).
  They are not near-misses: the corpus contains no `invoice`, `kroner`, `android`, `ios`, `courier`, `tablet`,
  `subscription`, `billing` or `ireland`, so a citation for one is an unrelated article chosen with confidence.
  `refund` and `shipping` occur exactly once each, in `kb-0016`'s list of things the assistant must not quote, which
  is the kind of surface match a lexical index finds and a reader would not call an answer.
- **Recall keeps its floor and its meaning**: recall@3 is divided by the forty answerable questions only, so the
  eight unanswerable cases cannot move it (0.925, 37/40, unchanged when the three new ones were added). Two
  citation metrics are measured beside it. precision@3 is the mean share of the three citations that were correct;
  rank-1 rate is the share of questions whose first citation was correct. For an unanswerable question zero is the
  right number of citations, so abstaining scores 1 and citing anything scores 0 on both — scoring those cases as
  0/3 either way would make them a constant in the average and leave a retriever that abstains indistinguishable
  from one that cites nonsense. Measured now: **precision@3 0.326, rank-1 0.646**; seven of the eight unanswerable
  questions were still handed three articles each, and only `rq-042` (the mobile app) was correctly left alone.
- **All three are floored, including the one that charges for confident nonsense.** `precisionAt3Floor: 0.32`
  sits beside `mrrFloor: 0.8` and `recallAt3Floor: 0.9`; all three are required in the data, so deleting one is a
  test failure rather than a quiet weakening. The latest entry carries `precisionAt3`, `rank1Rate`, `answerable`
  and a `falseCitations` map naming the questions that cited anything, and the acceptance test fails if those
  fields are missing or disagree with the run. `An_unanswerable_question_is_scored_on_abstaining` pins the
  semantics with two stubs that differ only in whether they abstain: same recall, precision 1/6 vs 2/3, rank-1 0.5
  vs 1.0, false-citation rate 1 vs 0. `A_retriever_that_buries_the_answer_fails_the_mrr_and_precision_floors`
  pins the bar itself: a stub that returns `[wrong, wrong, expected]` for every question has recall 1.0 and still
  fails both citation floors, which is the vector-store failure nearest-neighbour search makes easy.
- The ranks are the part that can move quietly: one question up a rank and another down leaves both aggregates
  identical, so the latest entry's ranks plus a printed `rank N -> M` for every case makes that swap a failure
  naming two question ids instead of a silent pass. Re-recording is per case too — the appended entry records the
  rank that actually moved. Floors catch a
  regression; the baseline catches a stale record — a case edited, an article added, a change that clears the floor
  while still moving the number — which would otherwise leave the file claiming a measurement nobody can reproduce.
- The corpus **reaches the runtime image**, which it did not before: `AIEnabledRma.Mcp` declared no knowledge-base
  content of its own, and a ProjectReference publishes its assembly, not the content another project copies, so
  `dotnet publish` produced an image with no `knowledge-base/` at all — the Dockerfile promised articles the
  container never had, and a host that reads them would have started on an empty corpus and called every question
  unanswered. The items are now declared in the tool server's own project, the same way the Web project declares
  them, and the image was rebuilt and inspected: `/app/knowledge-base` holds all nineteen articles, the README and
  the manifest. `PublishLayoutTests` runs `dotnet publish` for both hosts and asserts the manifest plus every
  article is present, so the check is on the artefact rather than on the declaration; removing the item group fails
  it with `the published AIEnabledRma.Mcp has no knowledge-base/REVIEWED.sha256`.
- Building the **Web image** then failed for a different reason: its Dockerfile restored the project after copying
  manifests for four of its five references, with `AIEnabledRma.Privacy.csproj` missing, so `docker build` stopped at
  `NETSDK1004` before the publish step and the image did not exist at all. Publishing from the source tree could
  never show that, because the whole checkout is present there. The line is added, the image builds and its
  `/app/knowledge-base` carries the same twenty files, and `The_dockerfile_copies_every_project_it_builds` walks each
  host's references transitively and fails when one is not in the COPY list above the restore — removing the Privacy
  line reproduces the exact message in CI rather than at the next hand-built image.
- The images themselves are a check, not just a claim: `scripts/verify-images.sh` builds both from their Dockerfiles
  and asserts each carries every article and `REVIEWED.sha256` under `/app/knowledge-base`, and the `runtime-images`
  CI job runs it. It is a different claim from the publish test — the Docker build sees only what the context and
  `.dockerignore` admit, and restores from a hand-written COPY list — and it fails as a named check rather than an
  aborted build: appending `knowledge-base/` to `.dockerignore` leaves both images without a corpus and the script
  reports `FAIL MCP image builds` and `FAIL Web image builds`, exit 1. Measured on this workstation: both images
  build, both carry all twenty files and the manifest, all checks passed.
- The Dockerfiles now refuse to produce such an image at all: each one asserts `/app/knowledge-base/REVIEWED.sha256`
  exists and at least one article does, right after publish, and fails the build with
  `knowledge-base is missing from /app: the publish output carried no corpus`. This is the check that lives with the
  artefact rather than beside it — `dotnet publish` exits zero when a project stops declaring the content, which is
  how the corpus went missing the first time, and a plain `docker build` now inherits the failure instead of needing
  someone to run the script. Mutation: deleting the corpus `ItemGroup` from the tool server's project leaves publish
  clean and the build fails at that line, exit 1; with the group restored the image carries all twenty articles.
- All three numbers are floored, not just printed. `mrrFloor: 0.8` sits beside `recallAt3Floor: 0.9` because
  recall@3 cannot see a hit slipping from rank 1 to rank 3: the same questions still appear in the top three, so the
  recall number does not move while the citation handed to the model gets worse. The acceptance test asserts the
  floors and names every case not at rank 1, so a fall reads as a diagnosis rather than a number. Measured:
  reversing the order the index returns keeps recall@3 at 0.925 and fails the test with `MRR is 0.438, below the
  recorded floor 0.800`; raising only `precisionAt3Floor` to 0.5 fails with `precision@3 is 0.326, below the recorded
  floor 0.500` and lists the seven questions that cited something.
- The stopword list was checked by ablation rather than assumed: with the same stemmer and the same forty cases,
  removing only the stopword filter measures recall@3 0.825 (33/40) and MRR 0.721 against 0.850 and 0.750 with it.
  One question (`rq-010`, "a deep scratch across the scanner window") and a rank demotion are what the generic
  words cost, which is what the comment beside the list claims and now has a number behind it. Re-running the
  ablation is a one-line edit to `TextTokens.Tokenize`; the assertion in the test means a change that keeps the
  list but loses the lift still fails.
- The floors follow the measurement rather than the aspiration: 0.9, 0.8 and 0.32 with forty answerable and eight
  unanswerable cases still fail on a single lost question, a single demotion, or the loss of one abstention, and
  changing any of them is a reviewable diff in the data with the before and after written beside it. The recall
  floor moved to 0.9 only because the measurement did (0.850 → 0.925). MRR's floor moved 0.75 → 0.8 and precision
  got its first floor in the commit that appended the measurement meeting both (0.825 and 0.326): raising a bar
  beside the run that clears it is what the rules allow, and 0.32 is the measurement rounded down, so the bar is not
  a record of the run by another name.

### P6. CRM support-case ingestion with PII removed (L) — D1, after P3 and P5

- A `ICaseSource` interface with a `DataverseCaseSource` (Dynamics 365 incidents: title, description, notes,
  resolution, product, status, closed date) and a `FakeCaseSource` for CI.
  - Credentials come from configuration or secret storage only.
  - The source is read-only, with an incremental watermark on modified date.
- Only **resolved** cases with a resolution are eligible. Customer, account, owner, contact and order fields are
  dropped at the source mapping and never read into the pipeline.
- The free text goes through P3's redactor plus case-specific recognisers: email signatures, "From:/To:" blocks,
  ticket and order numbers, phone or address in notes, and agent names.
  - It **fails closed:** a case whose verification pass still finds an identifier is quarantined, not indexed.
- Each case is summarised into symptom / diagnosis / resolution text, then chunked and embedded through P5 as
  `kind = crm_case`, linked to product family for the scope filter.
  - The local model is used if configured; otherwise the redacted fields are used as written.
- A dry-run report before the first real import: counts per entity type per case and quarantined cases, never
  values. A human reviews a sample before anything is indexed.
- **The leak test is the gate:** a synthetic case corpus with planted PII, ingested end to end, must leave zero
  planted values in `kb_chunks`, logs, audit rows or retrieval results.
- Citations to a case show "Resolved case (product, month)", never a case number.

### P7. Retrieval and triage evaluation (M)

- A golden set of fault statements, each with the expected articles or cases and the expected verdict class.
- Metrics: retrieval recall@k, MRR, citation validity rate, the human-review fallback rate, and the
  prompt-injection pass rate (P4's corpus).
- CI runs the eval with the `example-local` model and the fake embedder (deterministic). A nightly or manual job
  can run it against a real model.

### P8. Wizard hardening (S) — F5, F6, D6

- Rate limiting (`AddRateLimiter`) on Start and every POST, per IP and per request id.
- Capability expiry: an unconfirmed request older than N days refuses further steps.
- Customer lookup returns the same response shape whether the customer exists or not.
- `FakePaymentGateway` refuses to register outside Development unless a flag is set explicitly. Startup fails
  in Production with no real gateway.
- Security headers (CSP, nosniff, referrer, frame-ancestors). `/health` shows details only to authenticated
  operators.

**Status: shipped.** Commits `6577f78`, `12969a0`.
- Rate limiting on Start and every anonymous POST, partitioned on the caller's address, with `TrustedProxyTests`
  covering the proxy rule and `WebWizardTests` the 429. Live host tests.
- One chooser response for a known account or none, so the Start step is not an existence oracle. The known
  path is exercised by the wizard tests; the unknown-contact path is code, not an assertion.
- `FakePaymentGateway` refuses to register outside Development without the explicit opt-in flag
  (`AiProviderRegistrationTests`, a registration test rather than a host boot).
- The security headers are asserted on a real response in `WebWizardTests`.
- Every response that answers a capability sends `Cache-Control: no-store, no-cache, max-age=0` and
  `Pragma: no-cache`, set by a middleware that runs after routing. A capability is either a return id in
  the route or the chooser's `offer`/`identifiers`, plus *every* non-GET `/RmaWizard` request, because every step POST
  answers with a 302 whose `Location` is the next capability address — the rule is the verb, not an action list, after
  a list missed the chooser and then the Problems/Shipping/Payment/Confirm redirects: the chooser's offer key resolves server-side to the accounts the visitor was
  shown and the page hands back the account id to pick, so a cached chooser lets the next person on that
  machine start a return as the previous customer without knowing their address. Expiry bounds drafts, not caches: a page kept by a shared browser or an intermediary would
  hand the next person on that machine a working capability link for a return that has already been confirmed.
  Antiforgery already marks the pages that render a token, so the `max-age` and the 302 a step POSTs are what
  this adds — with the middleware disabled, `A_capability_step_cannot_be_stored_by_a_browser_or_a_cache` fails
  on the redirect carrying no `Cache-Control` at all. `The_chooser_cannot_be_stored_because_the_offer_key_is_a_capability`
  covers the chooser side: the 302 out of `Start`, the chooser page itself, and the 302 out of `ChooseCustomer`,
  each of which used to carry no `Cache-Control`. The capability-step test also submits the Problems step and
  asserts its 302 is marked, so narrowing the predicate back to an action list fails there. The tests also assert an ordinary page gets no directive, so
  the header is scoped rather than sprayed. `Start` as a GET is deliberately unmarked: nothing is pending yet.
- `/health` is public liveness only: an anonymous caller gets `{"status":"ok"}` and nothing else, the shape the
  RAG host's `/health` uses. The counts and the provider name are behind the operator key that host already
  uses (`Rag:Admin:ApiKey`, `X-Rag-Admin-Key`, fixed-time compare), and loopback when no key is configured.
  There is no account system on this host, so the alternative was inventing one for a status page.
  `Anonymous_health_reports_status_and_nothing_else` and `Operator_health_shows_counts_only_with_the_admin_key`
  pin both halves, including that a wrong key is treated as a stranger. The host refuses to start outside
  Development when the key is unset, using the RAG host's own rule (`RagAdminGate.EnsureStartupAllowed`):
  without a key the gate admits loopback, and behind a reverse proxy every caller is loopback, so the detail
  would be public and nothing in the response would look wrong. `A_production_host_refuses_to_start_without_the_admin_key`
  boots Production with no key and expects the throw; `A_production_host_with_a_key_starts_and_hides_the_detail`
  proves a keyed host still starts and still answers an anonymous caller with one word. Deleting the guard makes
  the first fail.
- Capability expiry is tested with the clock moved:
  `An_unconfirmed_draft_stops_working_when_the_capability_window_passes` creates a real draft through the
  wizard's own steps, checks the same URL still answers inside the window, then advances the host's
  `TimeProvider` 31 days and asserts the `Expired` page with 410 — on `Triage`, `Problems`, `Shipping` and
  `Confirm` — and that the request is still a draft, so expiring a link does not quietly change the return.
  A POST to `Triage`, carrying the anti-forgery token the still-valid page minted before the window closed,
  gets the same 410 with the `Expired` body, so the write path is exercised and not only the read one.
  Making `RefuseIfExpired` return null turns the test red (`Expected: Gone, Actual: OK`). The host's `TimeProvider`
  and the workflow's `IClock` are one instance behind both seams now, so the clock this test moves is the clock
  every step sees; a test that moved only one of them could not post a step that reads the other.

### P9. Real payment gateway (M) — D5

- A Stripe `IPaymentGateway`:
  - manual capture to match "authorised now, captured on inspection";
  - idempotency keys per request;
  - a webhook with signature verification;
  - test-mode keys only in development.
- A capture or void path driven from the operator view (P10).

### P10. Operator view (M)

- An authenticated back-office area covering:
  - the human-review queue (AI fallback, duplicate requests, fraud prefixes);
  - receive, inspect, then capture or void;
  - the knowledge-base review queue (P4's `reviewed_by`);
  - the quarantined CRM cases (P6).
- Every action is audited.

### P11. Production pack (S)

- `docker-compose.prod.yml`: Web, MCP (HTTP) and the RAG host behind a TLS proxy; PostgreSQL with pgvector, not
  published.
- Secrets come from the environment or a secret store. Configuration is validated at startup (no fake gateway,
  no default DB password, an AI base URL over HTTPS).
- Images pinned by digest, read-only root filesystem, healthchecks with a DB readiness probe separate from
  `/health`.

## 6. Order of work

| Step | Workstreams | Why this order |
|---|---|---|
| 1 | P0 | Everything after this needs CI with the live suites actually running. |
| 2 | P1, P3, P8 (small parts first) | These close the High findings on today's code before any new data is added. |
| 3 | P4, P2 | Secure ingestion and authenticated access before the corpus grows. |
| 4 | P5 | The vector store and chunker that P6 writes into. |
| 5 | P6, P7 | The CRM cases come in only once the redactor, the store and the eval all exist. |
| 6 | P9, P10, P11 | The parts a real deployment needs. |

## 7. Rules that hold throughout

- **The AI stays advisory.** Nothing in this plan lets a model, a retrieved article or a CRM case decide
  eligibility or money.
- **Synthetic data only** for every PII test and fixture. No real customer or CRM record is committed, logged or
  used in a test.
- **Commits:** author `Bob Huang <bob@ibegroup.com>`, no co-author or AI attribution lines (the commit-msg hook
  enforces this). Push only on the owner's go.
- **Tests:** each change adds tests. The suite stays green with 0 skipped in CI.

## 8. Definition of done

- Every High and Medium finding in §2 is closed, with a test that fails if it regresses.
- An authenticated remote agent can use the MCP server and only ever sees masked data, with every call audited.
- Troubleshooting answers draw on reviewed articles and redacted resolved CRM cases through hybrid vector
  retrieval, with the leak test at zero and the eval gated in CI.
- The app refuses to start in Production with a fake gateway, default secrets or an unauthenticated operator
  surface.
