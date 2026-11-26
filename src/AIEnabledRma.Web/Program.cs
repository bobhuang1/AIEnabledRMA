using System.Threading.RateLimiting;
using AIEnabledRma.Ai;
using AIEnabledRma.Data;
using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Configuration;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Triage;
using AIEnabledRma.Privacy;
using AIEnabledRma.Rag;
using AIEnabledRma.Web;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRmaDbContext(builder.Configuration);
builder.Services.AddRmaData(builder.Configuration);

// The same in-process corpus the RAG service and the MCP tools use, so all three hosts answer
// a troubleshooting question identically. AddRag also registers the IKnowledgeRetriever that
// TriagePipeline depends on.
// Embedding model first (D4: Ollama by default), so the hybrid retriever AddRag registers when
// Rag:VectorStore:Enabled embeds queries with the same model ingestion used.
builder.Services.AddEmbeddingModel(builder.Configuration);
builder.Services.AddRag(builder.Configuration);

builder.Services.AddAiProvider(builder.Configuration);

builder.Services.AddScoped<IRmaWorkflow, RmaWorkflow>();
builder.Services.AddScoped<ITriageService, TriagePipeline>();

// Where a refused question goes. Scoped with the context it writes through; the pipeline treats a
// failed write as a log line, because the refusal itself has already been decided and the
// customer's answer does not depend on the record being written.
builder.Services.AddScoped<ITriageRefusalLog, EfTriageRefusalLog>();

// The redactor holds no state across requests: each triage call starts its own session, so the
// placeholders it hands the model cannot link two conversations by number.
builder.Services.AddSingleton<IPiiRedactor, PiiRedactor>();

// Warranty windows and return windows are date-sensitive, so every date in the flow is read
// through IClock. Singleton, because it holds no state and must not vary between requests.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(TimeProvider.System);

// The bundled gateway keeps its authorisations in memory, because the wizard authorises on the
// payment step and captures or voids on a later request. Scoping it to the request would throw
// those authorisations away in between, so it is a singleton. It accepts every positive amount,
// so AddFakePaymentGateway refuses to register it outside Development; replace this call with a
// real provider to charge anything.
//
// Payments:Provider=stripe selects the Stripe gateway (P9, D5) instead.
builder.Services.AddPaymentGateway(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    builder.Environment.IsProduction());

// Only the thresholds are configuration. The topic vocabulary itself is not: it is read from
// the corpus on first use, because every article already declares the words it is about, and a
// hand-maintained word list is a second description of the domain that drifts from the corpus
// silently — the guard simply starts refusing ordinary phrasings. The guard holds the words it
// was built with, so this runs once per process and is shared.
builder.Services.AddOptions<ScopeGuardOptions>()
    .Bind(builder.Configuration.GetSection(ScopeGuardOptions.SectionName))
    .Validate(
        o => o.MinimumTopicTokenRatio is > 0 and <= 1,
        $"{ScopeGuardOptions.SectionName}:MinimumTopicTokenRatio must be greater than zero and "
        + "at most 1.")
    .ValidateOnStart();
builder.Services.AddSingleton<IScopeGuard>(sp => new ScopeGuard(
    sp.GetRequiredService<IOptions<ScopeGuardOptions>>().Value,
    ScopeGuardVocabulary.Derive(
        sp.GetRequiredService<KnowledgeIndexHolder>().Current.Articles
            .Select(article => new ScopeVocabularyArticle
            {
                Category = article.Category,
                Keywords = article.Keywords,
            }))));

builder.Services.AddOptions<TriagePipelineOptions>()
    .Bind(builder.Configuration.GetSection(TriagePipelineOptions.SectionName))
    .ValidateOnStart();

// Antiforgery is on because every wizard step is a POST that changes the return's state. The
// [ValidateAntiForgeryToken] attributes on the POST actions enforce it.
builder.Services.AddControllersWithViews();

// Operator back office (P10): cookie session, OpenID Connect sign-in when configured.
AIEnabledRma.Web.Security.OperatorAuthSetup.AddOperatorAuth(builder.Services, builder.Configuration, builder.Environment);
builder.Services.AddScoped<AIEnabledRma.Domain.Operations.OperatorService>();

// Abuse control for the POSTs an anonymous visitor can drive over and over: Start writes a
// request and consumes a reference from the sequence, Triage spends a model call, and
// ChooseCustomer runs the device lookups and can create the request. Without a limit, one script
// turns an anonymous form into a free triage API and a way to fill the database. A partition per
// client address with a small fixed window is the cheapest thing that stops that, and the
// allowance is far above anything a person filling in a form produces.
//
// All three share this policy deliberately. A bucket per endpoint would multiply the writes a
// caller gets by the number of steps, and ChooseCustomer is not a cheaper operation to abuse
// than Start: it resolves the same identifiers and can write the same request, so its own
// allowance would only widen the hole.
//
// 30 writes a minute is one every two seconds. A customer needs a page load and a read between
// steps; a script does not. Requests are not queued, so a refused caller finds out immediately
// rather than being parked, and callers behind one NAT address share a partition. That shared
// partition is the known trade-off: the alternative is a global limit, which one caller could
// use to shut the wizard down for everyone.
builder.Services.Configure<WizardOptions>(builder.Configuration.GetSection(WizardOptions.SectionName));

