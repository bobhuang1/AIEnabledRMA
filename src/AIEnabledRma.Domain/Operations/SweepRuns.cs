namespace AIEnabledRma.Domain.Operations;

/// <summary>
/// One row per run of a scheduled sweep. The sweep is a one-shot process on a timer, so this row is
/// the only evidence that the timer is still firing.
/// </summary>
/// <remarks>
/// It exists because a clean queue cannot tell two situations apart: a sweep that asked the provider
/// and found agreement, and a sweep that stopped being run a week ago. Anything that reads the queue
/// to decide whether reconciliation is working reads this row instead.
/// </remarks>
public sealed class SweepRun
{
    public required Guid Id { get; set; }

    /// <summary>See <see cref="SweepSources"/>.</summary>
    public required string Source { get; set; }

    public required DateTimeOffset AtUtc { get; set; }

    /// <summary>
    /// Whether the run finished the answer it exists to give: every transaction it asked the
    /// provider about came back. A run refused an answer is recorded as failed rather than counted
    /// as a quiet night, because "could not ask" is not "nothing was wrong".
    /// </summary>
    public required bool Succeeded { get; set; }

    /// <summary>How many rows it looked at, so a run that asked nothing is visible as well.</summary>
    public int Checked { get; set; }

    public int Filed { get; set; }

    public int Unanswered { get; set; }

    /// <summary>Why a failed run failed, in the words the process had. Null on a run that answered.</summary>
    public string? Detail { get; set; }
}

public static class SweepSources
{
    /// <summary>The nightly provider sweep: <c>DbAdmin reconcile-payments</c>.</summary>
    public const string PaymentReconciliation = "reconcile-payments";
}

/// <summary>
/// Where a sweep records that it ran. Recording is the sweep's last act, so it must never be able to
/// undo the work the run already did.
/// </summary>
public interface ISweepLedger
{
    Task RecordAsync(SweepRun run, CancellationToken cancellationToken);
}
