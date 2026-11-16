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
| `AX2-0001-0002` | Return already on its way | Human review: duplicate request |
| `NO-SUCH-SERIAL-0000` | Unknown | Refused: not in the catalog |

Use customer `j.smith@example.com` (a seeded account). Typing just `Smyth` exercises the
ambiguous-identity path; `j.smith@example.com` resolves to exactly one customer.

> The seeder expresses every warranty and shipping window relative to the day it runs, so the
> table above holds whenever the demo is seeded. These are stored dates, not rules, so a
> database seeded months ago drifts as the wall clock moves — re-anchor it with
> `dotnet run --project tools/DbAdmin -- reset --force`. The live wizard suite does not rely on
> the calendar at all: it seeds from a fixed anchor and pins the host clock to it.

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
| `tests/AIEnabledRma.Tests` | 253 tests, incl. live PostgreSQL sequence and end-to-end wizard suites. |

The Web and MCP hosts call the same `AddRmaData` registration, so the fuzzy-match thresholds an
agent's lookup tool uses cannot drift apart from the ones the tests exercise. The RAG host
serves the knowledge index and references only the domain project, so it opens no database.

The customer wizard is the deliberate exception. Its intake calls `LookupExactAsync`, which
accepts only a serial number or alternate identifier (MAC, IMEI) the caller typed in full — no
fuzzy scoring, no catalog scan. Fuzzy intake was an enumeration oracle: an anonymous visitor
could walk the catalog a serial prefix at a time, and every hit opened a return against the
real unit, which then blocked its owner as a duplicate.

Intake also counts only returns that have actually committed the unit — `AwaitingShipment`
through `Repaired`. A return someone is still filling in used to count, which meant anyone who
knew a serial could hold a unit by starting a return and walking away; the owner was then turned
away as a duplicate. Confirmation re-checks the unit with its own id excluded, so two returns for
one unit cannot both reach `AwaitingShipment`. Agent paths (`lookup_device`,
`check_coverage`, `get_fee_quote`) keep fuzzy resolution, because that caller is supposed to
already hold partial information.

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
   is the default AI provider, so no API key is needed. The description is also re-checked
   against the deterministic excluded-cause rules (`RmaPolicy:ExcludedCauses` →
   `RmaPolicyEvaluator`), so liquid or impact damage reported here is refused now rather than
   covered, because intake runs before any fault description exists.
3. **Problems** (`/RmaWizard/Problems/{id}`) — per-line detail for the bench. These descriptions
   are re-evaluated by the same rules as the triage statement, so a cause that only appears
   here can still refuse the return or turn it into a priced repair.
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

For the remote transport — the same tools behind a Keycloak-issued token — see
[`docs/mcp.md`](docs/mcp.md). It starts the realm from
`infra/keycloak/realm-aienabledrma.json` with `docker compose up -d keycloak`, mints a token for
the `mcp-agent` client, and ends with a `200` from `/mcp` and a `401` without one.

Tools:

| Tool | What it does |
| --- | --- |
| `check_coverage` | Policy verdict for a serial on a given date (`Eligible`, `EligibleForPaidRepair`, `RequiresHumanReview`, `NotEligible`) + explanation + rule trace |
| `get_fee_quote` | Repair price for a device from the same per-product price table the wizard bills from |
| `lookup_device` | Fuzzy catalog lookup by serial or alternate identifier |
| `find_customer` | Customer lookup by partial name, or confirmation by complete email or phone. Every field is masked |
| `get_customer_addresses` | A customer's saved addresses, each reduced to label, postcode prefix and country |
| `get_customer_returns` | A customer's return history, so an agent sees existing requests before adding one |
| `resolve_shipping_address` | Matches free text to a customer's stored addresses, never claiming a weak match is confident, and returns the match masked |

Everything the agent sees about a person comes from `CustomerProjection`, the same masking the
wizard uses: first name and surname initial, `j•••@e•••.com`, the last two phone digits, and an
address reduced to its label, postcode prefix and country. `externalCustomerId` is not exposed.
Name searches stay fuzzy — a name is not an identifier — but email and phone must be complete and
match exactly, because a partial contact search is an enumeration oracle however well the answer
is masked; terms shorter than three characters are refused outright.

