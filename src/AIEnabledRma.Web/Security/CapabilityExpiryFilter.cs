using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Rma;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Web.Security;

/// <summary>
/// The request GUID is the capability that drives a return (see RmaWizardController). A
/// capability that never expires means a leaked link drives that return forever, so a request
/// that is still unconfirmed after <see cref="WizardSecurityOptions.CapabilityLifetimeDays"/>
/// refuses every further step with 410 Gone.
/// </summary>
public sealed class CapabilityExpiryFilter(
    IRmaRepository requests,
    IOptions<WizardSecurityOptions> options,
    TimeProvider time) : IAsyncActionFilter
{
    private static readonly HashSet<RmaRequestStatus> Unconfirmed =
    [
        RmaRequestStatus.Draft,
        RmaRequestStatus.PendingPayment,
        RmaRequestStatus.Approved,
    ];

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var id = RequestIdOf(context);
        if (id is { } requestId)
        {
            var request = await requests.GetByIdAsync(requestId, context.HttpContext.RequestAborted);
            if (request is not null && IsExpired(request, time.GetUtcNow(), options.Value))
            {
                context.Result = new ViewResult
                {
                    ViewName = "Expired",
                    StatusCode = StatusCodes.Status410Gone,
                };
                return;
            }
        }

        await next();
    }

    public static bool IsExpired(RmaRequest request, DateTimeOffset now, WizardSecurityOptions options) =>
        Unconfirmed.Contains(request.Status)
        && now - request.CreatedAtUtc > TimeSpan.FromDays(options.CapabilityLifetimeDays);

    private static Guid? RequestIdOf(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("id", out var raw) && raw is Guid id && id != Guid.Empty)
        {
            return id;
        }

        // Later steps post a form that carries the request id as RmaId.
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument?.GetType().GetProperty("RmaId")?.GetValue(argument) is Guid formId && formId != Guid.Empty)
            {
                return formId;
            }
        }

        return null;
    }
}
