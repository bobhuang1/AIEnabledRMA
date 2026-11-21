using AIEnabledRma.Data;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Tests;

/// <summary>
/// Exercises RMA reference allocation against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// The bug these tests exist for was a read-then-increment: the repository selected the highest
/// stored number and returned that plus one. A fake repository cannot reproduce it, because the
/// race lives in the gap between the SELECT and the INSERT and only a real server with real
/// concurrency can lose it. So these tests talk to PostgreSQL, in a throwaway database that is
/// created and dropped within the test.
/// </remarks>
public sealed class RmaNumberSequenceTests : IAsyncLifetime
{
    private readonly LiveDatabase _database = new("rma_seq_test");

    public async ValueTask InitializeAsync() => await _database.InitializeAsync();

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Concurrent_callers_are_never_handed_the_same_reference()
    {
        await using (var warmup = CreateContext())
        {
            await warmup.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        const int callers = 64;
        var gate = new TaskCompletionSource();

        // One context per caller. A DbContext is not thread-safe, and in production each HTTP
        // request gets its own scope anyway, so sharing one would test EF's concurrency guard
        // rather than the numbering.
        //
        // Every call is released at once, so the requests genuinely overlap instead of
        // trickling through one at a time. A read-then-increment implementation returns the same
        // value to all of them here and the assertion fails.
        var tasks = Enumerable.Range(0, callers)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;

                await using var db = CreateContext();
                return await new RmaRepository(db).NextRmaNumberAsync(
                    new DateOnly(2026, 5, 1),
                    CancellationToken.None);
            }))
            .ToArray();

        gate.SetResult();
        var numbers = await Task.WhenAll(tasks);

        Assert.Equal(callers, numbers.Distinct(StringComparer.Ordinal).Count());
        Assert.All(numbers, n => Assert.StartsWith("RMA-2026-", n, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Allocation_continues_past_a_number_that_is_already_stored()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        // The demo seeder writes RMA-2026-00001. If the sequence were left at its starting value
        // the first genuine return would collide with it, which is exactly the regression the
        // alignment step exists to prevent.
        await db.Database.ExecuteSqlRawAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            sql:
            """
            INSERT INTO rma_requests (
                id, rma_number, status, kind, currency_code,
                shipping_charge, deposit_amount, created_at_utc, updated_at_utc)
            VALUES (
                gen_random_uuid(), 'RMA-2026-00001', 0, 0, 'USD',
                0, 0, now(), now())
            """);

        await RmaNumberSequence.AlignAsync(db, TestContext.Current.CancellationToken);

        var next = await new RmaRepository(db).NextRmaNumberAsync(
            new DateOnly(2026, 5, 1),
            CancellationToken.None);

        Assert.NotEqual("RMA-2026-00001", next);
        Assert.Equal("RMA-2026-00002", next);
    }

    [Fact]
    public async Task Alignment_does_not_rewind_a_sequence_that_has_already_advanced()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var repository = new RmaRepository(db);
        var today = new DateOnly(2026, 5, 1);

        var first = await repository.NextRmaNumberAsync(today, CancellationToken.None);

        // Realigning happens on every seed and reset. If it recomputed from the table alone it
        // would hand back a reference that had already been issued.
        await RmaNumberSequence.AlignAsync(db, TestContext.Current.CancellationToken);
        var second = await repository.NextRmaNumberAsync(today, CancellationToken.None);

        Assert.Equal("RMA-2026-00001", first);
        Assert.Equal("RMA-2026-00002", second);
    }

    private RmaDbContext CreateContext() => _database.CreateContext();
}