// Holds the account list a visitor was shown until they choose from it. Server-side, because
// the POST validates the chosen id against it and the browser must not be able to add one.
builder.Services.AddMemoryCache();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // QueueLimit keeps its default of zero, which is why a refused caller is turned away rather
    // than parked.
    options.AddPolicy(RateLimitPolicies.WizardWrites, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
            }));
});

// Development only: run the demo on a fixed date (Demo:Today=yyyy-MM-dd), for showing the seeded
// scenarios on a day other than the one they were anchored to.
if (builder.Environment.IsDevelopment()
    && DateOnly.TryParseExact(builder.Configuration["Demo:Today"], "yyyy-MM-dd", out var demoToday))
{
    var demoNow = new DateTimeOffset(demoToday.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
    builder.Services.AddSingleton<TimeProvider>(new AIEnabledRma.Web.Security.FixedTimeProvider(demoNow));
    builder.Services.AddSingleton<IClock>(new AIEnabledRma.Web.Security.FixedClock(demoNow));
}

// Production refuses a development database password and plain-http model or embedding
// endpoints, and it refuses to start without the operator identity provider (P11). The last one is
// required rather than optional: the dev sign-in cannot run outside Development, so a deployment
// without OperatorAuth:Authority and ClientId serves an /Operator area whose sign-in can never
// complete — a security surface that looks deployed and answers nobody. The fake gateway and the
// operator key keep the owner's own startup rules.
if (builder.Environment.IsProduction())
{
    ProductionConfiguration.EnsureSafe(builder.Configuration, checkOperatorSignIn: true);
}

var app = builder.Build();

// Response headers for every request, including the static files and the error page. The
// wizard renders no inline script or style, so a strict policy costs nothing: a page that
// cannot load anything but its own CSS and JS cannot be made to exfiltrate the request id
// sitting in the address bar. frame-ancestors and X-Frame-Options both say no framing, because
// a clickjacked confirm button is a confirmed return.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Frame-Options"] = "DENY";
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
        + "form-action 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";

    await next();
});

// First in the pipeline, because everything that cares about the caller's address reads
// Connection.RemoteIpAddress and the write limiter partitions on it. Behind a reverse proxy that
// address is the proxy, so without this every customer shares one partition and 30 writes a
// minute throttle the whole site. Only a peer named in ReverseProxy:TrustedProxies may set the
// client address: a caller that sends X-Forwarded-For itself is ignored and limited on its own
// address, which is what stops the header becoming a way to evade the limit.
var trustedProxies = new TrustedProxyOptions();
builder.Configuration.GetSection(TrustedProxyOptions.SectionName).Bind(trustedProxies);

if (trustedProxies.IsConfigured)
{
    app.UseForwardedHeaders(trustedProxies.ToForwardedHeadersOptions());
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

// After routing, because the route values are what identify a capability page. A step addressed by
// the request id is one: the id in the address bar is the whole secret. A shared browser or an
// intermediary that keeps a copy hands the next person on that machine a working link, and expiry
// only bounds drafts — a page for a confirmed return would never expire. no-store is the directive
// that also forbids writing it to disk; no-cache alone would let it be stored and revalidated.
// The antiforgery middleware already marks the pages that render a token, but it leaves the 302 a
// step POSTs unmarked and sends no max-age, so this is not a duplicate of it.
app.Use(async (context, next) =>
{
    if (AnswersWithACapability(context))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            return Task.CompletedTask;
        });
    }

    await next();
});

// After routing, because the limiter reads the endpoint's policy metadata, and before
// antiforgery, so a refused request stops before any of the work it was trying to make us do.
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

