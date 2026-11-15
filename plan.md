# AIEnabledRMA — plan to finish the project

Written 2026-11-14 from a code review of `main` (`e96156b`).

**Baseline:** `dotnet test` gives 190 passed and 9 skipped. The 9 live-PostgreSQL tests skip when
`RMA_TEST_CONNECTION` is unset.

**Updated 2026-11-15** with what has shipped since the review. The suite is at 320 passed, 0 skipped with
`RMA_TEST_CONNECTION` set; the workstream status lines below say what each item is proven by, and an item is
only marked done where a test would fail if the behaviour regressed.

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
- Masking through `CustomerProjection`, shared with the wizard; `externalCustomerId` is gone from output and
  `find_customer` refuses short terms and matches email or phone exactly. Pinned by `McpCustomerToolTests`,
  including the enumeration case (a partial email returns nothing).
- The audit sink and its `mcp_tool_calls` migration, with `EfToolAuditSinkTests` running both against a real
  PostgreSQL — the live level, not a stub.
- Budgets enforced in `ToolCallGuard` at the MCP `tools/call` filter rather than inside each of the four
  tools, so a tool added later cannot skip them. `McpToolGuardTests` drives the real server over the loopback
  transport. These are in-process tests, not a networked deployment.
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

**Status: shipped except the audit trail, the review itself, and end-to-end proof for titles.** Commits
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
- **Outstanding:**
  - `POST /rag/reload` is gated but **not audited**. A state change that swaps the corpus leaves no record of
    who asked for it.
  - **The provenance is synthetic.** `reviewed_by: docs-team` and `reviewed_at: 2026-09-29` were written by
    the implementer, and `REVIEWED.sha256` was generated from the same files, so it proves the articles have
    not changed since that commit — not that anyone reviewed them. A deployment must re-review the corpus
    and regenerate the manifest.
  - **Title sanitisation is loader-level only.** `f072698` sanitises the title (and drops neutralised
    keywords) and `KnowledgeBaseHardeningTests` asserts the title is neutralised, but nothing exercises a
    title-borne instruction end to end into a prompt. Implemented and unit-tested; not proven.

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

**Status: shipped except `/health` and one untested rule.** Commits `6577f78`, `12969a0`.
- Rate limiting on Start and every anonymous POST, partitioned on the caller's address, with `TrustedProxyTests`
  covering the proxy rule and `WebWizardTests` the 429. Live host tests.
- One chooser response for a known account or none, so the Start step is not an existence oracle. The known
  path is exercised by the wizard tests; the unknown-contact path is code, not an assertion.
- `FakePaymentGateway` refuses to register outside Development without the explicit opt-in flag
  (`AiProviderRegistrationTests`, a registration test rather than a host boot).
- The security headers are asserted on a real response in `WebWizardTests`.
- **Outstanding:**
  - **`/health`** on the Web host still returns `articleCount`, `loadErrorCount` and the AI provider name to
    anonymous callers. The filesystem path F7 named is gone, but there is no operator authentication, and the
    RAG host's status-only `/health` is the shape this one should follow.
  - **Capability expiry is untested.** `RefuseIfExpired` is wired into every route-id step, but no test
    advances the clock past the window and asserts the refusal, so nothing fails if it stops working.

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
