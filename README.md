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
Revisit before the wizard gains authenticated pages, an operator portal, or multiple users
per return.

## RMA numbering

References look like `RMA-2026-00042` but come from a **single global PostgreSQL sequence**
(`CACHE 1`, year is display-only). The obvious `MAX(rma_number) + 1` is wrong under
concurrency: two requests read the same maximum and one collides. `RmaNumberSequence` handles
allocation, migration of an existing database, and post-seed realignment so seeded demo rows
and the sequence can never disagree.

## Payments (development)

The bundled gateway (`FakePaymentGateway`) holds authorisations in memory, honours the
immediate-versus-manual-capture split, and **accepts every positive amount** — the point is to
exercise the payment plumbing, not simulate who gets approved. Resulting authorisations are
exposed on the diagnostics endpoint. Replace the `IPaymentGateway` registration with a real
provider before taking money.

## Health

`GET /health` reports the two things that fail independently and both look like "the assistant
is broken" to a customer: the loaded knowledge base (including per-file load errors) and the
configured AI provider. It does **not** check the database. Do not wire this endpoint into a
liveness or readiness probe and read a green result as "the app can serve returns".

## License

This project is free software, released under the **GNU General Public License v3.0**. You may redistribute and/or modify it under those terms; see [LICENSE.md](LICENSE.md) for the full text.
