using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Common;
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
                    LocalStatus: "Received",
                    ExpectedAmount: new Money(69m, "EUR"),
                    ProviderAmount: new Money(69m, "EUR")),
                new PaymentDrift(
                    Guid.NewGuid(),
                    "RMA-2026-00012",
                    "pi_12",
                    PaymentDriftDirection.RowOnly,
                    Expected: PaymentStatus.Captured,
                    Provider: PaymentStatus.Authorized,
                    LocalStatus: "InRepair",
                    ExpectedAmount: new Money(69m, "EUR"),
                    ProviderAmount: new Money(69m, "EUR")),
                new PaymentDrift(
                    Guid.NewGuid(),
                    "RMA-2026-00013",
                    "pi_13",
                    PaymentDriftDirection.Mismatched,
                    Expected: PaymentStatus.Voided,
                    Provider: PaymentStatus.Captured,
                    LocalStatus: "Cancelled",
                    ExpectedAmount: new Money(69m, "EUR"),
                    ProviderAmount: new Money(69m, "EUR")),
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
    public void The_exit_code_tells_a_scheduler_drift_from_could_not_ask_from_clean()
    {
        // 1 is how this tool reports a bad invocation, so drift and an unanswered transaction each get
        // their own code rather than sharing it.
        Assert.Equal(0, AuditPaymentsReport.ExitCode(
            new PaymentAuditReport(3, 3, [], [], More: false)));

        Assert.Equal(3, AuditPaymentsReport.ExitCode(
            new PaymentAuditReport(
                3,
                2,
                [new PaymentDrift(Guid.NewGuid(), "RMA-2026-00011", "pi_11", PaymentDriftDirection.ProviderOnly, PaymentStatus.Authorized, PaymentStatus.Captured, "Received", new Money(69m, "EUR"), new Money(69m, "EUR"))],
                [],
                More: false)));

        // Both at once: "ask again" wins, because the drift list is incomplete either way.
        Assert.Equal(2, AuditPaymentsReport.ExitCode(
            new PaymentAuditReport(
                2,
                0,
                [new PaymentDrift(Guid.NewGuid(), "RMA-2026-00012", "pi_12", PaymentDriftDirection.RowOnly, PaymentStatus.Captured, PaymentStatus.Authorized, "InRepair", new Money(69m, "EUR"), new Money(69m, "EUR"))],
                ["RMA-2026-00009 (pi_9): That authorisation could not be found."],
                More: false)));
    }

    /// <summary>
    /// A drift where the state agrees and the money does not is the case a status comparison misses,
    /// so the line has to name both sums.
    /// </summary>
    [Fact]
    public void A_drift_about_the_amount_alone_names_both_sums()
    {
        var report = new PaymentAuditReport(
            Checked: 1,
            InStep: 0,
            Drift:
            [
                new PaymentDrift(
                    Guid.NewGuid(),
                    "RMA-2026-00014",
                    "pi_14",
                    PaymentDriftDirection.Mismatched,
                    PaymentStatus.Captured,
                    PaymentStatus.Captured,
                    "InRepair",
                    new Money(70m, "EUR"),
                    new Money(69m, "EUR")),
            ],
            Unanswered: [],
            More: false);

        Assert.Contains(
            "  [mismatch] RMA-2026-00014: provider says Captured, this database expects Captured "
            + "(InRepair), pi_14; it holds 69.00 EUR where the return says 70.00 EUR",
            AuditPaymentsReport.Format(report));
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
