using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Rules;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// The evaluator is the only thing allowed to decide an outcome, and a model is never
/// consulted. These tests pin the decisions themselves, because a policy that quietly
/// changes what it approves is a business change, not a refactor.
/// </summary>
public sealed class RmaPolicyEvaluatorTests
{
    private static readonly DateOnly Today = new(2026, 9, 29);

    private static RmaPolicyEvaluator Build(RmaPolicyOptions? options = null) =>
        new(Options.Create(options ?? new RmaPolicyOptions
        {
            Tiers =
            [
                new WarrantyTierPolicy { Name = "standard", IsCovered = true },
                new WarrantyTierPolicy { Name = "no-service", IsCovered = false },
            ],
        }));

    // ---------- warranty selection ----------

    [Fact]
    public void In_warranty_device_is_eligible()
    {
        var device = TestData.Device(end: new DateOnly(2027, 1, 1));

        var decision = Build().Evaluate(new RmaPolicyRequest { Device = device, Today = Today });

        Assert.Equal(RmaEligibility.Eligible, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.InWarrantyConfirmedDefect, decision.Reason);
        Assert.True(decision.AllowsRmaCreation);
    }

    [Fact]
    public void Out_of_warranty_device_routes_to_a_paid_repair()
    {
        var device = TestData.Device(end: new DateOnly(2024, 1, 1));

        var decision = Build().Evaluate(new RmaPolicyRequest { Device = device, Today = Today });

        // Out of warranty is not a refusal: the unit is still repairable, so the policy offers
        // a paid repair that the wizard can create and charge for.
        Assert.Equal(RmaEligibility.EligibleForPaidRepair, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.OutOfWarranty, decision.Reason);
        Assert.True(decision.AllowsRmaCreation);
    }

    [Fact]
    public void An_uncovered_tier_routes_to_a_paid_repair()
    {
        var device = TestData.Device(end: new DateOnly(2027, 1, 1), tier: "no-service");

        var decision = Build().Evaluate(new RmaPolicyRequest { Device = device, Today = Today });

        // A plan that does not cover repairs is the same situation as no warranty at all:
        // the customer is offered a paid repair rather than refused.
        Assert.Equal(RmaEligibility.EligibleForPaidRepair, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.OutOfWarranty, decision.Reason);
        Assert.True(decision.AllowsRmaCreation);
    }

    [Fact]
    public void Grace_period_keeps_a_recently_expired_unit_covered()
    {
        var options = new RmaPolicyOptions { GracePeriodDays = 30 };
        var expiredTenDaysAgo = TestData.Device(end: Today.AddDays(-10));

        var decision = Build(options).Evaluate(
            new RmaPolicyRequest { Device = expiredTenDaysAgo, Today = Today });

        // The grace period is a configured business promise. If it is not honoured here,
        // configuring it changes nothing and the customer is refused inside the window.
        Assert.Equal(RmaEligibility.Eligible, decision.Eligibility);
    }

    [Fact]
    public void Grace_period_does_not_extend_beyond_its_window()
    {
        var options = new RmaPolicyOptions { GracePeriodDays = 30 };
        var expiredLongAgo = TestData.Device(end: Today.AddDays(-90));

        var decision = Build(options).Evaluate(
            new RmaPolicyRequest { Device = expiredLongAgo, Today = Today });

        // Beyond the grace window the unit has no cover at all, which routes it to the
        // paid-repair path rather than to a dead end.
        Assert.Equal(RmaEligibility.EligibleForPaidRepair, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.OutOfWarranty, decision.Reason);
    }

    [Fact]
    public void Zero_grace_period_makes_the_expired_unit_a_paid_repair()
    {
        var device = TestData.Device(end: Today.AddDays(-1));

        var decision = Build().Evaluate(new RmaPolicyRequest { Device = device, Today = Today });

        // With no grace configured an expired unit has no active warranty, so the only
        // remaining path is a paid repair.
        Assert.Equal(RmaEligibility.EligibleForPaidRepair, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.OutOfWarranty, decision.Reason);
    }

    [Fact]
    public void Revoked_warranty_record_is_ignored()
    {
        var device = TestData.Device(warrantyActive: false, end: new DateOnly(2027, 1, 1));

        var decision = Build().Evaluate(new RmaPolicyRequest { Device = device, Today = Today });

        // A superseded or revoked record must not keep authorising free returns, and a unit
        // without an active warranty takes the paid-repair path instead of being refused.
        Assert.Equal(RmaEligibility.EligibleForPaidRepair, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.OutOfWarranty, decision.Reason);
    }

