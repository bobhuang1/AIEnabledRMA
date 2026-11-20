# AIEnabledRMA — plan to finish the project

Written 2026-11-14 from a code review of `main` (`e96156b`). Re-checked 2026-11-19 against `0fa065c`;
progress against this plan is tracked in [progress.md](progress.md).

**Baseline (2026-11-14):** `dotnet test` gave 190 passed and 9 skipped. The 9 live-PostgreSQL tests skip when
`RMA_TEST_CONNECTION` is unset.

**Re-check (2026-11-19), three things the plan did not know:**
- The build failed on the .NET 11 SDK (the only current SDK on the dev machine besides 9): the SDK adds a
  Blazor hot-reload JS module to Debug web builds and this MVC app has no endpoint for it. Fixed in P0.
- With a database, 9 live tests failed rather than skipped: `CREATE DATABASE` on the shared Docker
  PostgreSQL takes about two minutes (cluster checkpoint on a slow volume), past Npgsql's 30 s timeout. Fixed
  in P0 with a DDL timeout; local runs use a throwaway tmpfs container (see progress.md).
- Two wizard tests had gone stale: one still expected unmasked emails on the chooser (masking landed in
  `e96156b`), and the demo serials are fixed around 2026-03-01, so on the wall clock the paid-repair serial
  has aged out of the 730-day window. The test host now runs on a pinned date. The live demo has the same
  drift: see "Demo data ages" under §5 P11.

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
  - Two providers ship: an OpenAI-compatible client and an offline `example-local` model.
- **Knowledge base:** 15 markdown articles under `knowledge-base/`, loaded into an in-memory BM25-style index
  (`AIEnabledRma.Rag`), plus a small RAG host with `/diagnostics`, `/retrieve` and `/rag/reload`.
- **MCP server** (`src/AIEnabledRma.Mcp`): a stdio server with 9 read-only tools: `find_customer`,
  `get_customer_addresses`, `get_customer_returns`, `resolve_shipping_address`, `find_device`, `search_devices`,
  `get_open_returns_for_device`, `check_warranty_coverage` and `get_fee_quote`.
- **Payments:** only `FakePaymentGateway` exists (accept-all, in memory).
- **Ops:** Dockerfiles for Web and MCP (non-root) and `tools/DbAdmin`.

**Missing:**
- CI of any kind (there is no `.github/`).
- Authentication anywhere.
- An operator or back-office view.
- Any CRM or support-case data.
- Vector search or chunking: articles are indexed whole.
- PII handling on the AI path.
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
| F7 | Low | **Web `/health` shows the knowledge-base filesystem path and the AI provider name** to anonymous callers. It injects `RmaDbContext` and never uses it. The README describes a payments diagnostics endpoint that does not exist. | `Web/Program.cs`, `README.md` |
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
| D2 | How agents will reach the MCP server: local stdio only, or remote HTTP with an identity provider (which one?) | P2 | Both: stdio for a developer, HTTP + OAuth (Keycloak or Entra ID) for anything shared |
| D3 | Whether an agent may see unmasked customer contact data at all | P1 | No. Masked by default, and an unmask tool only for an authenticated support role, audited |
| D4 | The embedding model: a local model (BGE-M3 / nomic via Ollama) or a hosted API | P5 | Local, so no case text leaves the company |
| D5 | The real payment provider | P9 | Stripe, test mode first |
| D6 | Whether the wizard stays anonymous or gets customer sign-in | P8 | Stay anonymous; add expiry and rate limits |

## 5. Workstreams

Each one is a branch and a PR. Every PR runs the build plus tests. Sizes: S is under a day, M is 1–3 days, L is
about a week.

### P0. Baseline and CI (S) — do first

- Add `.github/workflows/ci.yml`:
  - restore, build, and test with a `postgres:16` service container so the live suites run;
  - **fail if any test is skipped in CI**;
  - pin actions by SHA, with least-privilege `permissions`.
- Enable NuGet lock files (`RestorePackagesWithLockFile`) and run `dotnet list package --vulnerable` in CI.
- Bind compose PostgreSQL to `127.0.0.1` (F8). Make the README match the code (F7).
- **Done when** CI is green with 199 run and 0 skipped.

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

### P2. Remote MCP transport with OAuth (M) — D2

- Add a streamable HTTP transport (`ModelContextProtocol.AspNetCore`) beside stdio.
- Protected-resource metadata (RFC 9728), JWT validation (issuer, audience `aienabledrma-mcp`, required scope
  `rma:read`), an Origin check and an allow-list of client ids.
- Caller identity flows into P1's audit and budgets. Stdio keeps working for local development, as a named
  local caller.
- Tests: missing token, wrong audience, missing scope and disallowed Origin each give 401/403. A valid token
  reaches the tools.
- Add a `docs/mcp.md` runbook.

### P3. Redact before every model call (M) — F3

> Status 2026-11-19: done with pattern recognisers, in `AIEnabledRma.Domain.Privacy` rather than a new
> project (every host already references Domain). The local-model NER pass waits on D4.

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
  - hybrid retrieval against a golden question set (recall@3 not worse than today's lexical index on the 15
    articles);
  - live pgvector tests in CI (P0's service container).

### P6. CRM support-case ingestion with PII removed (L) — D1, after P3 and P5

> Status 2026-11-20: pipeline, file source, Dataverse source (tested against a fake Web API) and the leak
> gate are done. **Blocked on D1** for a real import: tenant URL, app registration (client credentials) and
> the agreed field list. The case summary uses the redacted fields as written; a local-model summary waits
> on D4. Deviation from the text below: the pipeline reads no name fields at all, so names in free text are
> caught by patterns (greetings, sign-offs, "Agent:" labels) and the fail-closed verification pass.

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
- **Demo data ages** (added 2026-11-19): the seeder anchors every warranty on 2026-03-01, so the README's demo
  serials change outcome as the wall clock moves (`AX2-0001-0005` is outside the window from about
  2026-11-01). Add a `Demo:Today` override (development only) that the wizard and MCP read through
  `IClock`/`TimeProvider`, or move the anchor relative to the seed date.

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
