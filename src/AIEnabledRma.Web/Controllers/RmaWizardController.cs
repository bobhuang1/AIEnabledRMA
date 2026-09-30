using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Rules;
using AIEnabledRma.Domain.Triage;
using AIEnabledRma.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace AIEnabledRma.Web.Controllers;

/// <summary>
/// The customer-facing return wizard.
///
/// Each step is one POST that calls exactly one <see cref="IRmaWorkflow"/> method. The workflow
/// owns every state transition and the AI owns none of them, so this controller never writes
/// status directly and never decides eligibility. It resolves identifiers, shapes form posts,
/// and renders what the workflow returned.
///
/// Route-id steps (<c>/RmaWizard/Triage/{id}</c> and later) are deliberately not guarded by a
/// session or a customer ownership check. The product decision is that the GUID is the
/// capability: an unguessable 122-bit token plays the role the session would otherwise play, so
/// a bookmarked step continues to work across browsers and sessions, and a shareable link is
/// precisely what lets a second device finish a return. The trade-off, that anyone holding a
/// request URL can drive that return, is accepted for <c>AwaitingShipment</c> and earlier
/// steps (the request is not yet a commitment until confirmed) and must be revisited if the
/// wizard grows authenticated pages, an operator portal, or a second user on one return.
/// </summary>
public sealed class RmaWizardController(
    IRmaWorkflow workflow,
    ITriageService triage,
    IDeviceRepository devices,
    ICustomerRepository customers,
    IRmaRepository requests,
    TimeProvider timeProvider) : Controller
{
    [HttpGet]
    public IActionResult Start() => View(new StartReturnForm());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(StartReturnForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(form);
        }

        var identifiers = form.IdentifierLines();
        if (identifiers.Count == 0)
        {
            ModelState.AddModelError(
                nameof(form.Identifiers),
                "Enter at least one serial number or other identifier.");

            return View(form);
        }

        // The customer is resolved but not trusted: a fuzzy hit is shown to the customer for
        // confirmation on the triage page rather than assumed.
        var customerMatches = await customers.SearchAsync(
            new CustomerSearchQuery
            {
                Email = LooksLikeEmail(form.Customer) ? form.Customer : null,
                PhoneNumber = LooksLikePhone(form.Customer) ? form.Customer : null,
                Name = LooksLikeEmail(form.Customer) || LooksLikePhone(form.Customer) ? null : form.Customer,
            },
            cancellationToken);

        if (customerMatches.Count > 1)
        {
            TempData["AmbiguousCustomer"] = form.Customer;
            TempData["AmbiguousMatches"] = System.Text.Json.JsonSerializer.Serialize(
                customerMatches
                    .Select(c => new CustomerMatchView(c.Id, c.FullName, c.Email, c.PhoneNumber))
                    .ToList());

            return RedirectToAction(nameof(ChooseCustomer), new { identifiers = string.Join('|', identifiers) });
        }

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var lookups = new List<DeviceLookupResult>();

        foreach (var identifier in identifiers)
        {
            lookups.Add(await devices.LookupAsync(identifier, today, cancellationToken));
        }

        var result = await workflow.StartAsync(
            new StartRmaCommand
            {
                Devices = lookups,
                Today = today,
                CustomerId = customerMatches.Count == 1 ? customerMatches[0].Id : null,
                RegionCode = NullIfBlank(form.RegionCode),
                CurrencyCode = string.IsNullOrWhiteSpace(form.CurrencyCode) ? "USD" : form.CurrencyCode.Trim().ToUpperInvariant(),
            },
            cancellationToken);

        if (result.RmaId is null)
        {
            // Nothing was created. Show the policy's reasons rather than a bare error.
            return View("Outcome", ToViewModel(result));
        }

        TempData["CustomerName"] = customerMatches.Count == 1
            ? $"{customerMatches[0].FullName}"
            : null;

        return RedirectToAction(nameof(Triage), new { id = result.RmaId });
    }

    /// <summary>
    /// Disambiguates an ambiguous customer match. Deliberately shows the candidates instead of
    /// guessing: attaching a return to the wrong customer exposes their address and their
    /// returns history.
    /// </summary>
    [HttpGet]
    public IActionResult ChooseCustomer(string? identifiers)
    {
        TempData.TryGetValue("AmbiguousMatches", out var matchesJson);

        // The match list crosses the redirect as a JSON string: cookie TempData round-trips
        // simple values dependably, and a structured list is pushed through the serializer
        // explicitly instead of being left to inference.
        IReadOnlyList<CustomerMatchView>? matches = matchesJson is string json
            ? System.Text.Json.JsonSerializer.Deserialize<List<CustomerMatchView>>(json)
            : null;

        TempData.Keep("AmbiguousMatches");

        ViewBag.Identifiers = identifiers;
        ViewBag.Matches = matches;

        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChooseCustomer(string customerId, string identifiers, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(customerId, out var id))
        {
            return RedirectToAction(nameof(Start));
        }

        var identifierList = identifiers
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var lookups = new List<DeviceLookupResult>();

        foreach (var identifier in identifierList)
        {
            lookups.Add(await devices.LookupAsync(identifier, today, cancellationToken));
        }

        var result = await workflow.StartAsync(
            new StartRmaCommand
            {
                Devices = lookups,
                Today = today,
                CustomerId = id,
            },
            cancellationToken);

        return result.RmaId is null
            ? View("Outcome", ToViewModel(result))
            : RedirectToAction(nameof(Triage), new { id = result.RmaId });
    }

    [HttpGet]
    public async Task<IActionResult> Triage(Guid id, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(id, cancellationToken);
        if (request is null)
        {
            return NotFound();
        }

        ViewBag.RmaNumber = request.RmaNumber;
        ViewBag.Lines = LinesOf(request);
        ViewBag.Excluded = ReadExclusions(request);
        ViewBag.PartialMessage = PartialMessageOf(request);

        return View(new TriageForm());
    }

    /// <summary>
    /// The exclusions persisted with the request, flattened for the page. Exclusions were
    /// carried in the session until they were moved into <c>rma_exclusions</c>; a refresh can
    /// now rebuild this page from the database alone, and a new session cannot lose the notice.
    /// </summary>
    private static IReadOnlyList<ExcludedItemView> ReadExclusions(RmaRequest request) =>
        request.Exclusions
            .Select(e => new ExcludedItemView
            {
                SerialNumber = e.SerialNumber ?? e.MatchedOn,
                Eligibility = e.Eligibility,
                Reason = e.Reason,
                Explanation = e.Explanation,
            })
            .ToList();

    /// <summary>
    /// The partial-create warning, rebuilt from the persisted exclusions so it survives a
    /// refresh and a new session. Counts and reasons come straight from what was stored at the
    /// start step, not from a copy kept anywhere else.
    /// </summary>
    private static string? PartialMessageOf(RmaRequest request)
    {
        if (request.Exclusions.Count == 0)
        {
            return null;
        }

        return $"{request.Exclusions.Count} item(s) could not be included in this return "
            + $"and need to be looked at: "
            + string.Join(
                " ",
                request.Exclusions.Select(e =>
                    $"{e.SerialNumber ?? e.MatchedOn ?? "One item"} ({e.Reason}): {e.Explanation}"));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Triage(Guid id, TriageForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.RmaNumber = id.ToString();
            return View(form);
        }

        var request = await requests.GetByIdAsync(id, cancellationToken);
        if (request is null)
        {
            return NotFound();
        }

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        // Retrieval and the model call happen inside the pipeline, which enforces the scope
        // guard, the closed schema, and citation verification. The controller cannot skip those
        // because it has no path to IChatModel.
        var triageResult = await triage.TriageAsync(
            new TriageRequest
            {
                Devices = TriageContext.Build(request, today),
                Conversation =
                [
                    new TriageTurn
                    {
                        Role = TriageRole.Customer,
                        Content = form.Statement,
                        AtUtc = timeProvider.GetUtcNow(),
                    },
                ],
                CustomerStatement = form.Statement,
                CorrelationId = $"rma:{request.RmaNumber}",
            },
            cancellationToken);

        var applied = await workflow.ApplyTriageAsync(id, triageResult, cancellationToken);

        if (applied.Outcome == StepOutcome.ResolvedByTroubleshooting)
        {
            return View("Outcome", ToViewModel(applied, triageResult));
        }

        return RedirectToAction(nameof(Problems), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Problems(Guid id, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(id, cancellationToken);
        if (request is null)
        {
            return NotFound();
        }

        return View(new ProblemsForm
        {
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            Lines = request.Lines
                .Select(l => new ProblemLineForm
                {
                    DeviceId = l.DeviceId,
                    SerialNumber = l.Device?.SerialNumber ?? "unknown",
                    ProductName = l.Device?.Product?.Name ?? "unknown",
                    Description = l.ProblemDescription,
                    WhatCustomerTried = l.WhatCustomerTried,
                    CategoryCode = l.ProblemCategoryCode,
                })
                .ToList(),
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Problems(ProblemsForm form, CancellationToken cancellationToken)
    {
        // Only lines belonging to this return may be written. Without this check the posted
        // form could annotate an arbitrary device on any request.
        var request = await requests.GetByIdAsync(form.RmaId, cancellationToken);
        if (request is null)
        {
            return NotFound();
        }

        var owned = request.Lines.Select(l => l.DeviceId).ToHashSet();
        var problems = form.Lines
            .Where(l => owned.Contains(l.DeviceId))
            .Select(l => new ProblemEntry
            {
                DeviceId = l.DeviceId,
                Description = l.Description,
                WhatCustomerTried = l.WhatCustomerTried,
                CategoryCode = l.CategoryCode,
            })
            .ToList();

        if (problems.Count != owned.Count)
        {
            ModelState.AddModelError(
                string.Empty,
                "Some items in this form are not part of this return. Reload the page.");

            return View(form);
        }

        if (!ModelState.IsValid)
        {
            return View(form);
        }

        await workflow.RecordProblemsAsync(form.RmaId, problems, cancellationToken);

        return RedirectToAction(nameof(Shipping), new { id = form.RmaId });
    }

    [HttpGet]
    public async Task<IActionResult> Shipping(Guid id, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(id, cancellationToken);
        if (request is null)
        {
            return NotFound();
        }

        ViewBag.RmaNumber = request.RmaNumber;

        if (request.CustomerId is { } customerId)
        {
            var customer = await customers.GetByIdAsync(customerId, cancellationToken);
            ViewBag.CustomerName = customer is null
                ? null
                : $"{customer.FullName}";
            ViewBag.Addresses = customer?.Addresses ?? [];
        }

        return View(new ShippingForm { RmaId = request.Id, RmaNumber = request.RmaNumber });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Shipping(ShippingForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.RmaNumber = form.RmaNumber;
            return View(form);
        }

        var request = await requests.GetByIdAsync(form.RmaId, cancellationToken);
        if (request is null)
        {
            return NotFound();
        }

        // Two ways to choose an address: pick a stored one, or type a free-text address that
        // gets fuzzy-matched. ResolveAddressAsync reports IsFallback when the typed address did
        // not match confidently and the default was substituted instead, and that case is shown
        // to the customer for confirmation rather than silently shipped to.
        var chosen = form.AddressId;
        var fallback = false;

        if (chosen is null && !string.IsNullOrWhiteSpace(form.FreeTextAddress))
        {
            if (request.CustomerId is { } customerId)
            {
                var resolution = await customers.ResolveAddressAsync(
                    customerId,
                    form.FreeTextAddress,
                    cancellationToken);

                chosen = resolution?.Address.Id;
                fallback = resolution?.IsFallback ?? false;
            }
        }
        else if (chosen is not null && request.CustomerId is { } customerId)
        {
            // Confirm the posted address really belongs to this customer.
            var customer = await customers.GetByIdAsync(customerId, cancellationToken);
            if (customer is null || customer.Addresses.All(a => a.Id != chosen))
            {
                ModelState.AddModelError(string.Empty, "That address is not on your account.");
                return View(form);
            }
        }

        var result = await workflow.SetShippingAsync(form.RmaId, chosen, cancellationToken);

        if (result.Outcome == StepOutcome.AwaitingPayment)
        {
            return RedirectToAction(nameof(Payment), new { id = form.RmaId });
        }

        return RedirectToAction(nameof(Confirm), new { id = form.RmaId, fallback });
    }

    [HttpGet]
    public async Task<IActionResult> Payment(Guid id, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(id, cancellationToken);
        return request is null ? NotFound() : View(new PaymentForm { RmaId = request.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Payment(PaymentForm form, CancellationToken cancellationToken)
    {
        var result = await workflow.AuthorizePaymentAsync(
            form.RmaId,
            NullIfBlank(form.PaymentMethodToken),
            cancellationToken);

        if (result.Outcome == StepOutcome.RequiresHumanReview)
        {
            return View("Outcome", ToViewModel(result));
        }

        return RedirectToAction(nameof(Confirm), new { id = form.RmaId });
    }

    [HttpGet]
    public async Task<IActionResult> Confirm(Guid id, bool fallback = false, CancellationToken cancellationToken = default)
    {
        var request = await requests.GetByIdAsync(id, cancellationToken);
        if (request is null)
        {
            return NotFound();
        }

        ViewBag.FallbackAddress = fallback;
        ViewBag.Address = request.ShipToAddress;

        return View(new WizardViewModel
        {
            RmaId = request.Id,
            RmaNumber = request.RmaNumber,
            Status = request.Status,
            Message = "Please check the details below, then confirm.",
            Lines = LinesOf(request),
            Kind = request.Kind,
            ShippingCharge = request.ShippingCharge,
            DepositAmount = request.DepositAmount,
            CurrencyCode = request.CurrencyCode,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(ConfirmForm form, CancellationToken cancellationToken)
    {
        var result = await workflow.ConfirmAsync(form.RmaId, cancellationToken);

        return View("Outcome", ToViewModel(result));
    }

    private static WizardViewModel ToViewModel(StepResult result, TriageResult? triage = null) => new()
    {
        RmaId = result.RmaId ?? Guid.Empty,
        RmaNumber = result.RmaNumber,
        Status = result.Status,
        Outcome = result.Outcome,
        Message = result.Message,
        ExcludedItems = result.ExcludedItems,
        Triage = triage,
    };

    private static IReadOnlyList<WizardLineViewModel> LinesOf(RmaRequest request) =>
        request.Lines
            .Select(l => new WizardLineViewModel
            {
                DeviceId = l.DeviceId,
                SerialNumber = l.Device?.SerialNumber ?? "unknown",
                ProductName = l.Device?.Product?.Name ?? "unknown",
                ProblemDescription = l.ProblemDescription,
            })
            .ToList();

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool LooksLikeEmail(string value) => value.Contains('@', StringComparison.Ordinal);

    private static bool LooksLikePhone(string value) =>
        value.Where(char.IsDigit).Count() >= 7 && !value.Contains('@', StringComparison.Ordinal);

    /// <summary>
    /// A concrete type instead of an anonymous one. The match list is stashed in TempData,
    /// which is serialized into a cookie on the way out and back, and anonymous types are not
    /// a safe shape to push through that round trip in every TempDataProvider.
    /// </summary>
    public sealed record CustomerMatchView(Guid Id, string Name, string? Email, string? PhoneNumber);
}
