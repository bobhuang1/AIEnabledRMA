namespace AIEnabledRma.Web.Security;

/// <summary>Response headers every page gets.</summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Scripts, styles and forms from this origin only; never framed. The views use no inline
    /// script or style, so no 'unsafe-inline' is needed.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
        + "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static IApplicationBuilder UseRmaSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Content-Security-Policy"] = ContentSecurityPolicy;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["X-Frame-Options"] = "DENY";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                return Task.CompletedTask;
            });

            await next();
        });
}
