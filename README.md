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
| `tools/DbAdmin` | Migrate, seed, reset, prune and inspect the database from the command line. |
| `tests/AIEnabledRma.Tests` | The full suite, incl. live PostgreSQL sequence and end-to-end wizard suites. |

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
`reset --force` (drop schema, migrate, seed, realign the RMA number sequence), `stats`,
`prune` (delete audit rows past their retention window — see below).

### Retention

Four tables are audit trails rather than business records, so each has a window after which its rows
are deleted. `prune` is an operator-run command: it is not a background job in a host and not an HTTP
endpoint, for the same reason `migrate` and `seed` are not — a running site should never be a way to
delete production data.

| Table | Kept | Aged from |
| --- | --- | --- |
| `mcp_tool_calls` | 90 days | the call (`at_utc`) |
| `operator_actions` | 730 days — the default return window | the action (`at_utc`) |
| `review_items` | 90 days **after resolution** | `resolved_at_utc`; an open item is never pruned |
| `webhook_events` | 90 days | the delivery (`received_at_utc`) |

`DbAdmin stats` prints these four tables after the business ones, because the number an operator wants
before a prune run is how much has accumulated, not only how many returns exist. `--json` writes the
same counts as one object, in the same two groups (`{"business":{…},"audit":{…}}`, one line), so a
monitor can scrape how fast the audit tables fill between prune runs instead of parsing the padding;
the text view is unchanged, and is still the one to read.

```bash
dotnet run --project tools/DbAdmin -- stats                              # row counts, business and audit
dotnet run --project tools/DbAdmin -- stats --json                       # the same counts, one JSON object
dotnet run --project tools/DbAdmin -- prune                              # dry run: counts only
dotnet run --project tools/DbAdmin -- prune --apply                      # delete what the dry run counted
dotnet run --project tools/DbAdmin -- prune --webhook-events --apply     # one table at a time
```

The dry run is the default and prints the cutoff it would use and the rows it would lose per table, so
`--apply` is a decision about a number somebody has seen rather than a guess. Naming no table prunes
all four; naming one limits the run; an unrecognised flag is refused rather than treated as "no table
named", because with `--apply` that would quietly turn one table into the whole trail. Each delete is a
range scan on the column the table already indexes for time, and the count and the delete are built
from one query, so the report cannot promise a number the next command does not remove.

`operator_actions` is tied to the return window on purpose: an action explains a decision for as long
as that decision can be disputed, and keeping the return while deleting who moved it would leave the
evidence half gone. `review_items` is aged from resolution rather than creation, so an item opened long
ago and closed yesterday is one day old as far as the queue is concerned. A window of zero or less is
refused at startup of the command rather than honoured, because it is not a short retention period —
it deletes the row that was just written. Raising `RmaPolicy:ReturnWindowDays` in a deployment does
not move the retention window with it; the two are equal at their defaults, and moving one without the
other is a decision for whoever changes it.

### Reconcile payments

A webhook is the fast path: the provider reports a capture and the return moves. A delivery that was
lost, rejected or never configured leaves the opposite state — the money has moved at the provider,
the row still says the deposit is only authorised, and nothing records that anybody asked.
`reconcile-payments` asks.

```bash
# returns carrying a transaction, untouched for the six-hour default window
dotnet run --project tools/DbAdmin -- reconcile-payments
dotnet run --project tools/DbAdmin -- reconcile-payments --older-than 24 --take 50
```

It reads those returns, asks the gateway what each transaction became, and files a `payment-mismatch`
review item where the answer is not what the return's own status implies: captured for a return in
repair or beyond, voided for a cancelled one, still authorised before that. It never changes a
return — the operator reads the item and decides — and it files at most one open item per return, so
a nightly run does not bury the queue in the same sentence; resolving the item lets a later run file
it again. Exit code 2 means the provider could not be asked about something and nothing was filed for
it, which is a different answer from "nothing was wrong".

Against the bundled fake gateway every process starts with an empty authorisation book, so a
development run reports those transactions as unanswered rather than as disagreements — that is the
fake's memory, not the command's judgement; against Stripe the same run is the real check.

### Scheduled reconciliation

