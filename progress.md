# AIEnabledRMA — progress against plan.md

Monitor for [plan.md](plan.md). One row per workstream; the log below records what landed and what proved it.

## How the evidence was produced

Local runs use a throwaway PostgreSQL container, because `CREATE DATABASE` on the long-lived
`aienabledrma-postgres` container takes about two minutes:

```powershell
docker run -d --name aienabledrma-testpg -p 127.0.0.1:55432:5432 -e POSTGRES_USER=rmauser -e POSTGRES_PASSWORD=testonly -e POSTGRES_DB=rmadev --tmpfs /var/lib/postgresql/data:rw,size=2g pgvector/pgvector:pg16 -c fsync=off -c full_page_writes=off -c max_connections=300
$env:RMA_TEST_CONNECTION='Host=localhost;Port=55432;Database=postgres;Username=rmauser;Password=testonly'
$env:RMA_REQUIRE_LIVE_DB='1'
dotnet test tests\AIEnabledRma.Tests\AIEnabledRma.Tests.csproj
```

## Status

Since the merge of `origin/main` on 2026-11-20, P0–P4, P7 and P8 are the owner's implementations (pushed to
`origin/main` on 2026-11-19); ours for those workstreams were removed in the merge. P5, P6, P9, P10, P11 and D4
are this branch's work, re-seated on the owner's code. plan.md section 5 carries the owner's status lines and a
"Merged 2026-11-20" note under each of ours.

| Step | Workstream | Status | Notes |
|---|---|---|---|
| 1 | P0 Baseline and CI | Done (owner) | Owner's CI, lock files, SDK pin and the three gates (`scripts/verify-all.sh`). The CI service and the suite gate now use `pgvector/pgvector:pg16`, which P5's suites need. |
| 2 | P1 MCP data surface | Done (owner) | Owner's masking, audit and PostgreSQL-counted budgets. |
| 2 | P3 Redact before model calls | Done (owner) | Owner's `AIEnabledRma.Privacy` redactor; P6 now uses it. |
| 2 | P8 Wizard hardening | Done (owner) | Owner's expiry, rate limits, headers, fake-gateway rule and operator key. |
| 3 | P4 Secure KB ingestion | Done (owner) | Owner's corpus manifest (`REVIEWED.sha256`) and RAG admin gate. |
| 3 | P2 Remote MCP + OAuth | Done (owner) | Owner's Keycloak realm, auth gate and transport. |
| 4 | P5 Vector store + chunking | Done (ours, merged) | Embedding model per D4. The owner's lexical retriever stays the authority for articles in the hybrid ranking. |
| 5 | P6 CRM case ingestion | Done (ours, merged); real import waits on the owner's tenant (D1) | Uses the owner's redactor. |
| 5 | P7 Retrieval/triage eval | Done (owner) | Owner's `eval/retrieval-golden.json` and measurement history; our evaluation test and `eval.yml` were removed in favour of it. |
| 6 | P9 Real payment gateway | Done (ours, merged); real payments wait on the owner's Stripe account (D5) | Fake gateway and its rule unchanged unless `Payments:Provider=stripe`. |
| 6 | P10 Operator view | Done (ours, merged) | Review queue now also takes the owner's re-run policy decisions that send a return to a person. |
| 6 | P11 Production pack | Done (ours, merged) | Startup checks narrowed to what the owner's Production rules do not already decide. |

## Log

### 2026-11-19

- **Re-check of plan.md against `0fa065c`.** Build failed on the .NET 11 SDK; with a database 9 live tests
  failed (CREATE DATABASE timeout), and after that 2 wizard tests were stale. plan.md updated with the details.
- **P0 landed.** SDK 11 build fix (`EnableHotReloadInRuntimeConfigDevFile=false` on the web project);
  `LiveDatabase` helper with a 600 s DDL timeout and `RMA_REQUIRE_LIVE_DB=1` turning skips into failures;
  test host pinned to 2026-09-29 so the demo serials keep their designed outcomes; chooser test asserts
  masking; NuGet lock files for all 8 projects; `.github/workflows/ci.yml` (pgvector/pgvector:pg16 service,
  locked restore, vulnerable-package gate, fail on any skipped test, actions pinned by SHA,
  `contents: read`); compose PostgreSQL bound to 127.0.0.1 (F8); README no longer claims a payments
  diagnostics endpoint (F7, part). Evidence: `dotnet test` 199 passed, 0 skipped, 0 failed with
  `RMA_REQUIRE_LIVE_DB=1`; `dotnet list package --vulnerable --include-transitive` clean for all projects.
