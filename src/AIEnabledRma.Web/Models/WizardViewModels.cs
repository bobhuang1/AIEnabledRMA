using System.ComponentModel.DataAnnotations;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Triage;

namespace AIEnabledRma.Web.Models;

/// <summary>Step 1: who the return is for, and what is in it.</summary>
public sealed class StartReturnForm
{
    /// <summary>Free-text customer identity: email, phone, or name. Matched fuzzily.</summary>
    [Required(ErrorMessage = "Enter an email address, phone number, or customer name.")]
    [StringLength(200)]
    [Display(Name = "Your email, phone, or name")]
    public string Customer { get; set; } = string.Empty;

    /// <summary>One identifier per line. A serial, MAC, IMEI, or SKU all resolve.</summary>
    [Required(ErrorMessage = "Enter at least one serial number or other identifier.")]
    [StringLength(2000)]
    [Display(Name = "Serial numbers or other identifiers")]
    public string Identifiers { get; set; } = string.Empty;

    [StringLength(8)]
    [Display(Name = "Country or region code")]
    public string? RegionCode { get; set; }

    [StringLength(3, MinimumLength = 3)]
    [Display(Name = "Currency")]
    public string CurrencyCode { get; set; } = "USD";

    public IReadOnlyList<string> IdentifierLines() =>
        Identifiers
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
}

/// <summary>Step 2: what is wrong, in the customer's own words.</summary>
public sealed class TriageForm
{
    [Required(ErrorMessage = "Describe the problem so we can help.")]
    [StringLength(2000, MinimumLength = 3)]
    [Display(Name = "What is going wrong?")]
    public string Statement { get; set; } = string.Empty;
}

/// <summary>Step 3: a description per item, for the repair bench.</summary>
public sealed class ProblemsForm
{
    public Guid RmaId { get; set; }

    public string? RmaNumber { get; set; }

    public List<ProblemLineForm> Lines { get; set; } = [];
}

public sealed class ProblemLineForm
{
    public Guid DeviceId { get; set; }

    public string SerialNumber { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Describe the problem with this item.")]
    [StringLength(2000, MinimumLength = 3)]
    [Display(Name = "What is wrong with it?")]
    public string Description { get; set; } = string.Empty;

    [StringLength(2000)]
    [Display(Name = "What have you already tried?")]
    public string? WhatCustomerTried { get; set; }

    [StringLength(64)]
    [Display(Name = "Category")]
    public string? CategoryCode { get; set; }
}

/// <summary>Step 4: where the replacement goes.</summary>
public sealed class ShippingForm
{
    public Guid RmaId { get; set; }

    public string? RmaNumber { get; set; }

    [StringLength(400)]
    [Display(Name = "Or type a different address")]
    public string? FreeTextAddress { get; set; }

    /// <summary>Id of a stored address, when the customer picked one.</summary>
    public Guid? AddressId { get; set; }
}

/// <summary>Step 5: payment, when the policy quotes something to collect.</summary>
public sealed class PaymentForm
{
    public Guid RmaId { get; set; }

    /// <summary>
    /// A token from the payment provider's client SDK, never a card number. There is no payment
    /// SDK wired up here, so the form accepts an opaque placeholder and the gateway decides.
    /// </summary>
    [StringLength(200)]
    [Display(Name = "Payment method token")]
    public string? PaymentMethodToken { get; set; }
}

/// <summary>Step 6: review and send.</summary>
public sealed class ConfirmForm
{
    public Guid RmaId { get; set; }
}

/// <summary>Everything a wizard page renders, plus the state it must not lose.</summary>
public sealed class WizardViewModel
{
    public Guid RmaId { get; set; }

    public string? RmaNumber { get; set; }

    public RmaRequestStatus Status { get; set; }

    public string Message { get; set; } = string.Empty;

    public StepOutcome Outcome { get; set; }

    /// <summary>
    /// Items the policy left out of the return. Surfaced verbatim on the outcome page: a
    /// customer who handed over three things and had one silently dropped has no way to know
    /// to ask about it.
    /// </summary>
    public IReadOnlyList<ExcludedItem> ExcludedItems { get; set; } = [];

public IReadOnlyList<WizardLineViewModel> Lines { get; set; } = [];

    public RmaRequestKind Kind { get; set; }

    public decimal ShippingCharge { get; set; }

    public decimal DepositAmount { get; set; }

    public string CurrencyCode { get; set; } = "USD";

    public decimal TotalDue => ShippingCharge + DepositAmount;

    /// <summary>True when this is a paid repair: the customer is charged the repair fee.</summary>
    public bool IsPaidRepair => Kind == RmaRequestKind.PaidRepair;

    public TriageResult? Triage { get; set; }
}

public sealed class WizardLineViewModel
{
    public Guid DeviceId { get; set; }

    public string SerialNumber { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;

    public string? ProblemDescription { get; set; }
}

/// <summary>
/// A flattened exclusion for the page. Enum members were stored as their names at the start
/// step, so a customer-facing string never depends on declaration order.
/// </summary>
public sealed class ExcludedItemView
{
    public string? SerialNumber { get; set; }

    public string Eligibility { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string? Explanation { get; set; }
}

/// <summary>Shown by the error page so support can find the matching log entry.</summary>
public sealed class ErrorViewModel
{
    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
