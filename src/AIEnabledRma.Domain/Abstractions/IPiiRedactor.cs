namespace AIEnabledRma.Domain.Abstractions;

/// <summary>
/// Removes customer identifiers from text before it leaves the process for a model.
/// </summary>
/// <remarks>
/// A session is one conversation: the same email seen twice becomes the same placeholder, so the
/// model can still follow "the address I gave you" without ever being told the address. Numbering
/// restarts with a new session, because a placeholder that outlives its conversation would let a
/// later caller link two transcripts by number alone.
/// </remarks>
public interface IPiiRedactor
{
    /// <param name="customerNames">
    /// Names of the customer attached to this conversation, when known. A free-text detector
    /// cannot tell "Josephine Smith" from any other two capitalised words; this is the one name
    /// the system actually knows.
    /// </param>
    /// <param name="protectedValues">
    /// Values that must survive redaction because the task needs them: the device serial, SKU and
    /// model. They look exactly like the identifiers the detector removes, so the caller states
    /// them rather than relying on the detector to guess.
    /// </param>
    IPiiRedactionSession BeginSession(
        IEnumerable<string>? customerNames = null,
        IEnumerable<string>? protectedValues = null);
}

public interface IPiiRedactionSession
{
    PiiRedactionResult Redact(string text);

    /// <summary>
    /// True when the text still holds something that looks like an identifier. Used to police
    /// model output without replacing its text, and without allocating placeholders for it.
    /// </summary>
    bool ContainsIdentifiers(string text);
}

/// <summary>Redacted text plus how many values of each kind were replaced. Never the values.</summary>
public sealed record PiiRedactionResult(string Text, IReadOnlyDictionary<string, int> Counts)
{
    public int TotalCount => Counts.Values.Sum();

    public bool Changed => TotalCount > 0;
}
