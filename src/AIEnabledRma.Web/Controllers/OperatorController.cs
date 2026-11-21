using System.Security.Claims;
using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Store;
using AIEnabledRma.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Web.Controllers;

/// <summary>
/// The back office (P10): the human-review queue, receive / inspect / capture or void for returns,
/// the knowledge-base review queue and the quarantined CRM cases. Operators only; every action is
/// written to <c>operator_actions</c> by <see cref="OperatorService"/>.
/// </summary>
[Authorize(Policy = OperatorAuthOptions.Policy)]
public sealed class OperatorController(
    OperatorService operatorService,
    IOperationsRepository operations,
    KnowledgeIndexHolder knowledge,
    IOptions<OperatorAuthOptions> authOptions,
    IServiceProvider services) : Controller
{
    private static readonly RmaRequestStatus[] Waiting =
    [
        RmaRequestStatus.AwaitingShipment,
        RmaRequestStatus.Received,
        RmaRequestStatus.InRepair,
    ];

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var caseReport = services.GetService<PgVectorKnowledgeStore>() is { } store
            ? await TryReadCaseReportAsync(store, cancellationToken)
            : null;

        return View(new OperatorDashboard
        {
            ReviewItems = await operations.OpenReviewItemsAsync(100, cancellationToken),
            Requests = await operations.RequestsInStatusAsync(Waiting, 100, cancellationToken),
            UnreviewedArticles = knowledge.Current.Articles.Where(a => a.ReviewedBy is null || a.ReviewedAt is null).Select(a => (a.Id, a.Title, a.ContentHash)).ToList(),
            KnowledgeLoadErrors = knowledge.Current.LoadErrors,
            CaseReportJson = caseReport,
            RecentActions = await operations.RecentActionsAsync(25, cancellationToken),
            Message = TempData["OperatorMessage"] as string,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(Guid id, CancellationToken cancellationToken) =>
        Done(await operatorService.MarkReceivedAsync(id, Me, cancellationToken));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Capture(Guid id, string? inspectionNote, CancellationToken cancellationToken) =>
        Done(await operatorService.CaptureAsync(id, Me, inspectionNote ?? string.Empty, cancellationToken));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Void(Guid id, string? reason, CancellationToken cancellationToken) =>
        Done(await operatorService.VoidAsync(id, Me, reason ?? string.Empty, cancellationToken));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(Guid id, string? resolution, CancellationToken cancellationToken) =>
        Done(await operatorService.ResolveReviewItemAsync(id, Me, resolution ?? string.Empty, cancellationToken));

    [AllowAnonymous]
    [HttpGet]
    public IActionResult SignIn(string? returnUrl)
    {
        var options = authOptions.Value;
        var target = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Action(nameof(Index));

        if (options.OidcConfigured)
        {
            return Challenge(new AuthenticationProperties { RedirectUri = target }, OperatorAuthOptions.OidcScheme);
        }

        ViewBag.DevSignIn = options.DevSignIn;
        ViewBag.ReturnUrl = target;
        return View();
    }

    /// <summary>Development and test host only (enforced at startup and here).</summary>
    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DevSignIn(string? returnUrl)
    {
        if (!authOptions.Value.DevSignIn)
        {
            return NotFound();
        }

        var identity = new ClaimsIdentity(
            [
                new Claim("sub", "dev-operator"),
                new Claim("preferred_username", "dev-operator"),
                new Claim("roles", authOptions.Value.RequiredRole),
            ],
            OperatorAuthOptions.CookieScheme,
            "preferred_username",
            "roles");

        await HttpContext.SignInAsync(OperatorAuthOptions.CookieScheme, new ClaimsPrincipal(identity));
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action(nameof(Index))!);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public new async Task<IActionResult> SignOut()
    {
        await HttpContext.SignOutAsync(OperatorAuthOptions.CookieScheme);
        return RedirectToAction(nameof(SignIn));
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Denied() => View();

    private string Me => OperatorAuthSetup.OperatorName(User);

    private RedirectToActionResult Done(OperatorResult result)
    {
        TempData["OperatorMessage"] = (result.Succeeded ? "Done: " : "Not done: ") + result.Message;
        return RedirectToAction(nameof(Index));
    }

    private static async Task<string?> TryReadCaseReportAsync(PgVectorKnowledgeStore store, CancellationToken cancellationToken)
    {
        try
        {
            return await store.GetMetaAsync(Rag.Cases.CaseIngestionPipeline.LastReportKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}

public sealed record OperatorDashboard
{
    public required IReadOnlyList<ReviewItem> ReviewItems { get; init; }

    public required IReadOnlyList<RmaRequest> Requests { get; init; }

    public required IReadOnlyList<(string Id, string Title, string ContentHash)> UnreviewedArticles { get; init; }

    public required IReadOnlyList<string> KnowledgeLoadErrors { get; init; }

    public string? CaseReportJson { get; init; }

    public required IReadOnlyList<OperatorAction> RecentActions { get; init; }

    public string? Message { get; init; }
}
