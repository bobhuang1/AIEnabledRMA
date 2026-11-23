# AIEnabledRMA — progress against plan.md

Monitor for [plan.md](plan.md). One row per workstream; the log below records what landed and what proved it.

## How the evidence was produced

Every number below comes from the gate scripts, not from a hand-run `dotnet test`. They are the same
commands CI runs, each with the database or containers it needs started as part of the step:

```bash
./scripts/verify-all.sh suite    # restore --locked-mode, build -warnaserror, EF model check, the suite
                                 # against a throwaway pgvector PostgreSQL, skip guard, audit
./scripts/verify-all.sh auth     # realm import, client-credentials and PKCE flows, refusals
./scripts/verify-all.sh images   # the three runtime images, both corpus boots, the production web boot
```

`suite` runs in the SDK container pinned by `global.json` and talks to a PostgreSQL on its own Docker
network; the working tree is copied in without `bin/`, `obj/` or `data/`, so a Windows artefact cannot
shadow the Linux build. `images` builds the Web, MCP and RAG images, checks the commit label and the
corpus content of each, boots both hosts that read the corpus in Production, and starts the production
compose web, mcp and rag services from `deploy/prod.env.example` (with synthetic non-placeholder secrets,
because the hosts now refuse the example's placeholders). `auth` runs from a scratch compose project on its
own ports.

CI runs the same three gates as separate jobs (`build-and-test`, `auth-flows`, `runtime-images`) and,
after the image gate, resolves the production pack:
`docker compose -f docker-compose.prod.yml --env-file deploy/prod.env.example config --quiet`.

## Status

Since the merge of `origin/main` on 2026-11-20, P0–P4, P7 and P8 are the owner's implementations (pushed to
`origin/main` on 2026-11-19); ours for those workstreams were removed in the merge. P5, P6, P9, P10, P11 and D4
are this branch's work, re-seated on the owner's code. plan.md section 5 carries the owner's status lines and a
"Merged 2026-11-20" note under each of ours.

| Step | Workstream | Status | Notes |
|---|---|---|---|
| 1 | P0 Baseline and CI | Done (owner) | Owner's CI, lock files, SDK pin and the three gates (`scripts/verify-all.sh`). CI runs them as `build-and-test`, `auth-flows` and `runtime-images`, then resolves the production compose file. The CI service and the suite gate both use `pgvector/pgvector:pg16`, which P5's suites need. |
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
| 6 | P11 Production pack | Done (ours, merged) | Startup checks cover every host that opens the database, refuse empty or placeholder passwords and secrets, and refuse the bundled example model; the Web host also requires the operator identity provider. The image gate builds all three images, boots Web and RAG on the corpus, starts the production web, mcp and rag services from the example env, and the compose file is resolved in CI. The realm carries the operator client and role the back office needs. |

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

## Final state (updated 2026-11-21)

All twelve workstreams are done and all six owner decisions are recorded as decided (plan.md section 4). Two
parts wait only on the owner supplying accounts:

| Blocked part | Needs |
|---|---|
| P6 real CRM import (D1, decided: Dataverse) | The tenant URL, app registration (client credentials) and the field list |
| P9 taking real payments (D5, decided: Stripe test mode first) | The Stripe account and keys, webhook registration, Stripe Elements card form |

The identity provider (D2) and the embedding model (D4) are configuration only. The real-model evaluation
workflow (`eval.yml`) runs once `RMA_EVAL_*` secrets and `RMA_EVAL_ENABLED` are set.

Everything else runs locally on synthetic data, and the merged tree is green in CI. Every push to `main`
since the merge has run and passed all three jobs; the most recent is run `44932712009` at `6b2ee20`
(2026-11-21), after `44931366309` (`f1a7d16`), `44929693971` (`0907fab`), `44926917839` (`7129ad4`),
`44923753946` (`6aba449`) and `44921464800` (`537e4db`). The last local run of `./scripts/verify-all.sh
suite` at `6b2ee20` was **478 passed, 0 skipped, 0 failed** (437 s), with the EF model check clean and the
audit clean for all nine projects. The last local run of `./scripts/verify-all.sh images` (at `42b5708`) was
**16 ok, 0 fail**, including both corpus boots and the production compose web boot; the Dockerfile and
smoke-environment changes since then ran in CI's `runtime-images` job on those pushes.

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

### 2026-11-21 (hardening after the merge)

All of the following is pushed to `origin/main`, and each commit has a green CI run.

- **`7b3fe85` — the retrieval measurement names its corpus.** `RetrievalGoldenSetTests` now asserts the
  latest measurement's `corpusFingerprint` and `reviewedCorpusHash` match the corpus being scored before any
  score is compared. Mutation-checked: editing an article and changing only the manifest both fail it.
- **`83d5021` — one Production database-password rule for every host.** `ProductionDatabaseCredentials`
  (the known development/CI passwords and the two problem sentences) and `ProductionConfigurationGuard`
  (the refusal) live in Domain, which the Web, MCP and RAG hosts already reference; the RAG host parses the
  connection string with Npgsql directly, so the rule is shared without adding a Data/EF reference. The RAG
  host applies it only when a database is configured, because the production compose runs the lexical-only
  RAG host without one. README records the behaviour.
- **`3532859` — the RAG image is booted, not just counted.** `boot_smoke` is parameterised by host, image,
  entry point and container; the RAG host boots in Production on the shipped corpus (19 articles, 0 problems)
  and the removed-article case must exit non-zero and name `REVIEWED.sha256`. `SMOKE_ASSEMBLY` was fixed so
  the drift case runs the right entry point inside `sh -c`.
