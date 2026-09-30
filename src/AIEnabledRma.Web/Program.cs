using AIEnabledRma.Ai;
using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Triage;
using AIEnabledRma.Rag;
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

// Warranty windows and return windows are date-sensitive, so every date in the flow is read
// through IClock. Singleton, because it holds no state and must not vary between requests.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(TimeProvider.System);

// The bundled gateway keeps its authorisations in memory, because the wizard authorises on the
// payment step and captures or voids on a later request. Scoping it to the request would throw
// those authorisations away in between, so it is a singleton. Replace this registration with a
// real provider to charge anything.
builder.Services.AddSingleton<IPaymentGateway, FakePaymentGateway>();

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
builder.Services.AddControllersWithViews();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

// Startup diagnostics, mirroring the RAG service. The wizard depends on three things that can
// each fail independently and all of which look like "the assistant is useless" from the
// customer's side: the corpus, the AI provider, and the database.
app.MapGet("/health", (
    KnowledgeIndexHolder holder,
    IChatModel chatModel,
    RmaDbContext db) =>
{
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
