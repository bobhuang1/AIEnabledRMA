using AIEnabledRma.Domain.Triage;

namespace AIEnabledRma.Domain.Abstractions;

/// <summary>
/// Where a scope-guard refusal is recorded.
/// </summary>
/// <remarks>
/// The pipeline refuses in the domain and has no database, so the write is an abstraction the host
/// supplies — the same shape as <see cref="IToolAuditSink"/>. A refusal is not a failure of the
/// request: the customer still gets the canned answer, and a host that cannot write the row (a
/// misconfigured connection, a bare unit test) must not turn a refusal into an error. Implementations
/// may throw; the pipeline catches, logs and carries on.
/// </remarks>
public interface ITriageRefusalLog
{
    Task RecordAsync(TriageRefusal refusal, CancellationToken cancellationToken = default);
}

/// <summary>
/// The log a host with nowhere to write refusals gets, so the pipeline can be built without one.
/// </summary>
public sealed class NoTriageRefusalLog : ITriageRefusalLog
{
    public Task RecordAsync(TriageRefusal refusal, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
