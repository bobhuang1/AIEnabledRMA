using System.Net;
using System.Text.RegularExpressions;
using AIEnabledRma.Ai;
using AIEnabledRma.Data;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Rules;
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
    public async Task Health_reports_a_loaded_knowledge_base()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var response = await client.GetAsync("/health", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);
        Assert.Contains("articleCount", body, StringComparison.Ordinal);

        // Counts only. This endpoint answers without authentication on the customer-facing
        // host, so where the corpus lives and which of its files failed to parse are operator
        // detail: they belong to the RAG host's gated /diagnostics, not here.
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

    private sealed class WizardFactory(string connectionString, DateOnly anchor)
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

                services.AddSingleton<TimeProvider>(new FrozenTimeProvider(at));
                services.AddSingleton<IClock>(new FakeClock(at));
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
        }

        /// <summary>A <see cref="TimeProvider"/> that always reports the anchor instant.</summary>
        private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