- **P1 part 1 landed.** `CustomerMasking` in Domain (the wizard now delegates to it), `McpToolCall` audit
  entity, `AddMcpToolCalls` migration, `EfToolAuditSink` (not yet registered in the MCP host). Evidence:
  solution builds with 0 errors. Work stopped here because the session's usage limit was reached.
- **P1 finished.** `find_customer` and the address tools return masked data only (no street lines, no
  external id); email/phone search is exact-only; name search needs 3+ characters. `ToolCallGuard`, wired as
  an MCP call-tool filter in `McpServerSetup`, refuses `reveal_customer_contact` without an authenticated
  `rma:support` caller, enforces calls-per-minute and customer-lookups-per-session budgets, and writes one
  `mcp_tool_calls` row per call with argument hash prefixes only. Evidence: 20 new tests in
  `McpSecurityTests` (projection, enumeration, audit, budgets, caller); full suite 219 passed, 0 skipped;
  stdio smoke against a throwaway database wrote two audit rows (`find_customer` Ok,
  `reveal_customer_contact` RefusedPermission) with hashes, not values.
- **P3 landed.** `PiiRedactor`/`RedactionSession` (email, Luhn card, phone, street address, order/ticket
  numbers, serial-like ids, self-introduced and signed-off names, known customer names; device serial/SKU/
  model protected; stable `[EMAIL_1]` placeholders). `TriagePipeline` redacts the statement and every turn
  before retrieval and the model call, logs counts only, and discards a reply containing a raw identifier
  (`pii-in-model-output`, human review). The wizard passes the matched customer's names. Evidence:
  `PiiRedactionTests` (31 cases incl. a 7-statement planted corpus: zero planted values in model requests
  or retrieval queries; serial still present).
- **P8 landed.** Wizard POSTs rate limited per client and per request id; unconfirmed request links expire
  after 14 days (410 via `CapabilityExpiryFilter`); a typed email/phone never reaches the chooser, and the full
  customer name is no longer put in TempData; CSP and other headers on every response; `FakePaymentGateway`
  only in Development/Testing or with `Payments:AllowFakeGateway`, otherwise startup fails; `/health` is bare,
  `/health/ready` checks the DB, `/health/details` needs `X-Operator-Key`. Also fixed a regression from the
  P3 commit that the full suite caught here: the wizard's customer lookup tracked the row and collided with
  the workflow's load (now `AsNoTracking`). Evidence: new tests in `WebWizardTests` (health split, headers,
  partial-email probe, rate limit 429, expiry 410) and `WebStartupTests` (Production refuses to start, expiry
  rule); full suite 258 passed, 0 skipped.
- **P4 landed.** RAG host: `X-Api-Key` admin/reader keys (`Rag:Security`); anonymous `/diagnostics` shows
  counts only; `/retrieve` needs a key outside Development; `/rag/reload` admin only with an `AUDIT` log line
  per attempt; no keys configured outside Development grants nothing. Loader: size/count/front-matter limits,
  strict UTF-8, no symlinked files or directories, provenance (`reviewed_by`, `reviewed_at`, `content_hash`
  checked against the body), review required outside Development, instruction-like text refused, HTML/link
  targets/URLs stripped and angle brackets neutralised. `TriagePipeline` also neutralises delimiters in
  retrieved text. All 19 shipped articles stamped (`kb-maintainers`, 2026-11-20, hash). Evidence:
  `KnowledgeIngestionSecurityTests` + `RagHostSecurityTests` (20 tests, including the shipped corpus loading
  with review required and a Production RAG host); full suite 278 passed, 1 skipped (the symlink test: this
  Windows machine cannot create symlinks without Developer Mode; it runs on Linux CI).
