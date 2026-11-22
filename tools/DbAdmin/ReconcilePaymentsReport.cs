using AIEnabledRma.Domain.Operations;

namespace AIEnabledRma.Tools.DbAdmin;

/// <summary>
/// What `DbAdmin reconcile-payments` prints, kept out of the top-level program so a test can pin it.
/// </summary>
/// <remarks>
/// The operator reads this to decide whether anything on the screen needs them: a run where every
/// authorisation agrees is the answer "nothing was lost", which is only useful if the report says so
/// in as few words as it says the opposite.
/// </remarks>
public static class ReconcilePaymentsReport
{
    public static IReadOnlyList<string> Format(PaymentReconciliationReport report)
    {
        var lines = new List<string>
        {
            $"  checked:     {report.Checked}",
            $"  agreed:      {report.Agreed}",
            $"  disagreed:   {report.Disagreements.Count}",
            $"  unanswered:  {report.Unanswered.Count}",
        };

        foreach (var disagreement in report.Disagreements)
        {
            // One line per disagreement, ending in what this database thought: the provider's answer
            // alone does not say what to fix.
            lines.Add(
                $"  {disagreement.RmaNumber}: provider says {disagreement.Provider}, "
                + $"this database expects {disagreement.Expected} ({disagreement.LocalStatus}), "
                + $"{disagreement.TransactionId}");
        }

        foreach (var unanswered in report.Unanswered)
        {
            lines.Add($"  ? {unanswered}");
        }

        return lines;
    }

    /// <summary>The sentence under the numbers, which is what an operator actually reads.</summary>
    public static string Summary(PaymentReconciliationReport report) =>
        report.Unanswered.Count > 0
            ? "The provider could not be asked about every transaction; nothing was filed for those. Re-run when it answers."
            : report.Disagreements.Count == 0
                ? "Every stale authorisation matches the provider."
                : $"{report.Disagreements.Count} disagreement(s) filed for review.";
}
