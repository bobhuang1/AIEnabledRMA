using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Operations;

namespace AIEnabledRma.Tests;

/// <summary>
/// The reason code a payment-mismatch item carries, and the provider state the operator dashboard
/// reads back out of it.
/// </summary>
/// <remarks>
/// The code is load-bearing twice: a later reconciliation run looks for it to avoid filing the same
/// disagreement again, and the queue row shows the provider state decoded from it. Changing the
/// prefix silently stops both — the queue would fill with duplicates and the row would stop saying
/// what the provider thinks — so the shape is pinned here rather than left to the reconciler.
/// </remarks>
public sealed class PaymentMismatchReasonTests
{
    [Theory]
    [InlineData(PaymentStatus.Authorized, "PaymentMismatch:Authorized")]
    [InlineData(PaymentStatus.Captured, "PaymentMismatch:Captured")]
    [InlineData(PaymentStatus.Voided, "PaymentMismatch:Voided")]
    [InlineData(PaymentStatus.Failed, "PaymentMismatch:Failed")]
    public void The_code_names_the_status_it_was_filed_for(PaymentStatus status, string expected)
    {
        Assert.Equal(expected, PaymentMismatchReason.For(status));
    }

    [Theory]
    [InlineData(PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Voided)]
    [InlineData(PaymentStatus.Failed)]
    public void The_status_survives_the_round_trip(PaymentStatus status)
    {
        Assert.Equal(status, PaymentMismatchReason.ProviderStatusIn(PaymentMismatchReason.For(status)));
    }

    [Theory]
    [InlineData("start-human-review")]
    [InlineData("payment-failed")]
    [InlineData("PaymentMismatch:")]
    [InlineData("PaymentMismatch:Something")]
    [InlineData("paymentmismatch:Captured")]
    public void A_reason_that_is_not_a_mismatch_code_has_no_provider_state(string reason)
    {
        Assert.Null(PaymentMismatchReason.ProviderStatusIn(reason));
    }

    /// <summary>
    /// What the dashboard row reads. An item filed by the reconciler says which provider state it was
    /// filed for; every other kind of item says nothing, because it has no transaction to have a
    /// state about.
    /// </summary>
    [Fact]
    public void A_review_item_exposes_the_provider_state_only_when_it_names_one()
    {
        var mismatch = new ReviewItem
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            Source = ReviewSources.PaymentMismatch,
            Reason = PaymentMismatchReason.For(PaymentStatus.Captured),
        };

        var refusedStart = new ReviewItem
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            Source = ReviewSources.StartRefused,
            Reason = "policy said a person should look",
        };

        Assert.Equal(PaymentStatus.Captured, mismatch.ProviderStatus);
        Assert.Null(refusedStart.ProviderStatus);
    }
}
