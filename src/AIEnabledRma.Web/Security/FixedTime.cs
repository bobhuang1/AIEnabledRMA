using AIEnabledRma.Domain.Rma;

namespace AIEnabledRma.Web.Security;

/// <summary>A clock stopped at one instant, for the Development-only Demo:Today setting.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow => now;

    public DateOnly Today => DateOnly.FromDateTime(now.UtcDateTime);
}
