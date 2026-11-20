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
| 2 | P1 MCP data surface | In progress | Shared `CustomerMasking`, `McpToolCall` entity, `mcp_tool_calls` migration and `EfToolAuditSink` landed. Left: masked MCP projection, exact-only contact search, call-tool filter (caller, budgets, audit), `reveal_customer_contact`, tests. |
| 2 | P3 Redact before model calls | Not started | |
| 2 | P8 Wizard hardening | Not started | |
| 3 | P4 Secure KB ingestion | Not started | |
| 3 | P2 Remote MCP + OAuth | Not started | |
| 4 | P5 Vector store + chunking | Not started | |
| 5 | P6 CRM case ingestion | Not started | |
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
