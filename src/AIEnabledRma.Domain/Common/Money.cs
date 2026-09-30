using System.Globalization;

namespace AIEnabledRma.Domain.Common;

/// <summary>
/// A money amount paired with its currency. Amounts are stored in the currency's
/// major unit as <see cref="decimal"/>; conversion to minor units (cents) happens only
/// at the payment-gateway boundary, where zero-decimal currencies must be handled.
/// </summary>
public readonly record struct Money(decimal Amount, string CurrencyCode)
{
    public static Money Zero(string currencyCode) => new(0m, Normalize(currencyCode));

    public Money Plus(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, CurrencyCode);
    }

    public Money Minus(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, CurrencyCode);
    }

    public Money Times(decimal factor) => new(Amount * factor, CurrencyCode);

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount:0.00} {CurrencyCode}");

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(CurrencyCode, other.CurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot combine {CurrencyCode} with {other.CurrencyCode} without an exchange rate.");
        }
    }

    private static string Normalize(string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw new ArgumentException("Currency code is required.", nameof(currencyCode));
        }

        return currencyCode.Trim().ToUpperInvariant();
    }
}