    [Fact]
    public void The_latest_ending_record_governs_when_records_overlap()
    {
        var device = TestData.Device(withWarranty: false);
        device.Warranties =
        [
            new Warranty
            {
                Id = Guid.NewGuid(), DeviceId = device.Id, PlanName = "Old", Tier = "standard",
                StartDate = new DateOnly(2024, 1, 1), EndDate = new DateOnly(2024, 6, 1), IsActive = true,
            },
            new Warranty
            {
                Id = Guid.NewGuid(), DeviceId = device.Id, PlanName = "Live", Tier = "standard",
                StartDate = new DateOnly(2024, 1, 1), EndDate = new DateOnly(2027, 1, 1), IsActive = true,
            },
        ];

        var evaluator = Build();
        var warranty = evaluator.SelectWarranty(device, Today);

        Assert.NotNull(warranty);
        Assert.Equal("Live", warranty.PlanName);
    }

    [Fact]
    public void A_tier_without_cover_still_offers_a_paid_repair()
    {
        var device = TestData.Device(tier: "no-service", end: new DateOnly(2027, 1, 1));

        var decision = Build().Evaluate(new RmaPolicyRequest { Device = device, Today = Today });

        // Inside the warranty but not covered: the plan changes what is free, not whether
        // the unit can be repaired for money.
        Assert.Equal(RmaEligibility.EligibleForPaidRepair, decision.Eligibility);
    }

    // ---------- other rules ----------

    [Fact]
    public void An_existing_open_request_escalates_rather_than_denying()
    {
        var device = TestData.Device(end: new DateOnly(2027, 1, 1));

        var decision = Build().Evaluate(new RmaPolicyRequest
        {
            Device = device,
            Today = Today,
            HasOpenRequestForDevice = true,
        });

        // Escalate, not deny: the customer may already be mid-return and deserves an answer.
        Assert.Equal(RmaEligibility.RequiresHumanReview, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.DuplicateOpenRequest, decision.Reason);
    }

    [Fact]
    public void Excluded_cause_is_denied_even_on_a_new_in_warranty_unit()
    {
        var device = TestData.Device(end: new DateOnly(2027, 1, 1));
        var options = new RmaPolicyOptions
        {
            ExcludedCauses =
            [
                new ProblemCauseRule
                {
                    RuleId = "cause.liquid",
                    Description = "Liquid damage is not covered.",
                    Terms = ["liquid", "spilled"],
                },
            ],
        };

        var decision = Build(options).Evaluate(new RmaPolicyRequest
        {
            Device = device,
            Today = Today,
            ProblemDescription = "it stopped working after I spilled coffee on it",
        });

        Assert.Equal(RmaEligibility.NotEligible, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.ExcludedCause, decision.Reason);
        Assert.Contains("cause.liquid", decision.MatchedRuleIds);
    }

    [Fact]
    public void An_excluded_cause_outside_the_warranty_window_is_still_identified()
    {
        var device = TestData.Device(end: new DateOnly(2024, 1, 1));
        var options = new RmaPolicyOptions
        {
            ExcludedCauses =
            [
                new ProblemCauseRule
                {
                    RuleId = "cause.liquid",
                    Description = "Liquid damage is not covered.",
                    Terms = ["liquid"],
                },
            ],
        };

        var decision = Build(options).Evaluate(new RmaPolicyRequest
        {
            Device = device,
            Today = Today,
            ProblemDescription = "liquid damage",
        });

        Assert.Equal(RmaDecisionReason.ExcludedCause, decision.Reason);
    }

    [Fact]
    public void A_human_review_region_escalates()
    {
        var device = TestData.Device(end: new DateOnly(2027, 1, 1));
        var options = new RmaPolicyOptions { HumanReviewOnlyRegions = ["XX"] };

        var decision = Build(options).Evaluate(new RmaPolicyRequest
        {
            Device = device,
            Today = Today,
            RegionCode = "XX",
        });

        Assert.Equal(RmaEligibility.RequiresHumanReview, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.EscalatedToAgent, decision.Reason);
    }

    [Fact]
    public void A_fraud_flagged_serial_escalates_rather_than_denying()
    {
        var device = TestData.Device(serial: "STOLEN-0001", end: new DateOnly(2027, 1, 1));
        var options = new RmaPolicyOptions { FraudIndicatorSerialPrefixes = ["STOLEN-"] };

        var decision = Build(options).Evaluate(new RmaPolicyRequest { Device = device, Today = Today });

        // A false positive on the fraud list must not silently discard a real customer's
        // return, so this escalates.
        Assert.Equal(RmaEligibility.RequiresHumanReview, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.SuspectedFraud, decision.Reason);
    }