- **P2 landed.** `--http` / `Mcp:Transport=http` runs the MCP server over stateless streamable HTTP
  (`McpHttpHost`): Origin allow-list, JWT bearer (issuer, audience `aienabledrma-mcp`, lifetime), policy
  requiring scope `rma:read` and an allowed client id, `/.well-known/oauth-protected-resource`; the caller's
  client id flows into `ToolCallGuard` (audit, budgets, support role). Startup refuses an empty client list or
  a non-https authority outside Development. MCP container now on the aspnet runtime image; `docs/mcp.md`
  runbook. Evidence: `McpHttpTransportTests` (9: no token 401 + metadata pointer, metadata content, wrong
  audience 401, wrong issuer 401, missing scope 403, unknown client 403, bad Origin 403, valid token lists and
  calls tools with masked output and an audit row under the client id, support-role reveal vs reader);
  full suite 287 passed, 1 skipped (symlink test, Windows only). Note: a stale VBCSCompiler cache produced
  bogus Razor errors in this session; `dotnet build-server shutdown` cleared it.
- **P5 landed.** `PgVectorKnowledgeStore` (kb schema created by `DbAdmin kb-ingest`, not EF, so plain
  PostgreSQL still works), `MarkdownChunker`, `IEmbeddingModel` with `HashingEmbeddingModel` (offline/CI) and
  `OpenAiCompatibleEmbeddingModel` (batched, retried), `HybridKnowledgeRetriever` (full-text + vector, RRF,
  scope filter in SQL, lexical fallback), `Rag:VectorStore:Enabled` switch, compose image `pgvector/pgvector:pg16`.
  Evidence: live `kb-ingest` on the throwaway container published 19 articles / 54 chunks, second run
  "unchanged"; `VectorStoreTests` (11: chunk boundaries, steps kept whole, overlap, merging, shipped corpus,
  embedder determinism, idempotent ingest with vector reuse, failed embedding keeps the old generation and
  retries, scope filtered in SQL, recall@3 hybrid 0.955 >= lexical 0.955 on the 22 answerable golden questions,
  fallback when the store is unreachable).
- **Note for the owner:** the next `docker compose up` recreates `aienabledrma-postgres` on the pgvector image
  (same PG 16, data directory kept). Not done from here: the running container was left alone.
- **P6 landed.** `SupportCase` (no customer/contact/owner/order fields), `ICaseSource` with `FileCaseSource`
  and `DataverseCaseSource` (`$select` of troubleshooting columns only, resolved incidents, incremental
  `modifiedon`), `CaseIngestionPipeline` (header/signature/quoted-reply stripping, greeted/signed/labelled names,
  `PiiRedactor`, fail-closed verification and quarantine, symptom/diagnosis/resolution chunks, opaque case keys,
  "Resolved case (product, month)" citations, incremental merge with a watermark), `DbAdmin kb-cases` (dry run
  by default, `--publish` needs `--reviewed-by`). Fixed on the way: `PiiRedactor.Detect` no longer reads the
  word after a removed placeholder as a name. Evidence: `CaseIngestionTests` + `CaseLeakLiveTests`; the leak
  gate ingests 6 synthetic cases with 30 planted values (4 published, 1 not eligible, 1 quarantined) and finds
  zero planted values in kb tables, logs, the report or retrieval results; live `kb-cases` dry run and publish
  on the throwaway database.
- **P7 landed, and finding F10 fixed.** Building the evaluation showed the scope guard refused every question
  in the running app (settings under a root `ScopeGuard` key the host never read, so no topic vocabulary).
  `Triage:ScopeGuard` now carries real vocabularies and deny phrases, and the host refuses to start with an
  empty vocabulary. `TriageEvaluationTests` computes recall@3, MRR, raw citation validity, human-review
  fallback rate, out-of-scope accuracy and prompt-injection pass rate over the 24-question golden set with the
  production configuration; CI gates on it and keeps `eval-report.json`; `eval.yml` runs it against a real
  model when enabled. Evidence (example-local model, lexical index): recall@3 1.000, MRR 0.955, citation
  validity 1.000 (84 citations), fallback rate 0.000, out-of-scope accuracy 1.000, injection pass rate 1.000
  (6 customer-side + 4 article-side).
