using System.Net;
using System.Text.RegularExpressions;
using AIEnabledRma.Data;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Rules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
    private const string AdminConnectionStringVariable = "RMA_TEST_CONNECTION";

    private const string DefaultAdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=rmauser;Password=placeholder";

    /// <summary>A serial the seeder creates and that no other test in this class consumes.</summary>
    private const string EligibleSerial = "AX2-0001-0001";

    /// <summary>Never present in the seed data, so intake must reject it and write nothing.</summary>
    private const string IneligibleSerial = "NO-SUCH-SERIAL-0000";

    /// <summary>
    /// Seeded 500 days past warranty but still inside the return window, so the policy no longer
    /// refuses it: the wizard now offers a paid repair. This serial is what walks the payment step.
    /// </summary>
    private const string PaidRepairSerial = "AX2-0001-0005";

    /// <summary>
    /// Seeded shipped 1,200 days ago, so far outside the return window that even a paid repair is
    /// not offered. Used alongside an accepted serial to force a partial create.
    /// </summary>
    private const string ExcludedSerial = "BX1-0003-0001";

    /// <summary>
    /// Intake asks who is returning the item as well as what it is, and the customer field is
    /// required. A seeded email is used rather than an arbitrary string so the fuzzy matcher
    /// resolves to exactly one customer; an ambiguous identity diverts to ChooseCustomer, which
    /// would make these tests assert on a different page than the one they mean to check.
    /// </summary>
    private const string CustomerEmail = "j.smith@example.com";

    private const string CurrencyCode = "USD";

    private string _databaseName = string.Empty;
    private string _adminConnectionString = string.Empty;
    private string _applicationConnectionString = string.Empty;
    private WebApplicationFactory<global::WebAppAnchor>? _factory;

    public async ValueTask InitializeAsync()
    {
        _adminConnectionString =
            Environment.GetEnvironmentVariable(AdminConnectionStringVariable)
            ?? DefaultAdminConnectionString;

        try
        {
            await using var probe = new NpgsqlConnection(_adminConnectionString);
            await probe.OpenAsync();
        }
        catch (Exception ex)
        {
            Assert.Skip($"PostgreSQL is not reachable: {ex.GetType().Name}: {ex.Message}");
        }

        _databaseName = $"rma_web_test_{Guid.NewGuid():N}";
        _applicationConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName,
        }.ConnectionString;

        await using (var create = new NpgsqlConnection(_adminConnectionString))
        {
            await create.OpenAsync();

            // The name comes from a GUID above, so there is nothing to inject here.
            await using var command = create.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        // Migrate and seed before the host starts, exactly as the deployment runbook does, so
        // the test exercises the same starting state an operator would produce.
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await DemoDataSeeder.SeedAsync(db, TestContext.Current.CancellationToken);
        }

        _factory = new WizardFactory(_applicationConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_databaseName.Length == 0)
        {
            return;
        }

        try
        {
            // The host's pooled connections would keep the database busy and make the drop fail.
            NpgsqlConnection.ClearAllPools();

            await using var drop = new NpgsqlConnection(_adminConnectionString);
            await drop.OpenAsync();

            await using var command = drop.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException)
        {
            // A leaked throwaway database is untidy but must never fail an otherwise green run.
        }
    }

    [Fact]
    public async Task Confirming_a_return_persists_the_request_and_its_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        // Step 1: serial in.
        var start = await client.GetAsync("/RmaWizard/Start", ct);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        var startStep = await PostStepAsync(
            client,
            "/RmaWizard/Start",
            TokenOf(await start.Content.ReadAsStringAsync(ct)),
            [
                ("Customer", CustomerEmail),
                ("Identifiers", EligibleSerial),
                ("CurrencyCode", CurrencyCode),
            ],
            ct);

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

        var start = await client.GetAsync("/RmaWizard/Start", ct);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        // One device the policy accepts and one it refuses, so the request is created from part of
        // what was handed over and the customer has to be told about the rest.
        var startStep = await PostStepAsync(
            client,
            "/RmaWizard/Start",
            TokenOf(await start.Content.ReadAsStringAsync(ct)),
            [
                ("Customer", CustomerEmail),
                ("Identifiers", $"{EligibleSerial}\n{ExcludedSerial}"),
                ("CurrencyCode", CurrencyCode),
            ],
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
        var start = await client.GetAsync("/RmaWizard/Start", ct);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        var startStep = await PostStepAsync(
            client,
            "/RmaWizard/Start",
            TokenOf(await start.Content.ReadAsStringAsync(ct)),
            [
                ("Customer", CustomerEmail),
                ("Identifiers", PaidRepairSerial),
                ("CurrencyCode", CurrencyCode),
            ],
            ct);

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
    public async Task An_ambiguous_customer_is_handed_to_them_to_choose_rather_than_guessed()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        // "Smyth" matches two seeded accounts, Josephine Smyth and Anne-Marie O'Brien-Smyth.
        // The wizard must show both rather than attach the return to one of them, because the
        // wrong account would expose its address and its returns history.
        var start = await client.GetAsync("/RmaWizard/Start", ct);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

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
        Assert.Contains("j.smith@example.com", chooserHtml, StringComparison.Ordinal);
        Assert.Contains("am.obrien@example.com", chooserHtml, StringComparison.Ordinal);
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
    public async Task An_ineligible_serial_is_reported_and_creates_no_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = CreateClient();

        var start = await client.GetAsync("/RmaWizard/Start", ct);
        var startStep = await PostStepAsync(
            client,
            "/RmaWizard/Start",
            TokenOf(await start.Content.ReadAsStringAsync(ct)),
            [
                ("Customer", CustomerEmail),
                ("Identifiers", IneligibleSerial),
                ("CurrencyCode", CurrencyCode),
            ],
            ct);

        Assert.Equal("/RmaWizard/Start", startStep.Path);
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

    /// <summary>
    /// Posts a wizard step and returns the page the step hands the customer to, together with the
    /// redirect that got it there.
    /// </summary>
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

    private RmaDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RmaDbContext>()
            .UseNpgsql(_applicationConnectionString, npgsql => npgsql.MigrationsAssembly(
                typeof(RmaDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new RmaDbContext(options);
    }

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

    private sealed class WizardFactory : WebApplicationFactory<global::WebAppAnchor>
    {
        private readonly string _connectionString;

        public WizardFactory(string connectionString) => _connectionString = connectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Rma", _connectionString);

            // The checked-in configuration carries a placeholder connection string, so without
            // this the host would start and then fail on its first query instead of at startup.
            builder.UseEnvironment("Testing");
        }
    }
}
