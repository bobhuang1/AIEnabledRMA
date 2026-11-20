# AI-Enabled RMA

An end-to-end returns (RMA) reference implementation. A language model helps the customer
describe and troubleshoot the fault, while a **deterministic, auditable policy** decides
eligibility, coverage, and money. The AI can suggest a fix — it can quote nothing and approve
nothing. Every eligibility decision, every price, and every state change comes from the same
policy evaluator whether the caller is the web wizard, an MCP agent, or a test.

GPL-3.0 licensed — see [LICENSE.md](LICENSE.md).

## What you can try in five minutes

```bash
docker compose up -d                                  # PostgreSQL 16 on :5432
export ConnectionStrings__Rma='Host=localhost;Port=5432;Database=rmadev;Username=rmauser;Password=rmapassword'
dotnet run --project tools/DbAdmin -- reset --force  # migrate + seed demo data
dotnet run --project src/AIEnabledRma.Web            # wizard on http://localhost:5xxx
```

Open the wizard and start a return. The shipped demo catalog gives you a device for every
policy outcome:

| Serial | Warranty | Wizard behaviour |
| --- | --- | --- |
| `AX2-0001-0001` | In warranty | Free replacement, no payment step |
| `AX2-0001-0005` | Expired, in window | **Paid repair — walks the payment step, $69.00** (`AX-200-HS`) |
| `BX1-0003-0001` | Shipped 1,200 days ago | Refused: outside the return window |
| `AX2-0001-0002` | Has an open RMA | Human review: duplicate request |
| `NO-SUCH-SERIAL-0000` | Unknown | Refused: not in the catalog |

Use customer `j.smith@example.com` (a seeded account). Typing just `Smyth` exercises the
ambiguous-identity path; `j.smith@example.com` resolves to exactly one customer.

