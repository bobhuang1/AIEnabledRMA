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

| Step | Workstream | Status | Notes |
|---|---|---|---|
| 1 | P0 Baseline and CI | Done (local) | CI workflow written; it has not run on GitHub yet because nothing is pushed. |
| 2 | P1 MCP data surface | Done | Masked projection, exact-only contact search, `ToolCallGuard` (permission, budgets, audit), `reveal_customer_contact` (support role only). Budgets are in-process memory. |
| 2 | P3 Redact before model calls | Done (pattern recognisers) | `PiiRedactor` in `Domain/Privacy` (not a separate project). The optional local-model NER pass is not built: it needs a local model choice (D4). |
| 2 | P8 Wizard hardening | Done | Rate limits, link expiry, no contact-detail probing, security headers, fake gateway refused outside Development/Testing, split health endpoints. `/health/details` uses a shared operator key until P10 brings sign-in. |
| 3 | P4 Secure KB ingestion | Done | API keys (not JWT) on the RAG host; JWT comes with P2's identity provider. Symlink test skips on Windows without Developer Mode, runs in Linux CI. |
| 3 | P2 Remote MCP + OAuth | Done (provider-neutral) | Streamable HTTP + JWT + RFC 9728 metadata; which identity provider (D2) is still the owner's call and is pure configuration. |
| 4 | P5 Vector store + chunking | Done (hashing embedder) | Real embedding model is D4 (config only). Token sizes are estimated (4/3 per word), not tokenizer-exact. |
| 5 | P6 CRM case ingestion | Done except real import: **blocked on D1** | Tenant, app registration and field list are the owner's; everything else runs on synthetic data. |
| 5 | P7 Retrieval/triage eval | Not started | |
| 6 | P9 Real payment gateway | Not started | |
| 6 | P10 Operator view | Not started | |
| 6 | P11 Production pack | Not started | |

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