- **P10 landed.** `review_items` and `operator_actions` tables (migration `AddOperations`); the workflow files
  review items for human-review starts (previously not persisted at all), low-confidence or fallback triage, and
  failed payment authorisations; `OperatorService` (receive, capture after inspection, void with reason,
  resolve review item; every attempt audited); `/Operator` area behind an `operator` policy (cookie session,
  OpenID Connect when configured, Development/Testing-only dev sign-in, refused at startup elsewhere); the
  link-expiry filter now applies to the wizard only. Evidence: new `WebWizardTests` (anonymous redirect to
  sign-in; receive + capture with a refused early capture, three audit rows, status InRepair, fake-gateway
  capture; a duplicate-serial start filed as a review item, shown on the dashboard and resolved) and
  `WebStartupTests` (dev sign-in refused in Production); full suite 309 passed, 1 skipped (symlink, Windows).
- **P9 landed.** `StripePaymentGateway` (PaymentIntents via REST: manual capture for repair fees, idempotency
  keys per RMA and operation, 3-D Secure cancelled and sent to human review, zero-decimal currencies),
  `StripeWebhookSignature` and `/payments/stripe/webhook` (HMAC + replay window; failed/cancelled intents become
  review items), `Payments:Provider` switch with test-keys-only outside Production. Capture/void are driven
  from the P10 operator view. Evidence: `StripePaymentGatewayTests` (11: form fields and idempotency key, repeat
  authorisation reuses the key, 3-D Secure cancel, capture/void endpoints, error message, minor units,
  webhook signature valid/tampered/wrong secret/stale/missing, live key refused in Development, forged webhook
  400 on a Stripe-configured host).
- **P11 landed.** `docker-compose.prod.yml` (Caddy TLS front door from `deploy/Caddyfile`, web, MCP over HTTP,
  RAG host, pgvector PostgreSQL unpublished; required values via `:?`; read-only root filesystems, tmpfs,
  `no-new-privileges`, all capabilities dropped), `deploy/prod.env.example`, Dockerfiles with digest-pinned
  base images and `--locked-mode` restore (new RAG Dockerfile), `ProductionConfiguration` startup checks
  (default/empty DB password, http model or embedding endpoint, fake gateway opt-in, missing operator OIDC; RAG
  keys), and the Development-only `Demo:Today` clock for the drifting demo serials. Evidence:
  `ProductionConfigurationTests` (7) plus the stricter RAG test; `docker compose -f docker-compose.prod.yml
  --env-file deploy/prod.env.example config` valid; all three images build; the web image runs read-only with
  dropped capabilities and answers `/health`, `/health/ready` and the wizard; the same image in Production with
  development defaults refuses to start and lists the problems. Full suite 328 passed, 1 skipped.

## Final state (2026-11-20)

All twelve workstreams are done and all six owner decisions are recorded as decided (plan.md section 4). Two
parts wait only on the owner supplying accounts:

| Blocked part | Needs |
|---|---|
| P6 real CRM import (D1, decided: Dataverse) | The tenant URL, app registration (client credentials) and the field list |
| P9 taking real payments (D5, decided: Stripe test mode first) | The Stripe account and keys, webhook registration, Stripe Elements card form |

The identity provider (D2) and the embedding model (D4) are configuration only. The real-model evaluation
workflow (`eval.yml`) runs once `RMA_EVAL_*` secrets and `RMA_EVAL_ENABLED` are set.

Everything else runs locally on synthetic data. Last full run: `dotnet test` 328 passed, 1 skipped (the
symlink test, which needs Windows Developer Mode and runs on Linux CI), 0 failed, with
`RMA_REQUIRE_LIVE_DB=1` against the throwaway pgvector container. CI has not run yet: nothing has been pushed.

Notes for the owner:
- The next `docker compose up` recreates `aienabledrma-postgres` on the pgvector image (same PG 16, data kept).
- `aienabledrma-testpg` (port 55432, tmpfs, test data only) was created by this work and can be removed with
  `docker rm -f aienabledrma-testpg`.
- On this machine the shared C# compiler server twice served stale Razor output (bogus errors in
  `Confirm.cshtml`); `dotnet build-server shutdown` clears it.

### 2026-11-20 (owner decisions)

