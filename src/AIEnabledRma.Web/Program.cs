using System.Threading.RateLimiting;
using AIEnabledRma.Ai;
using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Triage;
using AIEnabledRma.Privacy;
using AIEnabledRma.Rag;
using AIEnabledRma.Web;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRmaDbContext(builder.Configuration);
builder.Services.AddRmaData(builder.Configuration);

// The same in-process corpus the RAG service and the MCP tools use, so all three hosts answer
// a troubleshooting question identically. AddRag also registers the IKnowledgeRetriever that
// TriagePipeline depends on.
builder.Services.AddRag(builder.Configuration);

builder.Services.AddAiProvider(builder.Configuration);

builder.Services.AddScoped<IRmaWorkflow, RmaWorkflow>();
builder.Services.AddScoped<ITriageService, TriagePipeline>();

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
builder.Services.AddFakePaymentGateway(
    builder.Configuration,
    builder.Environment.IsDevelopment());

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
app.UseAntiforgery();

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

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

app.MapGet("/health", (
    HttpContext context,
    KnowledgeIndexHolder holder,
    IChatModel chatModel) =>
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
    });
});

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

    // These answer with a 302 whose Location carries one of the above, so a stored response would
    // send a later visitor to a step belonging to whoever came before. Start is a GET when the form
    // is rendered — nothing is pending yet — and a POST when it is submitted, which is the one with
    // somewhere to point.
    if (HttpMethods.IsGet(context.Request.Method)
        || context.Request.RouteValues["action"] is not string action)
    {
        return false;
    }

    return string.Equals(action, "Start", StringComparison.OrdinalIgnoreCase)
        || string.Equals(action, "ChooseCustomer", StringComparison.OrdinalIgnoreCase);
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