`reconcile-payments` only finds a lost delivery when it runs, so the production pack ships it as a
one-shot service and the deployment runs that on a timer. It cannot run from the host's crontab: the
pack's `postgres` publishes no port, and the sweep runs on the pack's network instead — a container
that exits when the sweep is done. It sits behind the `ops` profile, so `docker compose up` never
starts it, and it has no restart policy of its own, because a run that finished is not a run that
crashed.

```bash
# one sweep, by hand
docker compose -f docker-compose.prod.yml --env-file prod.env --profile ops run --rm reconcile-payments

# flags pass through
docker compose -f docker-compose.prod.yml --env-file prod.env --profile ops run --rm reconcile-payments --older-than 24 --take 200
```

Two units are in `deploy/`: `rma-reconcile.service` runs that one command, and `rma-reconcile.timer`
fires it nightly at 02:40 with `Persistent=true`, so a night the host was down is swept when it comes
back rather than skipped. Install them on the host that holds `prod.env`, adjusting `WorkingDirectory`
and `User` in the service to that checkout:

```bash
sudo cp deploy/rma-reconcile.service deploy/rma-reconcile.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now rma-reconcile.timer
systemctl list-timers rma-reconcile.timer      # when it next fires
journalctl -u rma-reconcile.service -n 50      # what the last run said
```

A crontab line does the same where there is no systemd, at the cost of the journal:

```cron
40 2 * * * cd /srv/aienabledrma && docker compose -f docker-compose.prod.yml --env-file prod.env --profile ops run --rm reconcile-payments >> /var/log/rma-reconcile.log 2>&1
```

What the run means: exit 0 is every transaction answering as the return implies, exit 2 is the
provider not answering about something — systemd records that as a failed unit, cron as a log line,
and it is the case worth reading rather than retrying. The default `--older-than` window is six hours,
so running it much more often than that mostly re-reads rows the previous run already answered;
mismatches arrive in the operator queue as one open `payment-mismatch` item per return, and resolving
one lets a later run file it again.

Every run is recorded in `sweep_runs`, because a clean review queue is what a healthy nightly
run and a timer that stopped a week ago both leave behind. `/health`'s gated details carry it
under `reconciliation` (`outcome`, `lastRunAtUtc`, `ageHours`, `stale`, `needsAttention`, and the
sentence in words), and the operator dashboard prints that sentence above the queue. A run the
provider left half-answered is recorded as failed rather than as a quiet night, a run that
crashed is recorded before the process exits, and no run at all reads as stale: 26 hours after
the last one, because the timer fires at 02:40 and a missed night is exactly what that silence
means. The sentence lives in one place, so the words an operator reads and the fields a monitor
scrapes cannot disagree.

### Audit payments

`audit-payments` is the sweep that finds a capture or a void the two sides disagree about, whichever
side is wrong. It compares every return carrying a transaction with the provider and has no staleness
window, because the shape it exists for is a capture whose row write lost a race: the money moved, the
row still says `Received`, and that row was written seconds ago — the one case `reconcile-payments`
skips on purpose, since its window is what tells a lost delivery from a slow one.

```bash
dotnet run --project tools/DbAdmin -- audit-payments            # every return carrying a transaction
dotnet run --project tools/DbAdmin -- audit-payments --take 50  # the first 50, and the report says so
```

Each drifting line starts with the side that holds the capture or void. `provider only` means the
money moved and this database never recorded it; `row only` means the return claims a capture the
provider never took, which is a customer told they paid when they did not; `mismatch` means both hold
one and they are not the same. It files nothing and changes nothing — `reconcile-payments` is the
command that files review items — so exit code 3 means drift was found, 2 means the provider could not
be asked about something, and 0 means every transaction agreed. Against the bundled fake gateway every
process starts with an empty authorisation book, so a development run reports those transactions as
unanswered rather than as drift; against Stripe it is the real comparison.

### The review queue

The operator dashboard is where a review item is resolved, but a headless deployment has no
browser and no sign-in, and the payment mismatch the 02:40 sweep files is a row only a psql
session could see until now. `review-queue` prints the open items with the five facts one is
judged on: its source, its reason, the return it names, the transaction, and the two sums the
reconciliation run stored.

```bash
dotnet run --project tools/DbAdmin -- review-queue            # every open item, up to 100
dotnet run --project tools/DbAdmin -- review-queue --take 50  # the oldest 50, and the report says so
```

