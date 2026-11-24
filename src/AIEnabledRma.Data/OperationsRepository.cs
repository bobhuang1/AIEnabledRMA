using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Domain.Rma;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AIEnabledRma.Data;

public sealed class EfOperationsRepository(RmaDbContext db) : IOperationsRepository
{
    public async Task<IReadOnlyList<ReviewItem>> OpenReviewItemsAsync(int take, CancellationToken cancellationToken) =>
        await db.ReviewItems
            .Where(r => r.Status == ReviewStatuses.Open)
            .OrderBy(r => r.CreatedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

    public Task<SweepRun?> LastSweepAsync(string source, CancellationToken cancellationToken) =>
        db.SweepRuns
            .AsNoTracking()
            .Where(run => run.Source == source)
            .OrderByDescending(run => run.AtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<ReviewItem?> GetReviewItemAsync(Guid id, CancellationToken cancellationToken) =>
        db.ReviewItems.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<RmaRequest>> RequestsInStatusAsync(
        IReadOnlyCollection<RmaRequestStatus> statuses,
        int take,
        CancellationToken cancellationToken) =>
        await db.RmaRequests
            .AsNoTracking()
            .Include(r => r.Lines)
            .Where(r => statuses.Contains(r.Status))
            .OrderBy(r => r.UpdatedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OperatorAction>> RecentActionsAsync(int take, CancellationToken cancellationToken) =>
        await db.OperatorActions.AsNoTracking().OrderByDescending(a => a.AtUtc).Take(take).ToListAsync(cancellationToken);

    public void AddAction(OperatorAction action) => db.OperatorActions.Add(action);
}

/// <summary>Records that a scheduled sweep ran, through a short-lived context of its own.</summary>
public sealed class EfSweepLedger(IServiceScopeFactory scopes) : ISweepLedger
{
    public async Task RecordAsync(SweepRun run, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RmaDbContext>();
        db.SweepRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Files review items through a short-lived context of its own.</summary>
public sealed class EfReviewQueue(IServiceScopeFactory scopes) : IReviewQueue
{
    public async Task AddAsync(ReviewItem item, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RmaDbContext>();
        db.ReviewItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
    }
}