- **`42b5708` — the production pack is started, not only resolved.** `prod_boot_smoke` starts the prod
  compose `web` service from `deploy/prod.env.example` under its own project name (`rma-image-smoke-prod`),
  waits for the host's own `19 article(s)` / `0 problem(s)` lines, checks it is still running, and tears the
  stack down with `down -v --rmi local`.
- **`537e4db` — every image restores in locked mode.** Both older Dockerfiles copy every referenced
  project's `packages.lock.json` and restore with `--locked-mode`; `PublishLayoutTests` asserts the COPY list
  covers each lock file for Web, MCP and Rag. Mutation-checked by deleting a lock-file `COPY` line.
- **`6aba449` — the tool server refuses at registration.** The Production check moved from a hosted service
  into `AddRmaMcpServices(configuration, environment)`, which both transports call, so an unsafe host cannot
  be constructed and no transport can skip the check by never starting the host.
- **`7129ad4` — the local suite no longer stalls at the publish tests.** The `dotnet publish` child left
  MSBuild nodes (`nodeReuse:true`) holding its redirected stdout/stderr pipes, so `ReadToEndAsync` waited for
  the nodes' fifteen-minute idle timeout; reproduced by the test host's pipe fds. The publish now disables node
  reuse, the CLI MSBuild server and the shared compiler, and the whole wait has a ten-minute deadline.
- **`0907fab` — the tool server's acceptance case covers Development.** The registration test refuses in
  Production and accepts the same password in Development and Testing; making the check unconditional fails
  the Development case.
- **`f1a7d16` — the dev compose can serve the default embedding model.** `ollama` is a service behind the
  `ollama` profile on `127.0.0.1:11434` (the default `Embeddings:BaseUrl`) with a kept volume, and the
  one-time `nomic-embed-text` pull is in the compose file and the README. An unreachable embedding model is
  now its own exception: the retriever logs a warning naming the model, that retrieval fell back to the
  lexical index, and both ways to fix it. Mutation-checked (profile removed; warning made unreachable).
- **`6b2ee20` — Production requires the operator identity provider.** The Web host passes
  `checkOperatorSignIn: true`, so a deployment without `OperatorAuth:Authority`/`ClientId` refuses to start
  instead of serving an `/Operator` sign-in that can never complete. The Production boot tests and the web
  image smoke supply synthetic OIDC settings so they still reach the guard they are about; the new refusal
  test fails when the flag is removed.
- **Production refuses placeholder and empty secrets.** `ProductionSecrets` (Domain) names the example env
  file's placeholders (`replace-me`, `replace-with-…`, `sk_live_replace-me`), and `ProductionDatabaseCredentials`
  now refuses empty and placeholder passwords as well as the six development defaults, so its long-standing
  "default, placeholder or empty" sentence is true. `ProductionConfiguration` applies the same rule to the
  operator client secret and both Stripe keys, and `RagAdminGate` refuses a placeholder admin key outside
  Development. Mutation-checked: restoring the empty exclusion fails
  `An_empty_or_placeholder_database_password_is_refused`.
- **Production refuses the bundled example model.** A blank or `example-local` `Ai:Provider` is a problem for
  every host that selects a provider. `docker-compose.prod.yml` requires `AI_PROVIDER` with no default and the
  example env file sets `openai-compatible` and an https base URL; the Production boot tests supply a synthetic
  compatible provider so each still reaches the guard it is about. Mutation-checked: removing the condition fails
  `The_bundled_example_model_is_refused`.
- **Operator sign-in is gated end to end.** The realm gains the `rma:operator` realm role, the confidential
  `rma-operator` client for the web host, and an `operator-roles` scope whose `oidc-usermodel-realm-role-mapper`
  flattens realm roles into the flat `roles` claim `OperatorAuthSetup.HasRole` reads (Keycloak's default
  `realm_access.roles` is nested, so a correct sign-in would otherwise be denied every page). `operator-demo`
  holds the role. `verify-auth.sh` asserts the imported shape (27 rows), mints an operator token and asserts the
  claim, and proves a user without the role does not get it.
- **Stripe webhook deliveries are recorded once.** New `webhook_events` table (`AddWebhookEvents` migration),
  `IWebhookEventLedger` implemented as one `INSERT … ON CONFLICT (event_id) DO NOTHING`, and a handler that
  refuses a delivery without an event id and acknowledges a repeat without filing a second review item.
  `WebhookDedupeTests` covers the three decisions against a recording ledger; `WebhookEventLedgerTests` proves the
  constraint against PostgreSQL. Mutation-checked: removing the guard files two review items for one delivery.
- **The image gate boots all three production services.** `prod_boot_smoke` starts web, mcp and rag from the
  pack (with synthetic non-placeholder secrets, because the hosts now refuse the example's), waits for each
  host's own line (the corpus count for web and rag, `Application started` for the tool server), and asserts each
  is still running. The compose file gives the tool server a startup log line, which its Warning default
  suppressed.
- **The RAG host applies the shared database rule.** It calls `ProductionConfiguration.DatabaseProblems` instead
  of carrying its own parse-and-refuse copy, so placeholder and malformed-connection handling cannot drift from
  the web and tool-server hosts; a database is still optional for a lexical-only deployment.
- **A triage says which retriever answered.** One `LogInformation` line after retrieval names the registered
  implementation and the article count. The retriever's own log only spoke when it fell back, so a lexical-only
  answer and a vector-backed one looked identical from the pipeline.
- **Docs match the tree.** README's CI section now describes what CI does (every push to `main`, three jobs, the
  pack resolution), the containers section lists all three Dockerfiles, the provider list matches the switch,
  the tests section states that the gates fail on a skip, and the operator section names the committed client and
  role. plan.md's P0 status no longer says no run has been observed, and P5 no longer says "not started".
