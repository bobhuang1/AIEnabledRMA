# AI-Enabled RMA

A reference implementation of a returns flow where a language model assists the customer and
a deterministic policy decides. The split is the point: **the AI can suggest a fix, quote
nothing, and approve nothing.** Every eligibility decision, every fee, and every state change
comes from the same policy evaluator whether the caller is a web browser, an MCP agent, or a
test.

## Why the AI is advisory

A return touches a customer's money, a company's stock, and a warranty obligation. A model
that can approve a return is a model that can be talked into approving a return. So:

- `RmaPolicyEvaluator` is the only thing that decides eligibility, tier, and fee. It is
  ordinary, testable C# with no model in the call path.
- The model contributes a troubleshooting verdict and a step list. It is stored on the line as
  advisory text.
- A model timeout, malformed output, empty retrieval, or low confidence all fall back to
  `RequiresHumanReview`. There is no path where a model failure becomes an approval.
- `ScopeGuard` refuses to answer questions outside the configured scope rather than guessing.
- When triage resolves a fault by troubleshooting, the RMA is **cancelled**, not approved.

## Projects

| Project | Purpose |
| --- | --- |
| `src/AIEnabledRma.Domain` | Entities, the `RmaWorkflow` state machine, the policy evaluator, abstractions. No I/O. |
| `src/AIEnabledRma.Data` | PostgreSQL via EF Core, repositories, migrations, schema bootstrap, demo seed. |
| `src/AIEnabledRma.Ai` | Provider selection, startup validation, the bundled fake payment gateway. |
| `src/AIEnabledRma.Rag` | In-process knowledge index, BM25-ish retrieval, reload-aware holder. |
| `src/AIEnabledRma.Mcp` | MCP tool server: the same policy, exposed to an agent. |
| `src/AIEnabledRma.Web` | Customer-facing return wizard (MVC). |
| `tools/DbAdmin` | Migrations, seeding, and schema checks from the command line. |
| `tests/AIEnabledRma.Tests` | 190 tests, including live PostgreSQL sequence tests and an end-to-end wizard test that boots the real host. |

All three hosts call the same `AddRmaData` registration, so the fuzzy-match thresholds a
customer-facing wizard uses and the ones an agent's lookup tool uses cannot drift apart.

## Mixed batches

A customer routinely returns a broken unit together with something we cannot accept. The
workflow keeps the eligible lines, drops the rest, and returns `StepOutcome.PartiallyCreated`
with an `ExcludedItem` per dropped line. `PartiallyCreated` is deliberately distinct from
`Continue`: a caller that ignores the detail list still cannot mistake a partial return for a
complete one. The wizard renders every exclusion and its reason, because silently discarding
one of three items leaves the customer with no way of knowing to ask about it.

Exclusions are **persisted** in `rma_exclusions`, one row per dropped item (reason and
eligibility stored as enum names), not kept in the session. The triage page rebuilds its
warning from the database, so a refresh or a new browser session cannot silently drop the
notice that part of the submission was refused.

## Paid repairs

An out-of-warranty unit is neither refused nor treated as free: the wizard offers a **paid
repair** and walks the customer through the payment step before confirmation. A device inside
warranty (including the grace period) is replaced or repaired for free. The only units that are
downright refused are the ones outside the return window, on an excluded cause, or otherwise
beyond the policy.

- The charge comes from a **per-product price table** (`RepairPrices` under `RmaPolicy` in
  `appsettings.json`), matched by SKU then by product name, falling back to `DefaultRepairFee`.
  Prices are per product, not per tier: two different units on one request each contribute
  their own price, summed once per request.
- The table is the single source of truth for both the wizard and the MCP `get_fee_quote` tool,
  so an agent's quote and the wizard's charge can never disagree.