    [Fact]
    public void An_empty_device_id_is_not_a_coverage_decision()
    {
        var device = TestData.Device(end: new DateOnly(2027, 1, 1));
        device.Id = Guid.Empty;

        var decision = Build().Evaluate(new RmaPolicyRequest { Device = device, Today = Today });

        Assert.Equal(RmaDecisionReason.DeviceNotFound, decision.Reason);
        Assert.False(decision.AllowsRmaCreation);
    }

    [Fact]
    public void An_item_shipped_beyond_the_return_window_is_denied()
    {
        var options = new RmaPolicyOptions { ReturnWindowDays = 30 };
        var device = TestData.Device(
            end: new DateOnly(2027, 1, 1),
            shipped: DateTimeOffset.UnixEpoch.AddDays(-90));

        var decision = Build(options).Evaluate(new RmaPolicyRequest { Device = device, Today = Today });

        Assert.Equal(RmaDecisionReason.OutsideReturnWindow, decision.Reason);
    }

    [Fact]
    public void Ai_confidence_is_ignored_while_the_lockdown_is_on()
    {
        var device = TestData.Device(end: new DateOnly(2027, 1, 1));
        var options = new RmaPolicyOptions
        {
            AiIsAdvisoryOnly = true,
            HumanReviewConfidenceThreshold = 0.99,
            Tiers = [new WarrantyTierPolicy { Name = "standard", IsCovered = true }],
        };

        var decision = Build(options).Evaluate(new RmaPolicyRequest
        {
            Device = device,
            Today = Today,
            AiConfidence = 0.01,
        });

        // The model cannot talk the policy into an approval, and it cannot talk it into a
        // denial either. Advisory means advisory.
        Assert.Equal(RmaEligibility.Eligible, decision.Eligibility);
    }

    [Fact]
    public void Low_confidence_escalates_once_the_lockdown_is_relaxed()
    {
        var device = TestData.Device(end: new DateOnly(2027, 1, 1));
        var options = new RmaPolicyOptions
        {
            AiIsAdvisoryOnly = false,
            HumanReviewConfidenceThreshold = 0.45,
            FallbackEligibility = RmaEligibility.RequiresHumanReview,
            Tiers = [new WarrantyTierPolicy { Name = "standard", IsCovered = true }],
        };

        var decision = Build(options).Evaluate(new RmaPolicyRequest
        {
            Device = device,
            Today = Today,
            AiConfidence = 0.1,
        });

        // This is the escape hatch, so it must still route doubt to a person rather than
        // falling through to an approval.
        Assert.Equal(RmaEligibility.RequiresHumanReview, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.LowConfidenceTriage, decision.Reason);
    }

    [Fact]
    public void Confidence_never_overrides_a_denial_earlier_in_the_chain()
    {
        // Confidence is the last rule, so a high score must not resurrect a denied unit.
        var device = TestData.Device(
            end: new DateOnly(2027, 1, 1),
            shipped: DateTimeOffset.UnixEpoch.AddDays(-90));
        var options = new RmaPolicyOptions
        {
            AiIsAdvisoryOnly = false,
            HumanReviewConfidenceThreshold = 0.45,
            ReturnWindowDays = 30,
            Tiers = [new WarrantyTierPolicy { Name = "standard", IsCovered = true }],
        };

        var decision = Build(options).Evaluate(new RmaPolicyRequest
        {
            Device = device,
            Today = Today,
            AiConfidence = 0.99,
        });

        Assert.Equal(RmaEligibility.NotEligible, decision.Eligibility);
        Assert.Equal(RmaDecisionReason.OutsideReturnWindow, decision.Reason);
    }

    // ---------- tier lookup ----------

    [Fact]
    public void GetTier_falls_back_so_a_bad_stored_tier_cannot_block_evaluation()
    {
        var options = new RmaPolicyOptions
        {
            DefaultTier = "standard",
            Tiers = [new WarrantyTierPolicy { Name = "standard", IsCovered = true }],
        };

        Assert.Equal("standard", options.GetTier("typo-in-the-data").Name);
    }

    [Fact]
    public void TryGetTier_refuses_to_substitute()
    {
        var options = new RmaPolicyOptions
        {
            DefaultTier = "standard",
            Tiers = [new WarrantyTierPolicy { Name = "standard", IsCovered = true }],
        };

        // Quoting money must never quietly answer a different question.
        Assert.Null(options.TryGetTier("premium"));
        Assert.Equal("standard", options.TryGetTier("standard")?.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetTier_is_null_for_blank_input(string? input) =>
        Assert.Null(new RmaPolicyOptions().TryGetTier(input));

    [Fact]
    public void TryGetTier_ignores_surrounding_whitespace_and_case() =>
        Assert.Equal(
            "standard",
            new RmaPolicyOptions
            {
                Tiers = [new WarrantyTierPolicy { Name = "standard" }],
            }.TryGetTier("  STANDARD  ")?.Name);
}
