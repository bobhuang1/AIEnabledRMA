using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Tools.DbAdmin;

namespace AIEnabledRma.Tests;

/// <summary>
/// `DbAdmin reconcile-payments` is the only thing that asks the provider what became of an
/// authorisation nobody reported on, so what it prints is pinned: an operator reads the counts and
/// the sentence under them, and a run with no drift has to look different from one that filed items.
/// </summary>
public sealed class DbAdminReconcileReportTests
{
    [Fact]
    public void The_report_names_every_disagreement_with_both_sides_of_it()
    {
        var report = new PaymentReconciliationReport(
            Checked: 4,
            Agreed: 3,
            Disagreements:
            [
                new PaymentDisagreement(
                    Guid.NewGuid(),
                    "RMA-2026-00007",
                    "pi_123",
                    Expected: PaymentStatus.Authorized,
                    Provider: PaymentStatus.Captured,
                    LocalStatus: "Approved"),
            ],
            Unanswered: []);

        Assert.Equal(
        [
            "  checked:     4",
            "  agreed:      3",
            "  disagreed:   1",
            "  unanswered:  0",
            "  RMA-2026-00007: provider says Captured, this database expects Authorized (Approved), pi_123",
        ],
            ReconcilePaymentsReport.Format(report));

        Assert.Equal("1 disagreement(s) filed for review.", ReconcilePaymentsReport.Summary(report));
    }

    [Fact]
    public void A_clean_run_says_so_rather_than_printing_zeros()
    {
        var report = new PaymentReconciliationReport(2, 2, [], []);

        Assert.Equal(
            "Every stale authorisation matches the provider.",
            ReconcilePaymentsReport.Summary(report));
    }

    [Fact]
    public void A_provider_that_could_not_be_asked_is_not_reported_as_agreement()
    {
        var report = new PaymentReconciliationReport(
            Checked: 1,
            Agreed: 0,
            Disagreements: [],
            Unanswered: ["RMA-2026-00009 (pi_9): That authorisation could not be found."]);


        var lines = ReconcilePaymentsReport.Format(report);

        // The unanswered transaction is listed, and the summary does not claim the run was clean.
        Assert.Contains(
            "  ? RMA-2026-00009 (pi_9): That authorisation could not be found.",
            lines);
        Assert.Contains("could not be asked", ReconcilePaymentsReport.Summary(report), StringComparison.Ordinal);
    }
}