// Stripe webhook (P9): signature-verified; a failed or cancelled intent is filed for human review.
if (PaymentGatewayServiceCollectionExtensions.UsesStripe(app.Configuration))
{
    app.MapPost("/payments/stripe/webhook", async (
        HttpContext context,
        IOptions<StripeOptions> stripe,
        AIEnabledRma.Domain.Operations.IWebhookEventLedger webhookEvents,
        TimeProvider time,
        ILogger<StripeOptions> log) =>
    {
        using var reader = new StreamReader(context.Request.Body);
        var payload = await reader.ReadToEndAsync(context.RequestAborted);
        if (payload.Length > 512 * 1024
            || !StripeWebhookSignature.IsValid(
                payload,
                context.Request.Headers["Stripe-Signature"],
                stripe.Value.WebhookSecret!,
                time.GetUtcNow(),
                stripe.Value.WebhookToleranceSeconds))
        {
            return Results.BadRequest();
        }

        // A signed payload this host cannot read must not be answered with a 500: Stripe retries a
        // 500 until it gives up, and the event is then lost twice over — once here and once from the
        // retry queue. Everything that reads the document happens inside the guard.
        AIEnabledRma.Domain.Operations.ProcessedWebhookEvent delivery;
        AIEnabledRma.Domain.Operations.WebhookEffect? effect;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return Results.BadRequest();
            }

            var type = root.GetProperty("type").GetString();

            // Stripe retries a delivery until it is answered 2xx, and can deliver one twice even
            // after that answer. Filing the retry would open a second review item for one failed
            // payment, so the delivery and the item it asks for are recorded together or not at
            // all. An event without an id cannot be deduplicated, which makes it unsafe to act on.
            var eventId = root.TryGetProperty("id", out var eventIdElement) ? eventIdElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return Results.BadRequest();
            }

            // Only a failed or cancelled intent is filed, and only those payloads are read as one: a
            // delivery of some other event that happens to carry no data object is still a delivery
            // this host accepts and records.
            var now = time.GetUtcNow();
            string? rmaNumber = null;

            if (type is "payment_intent.payment_failed" or "payment_intent.canceled")
            {
                var intent = root.GetProperty("data").GetProperty("object");

                rmaNumber = intent.TryGetProperty("metadata", out var metadata)
                    && metadata.TryGetProperty("rma_number", out var number)
                        ? number.GetString()
                        : null;

                delivery = new AIEnabledRma.Domain.Operations.ProcessedWebhookEvent
                {
                    EventId = eventId,
                    Type = type,
                    ReceivedAtUtc = now,
                };

                effect = new AIEnabledRma.Domain.Operations.WebhookEffect(
                    new AIEnabledRma.Domain.Operations.ReviewItem
                    {
                        Id = Guid.NewGuid(),
                        CreatedAtUtc = now,
                        Source = AIEnabledRma.Domain.Operations.ReviewSources.PaymentFailed,
                        Reason = "Stripe:" + type,
                        // The id and the return are in the sentence because the ledger links the item,
                        // and a reader of the queue sees the number even when no return matches it.
                        Explanation =
                            $"PaymentIntent {intent.GetProperty("id").GetString()} for {rmaNumber ?? "an unknown return"}.",
                    },
                    rmaNumber);
            }
            else
            {
                delivery = new AIEnabledRma.Domain.Operations.ProcessedWebhookEvent
                {
                    EventId = eventId,
                    Type = type ?? "unknown",
                    ReceivedAtUtc = now,
                };

                effect = null;
            }
        }
        catch (Exception failure) when (failure is System.Text.Json.JsonException
            or InvalidOperationException or KeyNotFoundException)
        {
            return Results.BadRequest();
        }

        if (!await webhookEvents.TryAcceptAsync(delivery, effect, context.RequestAborted))
        {
            log.LogInformation("Stripe webhook {Type} {EventId} was already accepted; ignored.", delivery.Type, delivery.EventId);
            return Results.Ok();
        }

        log.LogInformation("Stripe webhook {Type} {EventId} accepted.", delivery.Type, delivery.EventId);
        return Results.Ok();
    });
}

// Startup diagnostics, mirroring the RAG service. The wizard depends on two things that can
// each fail independently and both of which look like "the assistant is useless" from the
// customer's side: the corpus and the AI provider.
//
// This endpoint is public, and the counts and the provider name describe the deployment: how big
// the corpus is, how many of its files failed to parse, which model is answering. A caller who is
// nobody gets the same one word the RAG host's /health gives.
//
// The detail sits behind the operator key the RAG host already uses, rather than a second secret
// for one endpoint: the header is compared in fixed time, and when no key is configured only
// loopback is admitted. There is no account system on this host to authenticate against, and
// inventing one for a status page would be a bigger surface than the thing it protects.
var operatorKey = app.Configuration[RagAdminGate.ApiKeyConfigurationKey];

// The same rule, the same key and the same reason as the RAG host's operator endpoints. When no
// key is configured the gate admits loopback, and behind a reverse proxy on the same machine every
// caller is loopback — so a keyless host outside Development would publish how large the corpus is,
// which files failed to parse and which model is answering, to whoever can reach the proxy. That is
// a configuration nobody notices, so it stops the host instead.
RagAdminGate.EnsureStartupAllowed(operatorKey, app.Environment.IsDevelopment());