- **Gate at `6b2ee20`:** `suite` 478 passed, 0 skipped, 0 failed (437 s), EF model check clean, audit clean
  for all nine projects; CI's `build-and-test`, `auth-flows` and `runtime-images` jobs all passed, and the
  production compose resolution step passed.
- **Gate after this batch** (CI run `44943468403` on `7324ba7`: all three jobs green): `suite` 497 passed, 0 skipped, 0 failed; EF model check clean ("No changes have
  been made to the model since the last migration"); vulnerability audit clean for all nine projects. `auth`:
  all checks passed, including the 28-row realm shape and the operator token (subject, `preferred_username`,
  flat `roles` claim, and no role for a user without it). The production pack resolves with the example env
  (`docker compose ... config --quiet`). `images`: all checks passed — the three images build, carry the corpus
  they should (and the tool server none), boot on it and refuse a drifted one; the production pack's web, mcp
  and rag services each start from the example env with synthetic secrets and stay running. Two import defects
  were found by the auth gate and fixed: a client-scope
  description longer than Keycloak's 255-character column stopped the import, and an imported realm has no
  built-in profile scope, so `preferred_username` had to be mapped explicitly or the operator audit trail
  would have recorded a UUID.

- **The Web host operator policy reads the claim the realm emits.** `OperatorPolicyTests` asks the host's
  registered `operator` policy about principals shaped like the operator cookie: a flat `roles` claim
  carrying `rma:operator` is admitted, an authenticated principal without it is denied, and Keycloak's
  nested `realm_access.roles` alone does not count, so a missing `operator-roles` mapper is caught. The
  same file pins the token half (`MapInboundClaims` off, `RoleClaimType` `roles`, `RequiredRole` equal to
  the realm role) and that `/Operator` is guarded by the policy the tests exercise. Mutation-checked:
  dropping the role requirement fails the denial test, and removing `roles` from the claim check fails the
  admission test.
- **Gate for this change:** `suite` 502 passed, 0 skipped, 0 failed; EF model check clean ("No changes have
  been made to the model since the last migration"); vulnerability audit clean for all nine projects.

- **The shared Production rule left the data layer.** `ProductionConfiguration` moved from `AIEnabledRma.Data`
  to `AIEnabledRma.Domain`, beside the `ProductionDatabaseCredentials` and `ProductionSecrets` rules it applies,
  and Domain gained `Microsoft.Extensions.Configuration.Abstractions` for `IConfiguration`. The RAG host had
  referenced the EF Core project only for that call, so it now references Domain and Privacy alone, its
  Dockerfile copies no Data manifest, and its published image carries no `Microsoft.EntityFrameworkCore.*`,
  `Npgsql.EntityFrameworkCore.PostgreSQL` or `AIEnabledRma.Data.dll` while keeping `Npgsql`. Evidence:
  `PublishLayoutTests.The_published_rag_host_carries_no_orm` asserts the publish, and the Dockerfile-copy
  theory now covers the RAG image. Mutation-checked: restoring the Data reference fails the publish assertion.
- **Gate for this change:** `suite` 504 passed, 0 skipped, 0 failed; EF model check clean; vulnerability audit
  clean for all nine projects.

- **A retention policy, and one command that applies it.** `RetentionPolicy` (Domain) fixes the four
  windows the owner chose on 2026-11-22: `mcp_tool_calls` 90 days, `operator_actions` 730 days (the
  default return window), resolved `review_items` 90 days after resolution, and `webhook_events` 90
  days. `RetentionPruner` (Data) applies them with one query per table, so the count a dry run prints
  is the set the applied run deletes, and each predicate is a range scan on the column the table
  already indexes for time. `DbAdmin prune` is the operator surface: dry run by default, `--apply` to
  delete, one optional per-table flag, and an unrecognised flag refused rather than read as "all
  four", because with `--apply` that would turn one table into the whole audit trail. Open review
  items are unreachable at any age (the predicate needs `status = Resolved` and a non-null
  `resolved_at_utc`), and a window of zero or less is refused instead of deleting the row just
  written. Tests: `RetentionPolicyTests` pins the four numbers and the guard;
  `RetentionPruneTests` runs against PostgreSQL with rows straddling every window, including a
  resolved item created 400 days ago and resolved 89 days ago (survives: age is resolution) and an
  open item 4000 days old (survives). Mutation-checked twice: ageing review items by
  `created_at_utc` fails the apply and dry-run tests, and pruning the queue by creation without the
  status filter fails the open-item test.
- **Gate for this change:** `suite` 516 passed, 0 skipped, 0 failed; EF model check clean ("No
  changes have been made to the model since the last migration"); vulnerability audit clean for all
  nine projects.
- **The "no ORM in the RAG image" claim now has its measurement.** Built `05f80b5` (the commit before the
  move) and `8e74e61` (the move) from `git archive` of each tree through the same Dockerfile: the base digests
  and all seven base layers are byte-identical, so only the `COPY /app` layer differs, and every number below
  is a byte sum of the image's own layer blobs (`docker save`), not the rounded `docker images` column.
  The app layer fell from 8,396,288 to 2,346,496 bytes uncompressed (-6,049,792, -72.1%) and from 2,915,899 to
  889,554 bytes compressed (-2,026,345); the whole image, sum of all blobs, fell from 106,894,292 to 104,867,954
  bytes (-2,026,338, -1.9%). So the ORM leaving bought 72% of the application payload and under 2% of the pull:
  the ASP.NET runtime and Debian base are ~104 MB of the download and the app is now under 1% of it. `/app` went
  from 20 files to 13 and from 10 assemblies to 4, and the six removed assemblies are
  `Microsoft.EntityFrameworkCore.dll` (2,829,152), `Microsoft.EntityFrameworkCore.Relational.dll` (2,194,744),
  `Npgsql.EntityFrameworkCore.PostgreSQL.dll` (679,424), `AIEnabledRma.Data.dll` (250,368, plus its 37,104-byte
  PDB), `Microsoft.EntityFrameworkCore.Abstractions.dll` (34,616) and `EFCore.NamingConventions.dll` (29,184).
  `Npgsql.dll` stays because the host opens PostgreSQL directly, `AIEnabledRma.Domain.dll` grew 11,264 bytes as
  the moved rule compiled into it, and the corpus is unchanged at 19 articles plus README and `REVIEWED.sha256`.
  Recheck the whole comparison with one command: `scripts/rag-image-diff.sh 05f80b5 8e74e61` builds both
  trees from `git archive` through each commit's own Dockerfile, sums the image's layer blobs (a saved
  layer is a compressed blob, so the uncompressed figure is the layer decompressed) and prints the
  app-layer and whole-image deltas plus the assembly diff. Re-run on 2026-11-22 it reproduces the app
  layer exactly (8,396,288 -> 2,346,496, -72.1%; 2,915,899 -> 889,554), the same six removed assemblies at
  the same byte sizes, the same +11,264 on `AIEnabledRma.Domain.dll`, exactly one differing layer (the
  `COPY /app` layer, index 7) and `/app` 10 -> 4 assemblies; the whole-image compressed sum reads
  106,883,242 -> 104,856,897, whose -2,026,345 delta is 7 bytes from the -2,026,338 recorded above, the
  totals differing because the hand run summed one more blob.
- **The RAG image runs on a chiseled base, and the pull is little more than half what it was.** Its
  runtime stage was the full `dotnet/aspnet:10.0` image: a shell, a package manager and a debugger for
  a host that opens one database and reads one corpus. It now pins
  `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:7c625e44e5d588b4d7b2a913e94ca4bd5098ce47442da404829e4c7936a764e1`,
  the linux/amd64 entry — a tag moves under the next pull and a digest for another architecture would
  not run here at all — and the pull falls from 104,856,897 to **57,555,499 bytes**. That figure is
  counted the same way as the entry above: the image's own layer blobs from `docker save`, not the
  rounded `docker images` column. A local rebuild of the new image sums to 57,555,014 bytes of layer
  and config blobs, 485 bytes short of the registry figure, which is the manifest blobs a registry
  counts and a local save does not. The host still loads all 19 articles with 0 problems on the
  chiseled base: no ICU, no shell and no extra packages turned out to be needed by anything it does.
- **A base without a shell changes what the image gate can do, so the gate stopped doing it.** The
  corpus count used to come from `docker run --entrypoint sh -c`; it is now `docker create` plus
  `docker cp` of `/app/knowledge-base`, counted on the host — which is also the stronger claim, since
  it reads the files the image shipped rather than what a process chose to report about them. The
  drift case copies the corpus out, deletes the manifest's first article and mounts the result
  read-only over `/app/knowledge-base`, which is what a drifted deployment actually looks like: the
  files are wrong, the image is not. The entry point is read from the image's own config instead of
  being repeated in a shell one-liner, and `check_no_shell` fails the gate if the image has `/bin/sh`
  at all — the property the other two checks were rewritten around, asserted rather than assumed.
- **Tests:** `GateScriptTests` pins the chiseled linux/amd64 digest and the absence of both the old
  full-runtime digest and the bare chiseled tag, plus the `docker cp` count, the bind mount, the
  entrypoint assertion and the no-shell check
  (`The_rag_runtime_is_a_pinned_chiseled_linux_amd64_base`), and fails if `SMOKE_ASSEMBLY` comes
  back.
- **Two writers on one return no longer both win.** `RmaRequest` gained `RowVersion`, mapped to
  PostgreSQL's `xmin` system column (`IsRowVersion()`), so every update carries `WHERE xmin = <the
  value that was read>` and a write built from a stale read is refused instead of silently
  overwriting the newer state. `EfUnitOfWork` translates the provider's
  `DbUpdateConcurrencyException` into `ConcurrencyConflictException` and detaches the stale entry,
  because a row still tracked as Modified would make the refusal's own audit insert fail the same
  way. `OperatorService` records the attempted action again with `Succeeded = false` and returns a
  refusal (capture's wording says the provider may already hold the capture, since the gateway is
  called before the row is written). The wizard's confirmation and every other step that writes the
  row go through `TryCommitAsync`, which reports `InvalidTransition` with the status now stored:
  without it the new exception would have escaped as a 500 on triage, problems, shipping and payment
  as well. `AddRmaRequestConcurrency` is deliberately empty, because the scaffolder emits an
  `AddColumn` for `xmin` and PostgreSQL refuses a user column of that name.
- **Tests:** `RequestConcurrencyTests` runs against PostgreSQL and holds the same row in two contexts,
  which is what two browser sessions have — a stale `VoidAsync` after another session's
  `MarkReceivedAsync` is refused, the row stays `Received`, exactly two action rows exist and the
  second is the refusal; a confirmation from a stale page is refused and reports the stored
  `AwaitingShipment` rather than what the page meant to write. Mutation-checked: removing the
  `IsRowVersion` mapping fails both tests.
- **Gate for this change:** `suite` 518 passed, 0 skipped, 0 failed; EF model check clean ("No
  changes have been made to the model since the last migration"); vulnerability audit clean for all
  nine projects.
- **A restore drill, and readiness a proxy can act on.** `scripts/restore-drill.sh` (also
  `scripts/verify-all.sh restore`) starts a throwaway PostgreSQL from the pack's own image, seeds it
  through `DbAdmin seed`, dumps it with `pg_dump -Fc`, copies the file out of the container and back,
  restores it into a second database with `pg_restore --exit-on-error`, migrates that database and
  requires nothing left pending, then reads every row count back through `DbAdmin stats` and diffs
  them against the source. `--exit-on-error` is there because `pg_restore` otherwise reports a skipped
  object and exits zero — the failure that turns a half-restored backup into a green drill. The drill
  refuses to pass on an empty source, so a seeder that stopped writing cannot make it prove nothing.
- **Readiness, not liveness.** `web`, `mcp` and `rag` share one `x-http-readiness` healthcheck that
  asks `/health` for an HTTP 200, and `proxy` now waits for `service_healthy` on both services it
  fronts. The probe is bash `/dev/tcp` because no image carries curl and installing it into three
  production images to answer one question is a cost this repository declined. The MCP host gained an
  unauthenticated `/health` that answers only `{"status":"ok"}`: a probe cannot present a token,
  and unlike the web host's endpoint there is no corpus or model name to withhold behind a key.
- **Tests:** `ProductionPackReadinessTests` asserts the probe on all three services, that it asks for
  200 rather than a port, that it needs no package the images lack, that all three hosts map the path
  it calls, and that the proxy waits on health; `RestoreDrillScriptTests` pins the dump/restore/migrate/
  compare steps and the empty-source refusal; `McpHttpTransportTests` proves `/health` answers without
  a token and exposes exactly one property. Mutation-checked twice: removing the web service's probe
  fails the pack test, and pointing `pg_restore` at the seeded database instead of the scratch one
  makes the drill fail on the count comparison (0 rows restored, 2 checks failed).
- **Gates for this change:** `suite` 533 passed, 0 skipped, 0 failed; EF model check clean;
  vulnerability audit clean for all nine projects. `restore` end to end: 38,265-byte dump, scratch
  database at the current schema, every row count matched.
- **`DbAdmin stats` now shows the audit tables.** The command counted the seven business tables and
  nothing else, so between prune runs an operator had no way to see how fast `mcp_tool_calls`,
  `operator_actions`, `review_items` and `webhook_events` were filling — the prune report only speaks
  when it is run, and by then it is proposing deletions rather than showing growth. The four counts
  now print under the business ones, in a block of their own because they answer a different
  question. The printing moved out of the top-level program into `StatsReport` so
  `DbAdminStatsTests` pins the exact text: a table that drops out of the list reads exactly like a
  table that never grows, and that is the failure a pinned string catches. The tests project gained
  a reference to the `DbAdmin` Exe for this, and its lock file gained the matching entry.
- **Mutation-checked:** counting `review_items` from the `mcp_tool_calls` table makes the
  database-backed test fail (0 where 1 was expected); the restore drill still passes end to end with
  the longer output (audit counts 0 on both sides, 38,265-byte dump, no pending migrations).
- **Gates for this change:** `suite` 535 passed, 0 skipped, 0 failed; `restore` green.
- **A stale capture now has a live test of its own.** The token, the refusal path and the stale-void
  and stale-confirmation tests were already in place, but capture — the transition that moves money —
  had no test holding two contexts on the same row, so nothing proved its refusal either. The new case
  opens the capture form in Received in both sessions, captures in the first, and submits the second:
  that one's status guard still reads Received, so the guard cannot refuse it and only `xmin` can.
  It asserts the refusal, that its wording says the provider may already hold the capture rather than
  claiming the money stayed put, that the stored row is InRepair, and that exactly two `capture`
  action rows exist with one succeeded and one refused with "changed since it was read".
- **Mutation-checked:** removing `IsRowVersion()` from the `RowVersion` mapping fails all three
  concurrency tests, the new capture one included.
- **Gate for this change:** `suite` 536 passed, 0 skipped, 0 failed.
- **The restore drill is a gate, not a script somebody remembers.** `scripts/restore-drill.sh` existed
  and `scripts/verify-all.sh restore` could run it, but it sat outside the default set and outside CI:
  migrations here are forward-only and `reset --force` drops the schema, so the only thing speaking
  for the way back never ran unless a person typed it. It is now the fourth step of the default gate
  (`set -- suite auth images restore`, last because it is the slowest) and its own `restore-drill` job
  in `ci.yml`, with a 20-minute timeout and no .NET setup, because it publishes `DbAdmin` and moves a
  dump between containers rather than using the job's SDK or service database. The workflow's cost
  comment says what the extra minutes buy, next to the rule about matrices and second operating
  systems.
- **Both scripts are now executable in the index.** `restore-drill.sh` and `verify-all.sh` were staged
  as `100644`, so `run: ./scripts/restore-drill.sh` would have failed in CI with "Permission denied"
  while every local run passed — the working tree had the bit and the index did not.
- **Tests:** `RestoreDrillScriptTests` asserts the drill is in the default set rather than reachable
  only by name; `GateScriptTests` asserts the same line and the CI job beside it, so the local gate
  and CI cannot drift apart; `CiWorkflowTests` asserts the job exists with its own timeout.
  Mutation-checked: removing `restore` from the default set and replacing the job's `run:` with
  `echo` fails all three.
- **Gates for this change:** `./scripts/verify-all.sh suite restore` end to end — suite 537 passed,
  0 skipped, 0 failed, EF model check and vulnerability audit clean; then the drill: 38,265-byte
  dump, scratch database at the current schema, every row count matched, "all requested gates passed".
- **`DbAdmin reconcile-payments` asks the provider what became of a stale authorisation.** Webhook
  dedupe stopped a delivery being handled twice; nothing compared this database with the provider,
  so a delivery that was lost, rejected or never configured left a return "authorised" forever while
  the money had already moved, and no row recorded that anybody had looked. `IPaymentGateway` gained
  the one read-only call it was missing (`GetAsync`) implemented for Stripe (a GET of the
  PaymentIntent: succeeded → Captured, requires_capture → Authorized, canceled → Voided, anything
  else → Failed) and for the bundled fake; `IRmaRepository` gained the query that names the
  candidates, untouched rows carrying a transaction. `PaymentReconciler` derives the expected state
  from the return's own status — captured for InRepair and beyond, voided for a cancelled one,
  authorised before that — files a `payment-mismatch` review item where the provider disagrees, and
  never writes the return itself. One open item per return keeps a nightly run from burying the
  queue; resolving it lets a later run file again. A provider that cannot answer is reported, not
  filed: an unanswered question is not a disagreement. Exit code 2 says exactly that.
- **The fake gateway keeps a voided record instead of forgetting it.** `VoidAsync` removed the
  authorisation, so the one provider in the repository answered "unknown" for a transaction it had
  just cancelled; a real provider still reports `canceled`. The fake now marks the record Voided and
  keeps it, which is what made the disagreement path testable at all.
- **DbAdmin honours `ASPNETCORE_ENVIRONMENT`.** The generic host only reads `DOTNET_ENVIRONMENT`, so
  every script here that sets the ASP.NET variable was running the tool as Production: `reset
  --force` and the bundled payment gateway both refused for a reason unrelated to the request.
  `DOTNET_ENVIRONMENT` still wins.
- **Tests:** `PaymentReconciliationTests` runs against the bundled fake gateway and PostgreSQL — a
  capture the webhook never reported is filed for review, an authorisation that still matches files
  nothing, a provider-side void is filed while the row looks active, an unanswerable transaction is
  reported and not filed, rows inside the window are not asked about, a second run does not file the
  same item twice, and a resolved item lets it be filed again. `DbAdminReconcileReportTests` pins the
  printed output. Mutation-checked: making the comparison always agree and dropping the dedupe fails
  four of them.
- **Gates for this change:** `suite` 547 passed, 0 skipped, 0 failed; the command run end to end
  against a seeded database — clean run says so and exits 0, one stale row the gateway has never
  heard of is reported as unanswered and exits 2 with nothing filed.

- **Two operators cannot both resolve one review item.** Resolving was a read followed by a write:
  `ResolveReviewItemAsync` loaded the item, checked that its status was Open and wrote the resolution,
  so two operators who both had the item on screen passed that check and were both told "Resolved." —
  the second silently replacing the first one's name and explanation, with an audit trail showing two
  successes and nothing to say which one stands. `ReviewItem` now carries `RowVersion`, mapped to
  PostgreSQL's `xmin` (`IsRowVersion()`), the same token `RmaRequest` already had; the migration for it
  is deliberately empty because `xmin` is the server's own system column and the model mapping, not DDL,
  is what enforces it. The service catches `ConcurrencyConflictException` the way the request actions
  do — the resolution and its action row roll back together, the attempt is written again with
  `Succeeded = false` and `refused: the return changed since it was read`, and the operator is told to
  reload the queue rather than that the item is still open.
- **Test:** `Two_operators_resolving_one_review_item_leave_the_first_resolution_standing` holds two
  contexts on one open item, resolves it from the first, then submits from the second and asserts it is
  refused, that the stored row still names the first operator and their explanation, and that the ledger
  has two `resolve-review` rows with exactly one success. **Mutation-checked:** deleting the refusal
  handler makes that test fail with the escaped `ConcurrencyConflictException` while the other three
  concurrency tests still pass; restoring it returns all four to green. Removing the mapping instead is
  caught by the suite's `ef migrations has-pending-model-changes` step, which is how a token that stopped
  being enforced would surface first.
- **Gates:** `suite` 557 passed, 0 skipped, 0 failed.
- **One refusal path, not two.** `ConfirmAsync` still carried its own `try`/`catch` around
  `CommitAsync`, with its own reload and its own wording ("changed by someone else while you were
  confirming it"), while every step added since went through `TryCommitAsync` — the same decision
  written twice, free to drift, and the drift would only show on a stale page nobody exercises twice.
  The confirmation step now calls `TryCommitAsync(request, "confirmation", cancellationToken)` and the
  duplicate is deleted, so the reload, the `InvalidTransition` outcome and the message exist once. The
  stale-page test additionally asserts the refusal names the step ("confirmation step"), which a
  bespoke wording would not; mutation-checked both ways — calling `CommitAsync` directly again fails
  that test with the escaped `ConcurrencyConflictException`, and putting the old catch block back fails
  it on the missing "confirmation step".
- **Gate:** `suite` 559 passed, 0 skipped, 0 failed.
- **The capture race, seen from the losing side.** The existing capture test lets both operators capture,
  and both write `InRepair` — so a refused write and a landed one leave the same status and only the
  ledger can tell them apart. The new test makes the competing write a void: operator one cancels a
  return in `Received`, operator two's capture form is still open on that row, and its submit is refused
  after the provider was already called. The row must stay `Cancelled` with its cancellation reason (a
  write that landed would flip it to `InRepair` while the reason still names a withdrawal), and
  `operator_actions` must hold exactly two rows: the void as the only success, the capture as a refusal.
- **Gate:** `suite` 560 passed, 0 skipped, 0 failed. Mutation: dropping `recorded.Succeeded = false`
  from the refusal catch fails the new test on the ledger assertion.
- **`DbAdmin audit-payments` is the sweep beside reconciliation, not a second opinion on it.**
  `reconcile-payments` deliberately skips a row written inside its six-hour window, because a delivery
  that arrived writes the row — but a capture whose row write lost the race leaves the opposite shape:
  the money moved, the row still says `Received`, and the row was written seconds ago. Nothing found
  those. The new command reads every return carrying a transaction, with no window, asks the provider
  for each one, and says which side holds the capture or void: `provider only` (money moved, never
  recorded), `row only` (the row claims a capture the provider never took), or `mismatch` (both hold a
  terminal state, different ones) — the two directions the operator needs, since the first is a return
  to reconcile by hand and the second is a customer told they paid when they did not. It files and
  changes nothing (that is reconciliation's job), an unanswerable transaction is reported as unanswered
  rather than as drift, a `--take` bound that cuts the set short says so, and the exit code separates
  0 in step, 2 could not be asked, 3 drift found.
- **Gate:** `suite` 570 passed, 0 skipped, 0 failed. Mutations: flattening the direction to
  `ProviderOnly` fails the row-only and mismatch tests; swapping the 2 and 3 exit codes fails the
  test that pins them.
- **The pack's waits are checked, not only its syntax.** CI already resolved the pack with
  `docker compose config`, which catches a renamed value, a broken anchor and a dependency on a
  service that does not exist. It does not catch `condition: service_healthy` naming a service with
  no healthcheck: the project resolves, and `up --no-start` creates the containers, so the wait is
  found at deployment and never finishes — the proxy behind it simply never starts. `scripts/
  verify-compose.sh` resolves the pack from `deploy/prod.env.example` and then reads the *resolved*
  project, where anchors are expanded, for every `service_healthy` dependency whose target has no
  probe; a pack with no such dependency is a failure too, so an emptied `depends_on` cannot pass.
  CI's resolve step runs the script, `verify-all.sh`'s images gate runs it, and the suite pins the
  same property on the file itself, so the edit fails without docker as well.
- **Gate:** `suite` 572 passed, 0 skipped, 0 failed. Mutations: pointing proxy at `ollama` (no
  healthcheck) fails the script with `proxy waits for ollama to be healthy, but ollama has no
  healthcheck` and fails the two pack tests; the resolve still exits 0 for that file, which is the
  gap the new check closes.
- **The edge has a probe, and nothing starts without health.** The proxy was the one service `docker
  compose ps` reported as running while nothing asked whether it could serve. It now probes Caddy's
  admin endpoint on `127.0.0.1:2019/config/` — up only when the process is up with a configuration
  loaded — rather than a published port: :80 answers a redirect that a client follows into a
  certificate handshake, and :443 cannot answer before ACME has issued a certificate, so either
  probe calls a working edge dead exactly when a deployment is least able to look. Measured on the
  running pack: that URL exits 0, the same request to a closed port exits 1, and the :80 probe exits
  1 with the TLS error. Nothing in the pack starts after the proxy today; the rule that makes the
  next dependent safe is enforced instead — every `depends_on` in the pack must name
  `condition: service_healthy`, read from the file so a bare list (which inherits `service_started`)
  fails.
- **Gate:** `suite` 574 passed, 0 skipped, 0 failed. Mutations: deleting the proxy probe fails the new
  test; changing one condition to `service_started` fails three. Live: `docker compose up -d proxy`
  recreated the container and it reached healthy in 20s, probe exit 0, with web reported Healthy
  before the proxy started.
- **The drill runs at 03:17 whether or not anyone pushed.** `restore-drill` already ran on every
  merge, which is the day a backup path least often breaks: it breaks from outside — a republished
  base image, a dump format change, a migration the dump cannot carry — on a day nobody edits
  anything. `.github/workflows/restore-drill-scheduled.yml` runs `./scripts/verify-all.sh restore`
  nightly and on demand: the command the runbook documents rather than the script it calls, with a
  timeout, a concurrency group that queues instead of cancelling, `contents: read`, and a run-page
  summary that states the verdict either way, so a red night says what failed without a reviewer.
- **Gate:** `suite` 575 passed, 0 skipped, 0 failed, and `./scripts/verify-all.sh restore` ran the
  command the schedule uses: 7 checks, exit 0. Mutation: pointing the schedule at
  `./scripts/restore-drill.sh` instead of the documented command fails the new test.
- **`DbAdmin stats --json` for a monitor, the table left alone.** The counts were readable only by a
  person: a monitor had to parse padded lines. `--json` writes the same counts as one compact object,
  grouped the same way (`{"business":{…},"audit":{…}}`), so growth between prune runs is a field
  rather than a substring; the text view is byte-for-byte what it was, and the drill's regex over it
  is untouched. An unknown flag exits 1 rather than falling back to text, because a collector that
  asked for JSON and got padding parses nothing, or parses a number out of the padding.
- **Gate:** `suite` 577 passed, 0 skipped, 0 failed. The stats tests are 4/4; against a seeded
  database the command prints the pinned table unchanged, `stats --json` prints one line and exits 0,
  and `stats --bogus` writes to stderr and exits 1. Mutation: putting every count in the `business`
  group fails both new tests.
- **Three changes that had been waiting on a dead Docker engine, verified together.** The capture
  path now proves its row before it charges: `IRmaRepository.TryRunGuardedAsync` opens a transaction
  inside EF's retrying strategy, takes the row with `SELECT … FOR UPDATE`, reloads it and compares the
  version the operator's page was rendered from, and only then runs the provider call and the write —
  so a stale capture or void is refused with nothing sent to the gateway, and the refusal no longer
  has to hedge about money that may have moved. `reconcile-payments` now has a scheduled form: a
  one-shot compose service behind the `ops` profile, built from a new `tools/DbAdmin/Dockerfile`, with
  `deploy/rma-reconcile.service` and `.timer` and README instructions for installing them (the pack's
  postgres publishes no port, so the sweep runs on the pack's network rather than the host's crontab).
  And a payment-mismatch review row now shows what it is about: `PaymentMismatchReason` decodes the
  provider state from the reason code, `ReviewItem.ProviderStatus` exposes it, and `OperatorReviewRow`
  joins the RMA number and transaction from the return, so the row reads "RMA: … · provider: Captured
  · transaction: pi_…" without a psql session.
- **Gate:** `suite` 598 passed, 0 skipped, 0 failed; EF model check clean; vulnerability audit clean.
  Mutation: dropping the provider state from the dashboard row fails
  `A_mismatch_row_names_the_return_the_provider_state_and_the_transaction` with "Not found: provider:
  Captured".
- **A delivery is recorded and the item it files is written in one transaction.** The webhook handler
  recorded the delivery first and filed the review item second, through `EfReviewQueue`, which opens a
  scope and context of its own; the two could not share a transaction even in principle. A delivery
  whose filing failed — a deadlock, a restart, a context that would not save — left a row saying
  "handled" and no item, and Stripe's retry was answered as a repeat, so a failed payment was never
  seen by a person. `IWebhookEventLedger` is now one `TryAcceptAsync(delivery, effect)`: the insert
  goes in inside EF's execution strategy, a duplicate rolls back and answers false, and the item and
  the ledger row commit together or not at all. The handler also answers a signed payload it cannot
  read with 400 instead of 500, because Stripe retries a 500 until it gives up.
- **Gate:** `suite` 603 passed, 0 skipped, 0 failed. Mutation: swallowing the item's save failure in
  `EfWebhookEventLedger` makes `A_delivery_whose_item_could_not_be_filed_is_not_recorded` fail with
  "No exception was thrown"; the other nine webhook tests still pass, so that one test is what pins
  the atomicity.
- **The sweep's image is built by the gate, and its sources are in the build context.** `.dockerignore`
  excluded `tools/`, a pattern written when only the web, mcp and rag images existed, so
  `tools/DbAdmin/Dockerfile` failed at its first `COPY` with "packages.lock.json: not found" — and
  the only thing that would have noticed is the first night the timer ran. The context keeps
  `tools/` now, `verify-images.sh` builds the image as `Ops` (with `OPS_IMAGE` and the same
  revision-label check as the other three), and a test pins both halves. Proven locally: the image
  builds (376 MB) where before it could not.
- **Gate:** `suite` 603 passed, 0 skipped, 0 failed, with the gate and context test in it.
- **The provider's answer is compared as money, not only as a state.** Reconciliation and the audit asked the provider one question — what state is this transaction in — and called it agreement when the two statuses matched. A capture of 69.00 EUR against a return that says 70.00 EUR, or the same number in another currency, passed as a match, so the one drift only the provider can see was invisible exactly when the amount was wrong — which is the case the customer notices. `PaymentReconciler.AmountFor` derives the sum from the row the payment step already authorised (fee plus shipping, in the row's currency), `Agrees` now requires the state and the money, and `SameMoney` treats a currency code's case as a provider's formatting rather than a second currency. A disagreement carries both sums: the review item says which side holds what instead of repeating "Captured", and `reconcile-payments`/`audit-payments` print the amounts only where they differ, so a status drift stays one readable line.
- **Gate:** `suite` 611 passed, 0 skipped, 0 failed, all gates passed. Mutations: reducing `Agrees` to the status comparison fails exactly the two new reconciler tests (`A_capture_for_a_different_amount_is_a_disagreement_even_though_both_say_captured`, `A_charge_in_another_currency_is_a_disagreement_at_the_same_number`) with 7 others still passing; the audit's version of the same test fails when `PaymentAuditor` is left comparing states.
- **A delivered failed payment names the return it is about.** The webhook handler filed its review item with no `RmaRequestId` and no `CustomerId` — only the RMA number inside the explanation — so the same failure caught inline during authorisation opened from the dashboard while the delivered one could not be opened at all. The parsed number now travels with the effect (`WebhookEffect`), and `EfWebhookEventLedger` resolves it to the return inside the transaction that files the item, so the link and the row commit together or not at all. A number that matches nothing still files the item, unlinked: the payment failed whether or not the return exists, and withholding it to keep a foreign key tidy would lose the only trace of the money.
- **Gate:** `suite` 611 passed, 0 skipped, 0 failed, all gates passed. Mutations: never resolving the named return in `EfWebhookEventLedger` fails `A_delivery_naming_a_return_links_the_item_to_it` with the other ten webhook tests still passing, including the one that pins an unknown number being filed anyway; dropping the number from the effect fails `A_delivery_passes_the_return_it_names_to_the_ledger`.
