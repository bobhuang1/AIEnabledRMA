using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Common;
using AIEnabledRma.Domain.Operations;

namespace AIEnabledRma.Tests;

/// <summary>
/// The two sums a payment mismatch carries are shown only when both halves were recorded: a number
/// without its currency would render as a bare amount, which says something different from "the item
/// never recorded one".
/// </summary>
public sealed class ReviewItemMoneyTests
{
    [Fact]
    public void The_two_sums_are_money_when_the_currency_was_recorded_beside_them()
    {
        var item = Item(expected: 70m, expectedCurrency: "EUR", provider: 69m, providerCurrency: "eur");

        Assert.Equal(new Money(70m, "EUR"), item.ExpectedMoney);
        Assert.Equal(new Money(69m, "eur"), item.ProviderMoney);
    }

    [Fact]
    public void An_amount_without_its_currency_is_no_amount_at_all()
    {
        var item = Item(expected: 70m, expectedCurrency: null, provider: 69m, providerCurrency: "  ");

        Assert.Null(item.ExpectedMoney);
        Assert.Null(item.ProviderMoney);
    }

    private static ReviewItem Item(
        decimal? expected,
        string? expectedCurrency,
        decimal? provider,
        string? providerCurrency) =>
        new()
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            Source = ReviewSources.PaymentMismatch,
            Reason = PaymentMismatchReason.For(PaymentStatus.Captured),
            ExpectedAmount = expected,
            ExpectedCurrencyCode = expectedCurrency,
            ProviderAmount = provider,
            ProviderCurrencyCode = providerCurrency,
        };
}