// The edge's uptime depends on this one path: deploy/Caddyfile probes it on a timer and takes this
// host out of the upstream pool when the answer is not a 200, which is a 503 for every request
// through the proxy. It therefore asks only what readiness means — can this host reach its database
// — and says one word either way, because the path is reachable from the internet through the proxy
// and a readiness answer is not the place to describe which dependency failed.
//
// The container probe asks /health instead, and the two questions are deliberately different: a host
// that lost its database is still running, so liveness stays 200 while readiness turns 503.
app.MapGet("/health/ready", async (RmaDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet("/health", (
    HttpContext context,
    KnowledgeIndexHolder holder,
    IChatModel chatModel,
    IOperationsRepository operations,
    IClock clock,
    CancellationToken cancellationToken) =>
    HealthDetailsAsync(context, operatorKey, holder, chatModel, operations, clock, cancellationToken));

// The nightly reconciliation is a one-shot process on a timer, so a timer that stopped leaves an
// empty review queue behind and nothing else: the same screen a healthy sweep leaves. Its own run
// row is the difference, and the detail carries it beside the corpus and the model rather than
// behind a second endpoint nobody would watch.
static async Task<IResult> HealthDetailsAsync(
    HttpContext context,
    string? operatorKey,
    KnowledgeIndexHolder holder,
    IChatModel chatModel,
    IOperationsRepository operations,
    IClock clock,
    CancellationToken cancellationToken)
{
    if (!RagAdminGate.IsAllowed(context, operatorKey))
    {
        return Results.Ok(new { status = "ok" });
    }

    var index = holder.Current;

    return Results.Ok(new
    {
        status = "ok",
        knowledgeBase = new
        {
            articleCount = index.Articles.Count,
            loadErrorCount = index.LoadErrors.Count,
        },
        ai = new { provider = chatModel.ProviderName },
        reconciliation = await ReconciliationHealthAsync(operations, clock, cancellationToken),
    });
}

static async Task<SweepHealthDetails> ReconciliationHealthAsync(
    IOperationsRepository operations,
    IClock clock,
    CancellationToken cancellationToken)
{
    try
    {
        var last = await operations.LastSweepAsync(SweepSources.PaymentReconciliation, cancellationToken);

        return SweepStatus
            .For(SweepSources.PaymentReconciliation, last, clock.UtcNow, SweepStatus.NightlyStaleAfter)
            .Health();
    }
    catch (Exception failure) when (failure is not OperationCanceledException)
    {
        // Still a liveness answer: the container probe reads this endpoint, so a database it cannot
        // reach reports the sweep as unknown instead of turning /health into an error.
        return SweepHealthDetails.Unknown(SweepSources.PaymentReconciliation, "the database did not answer");
    }
}

// Whether the response answers an address that carries a capability. The wizard has no accounts, so
// what identifies one customer's progress is a value they hold: a return id in the route, or the
// chooser's offer key. The offer key is the same kind of secret — it resolves server-side to the
// accounts the visitor was shown, and the chooser hands back the account id to pick — so a cached
// chooser lets the next person on that machine start a return as the previous customer without ever
// knowing their address.
static bool AnswersWithACapability(HttpContext context)
{
    if (!context.Request.Path.StartsWithSegments("/RmaWizard", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    // The address carries one: a return id, or the chooser's identifiers and offer key.
    if (context.Request.RouteValues.ContainsKey("id")
        || context.Request.Query.ContainsKey("offer")
        || context.Request.Query.ContainsKey("identifiers"))
    {
        return true;
    }

    // Everything else that is not a GET is a step being submitted, and every one of them answers
    // with a 302 whose Location is the next address in that visit: a return id for Problems,
    // Shipping, Payment and Confirm, the chooser's offer key for Start. A stored copy would send a
    // later visitor into a step belonging to whoever came before. The rule is the verb rather than a
    // list of action names, because a list means the next step added to the wizard is quietly
    // storable until someone remembers to extend it — which is how this predicate already missed the
    // chooser once. A GET renders a form and has nothing pending yet, so it is left alone unless the
    // address itself carries a capability.
    return !HttpMethods.IsGet(context.Request.Method);
}

app.Run();

/// <summary>Exposed so the test project can boot the host with WebApplicationFactory.</summary>
public partial class Program
{
    protected Program() { }
}

/// <summary>
/// Anchor type for <c>WebApplicationFactory&lt;WebAppAnchor&gt;</c>.
/// </summary>
/// <remarks>
/// The factory only needs a public type from the assembly under test to locate its content root
/// and entry point, and it does not have to be the entry point itself. This project cannot be
/// identified by its generated <c>Program</c>, because the RAG and MCP projects each declare a
/// top-level-statement <c>Program</c> too, so the test project's reference to all three makes
/// that name ambiguous. A distinct name keeps the reference unambiguous.
/// </remarks>
public sealed class WebAppAnchor
{
}
