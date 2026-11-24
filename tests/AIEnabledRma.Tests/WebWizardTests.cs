using System.Net;
using System.Text.RegularExpressions;
using AIEnabledRma.Ai;
using AIEnabledRma.Data;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Rules;
using AIEnabledRma.Rag;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// Boots the real Web host and drives the wizard through every step against a real database.
/// </summary>
/// <remarks>
/// The gap these tests exist for is that every other suite in this project either calls the
/// workflow directly with fakes or tests one piece in isolation. That combination is exactly
/// what let a defect where no state-changing step ever committed survive a fully green 184-test
/// run: the fake <c>IUnitOfWork</c> recorded calls, so the tests were asserting that a commit
/// was requested, and nothing was checking that the data was actually there afterwards.
/// Booting the host and then reading the rows back is the only way to see that kind of break.
///
/// A real PostgreSQL server is required for the same reason as
/// <see cref="RmaNumberSequenceTests"/>: allocation, migration, and persistence semantics that
/// a substitute would paper over. The test is skipped rather than failed when no server is
/// reachable, matching that suite's behaviour.
/// </remarks>
public sealed class WebWizardTests : IAsyncLifetime
{
    /// <summary>A serial the seeder creates and that no other test in this class consumes.</summary>
    private const string EligibleSerial = "AX2-0001-0001";

    /// <summary>Never present in the seed data, so intake must reject it and write nothing.</summary>
    private const string IneligibleSerial = "NO-SUCH-SERIAL-0000";

    /// <summary>
    /// The seeder's out-of-warranty, inside-the-window unit: at <see cref="SeedAnchor"/> it is past
    /// its warranty and grace period but still well inside the return window, so the wizard offers
    /// a paid repair — the path that walks the payment step.
    /// </summary>
    private const string PaidRepairSerial = "AX2-0001-0005";

    /// <summary>
    /// Seeded shipped 1,200 days ago, so far outside the return window that even a paid repair is
    /// not offered. Used alongside an accepted serial to force a partial create.
    /// </summary>
    private const string ExcludedSerial = "BX1-0003-0001";

    /// <summary>
    /// Seeded with an open return already, so intake stops for a person instead of creating one.
    /// </summary>
    private const string DuplicateSerial = "AX2-0001-0002";

    /// <summary>
    /// Intake asks who is returning the item as well as what it is, and the customer field is
    /// required. A seeded email is used rather than an arbitrary string so the fuzzy matcher
    /// resolves to exactly one customer; an ambiguous identity diverts to ChooseCustomer, which
    /// would make these tests assert on a different page than the one they mean to check.
    /// </summary>
    private const string CustomerEmail = "j.smith@example.com";

    private const string CurrencyCode = "USD";

    /// <summary>
    /// The fixture's reference date: today, read once, then pinned into the host clock. The seeder
    /// writes every warranty and ship date as an offset from it and the wizard's "today" is frozen
    /// at it, so the data and the assertions agree whatever day the suite runs on. A literal date
    /// only held the fixture still relative to a day that keeps receding.
    /// </summary>
    private static readonly DateOnly SeedAnchor = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly LiveDatabase _database = new("rma_web_test");
    private WebApplicationFactory<global::WebAppAnchor>? _factory;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();