Each line ends in `expected` and `provider holds`, and says `unknown` where the item recorded
nothing rather than printing a bare amount or a zero: an item that is not about money has no sum,
which is a different answer from one that recorded 0.00. An item that names no return -- a start
the policy sent to a person creates none -- prints `(no return)` instead of a blank. The two
sums are there because the reason code cannot carry them: two equal states holding two different
amounts is money that moved unrecorded, and a queue that printed only the states would call it a
match. It reads and never resolves; the dashboard is still the only place that closes an item,
and it exits 0 whatever the queue holds, because items waiting is not a failure of the listing.

### Database backups

A dump on the host that holds the database is not a backup: it fails with the disk, the host and the
account that made it. `scripts/backup.sh` writes one to a destination outside the host, keeps a local
copy so a restore does not have to reach the network first, and prunes both to the retention window
in the same run.

```bash
BACKUP_REMOTE=s3:rma-prod-backups/postgres scripts/backup.sh backup   # dump, verify, encrypt, upload, prune
BACKUP_REMOTE=s3:rma-prod-backups/postgres scripts/backup.sh list     # names at the destination
BACKUP_REMOTE=s3:rma-prod-backups/postgres scripts/backup.sh fetch    # newest one back; name on stdout
BACKUP_REMOTE=s3:rma-prod-backups/postgres scripts/backup.sh purge-plaintext  # delete the old *.dump objects
```

`BACKUP_REMOTE` is an rclone destination, so one command covers object storage and a remote host:
`s3:bucket/prefix`, `b2:bucket/prefix`, `sftp:backup.example.com:/srv/rma`, or
`local:/mnt/offsite/rma` for a volume mounted from somewhere else. `local:` is a directory mounted
into the rclone container, which is the case the restore drill uses because it needs no credentials.
Object-store and SFTP credentials live in `rclone.conf` on the host (`RCLONE_CONFIG_DIR`, default
`/etc/aienabledrma`), never in `prod.env`: a storage key in the same file as the database password is
one file to leak instead of two, and `prod.env` is copied into every container.

The dump does not run on the host. `scripts/backup.sh` invokes it in the pack's own database
service -- `docker compose -f docker-compose.prod.yml --env-file prod.env exec -T postgres` --
because that server publishes no port and its container is where the credentials already are.
`COMPOSE_FILE` and `ENV_FILE` name another pack if the database lives in one. It is the only
route to the database, and the restore drill takes it too: the drill starts its throwaway server
as a `postgres` service, so the command the 01:20 timer runs is the command the gate proves, and
a service renamed on either side fails a test rather than a night's backup.

The destination holds ciphertext. Every dump is encrypted -- AES-256, PBKDF2 at 200,000
iterations, a fresh salt per run -- before it is uploaded, so a bucket, a backup host or a mounted
disk is unreadable to the people its own access policy admits, who are not the people allowed to
read this database. `fetch` decrypts the newest object back into `BACKUP_STAGING`, so a restore
gets a dump rather than having to know how it was wrapped. The key is `BACKUP_KEY_FILE`, default
`$RCLONE_CONFIG_DIR/backup.passphrase` -- a file beside `rclone.conf`, never a value in
`prod.env`, which is copied into every container:

```bash
openssl rand -base64 48 > /etc/aienabledrma/backup.passphrase
chmod 600 /etc/aienabledrma/backup.passphrase
```

Keep a copy of that file somewhere other than the destination. A dump without its passphrase is a
bucket of noise, and losing the key loses every copy at once. The script refuses to run without
it, and refuses a passphrase shorter than 16 characters, because a guessable key is the same as
none.

Two checks run before the file leaves. It must be larger than a kilobyte (an empty dump uploads
happily and is found at the restore), and `pg_restore --list` must read it (a file the restore tool
cannot open is not a dump). Either failing deletes the file, uploads nothing, and exits non-zero.

Retention is `BACKUP_KEEP_DAYS` (default 14) and applies on the destination and in `BACKUP_STAGING`
(default `/var/backups/aienabledrma`), so neither side grows without somebody deciding it should.
Names are `rma-YYYYmmddTHHMMSSZ.dump.enc`: fixed-width UTC, which is what makes "the newest" a
sort rather than a guess — and what the drill and `fetch` both rely on. An unencrypted
`*.dump` left by an earlier version is still inside the retention window, so it ages out instead
of staying readable at the destination for ever; `fetch` never returns one.