Every call of every MCP tool — these four, the device lookups, and the policy tools — writes a row
to `mcp_tool_calls` (tool, caller, argument hashes rather than values, result count, latency) and
counts against a per-caller limit of 60 calls a minute, configurable under `Mcp:Budget` — one
caller is the process on stdio and the token's client and subject on the HTTP transport. Both happen
at one choke point on the `tools/call` request rather than inside each tool, so a tool added later
is audited and throttled without anyone remembering to wrap it; a refused call is recorded before
it reaches the tool. On top of that, the customer tools above are limited to 200 customer lookups
per caller, because that cap is about what a call is for rather than how many there are.
Exposing contact data to an authenticated support role still needs a role check; the identity
provider the transport uses (P2) identifies a client, not a person.

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

## CI cost

Hosted-runner minutes are billed per account, so the pipeline is deliberately narrow. These
rules hold until someone changes the workflow on purpose:

- **Pull requests and manual runs only.** A commit on `main` is a commit a pull request already
  built; running the same job again on push pays twice for one signal. `workflow_dispatch` covers
  the "verify main now" case when it is wanted.
- **Documentation-only changes start no run.** `paths-ignore` covers root-level markdown
  (`README.md`, `plan.md`, the licence) and `docs/`. `knowledge-base/**/*.md` is deliberately not
  ignored: those files are data the retrieval tests read, so editing one can change what the suite
  sees. If the check ever becomes required for merging, replace the filter with a job that starts
  and exits early: a path-filtered run leaves the check absent, not green.
- **Superseded runs are cancelled** for the same ref, so a fixup push does not also pay for the
  run it replaced, and every job has a hard timeout so a hung test cannot bill indefinitely.
- **No matrix, no second operating system, no extra job** without a sentence in the pull request
  saying what the added minutes buy.
- **Local first.** `dotnet test` runs the whole suite against a local PostgreSQL in about four
  minutes; CI is a gate for what already passed locally, not the first place a change runs.

## Containers

Build and run the two shipped Dockerfiles:

```bash
docker build -f src/AIEnabledRma.Web/Dockerfile -t aienabledrma-web .
docker build -f src/AIEnabledRma.Mcp/Dockerfile -t aienabledrma-mcp .
docker run -p 8080:8080 -e ConnectionStrings__Rma="$ConnectionStrings__Rma" aienabledrma-web
```

## Configuration

Bind via `IConfiguration`, i.e. `appsettings.json` or environment overrides
(`RmaPolicy__...`). The important tree:

```
RmaPolicy:
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
    AllowInProduction: false # the accept-everything gateway exists for Development only

Wizard:
  CapabilityExpiryDays: 30   # an unconfirmed request id stops working after this

Rag:
  Admin:
    ApiKey: ""               # required outside Development; the host refuses to start without it
  KnowledgeBase:
    MaxFileBytes: 262144     # an article is prose, not a dataset
    MaxArticles: 2000
    MaxFrontMatterCharacters: 4096
    RequireReview: false     # always on in Production, whatever this says
```

`Rag:Admin:ApiKey` is the operator credential, presented as `X-Rag-Admin-Key`: it guards the RAG
host's `/rag/reload` and the detailed `/health`, and the Web host's own `/health` counts. The Web
host refuses to start outside Development when it is empty, because without a key those endpoints
admit any loopback caller — and behind a reverse proxy on the same machine every caller is loopback,
so a keyless Production would publish the corpus size, the load errors and the model name to whoever
can reach the proxy. In Development an empty key admits loopback only, which is why the value is
blank here and why a deployment sets it from its secret store rather than this file.

## Why the AI is advisory

A return touches a customer's money, a company's stock, and a warranty obligation. A model
that can approve a return is a model that can be talked into approving a return:

- `RmaPolicyEvaluator` is the only thing that decides eligibility, tier, and money. It is
  ordinary, testable C# with no model in the call path.
- The model contributes a troubleshooting verdict and a step list, stored on the line as
  advisory text.
- A model timeout, malformed output, empty retrieval, or low confidence all fall back to
  `RequiresHumanReview`. There is no path where a model failure becomes an approval.
- `ScopeGuard` refuses questions outside the vocabulary its own corpus declares rather than
  guessing: the topic words are derived from the knowledge base's categories and keywords, not
  maintained as a second list that drifts from it.
- When triage resolves the fault by troubleshooting, the RMA is **cancelled**, not approved.

## What the model is allowed to see

Customer text is redacted before it leaves the process (`AIEnabledRma.Privacy`), because an
MCP-free, vendor-free architecture still ends at somebody else's server:

