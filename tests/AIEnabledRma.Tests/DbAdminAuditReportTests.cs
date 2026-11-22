using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Tools.DbAdmin;

namespace AIEnabledRma.Tests;

/// <summary>
/// `DbAdmin audit-payments` is the only command that compares every return carrying a transaction
/// with the provider, so its lines are pinned: an operator has to see which side holds the capture,
/// and a run that stopped early or could not ask has to look different from a clean one.
/// </summary>
public sealed class DbAdminAuditReportTests
{
    [Fact]
    public void Each_drift_names_the_side_that_holds_it_and_both_states()
    {
        var report = new PaymentAuditReport(
            Checked: 5,
            InStep: 2,
            Drift:
            [
                new PaymentDrift(
                    Guid.NewGuid(),
                    "RMA-2026-00011",
                    "pi_11",
                    PaymentDriftDirection.ProviderOnly,
                    Expected: PaymentStatus.Authorized,
                    Provider: PaymentStatus.Captured,
                    LocalStatus: "Received"),
                new PaymentDrift(
                    Guid.NewGuid(),
                    "RMA-2026-00012",
                    "pi_12",
                    PaymentDriftDirection.RowOnly,
                    Expected: PaymentStatus.Captured,
                    Provider: PaymentStatus.Authorized,
                    LocalStatus: "InRepair"),
                new PaymentDrift(
                    Guid.NewGuid(),
                    "RMA-2026-00013",
                    "pi_13",
                    PaymentDriftDirection.Mismatched,
                    Expected: PaymentStatus.Voided,
                    Provider: PaymentStatus.Captured,
                    LocalStatus: "Cancelled"),
            ],
            Unanswered: [],
            More: false);

        Assert.Equal(
        [
            "  checked:     5",
            "  in step:     2",
            "  drifted:     3",
            "  unanswered:  0",
            "  [provider only] RMA-2026-00011: provider says Captured, this database expects Authorized (Received), pi_11",
            "  [row only] RMA-2026-00012: provider says Authorized, this database expects Captured (InRepair), pi_12",
            "  [mismatch] RMA-2026-00013: provider says Captured, this database expects Voided (Cancelled), pi_13",
        ],
            AuditPaymentsReport.Format(report));

        Assert.Equal(
            "3 return(s) out of step with the provider; nothing was changed.",
            AuditPaymentsReport.Summary(report));
    }

    [Fact]
    public void A_clean_run_says_the_returns_match_and_that_nothing_was_changed()
    {
        var report = new PaymentAuditReport(Checked: 4, InStep: 4, Drift: [], Unanswered: [], More: false);

        Assert.Equal(
            "Every return carrying a transaction matches the provider; nothing was changed.",
            AuditPaymentsReport.Summary(report));
    }

    [Fact]
    public void An_unanswered_transaction_and_a_short_run_are_both_in_the_summary()
    {
        var report = new PaymentAuditReport(
            Checked: 2,
            InStep: 1,
            Drift: [],
            Unanswered: ["RMA-2026-00009 (pi_9): That authorisation could not be found."],
            More: true);

        var lines = AuditPaymentsReport.Format(report);

        Assert.Contains(
            "  ? RMA-2026-00009 (pi_9): That authorisation could not be found.",
            lines);
        Assert.Contains(
            "  more:        --take cut the set short; re-run without it to compare every return.",
            lines);

        // Not "every return matches": one could not be asked about and the run stopped early. Both
        // facts are in the sentence the operator reads.
        Assert.Equal(
            "Every return that could be compared matches the provider; 1 transaction(s) the provider "
            + "could not be asked about; the --take bound cut the set short; nothing was changed.",
            AuditPaymentsReport.Summary(report));
    }
}