Aging out is not soon enough for a copy that is readable today. `purge-plaintext` lists the `*.dump`
objects at the destination, prints them, deletes exactly those, and asks the destination again to
prove they are gone; no `*.dump.enc` matches its pattern, so the backups stay. It needs no key — it
deletes rather than opens — which is what lets a host that has lost its passphrase still take the
plaintext off. The restore drill plants such a copy and fails unless the purge leaves no readable
dump behind.

`deploy/rma-backup.service` runs one backup and `deploy/rma-backup.timer` fires it nightly at 01:20,
before the reconciliation sweep, with `Persistent=true` so a night the host was down is not skipped.
Install on the host that holds `prod.env` and `rclone.conf`:

```bash
sudo cp deploy/rma-backup.service deploy/rma-backup.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now rma-backup.timer
systemctl list-timers rma-backup.timer      # when it next fires
journalctl -u rma-backup.service -n 50      # what the last run said
```

A nightly run that cannot reach its destination exits non-zero, so systemd reports the unit failed
the morning it happens. That is deliberate: a schedule that answers 0 while uploading nothing is the
failure this job exists to make impossible.

### Restore drill

A dump is a backup only if it can be restored into a database this application can migrate and read
back. `scripts/restore-drill.sh` proves that end to end, and it restores what the deployment's own
backup wrote off the host rather than a dump it made a moment earlier: throwaway PostgreSQL started as the pack's `postgres` service, so the dump goes through
`docker compose exec postgres` exactly as the timer's does, seeded
through `DbAdmin seed`, `scripts/backup.sh` run twice against a destination beside the drill (so
there is a newest copy to choose and a copy past the window to prune), a fetch with the wrong
passphrase that has to be refused, the local copies deleted, `fetch` bringing the newest one back
decrypted, `pg_restore --exit-on-error` into a second database, `DbAdmin
migrate` with nothing left pending, and every row count read back through the application's own
context and compared with the source.

```bash
scripts/restore-drill.sh            # or: scripts/verify-all.sh restore
```

It is the last step of `scripts/verify-all.sh` and the `restore-drill` job in CI, because migrations
are forward-only and `reset --force` drops the database rather than restoring it — a dump that cannot
be restored into a scratch database is the failure with no other way back. It costs minutes rather
than seconds (it publishes `DbAdmin` and moves a dump through two containers), so it runs last;
`KEEP=1` leaves the container and the dump behind for inspection. It also runs from
`.github/workflows/restore-drill-scheduled.yml` every night at 03:17 UTC and on demand, because a
backup path breaks from outside the repository — a republished base image, a dump format change — on a
day nobody edits anything; the run's summary carries the verdict, so a red night does not need a
reviewer to say what failed. The production pack's
`web`, `mcp` and `rag` services carry a readiness check that asks their own `/health` for an HTTP 200,
and `proxy` waits for `service_healthy` before it fronts them. The probe uses bash's `/dev/tcp`
rather than curl, because none of these images ships curl and a package installed into three
production images to answer one question is a cost already declined. The MCP host's `/health` is
unauthenticated and answers only `{"status":"ok"}`: a probe cannot present a token, and that host
has no corpus or model detail to name.

### Mutation testing

A green suite says the tests pass; it does not say they would notice a wrong branch. Every change in
this repository used to be mutation-checked by hand — break the code, watch a test go red, put it back
— which is exactly the step that disappears when a change is rushed. `scripts/mutation.sh` does it
mechanically over the types where a wrong branch is a wrong decision: the policy evaluator,
`OperatorService`, the payment reconciler, the retention pruner, the webhook ledger, and `PiiRedactor`.

```bash
scripts/mutation.sh                      # both projects, about half an hour
MUTATION_PROJECTS=privacy scripts/mutation.sh   # one of them, while working on it
```

It runs [Stryker.NET](https://stryker-mutator.io) inside the pinned SDK image (pinned in
`.config/dotnet-tools.json`), starts its own PostgreSQL because the operation suites are live, counts
what survived from the JSON report, and compares that number with `mutation/baseline.json`:

```
  killed:         168
  survivors:      102 (budget 102)
  never executed: 0 (budget 0)
```

The budget must not rise. A survivor is either a missing test or an equivalent mutant, and raising the
budget is a decision that belongs in the change that causes it, with the reason in the message — a
budget that moves silently is the hand-run step this replaced. Two numbers are compared: mutants no
test killed (`survivors`) and mutants no test even reached (`never executed`).

`.github/workflows/mutation.yml` runs it nightly at 03:17 UTC and on demand. It is deliberately not
part of per-push CI: it mutates hundreds of code paths, and the answer changes over days, not minutes.

## Run the web wizard

```bash
dotnet run --project src/AIEnabledRma.Web
```

Walk a return:

1. **Start** (`/RmaWizard/Start`) — enter customer + serial(s), one per line.
2. **Triage** (`/RmaWizard/Triage/{id}`) — describe the fault. A local `example-local` model
   is the default AI provider in Development, so no API key is needed. Production refuses it: a
   deployment must name a real provider (`Ai:Provider=openai-compatible`). The description is also re-checked
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
per caller per day, because that cap is about what a call is for rather than how many there are.
The counters live in PostgreSQL (`mcp_call_stamps`), not in the process, so the limit is the caller's
limit and not each replica's: two replicas of the HTTP transport spend one allowance, and a refused
call leaves no stamp behind.
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
with no environment still goes green. A gate does not: `scripts/verify-all.sh suite` and CI run the
same command against a service database and **fail the run when any test was skipped**, because a
suite that silently dropped its database tests answers a smaller question than the one it claims. The two live suites exist because concurrency semantics
(sequence allocation) and persistence (every wizard step reads what it wrote through a real
host) cannot be faked.

`WebWizardTests` boots the real web host against a throwaway database and walks the wizard
end to end, including the paid-repair path that lands on the payment step and bills the
per-product price.

## CI cost

Hosted-runner minutes are billed per account, so the pipeline is deliberately narrow. These
rules hold until someone changes the workflow on purpose:

- **Every push to `main`, every pull request, and manual runs.** Exempting `main` looked cheaper,
  but a merge is only pre-tested if the pull request actually ran, and this repository once had
  twenty commits reach origin without CI seeing them. `workflow_dispatch` is still there for a run
  on demand.
- **Documentation-only changes start no run.** `paths-ignore` covers root-level markdown
  (`README.md`, `plan.md`, the licence) and `docs/`. `knowledge-base/**/*.md` is deliberately not
  ignored: those files are data the retrieval tests read, so editing one can change what the suite
  sees. If the check ever becomes required for merging, replace the filter with a job that starts
  and exits early: a path-filtered run leaves the check absent, not green.
- **A superseded pull-request run is cancelled**, so a fixup push does not also pay for the run
  it replaced. A `main` run is never cancelled: its run is the only evidence that commit has. Every
  job has a hard timeout so a hung test cannot bill indefinitely.
- **Three jobs, each with its own reason.** `build-and-test` does restore, build, the EF
  model/migration comparison, the suite, the skip guard and the dependency audit. `auth-flows` boots
  the committed realm and drives the token flows. `runtime-images` builds the three images, boots
  both corpus hosts, starts the production pack's three application services and resolves the pack
  with `docker compose config`. No matrix, no second operating system, no fourth job without a
  sentence in the pull request saying what the added minutes buy.
- **Local first.** `dotnet test` runs the whole suite against a local PostgreSQL in about four
  minutes; CI is a gate for what already passed locally, not the first place a change runs.

## Containers

Build and run the three shipped Dockerfiles:

```bash
docker build -f src/AIEnabledRma.Web/Dockerfile -t aienabledrma-web .
docker build -f src/AIEnabledRma.Mcp/Dockerfile -t aienabledrma-mcp .
docker build -f src/AIEnabledRma.Rag/Dockerfile -t aienabledrma-rag .
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
  Provider:                  # example-local | openai-compatible; Production refuses example-local
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
- **Ranking:** the in-memory synonym-expanding lexical retriever stays the authority for articles (it is the
  measured baseline on `eval/retrieval-golden.json`); the store adds what it cannot see, chiefly redacted CRM
  cases, plus chunk-level snippets. With the hashing embedder both score recall@3 0.925 on the golden set.

### Choosing the embedding model

The default is a **local Ollama model**, so article and case text never leave the company:

```powershell
ollama pull nomic-embed-text      # once; or: ollama pull bge-m3
```

On a machine that uses the development compose file, the same server runs in a container behind the
`ollama` profile instead of on the host. `docker compose up` still starts only the database and the
identity provider; the profile publishes the server on `http://localhost:11434`, which is where the
default `Embeddings:BaseUrl` already points:

```bash
docker compose --profile ollama up -d ollama
docker compose exec ollama ollama pull nomic-embed-text   # once per volume
```

When hybrid retrieval is on (`Rag:VectorStore:Enabled=true`) and that server does not answer, each
query logs a warning naming the model and saying retrieval fell back to the lexical index — the answer
is lexical-only until the server answers, rather than failing or pretending the store was consulted.

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
token must carry the `rma:operator` role or scope. The committed development realm
(`infra/keycloak/realm-aienabledrma.json`) carries the confidential `rma-operator` client, the `rma:operator`
realm role and a mapper that flattens the user's realm roles into the flat `roles` claim this policy reads;
`scripts/verify-auth.sh` mints a token for `operator-demo` and asserts the claim, and that a user without the
role does not get it. For local development only, set
`OperatorAuth:DevSignIn=true` in `appsettings.Development.json` for a one-click `dev-operator` sign-in; the app
refuses to start with it in any other environment. In Production the host also refuses to start when
`OperatorAuth:Authority` and `ClientId` are missing: the dev sign-in is unavailable there, so `/Operator` would
otherwise be a sign-in page that can never complete.

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
exercise the payment plumbing, not simulate who gets approved. It is registered only in
Development: outside Development the host refuses to start unless
`Payments:Fake:AllowInProduction` is explicitly set to `true`. Replace the `IPaymentGateway`
registration with a real provider before taking money.

## Health

`GET /health` reports the two things that fail independently and both look like "the assistant
is broken" to a customer: the loaded knowledge base (including per-file load errors) and the
configured AI provider. It does **not** check the database. Do not wire this endpoint into a
liveness or readiness probe and read a green result as "the app can serve returns".

## Production

`docker-compose.prod.yml` runs the web host, the MCP server (HTTP) and the RAG host behind Caddy
(`deploy/Caddyfile`, automatic TLS); PostgreSQL with pgvector is not published. Copy
`deploy/prod.env.example` to `prod.env` (ignored by git) and fill it in; compose refuses to start while any
required value is missing.

- Base images are pinned by digest. All three images copy their lock files and restore with `--locked-mode`, so
  a dependency change that was not committed fails the image build instead of being resolved from the feed.
- App containers run as non-root with a read-only root filesystem, `no-new-privileges` and no capabilities.
- Caddy routes web traffic only while `/health/ready` (database reachable) answers.
- Database backups leave the host: `scripts/backup.sh` dumps through the pack's PostgreSQL image and
  copies the file to an rclone destination (`BACKUP_REMOTE`), pruning both sides to
  `BACKUP_KEEP_DAYS`; `deploy/rma-backup.timer` runs it nightly at 01:20.
- `scripts/verify-images.sh` starts this file's web service from `deploy/prod.env.example` (under its own
  project name, then removes it), so a value the example forgot, or a service the current host refuses, fails
  the gate instead of a deployment.
- In Production every host refuses a development database password, an empty password, or a placeholder
  from `deploy/prod.env.example` (`ProductionConfiguration`, `ProductionSecrets`). The tool server applies that
  rule from the services its stdio and HTTP transports share, and the RAG host applies the database half
  whenever a database is configured, so a lexical-only RAG host still starts. The same rule refuses the
  example env file's placeholder operator secret and Stripe keys, and every host refuses the bundled
  `example-local` model: `AI_PROVIDER` is required by the compose file with no default. The web host
  additionally refuses plain-`http` model or embedding endpoints, on top of its own rules for the fake
  gateway, the operator key and the corpus manifest.

For a demo on another day, `Demo:Today` (Development only, `yyyy-MM-dd`) runs the wizard on a fixed date.

## License

This project is free software, released under the **GNU General Public License v3.0**. You may redistribute and/or modify it under those terms; see [LICENSE.md](LICENSE.md) for the full text.
