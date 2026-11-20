using AIEnabledRma.Ai;
using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Triage;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using AIEnabledRma.Rag;
using AIEnabledRma.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRmaDbContext(builder.Configuration);
builder.Services.AddRmaData(builder.Configuration);

// The same in-process corpus the RAG service and the MCP tools use, so all three hosts answer
// a troubleshooting question identically. AddRag also registers the IKnowledgeRetriever that
// TriagePipeline depends on.
// The embedding model is registered before AddRag so the hybrid retriever (when
// Rag:VectorStore:Enabled) embeds queries with the same model that ingestion used.
builder.Services.AddEmbeddingModel(builder.Configuration);
builder.Services.AddRag(builder.Configuration);

builder.Services.AddAiProvider(builder.Configuration);

builder.Services.AddScoped<IRmaWorkflow, RmaWorkflow>();
builder.Services.AddScoped<ITriageService, TriagePipeline>();

// Warranty windows and return windows are date-sensitive, so every date in the flow is read
// through IClock. Singleton, because it holds no state and must not vary between requests.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(TimeProvider.System);

// The bundled gateway keeps its authorisations in memory, because the wizard authorises on the
// payment step and captures or voids on a later request, so it is a singleton. It accepts every
// payment, so it is only registered where no real money can be involved (Development, the test
// host) or when a demo deployment opts in explicitly. Anywhere else startup fails rather than
// "authorising" real customers' payments against nothing (F6).
var paymentOptions = builder.Configuration.GetSection(PaymentOptions.SectionName).Get<PaymentOptions>() ?? new PaymentOptions();
var useStripe = string.Equals(paymentOptions.Provider, "stripe", StringComparison.OrdinalIgnoreCase);
if (useStripe)
{
    // Stripe (P9). A live key is accepted only in Production; everywhere else test-mode keys
    // only, so a developer machine or a staging site can never move real money.
    var stripe = builder.Configuration.GetSection(StripeOptions.SectionName).Get<StripeOptions>() ?? new StripeOptions();
    if (string.IsNullOrWhiteSpace(stripe.SecretKey) || string.IsNullOrWhiteSpace(stripe.WebhookSecret))
    {
        throw new InvalidOperationException(
            "Payments:Provider is 'stripe' but Payments:Stripe:SecretKey or WebhookSecret is missing. Supply them from the environment.");
    }

    if (!builder.Environment.IsProduction() && !stripe.SecretKey.StartsWith("sk_test_", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Only Stripe test-mode keys (sk_test_...) are allowed in the '{builder.Environment.EnvironmentName}' environment.");
    }

    builder.Services.AddOptions<StripeOptions>().Bind(builder.Configuration.GetSection(StripeOptions.SectionName));
    builder.Services.AddHttpClient<IPaymentGateway, StripePaymentGateway>(client =>
    {
        client.BaseAddress = new Uri(stripe.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
    });
}
else if (builder.Environment.IsDevelopment()
    || builder.Environment.IsEnvironment("Testing")
    || paymentOptions.AllowFakeGateway)
{
    builder.Services.AddSingleton<IPaymentGateway, FakePaymentGateway>();
}
else
{
    throw new InvalidOperationException(
        "No real payment gateway is configured, and the accept-all FakePaymentGateway is refused in the "
        + $"'{builder.Environment.EnvironmentName}' environment. Register a real IPaymentGateway, or set "
        + "Payments:AllowFakeGateway=true for a demo that takes no real money.");
}

builder.Services.AddOptions<WizardSecurityOptions>()
    .Bind(builder.Configuration.GetSection(WizardSecurityOptions.SectionName))
    .Validate(o => o.CapabilityLifetimeDays > 0 && o.PostsPerMinutePerClient > 0 && o.PostsPerMinutePerRequest > 0,
        "WizardSecurity values must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<OperationsOptions>()
    .Bind(builder.Configuration.GetSection(OperationsOptions.SectionName));
builder.Services.AddScoped<CapabilityExpiryFilter>();

// Every wizard POST is rate limited per client address and per request id (F5). The Start step
// is where customer lookups happen, so this is also what bounds probing for who is a customer.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (!IsWizardPost(context))
            {
                return RateLimitPartition.GetNoLimiter("other");
            }

            var limit = context.RequestServices.GetRequiredService<IOptions<WizardSecurityOptions>>().Value.PostsPerMinutePerClient;
            return RateLimitPartition.GetFixedWindowLimiter(
                "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        }),
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (!IsWizardPost(context) || context.Request.RouteValues["id"] is not string id)
            {
                return RateLimitPartition.GetNoLimiter("other");
            }

            var limit = context.RequestServices.GetRequiredService<IOptions<WizardSecurityOptions>>().Value.PostsPerMinutePerRequest;
            return RateLimitPartition.GetFixedWindowLimiter(
                "rma:" + id,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        }));
});

// ScopeGuard takes its options by value rather than IOptions, so it is registered over the
// bound options instance. It is stateless and therefore shared.
builder.Services.AddOptions<ScopeGuardOptions>()
    .Bind(builder.Configuration.GetSection(ScopeGuardOptions.SectionName))
    // With no vocabulary every question scores zero topic overlap and is refused, so the
    // assistant silently never runs. That was the shipped state until 2026-11-20 (the settings
    // sat under a root "ScopeGuard" key this binding never read); refuse to start instead.
    .Validate(
        o => o.TopicVocabularies.Any(v => v.Count > 0),
        "Triage:ScopeGuard:TopicVocabularies is empty, so the scope guard would refuse every question.")
    .ValidateOnStart();
