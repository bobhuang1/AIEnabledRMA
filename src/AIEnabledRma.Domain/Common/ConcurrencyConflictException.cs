namespace AIEnabledRma.Domain.Common;

/// <summary>
/// A tracked change lost a race: the row it was written from has been updated by someone else
/// since it was read, so committing it would overwrite that update.
/// </summary>
/// <remarks>
/// A domain type rather than the provider's own exception, because the services that refuse work
/// live in this project and may not reference Entity Framework. The data layer translates
/// <c>DbUpdateConcurrencyException</c> into this, so the refusal path is testable and the message
/// is written once, where the person who sees it works.
/// </remarks>
public sealed class ConcurrencyConflictException(string message, Exception? innerException = null)
    : Exception(message, innerException);