- Email, phone numbers, Luhn-valid card numbers, postal addresses, dashed identifiers such as
tax ids, and serial- or order-looking tokens are replaced with placeholders — `[EMAIL_1]`,
`[ADDRESS_1]` — numbered in order of first appearance, so the same value keeps the same
placeholder for the length of a conversation and the model can still follow "the one above".
- The name on the return is redacted too, from the record rather than from a guess: a pattern
cannot tell a person's name from any other capitalised pair.
- The device serial, SKU and product stay, because the policy question is about a specific
item and a redacted serial would make the answer useless. The caller states which values are
protected rather than leaving it to the detector to guess.
- The reply is scanned before it is shown or stored. One that repeats an identifier is
replaced by the fail-closed result and the case goes to a human.
- Logs carry counts by kind (`email=1`), never the values. A log line that names the email is
the leak this exists to stop, and logs travel further than prompts.

This is pattern-based, with no NER model: it catches the identifiers the system knows the shape
of, which is the set that appears in fault statements. It is not a guarantee about free text
nobody has seen yet, and the plan's local NER pass is still open.

## What the corpus is allowed to add

The knowledge base is hand-written and code-reviewed, but it is still text going into a
prompt, so the loader treats it as untrusted input with a known shape:

- **Refused and reported**, never silently dropped: files over `MaxFileBytes`, front matter
  over `MaxFrontMatterCharacters`, more than `MaxArticles` articles, non-UTF-8 bytes, symlinks
  and paths that resolve outside the corpus root.
- **Provenance**: each article carries `reviewed_by`, `reviewed_at` and a SHA-256 content hash,
  so "the article is what was reviewed" is checkable. Production refuses any article without
  it.
- **Sanitised before indexing**: HTML tags and comments, markdown and bare link targets, and
  lines that address the model (`ignore previous instructions`, role markers, prompt-reveal
  requests) are removed or neutralised. This covers the **title**, which is rendered into the
  prompt as a heading, as well as the body: an instruction hidden in a title is neutralised the
  same way, and an article whose title is nothing but an instruction is refused. A keyword that
  had to be neutralised is dropped rather than indexed. Every removal is counted on the article
  and logged as a count, never as the text.
- **Delimited in the prompt**: the pipeline wraps each hit in `<article>` and
  `<article_body>`, and the system prompt declares both blocks data rather than instructions.

## Paid repairs

An out-of-warranty unit is neither refused nor treated as free: it is offered as a **paid
repair** with its own wizard step. In-warranty units (including the grace period) are replaced
or repaired free. Refusal happens only outside the return window, on an excluded cause, or
for an unknown device.

- The charge comes from a per-product price table (`RmaPolicy:RepairPrices`), matched by SKU then
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

- An email or phone lookup always answers with the same account chooser, whether it matched
  one account, several, or none. Attaching on an exact hit and showing a chooser only for a
  near miss made the redirect itself the answer to "is this address on file?". Candidates
  are **masked** (`Jane S.`, `j•••@e•••.com`, `••• 77`), the exact match is offered first,
  and only an account from that server-issued list can be picked — or the visitor continues
  without one.
- An unconfirmed request expires after `Wizard:CapabilityExpiryDays` (30 by default) and
  every step then answers **410 Gone**. The GUID is the capability, so the link has to stop
  working eventually.
- Every response carries `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
  `X-Frame-Options: DENY` and a CSP that allows nothing but the site's own CSS and JS, with
  `frame-ancestors 'none'`. A clickjacked confirm button is a confirmed return.
- Stored addresses are shown masked (label plus `•••, 62••• US`) on the shipping and
  confirmation steps.
- The payment currency comes from the price table (`RmaPolicy:RepairPrices`), never from
  the browser.
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
exercise the payment plumbing, not simulate who gets approved. It is registered only in
Development: outside Development the host refuses to start unless
`Payments:Fake:AllowInProduction` is explicitly set to `true`. Replace the `IPaymentGateway`
registration with a real provider before taking money.

## Health

`GET /health` reports the two things that fail independently and both look like "the assistant
is broken" to a customer: the loaded knowledge base (including per-file load errors) and the
configured AI provider. It does **not** check the database. Do not wire this endpoint into a
liveness or readiness probe and read a green result as "the app can serve returns".

## License

This project is free software, released under the **GNU General Public License v3.0**. You may redistribute and/or modify it under those terms; see [LICENSE.md](LICENSE.md) for the full text.
