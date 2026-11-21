using AIEnabledRma.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AIEnabledRma.Mcp;

/// <summary>
/// Keeps the budget in PostgreSQL, so every replica of the transport spends the same allowance.
/// </summary>
/// <remarks>
/// The earlier counter was a dictionary in the singleton <see cref="CallBudget"/>. That is one
/// allowance per process, and a process is the one thing a deployment is free to duplicate: with two
/// replicas each caller gets two full allowances, then three, and the limit stops being a limit
/// while every replica's own numbers look correct. The rows of <c>mcp_call_stamps</c> are the
/// counter instead — they are already the shared thing this host has, the database, and the audit
/// trail lives in the same one.
///
/// Two details make it exact rather than approximate:
/// <list type="bullet">
/// <item>the caller's rows are locked with a transaction-scoped advisory lock before they are
/// counted, so two replicas answering the same caller at the same instant cannot both see room and
/// both take it. The lock is per caller, so separate agents do not queue behind each other;</item>
/// <item>the count, the insert and the refusal are inside one transaction, so a refusal leaves no
/// stamp behind. A refused call must not lengthen the window of the calls that follow it.</item>
/// </list>
///
/// A transaction per call is a real cost — one round trip plus a lock — but this counter guards data
/// disclosure, and a second round trip is cheaper than a budget that only exists per process. It is
/// also the same budget both transports use, so stdio and HTTP cannot drift apart.
/// </remarks>
public sealed class PostgresCallMeter(IServiceScopeFactory scopes) : ICallMeter
{
    public async Task<bool> TryTakeAsync(
        string caller,
        string kind,
        int limit,
        DateTimeOffset now,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RmaDbContext>();

        // The context is registered with EnableRetryOnFailure, and an explicit transaction has to
        // run inside the retrying strategy or EF refuses it outright.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // hashtext is stable for the life of the database, and this lock is scoped to the
            // transaction: two calls for one caller serialise, different callers do not.
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({caller}))",
                cancellationToken);

            // Ageing out here, rather than on a timer, means no background job has to run for the
            // window to move: a caller that never calls again leaves one window's rows, and the
            // next caller-wide sweep removes them if it ever returns.
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM mcp_call_stamps WHERE caller = {caller} AND kind = {kind} AND at_utc <= {now - window}",
                cancellationToken);

            var taken = await db.Database
                .SqlQuery<long>(
                    $"SELECT count(*) AS \"Value\" FROM mcp_call_stamps WHERE caller = {caller} AND kind = {kind}")
                .SingleAsync(cancellationToken);

            if (taken >= limit)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO mcp_call_stamps (id, caller, kind, at_utc) VALUES (gen_random_uuid(), {caller}, {kind}, {now})",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }
}