- **Decisions D1-D6 recorded as decided** in plan.md section 4: D1, D2, D3, D5 and D6 accepted as built; D4
  decided as a local Ollama model by default, interchangeable by configuration.
- **D4 landed.** `Embeddings:Provider` = `ollama` (default, native `/api/embed`, `nomic-embed-text`/768 unless
  configured), `openai-compatible` or `hashing`; model, base URL, key and dimension in configuration; wrong-size
  vectors refused. The store records `embedding_model` on every chunk as well as in `kb_meta`, and refuses
  another model or dimension on schema check, ingest and query (`EmbeddingModelMismatchException`); the hybrid
  retriever falls back to the lexical index and logs an error until `kb-ingest --rebuild`. Web appsettings and
  the production compose default to Ollama (an internal, digest-pinned `ollama` service). README explains how to
  switch. Evidence: `EmbeddingProviderTests` (Ollama default, each provider selected by configuration, invalid
  configuration refused, `/api/embed` batching, wrong-size refusal) and `EmbeddingMismatchLiveTests` (another
  dimension refused at schema and ingest, another model refused at query, model recorded per chunk, lexical
  fallback, rebuild re-embeds); prod compose config valid; full suite 334 passed, 1 skipped (symlink, Windows), 0 failed.

### 2026-11-20 (merge of origin/main)

- **Merged `origin/main` into local `main`** (a merge, not a rebase, so every SHA cited above stays valid). The
  owner had pushed 97 commits on 2026-11-14..19 implementing P0–P4, P7 and P8 differently (the Privacy project,
  the Keycloak realm and auth gate, PostgreSQL-counted MCP budgets, the corpus manifest `REVIEWED.sha256`, the
  golden set in `eval/`, the SDK pin in `global.json`, and `scripts/verify-all.sh`). Where both sides solved the
  same thing, the owner's version was kept: every conflicted code file was taken from `origin/main`, and our
  duplicates were removed (our MCP guard and masking, `Domain/Privacy`, `KnowledgeIngestion`/`RagApiKeys`, our
  wizard filters and headers, our P0 test helper, our evaluation test, `eval.yml`, our MCP audit migration and our
  knowledge-base front-matter stamps). Our P5, P6, P9, P10, P11 and D4 were re-seated on the owner's code:
  - P6 uses the owner's `IPiiRedactor` (the Rag project now references Privacy);
  - P5's hybrid retriever wraps the owner's synonym-expanding retriever, which stays the authority for article
    ranking; the vector store contributes redacted cases and chunk snippets (recall@3 0.925 = lexical 0.925 on
    the owner's golden set with the hashing embedder; an earlier straight fusion scored 0.725–0.825 and was
    replaced);
  - P10's `review_items`/`operator_actions` are a new `AddOperations` migration after the owner's migrations;
    the review queue also takes the owner's re-run policy decisions that send a return to a person, and its
    test uses a human-review region because the owner moved the duplicate check to confirmation;
  - P9's Stripe registration sits beside the owner's `AddFakePaymentGateway` (`AddPaymentGateway`);
  - P11's `ProductionConfiguration` was narrowed to a development database password and plain-http model or
    embedding endpoints, because the owner's Production boot tests deliberately start on the placeholder
    password, the fake-gateway opt-in and no operator OIDC; the prod compose now uses the owner's `Mcp:Http:*` and
    `Rag:Admin:ApiKey` settings; the RAG Dockerfile copies `global.json` and the Privacy project.
  - CI, the suite gate and the dev compose use `pgvector/pgvector:pg16` (P5's suites need the extension).
- **Verification**, with the owner's gates: `scripts/verify-all.sh suite` (pinned SDK container, `restore
  --locked-mode`, `build -warnaserror`, throwaway pgvector PostgreSQL): **458 passed, 0 skipped, 0 failed**;
  `verify-auth.sh`: all checks passed, run from a scratch copy under its own compose project and ports so the
  gate's `docker compose up` could not recreate the owner's `aienabledrma-postgres` container (its containers were
  removed afterwards); `verify-all.sh images`: all checks passed; the RAG image builds with the corpus and its
  manifest; the production compose validates.
- A local backup of the pre-merge branch is kept as `backup/pre-merge-2026-11-20` (local only).
