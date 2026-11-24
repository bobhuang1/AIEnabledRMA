using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Tools.DbAdmin;

namespace AIEnabledRma.Tests;

/// <summary>
/// `DbAdmin review-queue` is the only way an operator without a browser sees what the nightly
/// reconciliation filed, so its lines are pinned: every item has to carry the return, the
/// transaction and both stored sums, and a queue the bound cut short must not read like the whole one.
/// </summary>
public sealed class DbAdminReviewQueueReportTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task The_queue_names_the_return_the_transaction_and_both_sums_of_each_open_item()
    {
        var returnId = Guid.NewGuid();
        var queued = new FakeReviewQueueRepository(
            [
                Item(
                    ReviewSources.PaymentMismatch,
                    PaymentMismatchReason.For(PaymentStatus.Captured),
                    returnId,
                    expected: (70m, "EUR"),
                    provider: (69m, "EUR")),
                Item(ReviewSources.TriageLowConfidence, "The model was not sure", rmaRequestId: null),
            ]);

        var report = await ReviewQueueReport.ReadAsync(
            queued,
            new FakeRmaRepository { Requests = { Return(returnId, "RMA-2026-00007", "pi_123") } },
            ReviewQueueReport.DefaultTake,
            Ct);

        Assert.Equal(
        [
            "  open:        2",
            "  mismatch:    1",
            "  RMA-2026-00007: payment-mismatch PaymentMismatch:Captured; transaction pi_123; "
            + "expected 70.00 EUR; provider holds 69.00 EUR",
            // An item that names no return is one a policy refusal filed, and saying so beats a
            // blank that reads like a lookup that failed.
            "  (no return): triage-low-confidence The model was not sure; transaction unknown; "
            + "expected unknown; provider holds unknown",
        ],
            ReviewQueueReport.Format(report));

        Assert.Equal(
            "2 open item(s) waiting; 1 of them a payment mismatch; this command only lists them.",
            ReviewQueueReport.Summary(report));
    }

    /// <summary>
    /// A bounded run that stopped early must not read like the whole queue, and the bound has to be
    /// the one the operator asked for: the reader looks one row past it, which is the only evidence
    /// anything was left out.
    /// </summary>
    [Fact]
    public async Task A_queue_the_bound_cut_short_says_so_and_the_reader_looked_one_row_past_it()
    {
        var queued = new FakeReviewQueueRepository(
            [
                Item(ReviewSources.StartRefused, "A return for this unit is already on its way", null),
                Item(ReviewSources.PolicyHumanReview, "Fraud indicator on the serial", null),
            ]);

        var report = await ReviewQueueReport.ReadAsync(queued, new FakeRmaRepository(), take: 1, Ct);

        Assert.Single(report.Rows);
        Assert.Equal(2, Assert.Single(queued.Takes));
        Assert.True(report.More);

        Assert.Contains(
            "  more:        --take cut the queue short; re-run with a larger --take to see the rest.",
            ReviewQueueReport.Format(report));
        Assert.Equal(
            "1 open item(s) waiting; the --take bound cut the queue short; this command only lists them.",
            ReviewQueueReport.Summary(report));
    }

    [Fact]
    public async Task An_empty_queue_says_nothing_is_waiting_rather_than_printing_zeros()
    {
        var report = await ReviewQueueReport.ReadAsync(
            new FakeReviewQueueRepository([]),
            new FakeRmaRepository(),
            ReviewQueueReport.DefaultTake,
            Ct);

        Assert.Equal(["  open:        0", "  mismatch:    0"], ReviewQueueReport.Format(report));
        Assert.Equal("No open review items.", ReviewQueueReport.Summary(report));
    }

    /// <summary>
    /// An amount whose currency was never recorded is not money, so it prints as nothing recorded:
    /// a bare 69.00 on the line would be read as a sum and compared with one from another currency.
    /// </summary>
    [Fact]
    public void An_amount_without_its_currency_is_shown_as_no_sum_at_all()
    {
        var row = new ReviewQueueRow(
            new ReviewItem
            {
                Id = Guid.NewGuid(),
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
                Source = ReviewSources.PaymentMismatch,
                Reason = PaymentMismatchReason.For(PaymentStatus.Captured),
                ExpectedAmount = 70m,
                ExpectedCurrencyCode = "EUR",
                ProviderAmount = 69m,
                ProviderCurrencyCode = null,
            },
            RmaNumber: "RMA-2026-00008",
            TransactionId: "pi_8");

        Assert.Contains(
            "  RMA-2026-00008: payment-mismatch PaymentMismatch:Captured; transaction pi_8; "
            + "expected 70.00 EUR; provider holds unknown",
            ReviewQueueReport.Format(new OpenReviewQueueReport([row], More: false)));
    }

    private static ReviewItem Item(
        string source,
        string reason,
        Guid? rmaRequestId,
        (decimal Amount, string Currency)? expected = null,
        (decimal Amount, string Currency)? provider = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            Source = source,
            Reason = reason,
            RmaRequestId = rmaRequestId,
            ExpectedAmount = expected?.Amount,
            ExpectedCurrencyCode = expected?.Currency,
            ProviderAmount = provider?.Amount,
            ProviderCurrencyCode = provider?.Currency,
        };

    private static RmaRequest Return(Guid id, string rmaNumber, string? transactionId) =>
        new()
        {
            Id = id,
            RmaNumber = rmaNumber,
            Status = RmaRequestStatus.AwaitingShipment,
            Kind = RmaRequestKind.PaidRepair,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
            PaymentTransactionId = transactionId,
            DepositAmount = 69m,
            CurrencyCode = "EUR",
        };

    /// <summary>
    /// The queue as the command reads it: open items, oldest first, bounded. It records the bound it
    /// was asked for because that number is what proves the reader looked one row past it.
    /// </summary>
    private sealed class FakeReviewQueueRepository(IEnumerable<ReviewItem> items) : IOperationsRepository
    {
        private List<ReviewItem> Items { get; } = [.. items];

        public List<int> Takes { get; } = [];

        public Task<IReadOnlyList<ReviewItem>> OpenReviewItemsAsync(int take, CancellationToken cancellationToken)
        {
            Takes.Add(take);

            return Task.FromResult<IReadOnlyList<ReviewItem>>(Items.Take(take).ToList());
        }

        public Task<ReviewItem?> GetReviewItemAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Items.FirstOrDefault(item => item.Id == id));

        /// <summary>Not what this test is about; the sweep ledger has its own tests.</summary>
        public Task<SweepRun?> LastSweepAsync(string source, CancellationToken cancellationToken) =>
            Task.FromResult<SweepRun?>(null);

        public Task<IReadOnlyList<RmaRequest>> RequestsInStatusAsync(
            IReadOnlyCollection<RmaRequestStatus> statuses,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RmaRequest>>([]);

        public Task<IReadOnlyList<OperatorAction>> RecentActionsAsync(int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OperatorAction>>([]);

        public void AddAction(OperatorAction action)
        {
        }
    }
}