builder.Services.AddSingleton<IScopeGuard, ScopeGuard>(sp =>
    new ScopeGuard(sp.GetRequiredService<IOptions<ScopeGuardOptions>>().Value));

builder.Services.AddOptions<TriagePipelineOptions>()
    .Bind(builder.Configuration.GetSection(TriagePipelineOptions.SectionName))
    .ValidateOnStart();

// Antiforgery is on because every wizard step is a POST that changes the return's state. The
// [ValidateAntiForgeryToken] attributes on the POST actions enforce it.
// The link-expiry filter is applied to the wizard controller only (ServiceFilter attribute):
// operators act on returns of any age.
builder.Services.AddControllersWithViews();

// Operator back office (P10): cookie session, OpenID Connect sign-in when configured.
builder.Services.AddOperatorAuth(builder.Configuration, builder.Environment);
builder.Services.AddScoped<AIEnabledRma.Domain.Operations.OperatorService>();

// Development only: run the demo on a fixed date. The seeded warranties are anchored on
// 2026-03-01, so on the wall clock the README's demo serials drift out of their scenarios
// (Demo:Today=2026-09-29 restores them).
if (builder.Environment.IsDevelopment()
    && DateOnly.TryParseExact(builder.Configuration["Demo:Today"], "yyyy-MM-dd", out var demoToday))
{
    var demoNow = new DateTimeOffset(demoToday.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
    builder.Services.AddSingleton<TimeProvider>(new AIEnabledRma.Web.Security.FixedTimeProvider(demoNow));
    builder.Services.AddSingleton<IClock>(new AIEnabledRma.Web.Security.FixedClock(demoNow));
}

// Last, so every other startup rule has had its say: Production refuses default database
// passwords, plain-http AI endpoints, the fake gateway and an operator area without real sign-in.
if (builder.Environment.IsProduction())
{
    ProductionConfiguration.EnsureSafe(builder.Configuration, checkOperatorSignIn: true);
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseRmaSecurityHeaders();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

// Stripe webhook: signature-verified, then a payment the customer abandoned or that failed after
// the wizard is filed for human review. Registered only when Stripe is the provider.
if (useStripe)
{
    app.MapPost("/payments/stripe/webhook", async (
        HttpContext context,
        IOptions<StripeOptions> stripe,
        AIEnabledRma.Domain.Operations.IReviewQueue reviews,
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

        using var json = System.Text.Json.JsonDocument.Parse(payload);
        var type = json.RootElement.GetProperty("type").GetString();
        if (type is "payment_intent.payment_failed" or "payment_intent.canceled")
        {
            var intent = json.RootElement.GetProperty("data").GetProperty("object");
            var rma = intent.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("rma_number", out var n) ? n.GetString() : null;
            await reviews.AddAsync(
                new AIEnabledRma.Domain.Operations.ReviewItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAtUtc = time.GetUtcNow(),
                    Source = AIEnabledRma.Domain.Operations.ReviewSources.PaymentFailed,
                    Reason = "Stripe:" + type,
                    Explanation = $"PaymentIntent {intent.GetProperty("id").GetString()} for {rma ?? "an unknown return"}.",
                },
                context.RequestAborted);
        }

        log.LogInformation("Stripe webhook {Type} accepted.", type);
        return Results.Ok();
    });
}

// Liveness: says only that the process answers. Nothing about the deployment is disclosed to an
// anonymous caller (F7).
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Readiness: can the app reach its database? Kept apart from /health so a probe can tell "the
// process is up" from "the process can serve returns".
app.MapGet("/health/ready", async (RmaDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "database-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

// Operator diagnostics: the corpus and the AI provider, which both fail in ways that look like
// "the assistant is useless" from the customer's side. Operators only.
app.MapGet("/health/details", (
    HttpContext context,
    IOptions<OperationsOptions> operations,
    KnowledgeIndexHolder holder,
    IChatModel chatModel) =>
{
    if (!IsOperator(context, operations.Value, app.Environment))
    {
        return Results.Unauthorized();
    }

    var index = holder.Current;

    return Results.Ok(new
    {
        status = "ok",
        knowledgeBase = new
        {
            path = index.RootPath,
            articleCount = index.Articles.Count,
            loadErrors = index.LoadErrors,
        },
        ai = new { provider = chatModel.ProviderName },
    });
});

app.Run();

static bool IsWizardPost(HttpContext context) =>
    HttpMethods.IsPost(context.Request.Method)
    && context.Request.Path.StartsWithSegments("/RmaWizard", StringComparison.OrdinalIgnoreCase);

static bool IsOperator(HttpContext context, OperationsOptions operations, IWebHostEnvironment environment)
{
    if (string.IsNullOrEmpty(operations.ApiKey))
    {
        return environment.IsDevelopment();
    }

    var supplied = context.Request.Headers["X-Operator-Key"].ToString();
    return supplied.Length > 0
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(operations.ApiKey));
}

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