        // Migrate and seed before the host starts, exactly as the deployment runbook does, so
        // the test exercises the same starting state an operator would produce.
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await DemoDataSeeder.SeedAsync(db, TestContext.Current.CancellationToken, SeedAnchor);
        }

        _factory = new WizardFactory(_database.ConnectionString, SeedAnchor);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    [Fact]
    public async Task A_stored_address_can_be_chosen_instead_of_a_typed_one()
    {
        // The other shipping test types an address, which the resolver fuzzy-matches. This one
        // posts an address id from the customer's own record, the path a returning customer takes
        // when the page offers their saved addresses. It used to land on the error page instead:
        // the action loads the customer through one repository and the workflow re-attached a
        // second, untracked copy of the same customer through the RMA graph, and EF refuses two
        // instances with one key.
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var rmaId = await ReachShippingStepAsync(client, ct);
        var addressId = await DefaultAddressIdAsync(ct);

        var shippingPage = await client.GetAsync($"/RmaWizard/Shipping/{rmaId}", ct);
        Assert.Equal(HttpStatusCode.OK, shippingPage.StatusCode);
        var shippingHtml = await shippingPage.Content.ReadAsStringAsync(ct);

        var shippingStep = await PostStepAsync(
            client,
            $"/RmaWizard/Shipping/{rmaId}",
            TokenOf(shippingHtml),
            [("RmaId", rmaId.ToString()), ("AddressId", addressId.ToString())],
            ct);

        // A step that re-renders its own form does not redirect, so this catches a stored address
        // being refused as well as a step that never got as far as saving.
        Assert.True(
            shippingStep.Redirect is not null,
            $"the shipping step did not advance: {(int)shippingStep.Page.StatusCode}");
        Assert.Contains($"/RmaWizard/Confirm/{rmaId}", shippingStep.Path);

        // Persisted, not merely accepted: the id the customer picked is the one on the request.
        await using var db = CreateContext();
        var stored = await db.RmaRequests.SingleAsync(r => r.Id == rmaId, ct);
        Assert.Equal(addressId, stored.ShipToAddressId);
    }

    [Fact]
    public async Task Confirming_a_return_persists_the_request_and_its_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        // Step 1: serial in.
        var startStep = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);

        // The triage page is addressed by request id, so the path carries the id rather than a
        // query string. Everything after this point is addressed the same way, which is what
        // makes the id the thing worth holding on to.
        var triagePath = startStep.Path;
        Assert.StartsWith("/RmaWizard/Triage/", triagePath, StringComparison.Ordinal);

        // Step 2: describe the fault.
        var rmaId = IdFromPath(triagePath);
        var triagePage = await client.GetAsync($"/RmaWizard/Triage/{rmaId}", ct);
        var triageStep = await PostStepAsync(
            client,
            $"/RmaWizard/Triage/{rmaId}",
            TokenOf(await triagePage.Content.ReadAsStringAsync(ct)),
            [("Statement", "The screen stays black although the fan is running.")],
            ct);

        Assert.Equal($"/RmaWizard/Problems/{rmaId}", triageStep.Path);

        // Step 3: per-line detail for the bench.
        var problemsPage = await client.GetAsync($"/RmaWizard/Problems/{rmaId}", ct);
        var problemHtml = await problemsPage.Content.ReadAsStringAsync(ct);
        var deviceId = await DeviceIdAsync(EligibleSerial, ct);

        var problemsStep = await PostStepAsync(
            client,
            "/RmaWizard/Problems",
            TokenOf(problemHtml),
            [
                ("RmaId", rmaId.ToString()),
                ("Lines[0].DeviceId", deviceId.ToString()),
                ("Lines[0].SerialNumber", EligibleSerial),
                ("Lines[0].Description", "Screen stays black although the fan is running."),
                ("Lines[0].WhatCustomerTried", "Reseated the cable and tried a second outlet."),
            ],
            ct);

        Assert.Equal($"/RmaWizard/Shipping/{rmaId}", problemsStep.Path);

        // Step 4: where to send the replacement. Free text forces the fuzzy matcher, so this
        // step is not quietly satisfied by picking a stored address behind the scenes.
        var shippingPage = await client.GetAsync($"/RmaWizard/Shipping/{rmaId}", ct);
        var shippingHtml = await shippingPage.Content.ReadAsStringAsync(ct);
        var shippingStep = await PostStepAsync(
            client,
            $"/RmaWizard/Shipping/{rmaId}",
            TokenOf(shippingHtml),
            [
                // The action resolves the request by the posted id, not by the id in the URL, so
                // the hidden field is part of the step's contract rather than decoration. The
                // posted RmaNumber is omitted deliberately: the action only reads it to redisplay
                // a validation error, so sending it would assert nothing.
                ("RmaId", rmaId.ToString()),
                ("FreeTextAddress", "Unit 4, Kingsway Court, Leeds LS1 2AB"),
            ],
            ct);

        Assert.Contains($"/RmaWizard/Confirm/{rmaId}", shippingStep.Path);

        // Step 5: the customer reviews and commits.
        var confirmPage = await client.GetAsync(shippingStep.Path, ct);
        var confirmStep = await PostStepAsync(
            client,
            $"/RmaWizard/Confirm/{rmaId}",
            TokenOf(await confirmPage.Content.ReadAsStringAsync(ct)),
            [("RmaId", rmaId.ToString())],
            ct);

        Assert.Equal(HttpStatusCode.OK, confirmStep.Page.StatusCode);
        Assert.Contains(
            "confirmed",
            await confirmStep.Page.Content.ReadAsStringAsync(ct),
            StringComparison.OrdinalIgnoreCase);

        // The point of the test. Reading the rows back is what proves each step committed;
        // a request that only ever existed in the change tracker would pass every other suite.
        await using var db = CreateContext();

        var request = await db.RmaRequests
            .Include(r => r.Lines)
            .ThenInclude(l => l.Device)
            .Include(r => r.ShipToAddress)
            .SingleOrDefaultAsync(r => r.Id == rmaId, ct);

        Assert.NotNull(request);
        Assert.StartsWith("RMA-", request!.RmaNumber, StringComparison.Ordinal);
        Assert.Equal(RmaRequestStatus.AwaitingShipment, request.Status);
        Assert.NotEqual(Guid.Empty, request.ShipToAddressId);
        Assert.NotNull(request.ShipToAddress);

        // A typed address that matches nothing is never shipped to as typed: the resolver
        // substitutes the customer's own default and the step hands the customer a confirmation
        // page instead. So the address that was stored is one of the customer's, not the string
        // from the form. This is the assertion that matters, and it is the reason the step
        // asserts ownership rather than echoing the value posted.
        Assert.NotNull(request.CustomerId);
        Assert.Equal(request.CustomerId, request.ShipToAddress!.CustomerId);

        // The substitution was surfaced rather than applied silently.
        Assert.Contains(
            "fallback",
            shippingStep.Redirect ?? shippingStep.Path,
            StringComparison.OrdinalIgnoreCase);

        // The line is asserted through its device, which is where the serial lives; the line
        // itself records only the intake detail and the triage result.
        var line = Assert.Single(request.Lines);
        Assert.Equal(EligibleSerial, line.Device!.SerialNumber);
        Assert.Equal(deviceId, line.DeviceId);
        Assert.False(string.IsNullOrWhiteSpace(line.ProblemDescription));
        Assert.False(string.IsNullOrWhiteSpace(line.WhatCustomerTried));

        // The specific decision reason and the rules that produced it are both stored, so an
        // operator can see afterwards on what grounds the return was allowed rather than
        // trusting the customer-facing page.
        Assert.Equal(nameof(RmaDecisionReason.InWarrantyConfirmedDefect), request.EligibilityReason);
        Assert.False(string.IsNullOrWhiteSpace(request.PolicyRuleTrace));
        Assert.Contains("warranty.tier", request.PolicyRuleTrace!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partial_create_still_warns_about_excluded_items_after_a_refresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        // One device the policy accepts and one it refuses, so the request is created from part of
        // what was handed over and the customer has to be told about the rest.
        var startStep = await BeginAsync(
            client,
            CustomerEmail,
            $"{EligibleSerial}\n{ExcludedSerial}",
            ct);

        var triagePath = startStep.Path;
        Assert.StartsWith("/RmaWizard/Triage/", triagePath, StringComparison.Ordinal);

        var rmaId = IdFromPath(triagePath);

        // The excluded serial is named on the first page, so the customer can act on it rather
        // than assume the whole list was accepted.
        var firstRender = await client.GetAsync(triagePath, ct);
        Assert.Equal(HttpStatusCode.OK, firstRender.StatusCode);

        var firstHtml = await firstRender.Content.ReadAsStringAsync(ct);
        Assert.Contains(ExcludedSerial, firstHtml, StringComparison.Ordinal);
        Assert.Contains(EligibleSerial, firstHtml, StringComparison.Ordinal);

        // Only the accepted device became a line. The excluded one is reported and dropped, so it
        // is deliberately absent from what the bench will see.
        await using var db = CreateContext();
        var request = await db.RmaRequests
            .Include(r => r.Lines)
            .ThenInclude(l => l.Device)
            .SingleAsync(r => r.Id == rmaId, ct);

        Assert.Single(request.Lines);
        Assert.Equal(EligibleSerial, request.Lines.Single().Device!.SerialNumber);

        // The exclusion is persisted with the request, not kept in the session. A refresh can
        // rebuild this page from the database alone, and a new session cannot lose the notice.
        var exclusions = await db.RmaExclusions
            .Where(e => e.RmaRequestId == rmaId)
            .OrderBy(e => e.SortOrder)
            .ToListAsync(ct);

        var persisted = Assert.Single(exclusions);
        Assert.Equal(ExcludedSerial, persisted.SerialNumber);

        // The regression: a refresh is the most likely thing a customer does, and the warning is
        // a disclosure that part of their submission was refused. Losing it on refresh would let
        // them finish the return believing every item is being taken back.
        var refreshed = await client.GetAsync(triagePath, ct);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var refreshedHtml = await refreshed.Content.ReadAsStringAsync(ct);
        Assert.Contains(ExcludedSerial, refreshedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_out_of_warranty_item_reaches_the_payment_step_and_is_billed()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        // Step 1: an expired unit is no longer a dead end. It is not refused and it is not
        // treated as free: the wizard keeps it in scope by quoting a paid repair.
        var startStep = await BeginAsync(client, CustomerEmail, PaidRepairSerial, ct);

        var triagePath = startStep.Path;
        Assert.StartsWith("/RmaWizard/Triage/", triagePath, StringComparison.Ordinal);
        var rmaId = IdFromPath(triagePath);

        // Step 2: describe the fault.
        var triagePage = await client.GetAsync($"/RmaWizard/Triage/{rmaId}", ct);
        var triageStep = await PostStepAsync(
            client,
            $"/RmaWizard/Triage/{rmaId}",
            TokenOf(await triagePage.Content.ReadAsStringAsync(ct)),
            [("Statement", "The screen stays black although the fan is running.")],
            ct);

        Assert.Equal($"/RmaWizard/Problems/{rmaId}", triageStep.Path);

        // Step 3: per-line detail for the bench.
        var problemsPage = await client.GetAsync($"/RmaWizard/Problems/{rmaId}", ct);
        var problemHtml = await problemsPage.Content.ReadAsStringAsync(ct);
        var deviceId = await DeviceIdAsync(PaidRepairSerial, ct);

        var problemsStep = await PostStepAsync(
            client,
            "/RmaWizard/Problems",
            TokenOf(problemHtml),
            [
                ("RmaId", rmaId.ToString()),
                ("Lines[0].DeviceId", deviceId.ToString()),
                ("Lines[0].SerialNumber", PaidRepairSerial),
                ("Lines[0].Description", "Screen stays black although the fan is running."),
                ("Lines[0].WhatCustomerTried", "Reseated the cable and tried a second outlet."),
            ],
            ct);

        Assert.Equal($"/RmaWizard/Shipping/{rmaId}", problemsStep.Path);

        // Step 4: shipping. A paid return must land on the payment step, not the confirmation
        // page, because nothing has been authorised yet. This redirection is what proves the
        // payment step is reachable at all.
        var shippingPage = await client.GetAsync($"/RmaWizard/Shipping/{rmaId}", ct);
        var shippingStep = await PostStepAsync(
            client,
            $"/RmaWizard/Shipping/{rmaId}",
            TokenOf(await shippingPage.Content.ReadAsStringAsync(ct)),
            [
                ("RmaId", rmaId.ToString()),
                ("FreeTextAddress", "Unit 4, Kingsway Court, Leeds LS1 2AB"),
            ],
            ct);

        Assert.Equal($"/RmaWizard/Payment/{rmaId}", shippingStep.Path);

        // The request is parked as pending payment before the customer does anything, and the
        // repair price from the product table is what the payment step will authorise.
        await using var db = CreateContext();
        var pending = await db.RmaRequests.SingleAsync(r => r.Id == rmaId, ct);
        Assert.Equal(RmaRequestStatus.PendingPayment, pending.Status);
        Assert.Equal(RmaRequestKind.PaidRepair, pending.Kind);
        Assert.Equal(69m, pending.DepositAmount);
        Assert.Equal(0m, pending.ShippingCharge);

        // Step 5: the customer authorises. The accept-all gateway used by the local build always
        // succeeds, so the walk continues to the confirmation page; a failure here would stay on
        // the payment step instead.
        var paymentPage = await client.GetAsync(shippingStep.Path, ct);
        Assert.Equal(HttpStatusCode.OK, paymentPage.StatusCode);
        var paymentHtml = await paymentPage.Content.ReadAsStringAsync(ct);

        var paymentStep = await PostStepAsync(
            client,
            $"/RmaWizard/Payment/{rmaId}",
            TokenOf(paymentHtml),
            [
                ("RmaId", rmaId.ToString()),
                ("PaymentMethodToken", "tok_test"),
            ],
            ct);

        Assert.Equal($"/RmaWizard/Confirm/{rmaId}", paymentStep.Path);

        // The charge is surfaced on the review page before the final submit, so the customer sees
        // the product's repair price rather than trusting a blank authorisation.
        var confirmPage = await paymentStep.Page.Content.ReadAsStringAsync(ct);
        Assert.Contains("Repair fee", confirmPage, StringComparison.Ordinal);
        Assert.Contains("69.00 USD", confirmPage, StringComparison.Ordinal);

        // Step 6: confirm commits the return for real.
        var confirmStep = await PostStepAsync(
            client,
            $"/RmaWizard/Confirm/{rmaId}",
            TokenOf(confirmPage),
            [("RmaId", rmaId.ToString())],
            ct);

        Assert.Equal(HttpStatusCode.OK, confirmStep.Page.StatusCode);
        Assert.Contains(
            "confirmed",
            await confirmStep.Page.Content.ReadAsStringAsync(ct),
            StringComparison.OrdinalIgnoreCase);

        // The billed amount is carried all the way onto the request, with the authorisation id
        // recorded against it, so the bench sees what the customer was charged for. A fresh
        // context reads the stored row again, because the earlier one still tracks the parked
        // pre-payment state it loaded.
        await using (var readBack = CreateContext())
        {
            var done = await readBack.RmaRequests.SingleAsync(r => r.Id == rmaId, ct);
            Assert.Equal(RmaRequestStatus.AwaitingShipment, done.Status);
            Assert.Equal(69m, done.DepositAmount);
            Assert.False(string.IsNullOrWhiteSpace(done.PaymentTransactionId));
        }
    }

    [Fact]
    public async Task A_liquid_damage_statement_is_refused_by_the_policy_at_triage()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var startStep = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);

        Assert.StartsWith("/RmaWizard/Triage/", startStep.Path, StringComparison.Ordinal);
        var rmaId = IdFromPath(startStep.Path);

        var triagePage = await client.GetAsync($"/RmaWizard/Triage/{rmaId}", ct);

        // The statement is the only place a fault description exists, so this is where the
        // excluded-cause rules first have something to match against.
        var triageStep = await PostStepAsync(
            client,
            $"/RmaWizard/Triage/{rmaId}",
            TokenOf(await triagePage.Content.ReadAsStringAsync(ct)),
            [("Statement", "I spilled water on it and now it will not power on.")],
            ct);

        // A refusal re-renders on the same URL instead of handing the customer to the problems
        // step, which would collect details for a return that will not happen.
        Assert.Equal($"/RmaWizard/Triage/{rmaId}", triageStep.Path);

        var html = await triageStep.Page.Content.ReadAsStringAsync(ct);
        Assert.Contains("We cannot take this return", html, StringComparison.Ordinal);
        Assert.Contains("Liquid ingress is not covered", html, StringComparison.Ordinal);

        // Persisted, and carrying the rule that produced it, so support can see why.
        await using var db = CreateContext();
        var refused = await db.RmaRequests.SingleAsync(r => r.Id == rmaId, ct);
        Assert.Equal(RmaRequestStatus.Rejected, refused.Status);
        Assert.Equal(nameof(RmaDecisionReason.ExcludedCause), refused.EligibilityReason);
        Assert.Contains("cause.liquid", refused.PolicyRuleTrace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_draft_cannot_be_paid_for_and_confirmed_into_awaiting_shipment()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var startStep = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);

        Assert.StartsWith("/RmaWizard/Triage/", startStep.Path, StringComparison.Ordinal);
        var rmaId = IdFromPath(startStep.Path);

        // A covered return is a Draft with nothing to pay. That zero total is what made the
        // original bypass work: the payment step authorised it and moved the request to
        // Approved, without triage, problems, shipping, or money.
        await using (var before = CreateContext())
        {
            var draft = await before.RmaRequests.SingleAsync(r => r.Id == rmaId, ct);
            Assert.Equal(RmaRequestStatus.Draft, draft.Status);
            Assert.Equal(0m, draft.ShippingCharge + draft.DepositAmount);
        }

        var paymentPage = await client.GetAsync($"/RmaWizard/Payment/{rmaId}", ct);
        var paymentStep = await PostStepAsync(
            client,
            $"/RmaWizard/Payment/{rmaId}",
            TokenOf(await paymentPage.Content.ReadAsStringAsync(ct)),
            [("RmaId", rmaId.ToString()), ("PaymentMethodToken", "tok_test")],
            ct);

        // Refused in place, not redirected to the confirmation page.
        Assert.Equal($"/RmaWizard/Payment/{rmaId}", paymentStep.Path);
        Assert.Contains(
            "That step is out of order",
            await paymentStep.Page.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);

        // And the confirmation step cannot turn the untouched draft into a committed return.
        var confirmPage = await client.GetAsync($"/RmaWizard/Confirm/{rmaId}", ct);
        var confirmStep = await PostStepAsync(
            client,
            "/RmaWizard/Confirm",
            TokenOf(await confirmPage.Content.ReadAsStringAsync(ct)),
            [("RmaId", rmaId.ToString())],
            ct);

        Assert.Contains(
            "That step is out of order",
            await confirmStep.Page.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);

        await using var db = CreateContext();
        var request = await db.RmaRequests.SingleAsync(r => r.Id == rmaId, ct);
        Assert.Equal(RmaRequestStatus.Draft, request.Status);
        Assert.Null(request.PaymentTransactionId);
    }

    [Theory]
    [InlineData(RmaRequestStatus.Approved, "Payment", "Confirm")]
    [InlineData(RmaRequestStatus.PendingPayment, "Confirm", "Payment")]
    public async Task An_out_of_order_step_links_to_where_the_return_actually_is(
        RmaRequestStatus status,
        string postedStep,
        string expectedStep)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var startStep = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);

        var rmaId = IdFromPath(startStep.Path);

        // Put the return where a customer with two tabs open would have it: the guards then
        // refuse the stale step, and the page it shows must offer the step the return is at.
        await using (var db = CreateContext())
        {
            var request = await db.RmaRequests.SingleAsync(r => r.Id == rmaId, ct);
            request.Status = status;
            await db.SaveChangesAsync(ct);
        }

        var stalePage = await client.GetAsync($"/RmaWizard/{postedStep}/{rmaId}", ct);
        var refused = await PostStepAsync(
            client,
            $"/RmaWizard/{postedStep}/{rmaId}",
            TokenOf(await stalePage.Content.ReadAsStringAsync(ct)),
            postedStep == "Payment"
                ? [("RmaId", rmaId.ToString()), ("PaymentMethodToken", "tok_test")]
                : [("RmaId", rmaId.ToString())],
            ct);

        var html = await refused.Page.Content.ReadAsStringAsync(ct);
        Assert.Contains("That step is out of order", html, StringComparison.Ordinal);

        // The old page offered triage for every refusal. A return past Draft refuses triage too,
        // so the only way out of that loop was to abandon the return and start again.
        Assert.Contains($"/RmaWizard/{expectedStep}/{rmaId}", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"/RmaWizard/Triage/{rmaId}", html, StringComparison.Ordinal);

        // Follow the link. An href that names the right step is not the same as a page that
        // opens it, and the whole complaint about this link was that it led to a second refusal.
        var resumed = await client.GetAsync($"/RmaWizard/{expectedStep}/{rmaId}", ct);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.DoesNotContain(
            "That step is out of order",
            await resumed.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_return_held_for_review_offers_no_link_into_a_step_that_would_refuse_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        // AX2-0001-0002 already carries an open return in the seed, so intake stops for a person.
        var startStep = await BeginAsync(client, CustomerEmail, DuplicateSerial, ct);

        // The intake stop renders on the page the chooser POST ended at.
        Assert.Equal("/RmaWizard/ChooseCustomer", startStep.Path);

        var html = await startStep.Page.Content.ReadAsStringAsync(ct);
        Assert.Contains("A person will take a look", html, StringComparison.Ordinal);

        // A review-pending return has no step the customer may open: triage is Draft-only and
        // every later step refuses a request that has not reached it, so "Continue this return"
        // led to a second page saying the step was out of order. Hiding it is the product call —
        // a person owns this return now, and offering a step back into it would be a loop.
        Assert.DoesNotContain("Continue this return", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"/RmaWizard/Triage/", html, StringComparison.Ordinal);
        Assert.Contains("Start another return", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_step_on_a_review_pending_return_offers_no_step_to_continue()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var startStep = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);

        var rmaId = IdFromPath(startStep.Path);

        // A specialist owns this return, so the only page it can show is the outcome. This is the
        // case the intake-stop test cannot reach: that refusal writes no request, so its id is
        // empty and the button is hidden for the wrong reason. Here the id is real, and what has
        // to hold the button back is the status itself.
        await using (var db = CreateContext())
        {
            var request = await db.RmaRequests.SingleAsync(r => r.Id == rmaId, ct);
            request.Status = RmaRequestStatus.NeedsReview;
            await db.SaveChangesAsync(ct);
        }

        var stalePage = await client.GetAsync($"/RmaWizard/Confirm/{rmaId}", ct);
        var refused = await PostStepAsync(
            client,
            $"/RmaWizard/Confirm/{rmaId}",
            TokenOf(await stalePage.Content.ReadAsStringAsync(ct)),
            [("RmaId", rmaId.ToString())],
            ct);

        var html = await refused.Page.Content.ReadAsStringAsync(ct);
        Assert.Contains("That step is out of order", html, StringComparison.Ordinal);

        // Triage is Draft-only, and Payment and Confirm refuse this status too, so every link the
        // page could offer would land on a second refusal. The customer starts again instead.
        Assert.DoesNotContain("Continue this return", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"/RmaWizard/Triage/{rmaId}", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"/RmaWizard/Payment/{rmaId}", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"/RmaWizard/Confirm/{rmaId}", html, StringComparison.Ordinal);
        Assert.Contains("Start another return", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partial_serial_is_not_resolved_into_a_return_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        // Counted before the attempt, because the claim under test is that nothing at all was
        // written: not a return, not a line. Naming a device instead would make the assertion
        // depend on which of the AX2-0001-* units a fuzzy matcher happened to score first.
        await using var db = CreateContext();
        var requestsBefore = await db.RmaRequests.CountAsync(ct);
        var linesBefore = await db.RmaLines.CountAsync(ct);

        var start = await client.GetAsync("/RmaWizard/Start", ct);

        // A prefix of a real serial, not a serial: fuzzy intake matching scored this 0.90
        // against AX2-0001-0001 and opened a return for a unit the visitor does not have.
        var started = await PostStepAsync(
            client,
            "/RmaWizard/Start",
            TokenOf(await start.Content.ReadAsStringAsync(ct)),
            [("Customer", CustomerEmail), ("Identifiers", "AX2-0001"), ("CurrencyCode", CurrencyCode)],
            ct);

        Assert.StartsWith("/RmaWizard/ChooseCustomer", started.Path, StringComparison.Ordinal);

        var chooserHtml = await started.Page.Content.ReadAsStringAsync(ct);
        var carried = Regex.Match(chooserHtml, "name=\"identifiers\"[^>]*value=\"(?<ids>[^\"]*)\"");
        Assert.True(carried.Success, "the chooser did not carry the identifiers on");

        var startStep = await PostStepAsync(
            client,
            "/RmaWizard/ChooseCustomer",
            TokenOf(chooserHtml),
            [
                ("customerId", string.Empty),
                ("identifiers", carried.Groups["ids"].Value),
                ("offer", OfferOf(chooserHtml)),
            ],
            ct);

        Assert.DoesNotContain("/RmaWizard/Triage/", startStep.Path, StringComparison.Ordinal);

        var html = await startStep.Page.Content.ReadAsStringAsync(ct);
        Assert.Contains("cannot take this return", html, StringComparison.OrdinalIgnoreCase);

        // A mistyped serial is a lookup failure, not a policy refusal, and the page has to read
        // that way: it names what was typed and tells the customer to check it. Wording that
        // says the item is not in the catalogue leaves them believing the return was judged and
        // turned down, with nothing to act on.
        Assert.Contains("could not find an item with the identifier", html, StringComparison.Ordinal);
        Assert.Contains("AX2-0001", html, StringComparison.Ordinal);
        Assert.Contains("Check what you typed", html, StringComparison.Ordinal);

        Assert.Equal(requestsBefore, await db.RmaRequests.CountAsync(ct));
        Assert.Equal(linesBefore, await db.RmaLines.CountAsync(ct));

        // The consequence that actually hurts a customer: a line created here would sit against
        // the real unit, and the owner's own return would then be refused as a duplicate. So the
        // unit the prefix pointed at must still start a return normally.
        var ownerStep = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);

        Assert.StartsWith("/RmaWizard/Triage/", ownerStep.Path, StringComparison.Ordinal);

        // And the owner's attempt is the only thing that wrote a row.
        Assert.Equal(requestsBefore + 1, await db.RmaRequests.CountAsync(ct));
        Assert.Equal(linesBefore + 1, await db.RmaLines.CountAsync(ct));
    }

    [Fact]
    public async Task A_draft_left_behind_does_not_hold_the_unit_for_its_owner()
    {
        var ct = TestContext.Current.CancellationToken;

        // Starting a return only needs the serial, so intake treating a draft as an open return
        // was a lock anyone could place in two form posts and never lift: the owner then met the
        // duplicate rule on their own unit. Only a return that has reached AwaitingShipment counts
        // as in progress now, and confirmation re-checks the unit with its own id excluded.
        var squatterStep = await StartReturnAsync(CreateClient());
        Assert.StartsWith("/RmaWizard/Triage/", squatterStep.Path, StringComparison.Ordinal);

        var ownerStep = await StartReturnAsync(CreateClient());

        // The review outcome renders at /RmaWizard/Start, so reaching triage is what says intake
        // did not refuse. The second assertion keeps the fix honest: the owner gets a return of
        // their own instead of being handed the squatter's to edit.
        Assert.StartsWith("/RmaWizard/Triage/", ownerStep.Path, StringComparison.Ordinal);
        Assert.NotEqual(IdFromPath(squatterStep.Path), IdFromPath(ownerStep.Path));

        async Task<StepOutcome> StartReturnAsync(HttpClient client) =>
            await BeginAsync(client, CustomerEmail, PaidRepairSerial, ct);
    }

    [Fact]
    public async Task A_liquid_damage_description_at_the_problems_step_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var startStep = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);

        var rmaId = IdFromPath(startStep.Path);
        var triagePage = await client.GetAsync($"/RmaWizard/Triage/{rmaId}", ct);

        // An innocuous statement, so triage has nothing to refuse.
        var triageStep = await PostStepAsync(
            client,
            $"/RmaWizard/Triage/{rmaId}",
            TokenOf(await triagePage.Content.ReadAsStringAsync(ct)),
            [("Statement", "The screen stays black although the fan is running.")],
            ct);

        Assert.Equal($"/RmaWizard/Problems/{rmaId}", triageStep.Path);

        var problemsPage = await client.GetAsync($"/RmaWizard/Problems/{rmaId}", ct);

        // The damage is described only here. It has to stop the return all the same, or one
        // unwatched field is all it takes to skip the excluded-cause rules.
        var problemsStep = await PostStepAsync(
            client,
            "/RmaWizard/Problems",
            TokenOf(await problemsPage.Content.ReadAsStringAsync(ct)),
            [
                ("RmaId", rmaId.ToString()),
                ("Lines[0].DeviceId", (await DeviceIdAsync(EligibleSerial, ct)).ToString()),
                ("Lines[0].SerialNumber", EligibleSerial),
                ("Lines[0].Description", "I spilled coffee on it and now it will not switch on."),
                ("Lines[0].WhatCustomerTried", "Dried it out and left it overnight."),
            ],
            ct);

        // Refused in place rather than handed to the shipping step, which would have collected
        // an address for a return that will not happen.
        Assert.Equal("/RmaWizard/Problems", problemsStep.Path);
        Assert.Contains(
            "cannot take this return",
            await problemsStep.Page.Content.ReadAsStringAsync(ct),
            StringComparison.OrdinalIgnoreCase);

        await using var db = CreateContext();
        var request = await db.RmaRequests.SingleAsync(r => r.Id == rmaId, ct);

        Assert.Equal(RmaRequestStatus.Rejected, request.Status);
        Assert.Equal(nameof(RmaDecisionReason.ExcludedCause), request.EligibilityReason);

        // Both decisions are on the record: intake cleared the unit and triage cleared the
        // statement, so the trace has to show the rule that fired here rather than replace them.
        Assert.Contains("cause.liquid", request.PolicyRuleTrace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partial_serial_still_resolves_on_the_fuzzy_path_the_agent_tools_use()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var db = CreateContext();
        var devices = new DeviceRepository(db, Options.Create(new DeviceLookupOptions()));
        var today = SeedAnchor;

        // Intake is exact-only, but the MCP tools (lookup_device, check_coverage, get_fee_quote)
        // still resolve a truncated serial. An operator or agent that already holds partial
        // information is not the enumeration risk an anonymous wizard visitor is: the tool call
        // needs a tool server, and it does not open a return against whatever it finds.
        var fuzzy = await devices.LookupAsync("AX2-0001", today, ct);

        Assert.Equal("fuzzy", fuzzy.MatchedOn);
        Assert.NotNull(fuzzy.Device);

        // The same identifier on the intake path resolves nothing at all.
        var exact = await devices.LookupExactAsync("AX2-0001", today, ct);

        Assert.Null(exact.Device);
    }

    [Theory]
    [InlineData("ax2-0001-0001")]
    [InlineData("a4:c1:f2:e3:00:01")]
    public async Task An_exact_identifier_resolves_whatever_its_case_or_separator(string identifier)
    {
        // Exact is not literal. A serial in lower case, and the same unit's MAC with colons, are
        // both things a customer can read off the label and type in full; refusing them would
        // make the exact-only rule a usability trap rather than an anti-enumeration measure.
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var startStep = await BeginAsync(client, CustomerEmail, identifier, ct);

        Assert.StartsWith("/RmaWizard/Triage/", startStep.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ambiguous_customer_is_handed_to_them_to_choose_rather_than_guessed()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        // "Smyth" matches two seeded accounts, Josephine Smyth and Anne-Marie O'Brien-Smyth.
        // The wizard must show both rather than attach the return to one of them, because the
        // wrong account would expose its address and its returns history.
        var start = await client.GetAsync("/RmaWizard/Start", ct);
        var startStep = await PostStepAsync(
            client,
            "/RmaWizard/Start",
            TokenOf(await start.Content.ReadAsStringAsync(ct)),
            [
                ("Customer", "Smyth"),
                ("Identifiers", EligibleSerial),
                ("CurrencyCode", CurrencyCode),
            ],
            ct);

        Assert.Equal(HttpStatusCode.OK, startStep.Page.StatusCode);
        Assert.StartsWith("/RmaWizard/ChooseCustomer", startStep.Path, StringComparison.Ordinal);

        // Both candidates survive the redirect onto the chooser, so the posted choice is
        // genuinely informed and no return has been created for either account yet.
        var chooserHtml = await startStep.Page.Content.ReadAsStringAsync(ct);

        // Both candidates are listed, and both are masked. The list used to be empty: the view
        // cast the controller's List<CustomerMatchView> with `as IEnumerable<dynamic>`, which
        // is IEnumerable<object> and therefore always null. Names prove the list rendered;
        // the full contact details prove masking still holds on the page.
        Assert.Contains("Josephine S.", chooserHtml, StringComparison.Ordinal);
        Assert.Contains("Anne-Marie O.", chooserHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("j.smith@example.com", chooserHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("am.obrien@example.com", chooserHtml, StringComparison.Ordinal);
        Assert.Contains(EligibleSerial, chooserHtml, StringComparison.Ordinal);

        await using var db = CreateContext();
        var chosen = await db.Customers.SingleAsync(
            c => c.Email == "j.smith@example.com" && c.ExternalCustomerId == "EXT-10001", ct);

        var chosenStep = await PostStepAsync(
            client,
            "/RmaWizard/ChooseCustomer",
            TokenOf(chooserHtml),
            [
                ("CustomerId", chosen.Id.ToString()),
                ("Identifiers", EligibleSerial),
                ("offer", OfferOf(chooserHtml)),
            ],
            ct);

        var triagePath = chosenStep.Path;
        Assert.StartsWith("/RmaWizard/Triage/", triagePath, StringComparison.Ordinal);

        var request = await db.RmaRequests
            .SingleAsync(r => r.Id == IdFromPath(triagePath), ct);

        // The wizard did not simply trust the claimed identity: the request is attached to the
        // account that was selected, and to nobody else.
        Assert.Equal(chosen.Id, request.CustomerId);
    }

    [Fact]
    public async Task A_customer_id_the_visitor_was_never_offered_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        // An ambiguous identity puts the visitor on the chooser, where the server remembers which
        // accounts it offered. Everything the chooser can submit afterwards has to come from that
        // list: this is the only wizard POST that attaches a customer by id, and the id reaches
        // the form as a value a caller can rewrite.
        var start = await client.GetAsync("/RmaWizard/Start", ct);
        var chooser = await PostStepAsync(
            client,
            "/RmaWizard/Start",
            TokenOf(await start.Content.ReadAsStringAsync(ct)),
            [
                ("Customer", "Smyth"),
                ("Identifiers", EligibleSerial),
                ("CurrencyCode", CurrencyCode),
            ],
            ct);

        Assert.StartsWith("/RmaWizard/ChooseCustomer", chooser.Path, StringComparison.Ordinal);

        var chooserHtml = await chooser.Page.Content.ReadAsStringAsync(ct);

        await using var db = CreateContext();
        var requestsBefore = await db.RmaRequests.CountAsync(ct);
        var linesBefore = await db.RmaLines.CountAsync(ct);

        // A real account from the same seed that this visitor was never shown. Eight are seeded
        // and only the two Smyths are offered, so the pick cannot fail for want of a candidate.
        var unoffered = (await db.Customers.Select(c => c.Id).ToListAsync(ct))
            .First(id => !chooserHtml.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase));

        var rejected = await PostStepAsync(
            client,
            "/RmaWizard/ChooseCustomer",
            TokenOf(chooserHtml),
            [
                ("CustomerId", unoffered.ToString()),
                ("Identifiers", EligibleSerial),
                ("offer", OfferOf(chooserHtml)),
            ],
            ct);

        // Back to the start, not an error page: the posted identity is simply not one this
        // visitor may choose. A return attached to it would have shown a stranger's stored
        // address, and put a line against a unit they do not own.
        Assert.Equal("/RmaWizard/Start", rejected.Path);
        Assert.Equal(requestsBefore, await db.RmaRequests.CountAsync(ct));
        Assert.Equal(linesBefore, await db.RmaLines.CountAsync(ct));
    }

    [Fact]
    public async Task An_ineligible_serial_is_reported_and_creates_no_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var startStep = await BeginAsync(client, CustomerEmail, IneligibleSerial, ct);

        // The refusal renders where the POST ended, which is the chooser the visitor was sent
        // through: intake answers every contact lookup with that page.
        Assert.Equal("/RmaWizard/ChooseCustomer", startStep.Path);
        Assert.Contains(
            "cannot take this return",
            await startStep.Page.Content.ReadAsStringAsync(ct),
            StringComparison.OrdinalIgnoreCase);

        // Nothing may be left behind by a rejected return: a half-written request would consume
        // a reference from the sequence and show up in staff queues.
        await using var db = CreateContext();
        var created = await db.RmaRequests
            .Include(r => r.Lines)
            .ThenInclude(l => l.Device)
            .Where(r => r.Lines.Any(l => l.Device!.SerialNumber == IneligibleSerial))
            .ToListAsync(ct);

        Assert.Empty(created);
    }

    [Fact]
    public async Task A_capability_step_cannot_be_stored_by_a_browser_or_a_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var startStep = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);
        var rmaId = IdFromPath(startStep.Path);

        var page = await client.GetAsync($"/RmaWizard/Triage/{rmaId}", ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        // The request id in the address bar is the capability. A page kept by a shared browser or
        // an intermediary outlives the session that produced it, and expiry only bounds drafts: a
        // page for a confirmed return would sit there indefinitely. no-store rather than no-cache,
        // because the second still allows a copy to be written to disk. Antiforgery marks this page
        // too, so the max-age is what distinguishes this middleware here; the redirect below is the
        // response only this middleware marks.
        Assert.NotNull(page.Headers.CacheControl);
        Assert.True(page.Headers.CacheControl!.NoStore);
        Assert.True(page.Headers.CacheControl.NoCache);
        Assert.Equal(TimeSpan.Zero, page.Headers.CacheControl.MaxAge);
        Assert.Contains("no-cache", page.Headers.Pragma.ToString(), StringComparison.OrdinalIgnoreCase);

        // The 302 a step POSTs carries the next capability address, so it must not be storable
        // either, or a cache would hold the redirect and keep sending people to that step.
        var token = TokenOf(await page.Content.ReadAsStringAsync(ct));
        var posted = await client.PostAsync(
            $"/RmaWizard/Triage/{rmaId}",
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("Statement", "The screen stays black although the fan is running."),
            ]),
            ct);

        Assert.Equal(HttpStatusCode.Found, posted.StatusCode);
        Assert.True(posted.Headers.CacheControl?.NoStore);

        // Every step POST answers the same way, and the rule is the verb rather than a list of
        // action names: /Problems, /Shipping, /Payment and /Confirm were all storable while the
        // predicate named Start and ChooseCustomer. Asserting one of them here is what keeps the
        // verb rule honest, because a list would pass everything above and fail only this.
        var problemsPage = await client.GetAsync($"/RmaWizard/Problems/{rmaId}", ct);
        Assert.Equal(HttpStatusCode.OK, problemsPage.StatusCode);

        var deviceId = await DeviceIdAsync(EligibleSerial, ct);
        var problems = await client.PostAsync(
            "/RmaWizard/Problems",
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>(
                    "__RequestVerificationToken",
                    TokenOf(await problemsPage.Content.ReadAsStringAsync(ct))),
                new KeyValuePair<string, string>("RmaId", rmaId.ToString()),
                new KeyValuePair<string, string>("Lines[0].DeviceId", deviceId.ToString()),
                new KeyValuePair<string, string>("Lines[0].SerialNumber", EligibleSerial),
                new KeyValuePair<string, string>("Lines[0].Description", "Screen stays black although the fan is running."),
                new KeyValuePair<string, string>("Lines[0].WhatCustomerTried", "Reseated the cable and tried a second outlet."),
            ]),
            ct);

        Assert.Equal(HttpStatusCode.Found, problems.StatusCode);
        Assert.StartsWith(
            $"/RmaWizard/Shipping/{rmaId}",
            problems.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        Assert.True(problems.Headers.CacheControl?.NoStore);

        // Scoped to the wizard: an ordinary page is not handed the directive by accident.
        var home = await client.GetAsync("/", ct);

        Assert.Null(home.Headers.CacheControl);
    }

    [Fact]
    public async Task The_chooser_cannot_be_stored_because_the_offer_key_is_a_capability()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var start = await client.GetAsync("/RmaWizard/Start", ct);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        using var posted = await client.PostAsync(
            "/RmaWizard/Start",
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>(
                    "__RequestVerificationToken",
                    TokenOf(await start.Content.ReadAsStringAsync(ct))),
                new KeyValuePair<string, string>("Customer", CustomerEmail),
                new KeyValuePair<string, string>("Identifiers", EligibleSerial),
                new KeyValuePair<string, string>("CurrencyCode", CurrencyCode),
            ]),
            ct);

        Assert.Equal(HttpStatusCode.Found, posted.StatusCode);

        // The 302 names the chooser with its offer key in the query, so a stored copy would send
        // the next visitor on that machine to this customer's account list.
        Assert.True(posted.Headers.CacheControl?.NoStore);

        var chooserAddress = posted.Headers.Location!.OriginalString;
        Assert.StartsWith("/RmaWizard/ChooseCustomer", chooserAddress, StringComparison.Ordinal);

        var chooser = await client.GetAsync(chooserAddress, ct);
        Assert.Equal(HttpStatusCode.OK, chooser.StatusCode);

        // The chooser is the same kind of secret as a step id. The offer key resolves server-side
        // to the accounts this visitor was shown, and the page hands back the account id to pick,
        // so a cached copy lets the next person start a return as this customer without ever
        // knowing their address.
        Assert.True(chooser.Headers.CacheControl?.NoStore);

        var html = await chooser.Content.ReadAsStringAsync(ct);
        var offer = OfferOf(html);
        var carried = Regex.Match(html, "name=\"identifiers\"[^>]*value=\"(?<ids>[^\"]*)\"");
        var account = Regex.Match(html, "name=\"customerId\" value=\"(?<id>[0-9a-fA-F-]{36})\"");

        Assert.False(string.IsNullOrEmpty(offer), "the chooser page carried no offer key");

        // Choosing an account answers with the new request id, so that 302 is addressed the same
        // way and must not be storable either.
        using var chosen = await client.PostAsync(
            "/RmaWizard/ChooseCustomer",
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", TokenOf(html)),
                new KeyValuePair<string, string>("customerId", account.Groups["id"].Value),
                new KeyValuePair<string, string>("identifiers", carried.Groups["ids"].Value),
                new KeyValuePair<string, string>("offer", offer),
            ]),
            ct);

        Assert.Equal(HttpStatusCode.Found, chosen.StatusCode);
        Assert.True(chosen.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task An_unconfirmed_draft_stops_working_when_the_capability_window_passes()
    {
        var ct = TestContext.Current.CancellationToken;

        // The host's clock, held here so this test can move it. The seed data stays authored
        // against the anchor; what changes is how long the link has been sitting in an inbox.
        var clock = new SettableTimeProvider(new DateTimeOffset(
            SeedAnchor.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));

        using var factory = new WizardFactory(_database.ConnectionString, SeedAnchor, clock);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

        client.DefaultRequestHeaders.Add("User-Agent", "WebWizardTests");

        var startStep = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);
        var rmaId = IdFromPath(startStep.Path);

        // Inside the window the same URL works, which is what makes the refusal below the
        // window's doing rather than the page having been broken all along.
        var fresh = await client.GetAsync($"/RmaWizard/Triage/{rmaId}", ct);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        // Minted while the link is still alive: the POST below is then a request a real browser
        // could make from a page it had already loaded before the window closed.
        var token = TokenOf(await fresh.Content.ReadAsStringAsync(ct));

        // Thirty days is the default the wizard is configured with; a day past it is the moment
        // the capability is supposed to stop being one.
        clock.Now = clock.Now.AddDays(31);

        var expired = await client.GetAsync($"/RmaWizard/Triage/{rmaId}", ct);
        var page = await expired.Content.ReadAsStringAsync(ct);

        // 410 rather than 404: the return really was here, and the customer needs to be told to
        // start again rather than to suspect a typo in the link.
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Contains("This return has expired", page, StringComparison.Ordinal);
        Assert.Contains("30 days", page, StringComparison.Ordinal);

        // Every later route-id step, not just the first: the guard is called at the top of each,
        // and a step that forgot to call it would otherwise hand out a page for a dead link.
        foreach (var action in new[] { "Problems", "Shipping", "Confirm" })
        {
            using var later = await client.GetAsync($"/RmaWizard/{action}/{rmaId}", ct);

            Assert.Equal(HttpStatusCode.Gone, later.StatusCode);
        }

        // The write path as well as the read one, asserted on the POST response itself rather
        // than on whatever a redirect leads to: the client does not follow redirects here, so a
        // step that accepted the statement and moved on would show up as a 302, not as the GET
        // step's expired page.
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("Statement", "The screen stays black although the fan is running."),
        ]);

        var posted = await client.PostAsync($"/RmaWizard/Triage/{rmaId}", form, ct);

        Assert.Equal(HttpStatusCode.Gone, posted.StatusCode);
        Assert.Contains(
            "This return has expired",
            await posted.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);

        // Expiring the link must not quietly change the return: it is still an unconfirmed draft
        // the customer can re-enter from the start, not a cancelled or confirmed request.
        await using var db = CreateContext();
        var stored = await db.RmaRequests.SingleAsync(r => r.Id == rmaId, ct);
        Assert.Equal(RmaRequestStatus.Draft, stored.Status);
    }

    [Fact]
    public async Task Anonymous_health_reports_status_and_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var response = await client.GetAsync("/health", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The same one-word answer the RAG host gives. The counts and the provider name describe
        // the deployment — corpus size, files that failed to parse, which model answers — and a
        // caller who is nobody has no business reading them.
        Assert.Equal("{\"status\":\"ok\"}", body);
        Assert.DoesNotContain("articleCount", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("loadErrorCount", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Readiness is what the TLS front door asks, on a timer, to decide whether this host may receive
    /// traffic. It has to answer while the database is reachable and stop answering when it is not:
    /// a readiness endpoint that always says yes is a host that is routed to while it cannot serve.
    /// </summary>
    [Fact]
    public async Task Readiness_answers_while_the_database_is_reachable_and_not_when_it_is_not()
    {
        var ct = TestContext.Current.CancellationToken;

        using var reachable = CreateClient();

        var ready = await reachable.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        // A readiness answer is public: one word, nothing about the corpus or the provider.
        Assert.Equal("{\"status\":\"ready\"}", await ready.Content.ReadAsStringAsync(ct));

        // Nothing on the connection string is listening, which is the shape of a host that started
        // before the database did and is still running.
        using var unreachable = _factory!.WithWebHostBuilder(
            builder => builder.UseSetting(
                "ConnectionStrings:Rma",
                "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1"));

        using var stranger = unreachable.CreateClient();

        var notReady = await stranger.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        Assert.Equal("{\"status\":\"unavailable\"}", await notReady.Content.ReadAsStringAsync(ct));

        // Liveness is a different question: the host is running, so /health stays 200.
        var alive = await stranger.GetAsync("/health", ct);

        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }

    [Fact]
    public async Task Operator_health_shows_counts_only_with_the_admin_key()
    {
        var ct = TestContext.Current.CancellationToken;
        const string key = "health-test-operator-key";

        using var factory = _factory!.WithWebHostBuilder(
            builder => builder.UseSetting(RagAdminGate.ApiKeyConfigurationKey, key));

        // A caller with no key, or the wrong one, is a stranger: the detail is not a reward for
        // guessing. Once a key is configured it is required from every caller, loopback
        // included — the same rule, from the same gate, as the RAG host's operator endpoints.
        using var stranger = factory.CreateClient();
        stranger.DefaultRequestHeaders.Add(RagAdminGate.HeaderName, "not-the-key");

        var refused = await stranger.GetAsync("/health", ct);

        Assert.Equal("{\"status\":\"ok\"}", await refused.Content.ReadAsStringAsync(ct));

        using var operatorClient = factory.CreateClient();
        operatorClient.DefaultRequestHeaders.Add(RagAdminGate.HeaderName, key);

        var response = await operatorClient.GetAsync("/health", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);

        // Still counts only: where the corpus lives and which file failed to parse stay with the
        // RAG host's gated /diagnostics.
        Assert.Contains("articleCount", body, StringComparison.Ordinal);
        Assert.Contains("provider", body, StringComparison.Ordinal);
        Assert.DoesNotContain("path", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("loadErrors", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_anonymous_wizard_writes_share_one_rate_limit_per_client()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var page = await client.GetAsync("/RmaWizard/Start", ct);
        var token = TokenOf(await page.Content.ReadAsStringAsync(ct));

        // An empty form, so every allowed request is a validation error: no request is created,
        // no customer is looked up, and nothing is spent. What is measured is the limiter.
        var values = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Customer", string.Empty),
            new("Identifiers", string.Empty),
        };

        // The policy allows 30 writes a minute per client address.
        for (var allowed = 0; allowed < 30; allowed++)
        {
            var permitted = await client.PostAsync(
                "/RmaWizard/Start",
                new FormUrlEncodedContent(values),
                ct);

            Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
        }

        // The 31st is refused before the action runs, which is what stops a script from
        // treating an anonymous form as a free triage API.
        var limited = await client.PostAsync(
            "/RmaWizard/Start",
            new FormUrlEncodedContent(values),
            ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // And the allowance is the caller's, not the endpoint's. ChooseCustomer resolves the
        // same identifiers and can create the same request, so a bucket of its own would double
        // what one script can do per minute.
        var chooseCustomer = await client.PostAsync(
            "/RmaWizard/ChooseCustomer",
            new FormUrlEncodedContent(values),
            ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, chooseCustomer.StatusCode);

        // Triage is the step that spends a model call, so it shares the same bucket rather than
        // getting its own. The limiter runs before the action, so the request id does not have to
        // exist for the refusal to happen.
        var triage = await client.PostAsync(
            $"/RmaWizard/Triage/{Guid.NewGuid()}",
            new FormUrlEncodedContent(values),
            ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, triage.StatusCode);

        // Every later write shares the bucket too. Limiting only Start and Triage left a caller
        // with one successful Start free to hammer these as fast as the socket allowed, and they
        // are the steps that rewrite the descriptions policy is decided on, quote and capture
        // money, and commit the return.
        string[] laterSteps =
        [
            "/RmaWizard/Problems",
            "/RmaWizard/Shipping",
            "/RmaWizard/Payment",
            "/RmaWizard/Confirm",
        ];

        foreach (var laterStep in laterSteps)
        {
            var refused = await client.PostAsync(
                laterStep,
                new FormUrlEncodedContent(values),
                ct);

            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        }
    }

    // ---------- operator back office (P10) ----------

    private WebApplicationFactory<global::WebAppAnchor> OperatorHost() =>
        _factory!.WithWebHostBuilder(builder => builder.UseSetting("OperatorAuth:DevSignIn", "true"));

    private static DateTimeOffset AnchorInstant => new(SeedAnchor.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    [Fact]
    public async Task The_operator_area_needs_sign_in()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await CreateClient().GetAsync("/Operator", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Operator/SignIn", response.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_operator_receives_inspects_and_captures_and_every_action_is_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = OperatorHost();

        // A confirmed paid repair with a real (fake-gateway) authorisation behind it.
        var gateway = factory.Services.GetRequiredService<AIEnabledRma.Domain.Abstractions.IPaymentGateway>();
        var authorisation = await gateway.AuthorizeAsync(
            new AIEnabledRma.Domain.Abstractions.PaymentAuthorizationRequest
            {
                RmaNumber = "RMA-2026-09901",
                Amount = new AIEnabledRma.Domain.Common.Money(69m, "USD"),
                Purpose = AIEnabledRma.Domain.Abstractions.PaymentPurpose.Deposit,
                CaptureMode = AIEnabledRma.Domain.Abstractions.PaymentCaptureMode.ManualLater,
                CustomerEmail = "test@example.com",
            },
            ct);
        var requestId = Guid.NewGuid();
        await using (var db = CreateContext())
        {
            db.RmaRequests.Add(new RmaRequest
            {
                Id = requestId,
                RmaNumber = "RMA-2026-09901",
                Status = RmaRequestStatus.AwaitingShipment,
                Kind = RmaRequestKind.PaidRepair,
                DepositAmount = 69m,
                CurrencyCode = "USD",
                PaymentTransactionId = authorisation.Value!.TransactionId,
                CreatedAtUtc = AnchorInstant,
                UpdatedAtUtc = AnchorInstant,
            });
            await db.SaveChangesAsync(ct);
        }

        var client = await SignedInOperatorAsync(factory, ct);

        // Capture before receipt is refused, and the refusal is audited too.
        var refused = await OperatorPostAsync(client, $"/Operator/Capture/{requestId}", [("inspectionNote", "looks fine")], ct);
        Assert.Contains("Not done", refused, StringComparison.Ordinal);

        await OperatorPostAsync(client, $"/Operator/Receive/{requestId}", [], ct);
        var captured = await OperatorPostAsync(client, $"/Operator/Capture/{requestId}", [("inspectionNote", "Charging port corroded; repair confirmed.")], ct);
        Assert.Contains("Done: Captured 69.00 USD", captured, StringComparison.Ordinal);

        await using var check = CreateContext();
        var request = await check.RmaRequests.SingleAsync(r => r.Id == requestId, ct);
        Assert.Equal(RmaRequestStatus.InRepair, request.Status);

        var actions = await check.OperatorActions.Where(a => a.Target == "RMA-2026-09901").OrderBy(a => a.AtUtc).ToListAsync(ct);
        Assert.Equal(["capture", "receive", "capture"], actions.Select(a => a.Action).ToArray());
        Assert.Equal([false, true, true], actions.Select(a => a.Succeeded).ToArray());
        Assert.All(actions, a => Assert.Equal("dev-operator", a.Operator));
    }

    /// <summary>
    /// A mismatch item used to reach the operator as an explanation and nothing else, so judging it
    /// meant opening psql to find the return and asking the provider what the transaction is. The row
    /// now carries the three facts: which return, what the provider says, and which transaction.
    /// </summary>
    [Fact]
    public async Task A_mismatch_row_names_the_return_the_provider_state_and_the_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = OperatorHost();

        var requestId = Guid.NewGuid();

        await using (var db = CreateContext())
        {
            db.RmaRequests.Add(new RmaRequest
            {
                Id = requestId,
                RmaNumber = "RMA-2026-09902",
                Status = RmaRequestStatus.Received,
                Kind = RmaRequestKind.PaidRepair,
                DepositAmount = 69m,
                CurrencyCode = "USD",
                PaymentTransactionId = "pi_mismatch_visible",
                CreatedAtUtc = AnchorInstant,
                UpdatedAtUtc = AnchorInstant,
            });

            db.ReviewItems.Add(new AIEnabledRma.Domain.Operations.ReviewItem
            {
                Id = Guid.NewGuid(),
                CreatedAtUtc = AnchorInstant,
                Source = AIEnabledRma.Domain.Operations.ReviewSources.PaymentMismatch,
                Reason = AIEnabledRma.Domain.Operations.PaymentMismatchReason.For(
                    AIEnabledRma.Domain.Abstractions.PaymentStatus.Captured),
                Explanation = "The payment provider reports Captured for a return this database calls Received.",
                RmaRequestId = requestId,
            });

            await db.SaveChangesAsync(ct);
        }

        var client = await SignedInOperatorAsync(factory, ct);
        var dashboard = await client.GetStringAsync("/Operator", ct);

        Assert.Contains("RMA-2026-09902", dashboard, StringComparison.Ordinal);
        Assert.Contains("provider: Captured", dashboard, StringComparison.Ordinal);
        Assert.Contains("transaction: pi_mismatch_visible", dashboard, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mismatch a state column cannot show: both sides say captured and the sums differ. The row
    /// carries the money the run saw, so the operator compares two numbers instead of reading them out
    /// of the explanation sentence.
    /// </summary>
    [Fact]
    public async Task A_mismatch_row_shows_the_two_sums_the_run_saw()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = OperatorHost();

        var requestId = Guid.NewGuid();

        await using (var db = CreateContext())
        {
            db.RmaRequests.Add(new RmaRequest
            {
                Id = requestId,
                RmaNumber = "RMA-2026-09903",
                Status = RmaRequestStatus.InRepair,
                Kind = RmaRequestKind.PaidRepair,
                DepositAmount = 70m,
                CurrencyCode = "USD",
                PaymentTransactionId = "pi_mismatch_money",
                CreatedAtUtc = AnchorInstant,
                UpdatedAtUtc = AnchorInstant,
            });

            db.ReviewItems.Add(new AIEnabledRma.Domain.Operations.ReviewItem
            {
                Id = Guid.NewGuid(),
                CreatedAtUtc = AnchorInstant,
                Source = AIEnabledRma.Domain.Operations.ReviewSources.PaymentMismatch,
                Reason = AIEnabledRma.Domain.Operations.PaymentMismatchReason.For(
                    AIEnabledRma.Domain.Abstractions.PaymentStatus.Captured),
                Explanation = "The payment provider holds 69.00 USD where RMA-2026-09903 says 70.00 USD.",
                ExpectedAmount = 70m,
                ExpectedCurrencyCode = "USD",
                ProviderAmount = 69m,
                ProviderCurrencyCode = "USD",
                RmaRequestId = requestId,
            });

            await db.SaveChangesAsync(ct);
        }

        var client = await SignedInOperatorAsync(factory, ct);
        var dashboard = await client.GetStringAsync("/Operator", ct);

        // The text the operator reads, not the markup around it: each sum sits in its own element so a
        // narrow column cannot break it between the amount and the currency, and an assertion on the
        // source formatting would punish exactly that.
        var visible = Regex.Replace(dashboard, "<[^>]+>", string.Empty);

        Assert.Contains("expected: 70.00 USD", visible, StringComparison.Ordinal);
        Assert.Contains("provider holds: 69.00 USD", visible, StringComparison.Ordinal);

        // And both sums reach the browser inside the site's document. Without a view start the page
        // shipped as a bare fragment with no stylesheet, which is what let the queue's columns - and
        // the money line under them - collapse into each other.
        Assert.Contains("/css/site.css", dashboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_start_sent_to_human_review_is_queued_for_an_operator_and_can_be_resolved()
    {
        var ct = TestContext.Current.CancellationToken;
        // A region the policy hands to a person: the start creates no return, only a review item.
        await using var factory = _factory!.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OperatorAuth:DevSignIn", "true");
            builder.UseSetting("RmaPolicy:HumanReviewOnlyRegions:0", "ZZ");
        });
        var customer = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

        var start = await customer.GetAsync("/RmaWizard/Start", ct);
        await PostStepAsync(
            customer,
            "/RmaWizard/Start",
            TokenOf(await start.Content.ReadAsStringAsync(ct)),
            [("Customer", "Qzxvq Wplkh"), ("Identifiers", EligibleSerial), ("CurrencyCode", CurrencyCode), ("RegionCode", "ZZ")],
            ct);


        Guid itemId;
        await using (var db = CreateContext())
        {
            var item = await db.ReviewItems.SingleAsync(ct);
            Assert.Equal(AIEnabledRma.Domain.Operations.ReviewSources.StartRefused, item.Source);
            Assert.Contains(EligibleSerial, item.Serials, StringComparison.Ordinal);
            itemId = item.Id;
        }

        var operatorClient = await SignedInOperatorAsync(factory, ct);
        var dashboard = await operatorClient.GetStringAsync("/Operator", ct);
        Assert.Contains(AIEnabledRma.Domain.Operations.ReviewSources.StartRefused, dashboard, StringComparison.Ordinal);

        await OperatorPostAsync(operatorClient, $"/Operator/Resolve/{itemId}", [("resolution", "Duplicate of the open return; customer told.")], ct);

        await using var after = CreateContext();
        var resolved = await after.ReviewItems.SingleAsync(ct);
        Assert.Equal(AIEnabledRma.Domain.Operations.ReviewStatuses.Resolved, resolved.Status);
        Assert.Equal("dev-operator", resolved.ResolvedBy);
        Assert.True(await after.OperatorActions.AnyAsync(a => a.Action == "resolve-review" && a.Succeeded, ct));
    }

    private static async Task<HttpClient> SignedInOperatorAsync(WebApplicationFactory<global::WebAppAnchor> factory, CancellationToken ct)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        var signIn = await client.GetAsync("/Operator/SignIn", ct);
        var posted = await client.PostAsync(
            "/Operator/DevSignIn",
            new FormUrlEncodedContent([new("__RequestVerificationToken", TokenOf(await signIn.Content.ReadAsStringAsync(ct)))]),
            ct);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        return client;
    }

    /// <summary>Posts an operator form and returns the dashboard it redirects to.</summary>
    private static async Task<string> OperatorPostAsync(HttpClient client, string path, (string Key, string Value)[] fields, CancellationToken ct)
    {
        var page = await client.GetStringAsync("/Operator", ct);
        var values = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", TokenOf(page)) };
        values.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

        var posted = await client.PostAsync(path, new FormUrlEncodedContent(values), ct);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        return System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Operator", ct));
    }

    private HttpClient CreateClient()
    {
        Assert.NotNull(_factory);

        // Cookies must persist. The wizard threads the pending request and its anti-forgery
        // token through TempData, which is backed by a cookie, so a client that dropped
        // cookies would fail at step two for reasons that have nothing to do with the wizard.
        //
        // Redirects are not followed automatically. Each step is supposed to hand the customer
        // to the next page, and asserting the Location directly is what proves it; a client
        // that silently follows would report only the final page and would pass even if a step
        // bounced the customer through somewhere unexpected on the way.
        var client = _factory!.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                HandleCookies = true,
                AllowAutoRedirect = false,
            });

        client.DefaultRequestHeaders.Add("User-Agent", "WebWizardTests");

        return client;
    }

    /// <summary>
    /// The outcome of posting one wizard step: the page the customer lands on, and the target the
    /// step redirected to when it redirected.
    /// </summary>
    /// <remarks>
    /// The redirect target is kept because it carries information the page itself does not. The
    /// shipping step, for example, signals that it substituted an address for the one the
    /// customer typed only by adding a flag to the redirect it issues, so a client that follows
    /// redirects silently throws that away.
    /// </remarks>
    private sealed record StepOutcome(HttpResponseMessage Page, string? Redirect)
    {
        public string Path => Page.RequestMessage?.RequestUri?.AbsolutePath
            ?? throw new InvalidOperationException("The response had no request URI to inspect.");
    }

    [Fact]
    public async Task An_unknown_account_gets_the_same_chooser_page_as_a_known_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();
        var start = await client.GetAsync("/RmaWizard/Start", ct);
        var token = TokenOf(await start.Content.ReadAsStringAsync(ct));

        (string Key, string Value)[] Fields(string customer) =>
        [
            ("Customer", customer),
            ("Identifiers", EligibleSerial),
            ("CurrencyCode", CurrencyCode),
        ];

        var known = await PostStepAsync(client, "/RmaWizard/Start", token, Fields(CustomerEmail), ct);
        var unknown = await PostStepAsync(
            client,
            "/RmaWizard/Start",
            token,
            Fields("definitely-not-a-customer@example.com"),
            ct);

        // The response is the same shape either way. Attaching an exact match at Start and
        // showing a chooser only for near misses made the redirect itself the answer to "is
        // this address on file?", which is an enumeration oracle over the customer table.
        Assert.StartsWith("/RmaWizard/ChooseCustomer", known.Path, StringComparison.Ordinal);
        Assert.StartsWith("/RmaWizard/ChooseCustomer", unknown.Path, StringComparison.Ordinal);

        var unknownHtml = await unknown.Page.Content.ReadAsStringAsync(ct);
        Assert.Contains("could not find that account", unknownHtml, StringComparison.OrdinalIgnoreCase);

        // And the no-match page can finish the return, just as the chooser can.
        var finished = await PostStepAsync(
            client,
            "/RmaWizard/ChooseCustomer",
            TokenOf(unknownHtml),
            [
                ("customerId", string.Empty),
                ("identifiers", EligibleSerial),
                ("offer", OfferOf(unknownHtml)),
            ],
            ct);

        Assert.StartsWith("/RmaWizard/Triage/", finished.Path, StringComparison.Ordinal);

        await using var db = CreateContext();
        var request = await db.RmaRequests.SingleAsync(r => r.Id == IdFromPath(finished.Path), ct);
        Assert.Null(request.CustomerId);
    }

    [Fact]
    public async Task A_draft_past_the_capability_window_refuses_every_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var started = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);
        var rmaId = IdFromPath(started.Path);

        await using (var db = CreateContext())
        {
            var request = await db.RmaRequests.SingleAsync(r => r.Id == rmaId, ct);
            request.CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-31);
            await db.SaveChangesAsync(ct);
        }

        // The request id is the capability. A link left in an inbox must stop working.
        var page = await client.GetAsync($"/RmaWizard/Triage/{rmaId}", ct);

        Assert.Equal(HttpStatusCode.Gone, page.StatusCode);
        Assert.Contains("expired", await page.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);

        // A draft inside the window is untouched: the rule bounds the link, not the step.
        var fresh = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);
        Assert.StartsWith("/RmaWizard/Triage/", fresh.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Security_headers_are_sent_with_the_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var response = await client.GetAsync("/RmaWizard/Start", ct);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", csp, StringComparison.Ordinal);
    }

    /// <summary>
    /// Posts a wizard step and returns the page the step hands the customer to, together with the
    /// redirect that got it there.
    /// </summary>
    /// <summary>
    /// Starts a return and picks the account on the chooser page it always lands on.
    /// </summary>
    /// <remarks>
    /// A contact lookup is answered with the chooser whether it matched nothing, one account or
    /// several: the response must not be the answer to "is this address on file?". So every
    /// walk-through goes through the chooser, and an account is selected only when one was
    /// offered — the no-match form posts an empty id.
    /// </remarks>
    private static string OfferOf(string html) =>
        Regex.Match(html, "name=\"offer\"[^>]*value=\"(?<offer>[^\"]*)\"").Groups["offer"].Value;

    private static async Task<StepOutcome> BeginAsync(
        HttpClient client,
        string customer,
        string identifiers,
        CancellationToken cancellationToken)
    {
        var start = await client.GetAsync("/RmaWizard/Start", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        var started = await PostStepAsync(
            client,
            "/RmaWizard/Start",
            TokenOf(await start.Content.ReadAsStringAsync(cancellationToken)),
            [
                ("Customer", customer),
                ("Identifiers", identifiers),
                ("CurrencyCode", CurrencyCode),
            ],
            cancellationToken);

        Assert.StartsWith("/RmaWizard/ChooseCustomer", started.Path, StringComparison.Ordinal);

        var chooser = await client.GetAsync(started.Redirect ?? started.Path, cancellationToken);
        var html = await chooser.Content.ReadAsStringAsync(cancellationToken);
        var offered = Regex.Match(html, "name=\"customerId\" value=\"(?<id>[0-9a-fA-F-]{36})\"");

        // The chooser carries the server's own joined identifier list, which is not the same
        // string the visitor typed (several serials are joined with '|'). Posting the typed
        // text back would drop every identifier but the first.
        var carried = Regex.Match(html, "name=\"identifiers\"[^>]*value=\"(?<ids>[^\"]*)\"");

        // The chooser posts its server-side offer key back, and that is the only thing that
        // makes the next POST legitimate. Without it every walk-through would bounce to Start
        // and no test below would be exercising the step it names.
        Assert.False(string.IsNullOrEmpty(OfferOf(html)), "the chooser page carried no offer key");
        Assert.True(
            html.Contains("Which account is yours?", StringComparison.Ordinal),
            "chooser for a known account rendered no candidates; redirect was " + started.Redirect);

        return await PostStepAsync(
            client,
            "/RmaWizard/ChooseCustomer",
            TokenOf(html),
            [
                ("customerId", offered.Success ? offered.Groups["id"].Value : string.Empty),
                ("identifiers", carried.Success ? carried.Groups["ids"].Value : identifiers),
                ("offer", OfferOf(html)),
            ],
            cancellationToken);
    }

    /// <remarks>
    /// A redirect is followed only as a GET, which is what the wizard uses between steps. A 307
    /// or 308 would preserve the POST verb, and reposting a step can charge a card or allocate
    /// another RMA reference, so the helper refuses rather than repeating it.
    /// </remarks>
    private static async Task<StepOutcome> PostStepAsync(
        HttpClient client,
        string path,
        string token,
        (string Key, string Value)[] fields,
        CancellationToken cancellationToken)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
        };

        values.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

        var posted = await client.PostAsync(
            path,
            new FormUrlEncodedContent(values),
            cancellationToken);

        if (posted.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.MovedPermanently))
        {
            // A step that re-renders its own form is either a validation error or a refusal, and
            // the caller decides which. It is not an error in itself.
            return new StepOutcome(posted, null);
        }

        // Only 307 and 308 preserve the verb. A 301, 302, or 303 is re-issued as a GET by every
        // real client, so following one here matches what a browser does. A 307 or 308 would
        // repost a wizard step, which can charge a card or allocate a second reference, so the
        // helper refuses those instead of repeating the step.
        if (posted.StatusCode is HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect)
        {
            throw new InvalidOperationException(
                $"POST {path} redirected with {posted.StatusCode}, which preserves the POST. "
                + "Reposting a wizard step can charge a card or allocate another reference, so "
                + "this is treated as a defect rather than followed.");
        }

        var location = posted.Headers.Location
            ?? throw new InvalidOperationException($"{posted.StatusCode} had no Location header.");

        var target = location.IsAbsoluteUri
            ? location
            : new Uri(posted.RequestMessage!.RequestUri!, location.OriginalString);

        // The query string is the part worth keeping: it is where a step flags that it
        // substituted something, and the followed page does not repeat it.
        var redirectPath = target.PathAndQuery;

        posted.Dispose();

        return new StepOutcome(await client.GetAsync(target, cancellationToken), redirectPath);
    }

    /// <summary>
    /// Walks a fresh request as far as the shipping step and returns its id. The steps after
    /// intake are addressed by request id, so the id is what a later assertion needs.
    /// </summary>
    private async Task<Guid> ReachShippingStepAsync(HttpClient client, CancellationToken ct)
    {
        var start = await BeginAsync(client, CustomerEmail, EligibleSerial, ct);
        Assert.StartsWith("/RmaWizard/Triage/", start.Path, StringComparison.Ordinal);
        var rmaId = IdFromPath(start.Path);

        var triagePage = await client.GetAsync($"/RmaWizard/Triage/{rmaId}", ct);
        var triage = await PostStepAsync(
            client,
            $"/RmaWizard/Triage/{rmaId}",
            TokenOf(await triagePage.Content.ReadAsStringAsync(ct)),
            [("Statement", "The screen stays black although the fan is running.")],
            ct);
        Assert.Equal($"/RmaWizard/Problems/{rmaId}", triage.Path);

        var problemsPage = await client.GetAsync($"/RmaWizard/Problems/{rmaId}", ct);
        var deviceId = await DeviceIdAsync(EligibleSerial, ct);
        var problems = await PostStepAsync(
            client,
            "/RmaWizard/Problems",
            TokenOf(await problemsPage.Content.ReadAsStringAsync(ct)),
            [
                ("RmaId", rmaId.ToString()),
                ("Lines[0].DeviceId", deviceId.ToString()),
                ("Lines[0].SerialNumber", EligibleSerial),
                ("Lines[0].Description", "Screen stays black although the fan is running."),
                ("Lines[0].WhatCustomerTried", "Reseated the cable and tried a second outlet."),
            ],
            ct);
        Assert.Equal($"/RmaWizard/Shipping/{rmaId}", problems.Path);

        return rmaId;
    }

    private async Task<Guid> DefaultAddressIdAsync(CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var customer = await db.Customers
            .Include(c => c.Addresses)
            .SingleAsync(c => c.Email == CustomerEmail, cancellationToken);

        return customer.Addresses.Single(a => a.IsDefault).Id;
    }

    private async Task<Guid> DeviceIdAsync(string serialNumber, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();

        var device = await db.Devices
            .SingleAsync(d => d.SerialNumber == serialNumber, cancellationToken);

        return device.Id;
    }

    private RmaDbContext CreateContext() => _database.CreateContext();

    /// <summary>
    /// The anti-forgery tag is the one place the markup is inspected, because a post without a
    /// valid token is rejected with 400 and every assertion in a step test would then pass for
    /// the wrong reason. Matches the tag's name attribute followed by any intervening
    /// attributes, then the value.
    /// </summary>
    private static readonly Regex TokenPattern = new(
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static string TokenOf(string html) =>
        TokenPattern.Match(html).Groups[1].Value is { Length: > 0 } token
            ? token
            : throw new InvalidOperationException(
                "The page carried no anti-forgery token, so the form cannot be posted.");

    /// <summary>
    /// Asserts the redirect target came back as a 200 rendering rather than an error page, and
    /// returns the path. The status and any Allow header are included in the failure because a
    /// bare "expected OK, got 405" does not say which step or which verb was rejected.
    /// </summary>
    private static string PathOf(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            response.Content.Headers.TryGetValues("Allow", out var allowed);

            var allow = allowed is null
                ? string.Empty
                : $" Allow: {string.Join(", ", allowed)}.";

            var body = response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult();

            throw new InvalidOperationException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} "
                + $"returned {(int)response.StatusCode} {response.StatusCode}.{allow} "
                + $"Body: {Truncate(body)}");
        }

        return response.RequestMessage?.RequestUri?.AbsolutePath
            ?? throw new InvalidOperationException("The response had no request URI to inspect.");
    }

    private static string Truncate(string text) =>
        text.Length <= 400 ? text : string.Concat(text.AsSpan(0, 400), " ...") + $"({text.Length} chars)";

    private static Guid IdFromPath(string path) =>
        Guid.TryParse(path.Split('/').Last(), out var id)
            ? id
            : throw new InvalidOperationException($"'{path}' does not end in a request id.");

    /// <summary>
    /// The host's clock, held by a test that needs to move time forward. Every other test uses
    /// the frozen one, because the fixture is authored against a single date.
    /// </summary>
    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// The workflow's <see cref="IClock"/>, read from the host's <see cref="TimeProvider"/> so the
    /// two cannot drift apart when a test moves time.
    /// </summary>
    private sealed class ClockOver(TimeProvider time) : IClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();

        public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);
    }

    private sealed class WizardFactory(
        string connectionString,
        DateOnly anchor,
        TimeProvider? clock = null)
        : WebApplicationFactory<global::WebAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Rma", connectionString);

            builder.ConfigureTestServices(services =>
            {
                // The fixture is authored for the anchor, so the host's "today" is pinned to it as
                // well. Otherwise the calendar advances past the data and the paid-repair test
                // turns red for a reason that has nothing to do with the code under test.
                var at = new DateTimeOffset(anchor.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

                // One clock behind both seams. The controller asks TimeProvider and the workflow
                // asks IClock, so a test that moved only one of them could not exercise a step
                // that consults both without the two disagreeing about what day it is.
                var time = clock ?? (TimeProvider)new FrozenTimeProvider(at);

                services.AddSingleton(time);
                services.AddSingleton<IClock>(new ClockOver(time));
            });

            // The checked-in configuration carries a placeholder connection string, so without
            // this the host would start and then fail on its first query instead of at startup.
            builder.UseEnvironment("Testing");

            // This host runs as "Testing" so the production error page is exercised, and the
            // bundled accept-everything gateway refuses to register outside Development. The
            // explicit opt-in is the supported escape hatch; widening the guard to a list of
            // environment names would make "anything not named Production" quietly acceptable.
            builder.UseSetting(
                $"{FakePaymentGatewayOptions.SectionName}:{nameof(FakePaymentGatewayOptions.AllowInProduction)}",
                "true");

            // Testing is outside Development, where a host with no operator key refuses to start:
            // /health's detail would be open to every caller behind a proxy.
            builder.UseSetting(RagAdminGate.ApiKeyConfigurationKey, "wizard-tests-operator-key");
        }

        /// <summary>A <see cref="TimeProvider"/> that always reports the anchor instant.</summary>
        private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
