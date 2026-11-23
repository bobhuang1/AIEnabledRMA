using AIEnabledRma.Domain.Operations;

namespace AIEnabledRma.Tools.DbAdmin;

/// <summary>
/// What `DbAdmin audit-payments` prints, kept out of the top-level program so a test can pin it.
/// </summary>
/// <remarks>
/// Every drifting line says which side holds the capture or void, because the two shapes lead to
/// different actions: money at the provider the row never recorded is a return to reconcile by hand,
/// while a row claiming a capture the provider never took is a customer who was told they paid when
/// they did not.
/// </remarks>
public static class AuditPaymentsReport
{
    public static IReadOnlyList<string> Format(PaymentAuditReport report)
    {
        var lines = new List<string>
        {
            $"  checked:     {report.Checked}",
            $"  in step:     {report.InStep}",
            $"  drifted:     {report.Drift.Count}",
            $"  unanswered:  {report.Unanswered.Count}",
        };

        foreach (var drift in report.Drift)
        {
            // The amounts appear only where they differ: a status drift already says what is wrong,
            // while two equal states with two different sums is a sentence about the money.
            var money = PaymentReconciler.SameMoney(drift.ExpectedAmount, drift.ProviderAmount)
                ? string.Empty
                : $"; it holds {drift.ProviderAmount} where the return says {drift.ExpectedAmount}";

            lines.Add(
                $"  [{Tag(drift.Direction)}] {drift.RmaNumber}: provider says {drift.Provider}, "
                + $"this database expects {drift.Expected} ({drift.LocalStatus}), {drift.TransactionId}{money}");
        }

        foreach (var unanswered in report.Unanswered)
        {
            lines.Add($"  ? {unanswered}");
        }

        if (report.More)
        {
            // A bounded run that stopped early must not read like a clean one.
            lines.Add("  more:        --take cut the set short; re-run without it to compare every return.");
        }

        return lines;
    }

    /// <summary>The sentence under the numbers, which is what an operator actually reads.</summary>
    public static string Summary(PaymentAuditReport report)
    {
        var parts = new List<string>
        {
            report.Drift.Count > 0
                ? $"{report.Drift.Count} return(s) out of step with the provider"
                : report.Unanswered.Count > 0
                    // Not "every return matches": one of them was never compared.
                    ? "Every return that could be compared matches the provider"
                    : "Every return carrying a transaction matches the provider",
        };

        if (report.Unanswered.Count > 0)
        {
            parts.Add($"{report.Unanswered.Count} transaction(s) the provider could not be asked about");
        }

        if (report.More)
        {
            parts.Add("the --take bound cut the set short");
        }

        // Said every time, including on a clean run: the operator has to know this command looks and
        // does not fix, or the next person will assume the drift was dealt with.
        return string.Join("; ", parts) + "; nothing was changed.";
    }

    /// <summary>
    /// What a scheduler sees: drift is the reason this command exists, so it must not exit like a
    /// clean run; an unanswered transaction is neither drift nor agreement, and re-running is the
    /// answer to it. 1 stays "called wrongly" and never comes from here.
    /// </summary>
    public static int ExitCode(PaymentAuditReport report) =>
        report.Unanswered.Count > 0 ? 2 : report.Drift.Count > 0 ? 3 : 0;

    private static string Tag(PaymentDriftDirection direction) => direction switch
    {
        PaymentDriftDirection.ProviderOnly => "provider only",
        PaymentDriftDirection.RowOnly => "row only",
        _ => "mismatch",
    };
}