> The seeder expresses warranty windows relative to a fixed reference date, so the
> grace-period and boundary demos gradually age into "paid repair" as the wall clock moves.
> The policy logic does not change; resetting the database does not move the seeder reference
> date.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Docker (for the PostgreSQL server) — or any PostgreSQL 16 you can point the connection at
- `RMA_TEST_CONNECTION` if you want the live-database tests to run (see [Tests](#tests))

## Repository layout

| Project | Purpose |
| --- | --- |
| `src/AIEnabledRma.Domain` | Entities, `RmaWorkflow` state machine, policy evaluator, abstractions. No I/O. |
| `src/AIEnabledRma.Data` | PostgreSQL via EF Core 10, repositories, migrations, schema bootstrap, demo seed. |
| `src/AIEnabledRma.Ai` | Provider selection, startup validation, the bundled fake payment gateway. |
| `src/AIEnabledRma.Rag` | In-process knowledge index (BM25-ish retrieval) that feeds the AI with troubleshooting articles. |
| `src/AIEnabledRma.Mcp` | MCP tool server: the same policy and lookups exposed to an agent. |
| `src/AIEnabledRma.Web` | Customer-facing return wizard (ASP.NET Core MVC). |
| `tools/DbAdmin` | Migrate, seed, reset, and inspect the database from the command line. |
| `tests/AIEnabledRma.Tests` | 190 tests, incl. live PostgreSQL sequence and end-to-end wizard suites. |

All three hosts call the same `AddRmaData` registration, so the fuzzy-match thresholds a
customer-facing wizard uses and the ones an agent's lookup tool uses cannot drift apart.

## Setup

### 1. Start PostgreSQL

```bash
docker compose up -d
```

This runs PostgreSQL 16 exposed on `localhost:5432` with `rmauser`/`rmapassword` (demo
credentials only).

### 2. Configure the connection

The checked-in `appsettings.json` ships a **placeholder** connection string — nothing that
touches the database will work until you supply a real one. Use an environment variable
(exactly the name EF config binding expects):

```bash
export ConnectionStrings__Rma='Host=localhost;Port=5432;Database=rmadev;Username=rmauser;Password=rmapassword'
```

On Windows PowerShell:

```powershell
$env:ConnectionStrings__Rma='Host=localhost;Port=5432;Database=rmadev;Username=rmauser;Password=rmapassword'
```

Never commit a connection string containing a real password. Local overrides go in
`appsettings.Development.json` / `appsettings.Local.json`, both git-ignored.

### 3. Create the schema and seed demo data

```bash
dotnet run --project tools/DbAdmin -- reset --force
```

or, if the database already exists:

```bash
dotnet run --project tools/DbAdmin -- migrate
dotnet run --project tools/DbAdmin -- seed
```

`DbAdmin` commands: `migrate`, `migrations`, `seed` (only when the catalog is empty),
`reset --force` (drop schema, migrate, seed, realign the RMA number sequence), `stats`.

## Run the web wizard

```bash
dotnet run --project src/AIEnabledRma.Web
```

Walk a return:

1. **Start** (`/RmaWizard/Start`) — enter customer + serial(s), one per line.
2. **Triage** (`/RmaWizard/Triage/{id}`) — describe the fault. A local `example-local` model
   is the default AI provider, so no API key is needed.
3. **Problems** (`/RmaWizard/Problems/{id}`) — per-line detail for the bench.
4. **Shipping** (`/RmaWizard/Shipping/{id}`) — choose a stored address or type a free-text one
   (matched, falling back to the customer's default with a warning).
5. **Payment** only when money is due (an out-of-warranty repair) — the fee is authorised for
   manual capture later; nothing is charged instantly.
6. **Confirm** — the review page shows the total (or "Nothing to pay. This repair is
   covered."), and submitting commits the request.

Route-id steps are addressed by the request GUID, not a session (see [Security model](#security-model)).

## Run the MCP server

The MCP server exposes the *same* policy and repositories an agent can call:

```bash
dotnet run --project src/AIEnabledRma.Mcp
```

It is a stdio MCP server by default. Configure it in Claude / an MCP client via
`npx`-style launch, or test the tools directly through their classes in
`src/AIEnabledRma.Mcp/Tools`.

Tools:

| Tool | What it does |
| --- | --- |
| `check_coverage` | Policy verdict for a serial on a given date (`Eligible`, `EligibleForPaidRepair`, `RequiresHumanReview`, `NotEligible`) + explanation + rule trace |
| `get_fee_quote` | Repair price for a device from the same per-product price table the wizard bills from |
| `lookup_device` / `lookup_customer` | Catalog and customer lookups through the same fuzzy matchers the wizard uses |
| `resolve_shipping_address` | Matches free text to a customer's stored addresses, never claiming a weak match is confident |
| `reveal_customer_contact` | Full name, email and phone. Authenticated callers with the `rma:support` role only; needs a reason |

**What an agent can see.** Customer results are masked exactly as the anonymous wizard masks them
(`Jane S.`, a masked email and phone, city plus a postal-code prefix; no street lines and no external
CRM id). `find_customer` matches email and phone exactly, so a partial value returns nothing, and a
name search needs at least 3 characters. Every tool call passes through `ToolCallGuard`, which
writes one row to `mcp_tool_calls` (tool, caller, session, SHA-256 prefixes of the arguments but
never their values, result count, outcome, latency) and enforces `McpSecurity:CallsPerMinute` and
`McpSecurity:CustomerLookupsPerSession`. On stdio the caller is the configured `LocalCallerName`
and has no roles, so `reveal_customer_contact` is always refused there.

Example agent question: *"AX2-0001-0005 is dead on arrival and out of warranty — what happens
if the customer returns it, and how much would it cost?"* — `check_coverage` routes it to a
paid repair and `get_fee_quote` answers `$69.00` for the `AX-200-HS`.

## Tests

```bash
export RMA_TEST_CONNECTION='Host=localhost;Port=5432;Database=postgres;Username=rmauser;Password=rmapassword'
dotnet test
```

Tests that need a real server (`RmaNumberSequenceTests`, `WebWizardTests`) **skip** rather
than fail when `RMA_TEST_CONNECTION` is unset or PostgreSQL is unreachable, so `dotnet test`
with no environment still goes green. The two live suites exist because concurrency semantics
(sequence allocation) and persistence (every wizard step reads what it wrote through a real
host) cannot be faked.

Set `RMA_REQUIRE_LIVE_DB=1` to turn those skips into failures. CI does, and also fails the run if
the test report shows any skipped test, so a green CI run always includes the database suites
(`.github/workflows/ci.yml`). NuGet lock files (`packages.lock.json`) are committed and CI restores
with `--locked-mode`; after changing a package version run `dotnet restore` locally and commit the
lock-file diff.

`WebWizardTests` boots the real web host against a throwaway database and walks the wizard
end to end, including the paid-repair path that lands on the payment step and bills the
per-product price.

## Containers

Build and run the two shipped Dockerfiles:

```bash
docker build -f src/AIEnabledRma.Web/Dockerfile -t aienabledrma-web .
docker build -f src/AIEnabledRma.Mcp/Dockerfile -t aienabledrma-mcp .
docker run -p 8080:8080 -e ConnectionStrings__Rma="$ConnectionStrings__Rma" aienabledrma-web
```

## Configuration

Bind via `IConfiguration`, i.e. `appsettings.json` or environment overrides
(`Policy__...`). The important tree:

```
Policy:
  ReturnWindowDays            # 730
  GracePeriodDays             # 30
  DefaultRepairFee            # fallback price when a product has no table entry (99.00)
  RepairPrices:               # per-product price table — the source of truth for
    - Sku: AX-200-HS         #   both the wizard's bill and the MCP fee-quote tool
      ProductName: AX-200 Handheld Terminal
      RepairFee: 69.00
    ...
  WarrantyTiers:             # coverage + turnaround; fees live in RepairPrices, not tiers
    - Name: standard
      IsCovered: true
    - Name: no-service       # kept as the example of an uncovered tier → paid repair
      IsCovered: false
  ExcludedCauses:            # matched on problem text (e.g. liquid damage) → refused
  FraudIndicatorSerialPrefixes:
  HumanReviewOnlyRegions:
  AiIsAdvisoryOnly: true     # when false, low AI confidence can relax only the fallback
  HumanReviewConfidenceThreshold: 0.7
  FallbackEligibility: RequiresHumanReview

Ai:
  Provider:                  # openai | openai-compatible | ollama | lmstudio | llamacpp
  BaseUrl:                   # absolute URL, required + validated for openai-compatible
  ApiKey:                    # optional for local runtimes; never commit a real key

Payments:
  Fake:
    AcceptAllAmounts: true   # bundled dev gateway: authorise every positive amount
```

## Knowledge store (vector search)

By default the assistant retrieves from an in-memory BM25-style index of the reviewed articles. For hybrid
retrieval (PostgreSQL full-text + pgvector cosine, fused by reciprocal rank) publish the knowledge base to
the database and switch it on:

```powershell
dotnet run --project tools\DbAdmin -- kb-ingest            # chunk, embed, publish (idempotent)
$env:Rag__VectorStore__Enabled = 'true'
dotnet run --project src\AIEnabledRma.Web
```

- **Schema:** `kb_documents`, `kb_chunks` (HNSW index on `embedding`, GIN on a generated `tsvector`) and
  `kb_meta`, created by `kb-ingest` itself rather than the EF migrations, so the application schema still runs
  on a PostgreSQL without the extension. The compose file uses `pgvector/pgvector:pg16`.
- **Chunking:** markdown-aware; split on headings with the title and heading path as each chunk's prefix,
  packed towards 350 and at most 500 estimated tokens, short overlap inside long sections, lists and code
  blocks never split (`Rag:Chunking`).
- **Embeddings:** a configuration choice (`Embeddings` section), see "Choosing the embedding model" below.
  The store records the model and dimension on every chunk and in `kb_meta`, and refuses to mix: ingesting
  or querying with a different model fails until `kb-ingest --rebuild` re-embeds everything (retrieval falls
  back to the in-memory index meanwhile).
- **Publishing:** each run writes a new generation and activates it only when every chunk has a vector;
  unchanged content is a no-op, unchanged chunks reuse their stored vectors, and a failed embedding leaves the
  previous generation in service (exit code 2) for a retry.
- **Scope:** the product-scope rule of the lexical index is applied in SQL before ranking. If the store is
  unreachable, retrieval falls back to the in-memory index.
- **Evidence:** on the golden question set (`tests/.../Fixtures/golden-questions.json`) recall@3 is 0.955 for
  both the lexical index and hybrid retrieval with the hashing embedder; a real embedding model is expected to
  do better on paraphrases.

### Choosing the embedding model

The default is a **local Ollama model**, so article and case text never leave the company:

```powershell
ollama pull nomic-embed-text      # once; or: ollama pull bge-m3
```

| Setting | Ollama (default) | Hosted / OpenAI-compatible | Offline tests |
| --- | --- | --- | --- |
| `Embeddings:Provider` | `ollama` | `openai-compatible` | `hashing` |
| `Embeddings:BaseUrl` | `http://localhost:11434` | e.g. `https://embeddings.example.com/v1` | (none) |
| `Embeddings:Model` | `nomic-embed-text` or `bge-m3` | e.g. `text-embedding-3-small` | (none) |
| `Embeddings:Dimensions` | `768` (nomic) or `1024` (bge-m3) | the model's size, e.g. `1536` | e.g. `384` |
| `Embeddings:ApiKey` | (none) | from the environment only | (none) |

Switching is a configuration change plus a re-embed:

```powershell
$env:Embeddings__Provider = 'ollama'; $env:Embeddings__Model = 'bge-m3'; $env:Embeddings__Dimensions = '1024'
dotnet run --project tools\DbAdmin -- kb-ingest --rebuild
```

Use the same `Embeddings` settings for `DbAdmin` and the web host: a host whose model differs from the store's is
refused (vectors from different models are never compared). Vectors of the wrong size are refused at ingest.
In Production a hosted endpoint must be `https`; Ollama is expected on the internal network
(`docker-compose.prod.yml` runs it as the `ollama` service). CI and the unit tests use `hashing`.

## Resolved CRM cases as knowledge

`DbAdmin kb-cases` turns resolved support cases into troubleshooting knowledge with the customer removed:

```powershell
dotnet run --project tools\DbAdmin -- kb-cases --file cases.json --report report.json          # dry run
dotnet run --project tools\DbAdmin -- kb-cases --file cases.json --publish --reviewed-by "J. Reviewer"
```

- The case model (`SupportCase`) has no customer, account, contact, owner or order field; a source drops them
  when it maps the CRM record. `DataverseCaseSource` requests only troubleshooting columns of resolved
  incidents (`$select`), incrementally on `modifiedon`; wiring it to a tenant waits on decision D1.
- Only resolved cases with a resolution are used. E-mail header lines, quoted replies and signature blocks
  are removed; names from greetings, sign-offs and labels ("Agent: ...") are redacted throughout the case,
  together with everything the PII redactor recognises.
- **Fails closed:** a verification pass looks for anything identifying that survived (an `@`, a long digit run,
  a case/order reference, a recognisable identifier). Such a case is quarantined, not indexed.
- The default is a dry run whose report holds counts per kind per case and the quarantine list, never values.
  Publishing needs `--reviewed-by`: a person reviews a sample first.
- Cases are stored as symptom / diagnosis / resolution chunks (`kind = crm_case`), scoped to their product,
  keyed by an opaque hash, and cited as "Resolved case (product, month)", never by case number.
- The leak test (`CaseLeakLiveTests`) ingests a synthetic corpus with planted personal data end to end and checks
  that none of it reaches the store, the logs, the report or retrieval results.

## Evaluation

`TriageEvaluationTests` runs the golden question set (`tests/AIEnabledRma.Tests/Fixtures/golden-questions.json`)
through the real triage pipeline with the web host's own `appsettings.json` and reports retrieval recall@3,
MRR, citation validity of the raw model output, the human-review fallback rate, out-of-scope accuracy and the
prompt-injection pass rate (customer-side injections, plus instruction-carrying articles that must be refused
at load). CI runs it with the deterministic `example-local` model and gates on the result; the report is
written to `TestResults/eval-report.json`. `.github/workflows/eval.yml` runs the same evaluation against a real
OpenAI-compatible model, manually or nightly, when the repository variable `RMA_EVAL_ENABLED` is `true` and
the `RMA_EVAL_*` secrets are set. Locally:

```powershell
$env:RMA_EVAL_REPORT = "$PWD\TestResults\eval-report.json"
dotnet test tests\AIEnabledRma.Tests --filter "FullyQualifiedName~TriageEvaluationTests"
```

## Operator back office

`/Operator` is the authenticated back office:

- **Human review queue** (`review_items`): starts the policy sent to a person (these create no return, so before
  this they were shown to the customer and lost), triage that fell back or came back below the confidence
  threshold, and failed payment authorisations. Operators resolve each item with a note.
- **Returns in progress:** mark received, record the inspection and capture the authorised repair fee, or void
  the authorisation and cancel. Capture is refused before the item is received.
- **Knowledge base review:** articles refused at load, and (Development only) unreviewed articles with the
  content hash to stamp. **CRM ingestion:** the last `kb-cases` report (counts and opaque keys only).
- Every action, refused or not, is written to `operator_actions` with the operator's name.

Sign-in is OpenID Connect (`OperatorAuth:Authority`, `ClientId`, `ClientSecret` from the environment) and the
token must carry the `rma:operator` role or scope. For local development only, set
`OperatorAuth:DevSignIn=true` in `appsettings.Development.json` for a one-click `dev-operator` sign-in; the app
refuses to start with it in any other environment.

## Why the AI is advisory

A return touches a customer's money, a company's stock, and a warranty obligation. A model
that can approve a return is a model that can be talked into approving a return:

- `RmaPolicyEvaluator` is the only thing that decides eligibility, tier, and money. It is
  ordinary, testable C# with no model in the call path.
- The model contributes a troubleshooting verdict and a step list, stored on the line as
  advisory text.
- A model timeout, malformed output, empty retrieval, or low confidence all fall back to
  `RequiresHumanReview`. There is no path where a model failure becomes an approval.
- `ScopeGuard` refuses questions outside the configured scope rather than guessing.
- When triage resolves the fault by troubleshooting, the RMA is **cancelled**, not approved.

## Paid repairs

An out-of-warranty unit is neither refused nor treated as free: it is offered as a **paid
repair** with its own wizard step. In-warranty units (including the grace period) are replaced
or repaired free. Refusal happens only outside the return window, on an excluded cause, or
for an unknown device.

- The charge comes from a per-product price table (`Policy:RepairPrices`), matched by SKU then
  product name, falling back to `DefaultRepairFee`. Prices are per product, not per tier: two
  units on one request each contribute their own price, summed once per request.
- The table is the single source of truth for the wizard and the MCP `get_fee_quote` tool, so
  an agent's quote and the wizard's charge can never disagree.
- The fee is stored on the request as its deposit amount. The payment step authorises it with
  manual-capture semantics ("authorised now; captured once the item is received and
  inspected"), matching the bundled gateway's behaviour.

## Mixed batches

A customer routinely returns a broken unit together with something we cannot accept. The
workflow keeps the eligible lines, drops the rest, and returns `StepOutcome.PartiallyCreated`
— deliberately distinct from a complete `Continue`, so a caller that ignores the detail list
cannot mistake a partial return for a complete one.

Exclusions are **persisted** in `rma_exclusions` (one row per dropped item, reason and
eligibility stored as enum names), not kept in the session. The triage page rebuilds its
warning from the database, so a refresh or a new browser session cannot silently drop the
notice that part of the submission was refused.

## Security model of the wizard

Route-id steps (`/RmaWizard/Triage/{id}` and later) are intentionally **not** guarded by a
session or a customer-ownership check. The product decision is that the GUID is the
capability: a 122-bit unguessable token plays the role a session would play, so a bookmarked
step works across browsers, and a shareable link lets a second device finish a return.
Accepted trade-off: anyone holding a request URL can drive that return until it is confirmed.

Because the wizard is anonymous, it never shows another customer's details in full:

- A customer is attached directly only when the visitor types the exact email or full
  phone number on file. A weaker (fuzzy) match lists candidates **masked** (`Jane S.`,
  `j•••@e•••.com`, `••• 77`), and only an account from that server-issued list can be
  picked.
- Stored addresses are shown masked (label plus `•••, 62••• US`) on the shipping and
  confirmation steps.
- The payment currency comes from the price table (`Policy:RepairPrices`), never from
  the browser.
Revisit before the wizard gains authenticated pages, an operator portal, or multiple users
per return.

## RMA numbering

References look like `RMA-2026-00042` but come from a **single global PostgreSQL sequence**
(`CACHE 1`, year is display-only). The obvious `MAX(rma_number) + 1` is wrong under
concurrency: two requests read the same maximum and one collides. `RmaNumberSequence` handles
allocation, migration of an existing database, and post-seed realignment so seeded demo rows
and the sequence can never disagree.

## Payments (Stripe)

`Payments:Provider=stripe` switches to `StripePaymentGateway` (PaymentIntents over the REST API):

- repair fees are authorised with `capture_method=manual` and captured by an operator after inspection
  (`/Operator`), or voided; immediate charges use automatic capture;
- every call carries an idempotency key derived from the RMA and the operation, so a retry never charges twice;
- a payment that needs 3-D Secure is cancelled and sent to human review (the wizard has no Stripe.js step yet);
- `/payments/stripe/webhook` verifies the `Stripe-Signature` HMAC with a five-minute replay window and files
  failed or cancelled intents as review items;
- `Payments:Stripe:SecretKey` and `WebhookSecret` come from the environment. Outside Production only test-mode
  keys (`sk_test_...`) are accepted.

The payment page still takes a PaymentMethod id in a text field (for Stripe test mode, `pm_card_visa`).
Collecting card details with Stripe Elements needs the owner's Stripe account (decision D5) and a CSP change
for `js.stripe.com`.

## Payments (development)

The bundled gateway (`FakePaymentGateway`) holds authorisations in memory, honours the
immediate-versus-manual-capture split, and **accepts every positive amount** — the point is to
exercise the payment plumbing, not simulate who gets approved. Its authorisations live only in
process memory; there is no endpoint that lists them. Replace the `IPaymentGateway` registration with a real
provider before taking money.

## Health

- `GET /health` is liveness only and returns `{"status":"ok"}` to anyone. It discloses nothing.
- `GET /health/ready` checks the database connection (503 when it cannot connect). Use it for readiness probes.
- `GET /health/details` reports the loaded knowledge base (path, article count, per-file load errors) and the
  configured AI provider. It needs the `X-Operator-Key` header matching `Operations:ApiKey` (supply the key
  from the environment, never a file). With no key configured it is served only in Development.

## Wizard hardening

- **Rate limits:** every wizard POST is limited per client address (`WizardSecurity:PostsPerMinutePerClient`,
  default 30) and per request id (`PostsPerMinutePerRequest`, default 20); excess gets 429.
- **Link expiry:** a request that is still unconfirmed `WizardSecurity:CapabilityLifetimeDays` (default 14)
  after it was created refuses every further step with 410 Gone.
- **No probing by contact detail:** a typed email or phone attaches the customer only on an exact match;
  anything else continues unattached, the same as an unknown customer. Only a typed name can reach the
  (masked) chooser.
- **Headers:** CSP without inline script or style, `frame-ancestors 'none'`, `nosniff`, `no-referrer`.
- **Payments:** the accept-all `FakePaymentGateway` is registered only in Development and the test host, or
  when `Payments:AllowFakeGateway=true` is set for a demo that takes no real money. Anywhere else the app
  refuses to start.

## Production

`docker-compose.prod.yml` runs the web host, the MCP server (HTTP) and the RAG host behind Caddy
(`deploy/Caddyfile`, automatic TLS); PostgreSQL with pgvector is not published. Copy
`deploy/prod.env.example` to `prod.env` (ignored by git) and fill it in; compose refuses to start while any
required value is missing.

- Base images are pinned by digest and every image restores with `--locked-mode`.
- App containers run as non-root with a read-only root filesystem, `no-new-privileges` and no capabilities.
- Caddy routes web traffic only while `/health/ready` (database reachable) answers.
- In Production every host refuses to start with an unsafe configuration: a default or empty database
  password, an `http` model or embedding endpoint, the fake payment gateway, the web host without OpenID
  Connect operator sign-in, or the RAG host without its API keys.

For the local demo, `Demo:Today` (Development only, `yyyy-MM-dd`) runs the wizard on a fixed date so the
demo serials keep their designed outcomes: `Demo:Today=2026-09-29`.

## License

This project is free software, released under the **GNU General Public License v3.0**. You may redistribute and/or modify it under those terms; see [LICENSE.md](LICENSE.md) for the full text.