- The fee is stored on the request as its deposit amount. The payment step authorises it with
  manual-capture semantics ("authorised now; captured once the item is received and
  inspected"), matching the bundled gateway's behaviour.

## Security model of the wizard

Route-id steps (`/RmaWizard/Triage/{id}` and later) are intentionally **not** guarded by a
session or a customer-ownership check. The product decision is that the GUID is the
capability: a 122-bit unguessable token plays the role a session would play, so a bookmarked
step works across browsers, and a shareable link lets a second device finish a return.
Accepted trade-off: anyone holding a request URL can drive that return until it is confirmed.
Revisit if the wizard gains authenticated pages, an operator portal, or multiple users per
return.

## RMA numbering

References look like `RMA-2026-00042` but come from a **single global PostgreSQL sequence**
(`CACHE 1`, year as display only). The obvious `MAX(rma_number) + 1` is wrong under
concurrency: two requests read the same maximum and one collides. `RmaNumberSequence` handles
allocation, migration of an existing database, and post-seed realignment so seeded demo rows
and the sequence can never disagree.

## AI providers

Set `Ai:Provider` to `openai`, `openai-compatible`, `ollama`, `lmstudio`, or `llamacpp`.
`openai-compatible` requires an absolute `BaseUrl` and a positive timeout, validated at
startup rather than at first call. `ApiKey` stays optional for the local runtimes.

## Running it

The checked-in configuration ships a placeholder connection string, so the connection has to be
supplied before anything that touches the database will work. For the compose database that is
`rmapassword` on `localhost`:

```bash
export ConnectionStrings__Rma='Host=localhost;Port=5432;Database=rmadev;Username=rmauser;Password=rmapassword'

docker compose up -d                     # PostgreSQL on :5432
dotnet run --project tools/DbAdmin -- migrate
dotnet run --project tools/DbAdmin -- seed
dotnet run --project src/AIEnabledRma.Web        # wizard on :5xxx
dotnet run --project src/AIEnabledRma.Mcp        # MCP tool server
```

`DbAdmin` reads the same variable, so exporting it once covers all four commands. If you set the
variable for only some of them, the ones without it will fail on their first query rather than at
startup.

The PostgreSQL-backed tests read a separate variable, and skip rather than fail when it is unset
or unreachable, so a developer without the compose database still gets a green build:

```bash
export RMA_TEST_CONNECTION='Host=localhost;Port=5432;Database=postgres;Username=rmauser;Password=rmapassword'
dotnet test
```

`WebWizardTests` uses that connection too, and boots the real host to walk the wizard end to end
against a throwaway database. It is the only test that would have caught the wizard persisting
nothing, because every other suite either calls the workflow with a fake unit of work or tests
one piece in isolation.

### Containers

Both images are built from the repository root, because the Dockerfiles copy the shared build
props and sibling projects:

```bash
docker build -f src/AIEnabledRma.Web/Dockerfile -t aienabledrma-web .
docker build -f src/AIEnabledRma.Mcp/Dockerfile -t aienabledrma-mcp .
docker run -p 8080:8080 -e ConnectionStrings__Rma="$ConnectionStrings__Rma" aienabledrma-web
```

## Health

`GET /health` reports the two things that fail independently and both look like "the assistant
is broken" from the customer's side: the loaded knowledge base, including per-file load errors,
and the configured AI provider.

It does **not** check the database. The handler takes an `RmaDbContext` but never queries it, so
an unreachable database still reports `"status":"ok"`. Until that is added, do not wire this
endpoint into a liveness or readiness probe and read a green result as "the app can serve
returns".

## Configuration

`ConnectionStrings:Rma` must be supplied via user secrets, an environment variable
(`ConnectionStrings__Rma`), or a secret store. Never commit a connection string containing a
real password. Everything else — policy tiers, excluded causes, triage limits, scope guard,
AI provider — binds from `appsettings.json` and can be overridden per environment.

The bundled payment gateway (`Payments:Fake` behaviour in `FakePaymentGateway`) holds
authorisations in memory, honours the immediate-versus-manual-capture split, and **accepts
every positive amount**. The product decision is to exercise the payment plumbing in
development rather than simulate who gets approved, so nothing the wizard posts is declined
(short of a negative amount, which is a defect). The resulting authorisations are exposed on
the diagnostics endpoint. Replace the `IPaymentGateway` registration with a real provider
before taking money.
