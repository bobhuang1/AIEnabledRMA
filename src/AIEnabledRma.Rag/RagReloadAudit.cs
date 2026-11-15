using Microsoft.Extensions.Logging;

namespace AIEnabledRma.Rag;

/// <summary>
/// One attempt to swap the corpus, as the host records it.
/// </summary>
/// <remarks>
/// A reload is the only operation that changes what the assistant will say to the next customer,
/// and it is a shared credential that performs it, so the record is about the request rather than
/// a person: which credential kind was used, from which address, at what time, and what the corpus
/// looked like before and after. A refusal is recorded too — "someone tried and it was refused" is
/// the answer an operator needs after a bad deploy, and it is exactly the attempt that a
/// success-only log would lose.
/// </remarks>
public sealed record RagReloadAttempt
{
    public required DateTimeOffset AtUtc { get; init; }

    /// <summary><c>admin-key</c> or <c>loopback</c>: the credential, not a person.</summary>
    public required string Caller { get; init; }

    public required string? RemoteAddress { get; init; }

    public required bool Succeeded { get; init; }

    public required int ArticleCountBefore { get; init; }

    public required int ArticleCountAfter { get; init; }

    public required long DurationMilliseconds { get; init; }

    public string? RefusalReason { get; init; }
}

/// <summary>
/// Where a reload attempt is recorded.
/// </summary>
/// <remarks>
/// An interface rather than a direct log call so the tests can assert what was recorded without
/// scraping formatted output, and so a deployment can route it somewhere durable without touching
/// the endpoint. The default writes one structured log line.
/// </remarks>
public interface IRagReloadAudit
{
    void Record(RagReloadAttempt attempt);
}

/// <inheritdoc />
public sealed class LoggingRagReloadAudit(ILogger<LoggingRagReloadAudit> logger) : IRagReloadAudit
{
    public void Record(RagReloadAttempt attempt) =>
        logger.LogInformation(
            "Knowledge base reload by {Caller} from {RemoteAddress}: succeeded={Succeeded}, "
            + "articles {ArticlesBefore} -> {ArticlesAfter}, {DurationMilliseconds} ms, refusal={RefusalReason}",
            attempt.Caller,
            attempt.RemoteAddress ?? "unknown",
            attempt.Succeeded,
            attempt.ArticleCountBefore,
            attempt.ArticleCountAfter,
            attempt.DurationMilliseconds,
            attempt.RefusalReason ?? "none");
}
