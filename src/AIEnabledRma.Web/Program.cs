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
if (builder.Environment.IsDevelopment()
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
    .ValidateOnStart();
builder.Services.AddSingleton<IScopeGuard, ScopeGuard>(sp =>
    new ScopeGuard(sp.GetRequiredService<IOptions<ScopeGuardOptions>>().Value));

builder.Services.AddOptions<TriagePipelineOptions>()
    .Bind(builder.Configuration.GetSection(TriagePipelineOptions.SectionName))
    .ValidateOnStart();

// Antiforgery is on because every wizard step is a POST that changes the return's state. The
// [ValidateAntiForgeryToken] attributes on the POST actions enforce it.
builder.Services.AddControllersWithViews(mvc => mvc.Filters.AddService<CapabilityExpiryFilter>());

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
app.UseAntiforgery();

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

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
