using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace AIEnabledRma.Web;

/// <summary>
/// Which reverse proxies this host believes when they report the client address.
/// </summary>
/// <remarks>
/// The wizard's write limit partitions on <c>Connection.RemoteIpAddress</c>. Behind a reverse
/// proxy that address is the proxy, so every customer shares one partition and 30 writes a
/// minute throttle the whole site. Believing <c>X-Forwarded-For</c> unconditionally is the
/// opposite failure: the header is caller-controlled, so a script could put a different address
/// in every request and never be limited at all.
///
/// So it is opt-in, and it names the proxies. Empty by default — the header is ignored and the
/// limiter behaves exactly as it did without a proxy. A deployment that sits behind one lists
/// that proxy's address, and only requests whose immediate peer is on the list have their
/// forwarded entry believed.
///
/// Addresses rather than ranges or host names: an entry is a statement about who may choose the
/// address the limit is keyed on, so the smallest trust surface is the one worth having.
/// </remarks>
public sealed class TrustedProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>IP addresses of proxies whose <c>X-Forwarded-For</c> entries are believed.</summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>True when the deployment has named at least one proxy.</summary>
    public bool IsConfigured => TrustedProxies.Any(entry => !string.IsNullOrWhiteSpace(entry));

    /// <summary>
    /// Translates the configured proxies into the middleware's options.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// An entry is not an IP address. Ignoring it would silently put every customer back in one
    /// partition, which is the failure this configuration exists to prevent.
    /// </exception>
    public ForwardedHeadersOptions ToForwardedHeadersOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            // Only the client address; scheme and host are not this host's business.
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
        };

        // ForwardLimit keeps the framework default of one hop, so the entry counted is the one
        // the nearest trusted proxy appended. Exposing it as configuration would let a chain
        // decide the address the limit is keyed on, which is the caller-controlled case again.

        // The framework's defaults trust loopback. That suits a sidecar, but this host must
        // believe only what the deployment named: cleared, "not configured" means "no forwarded
        // headers", not "loopback is trusted".
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var entry in TrustedProxies.Where(entry => !string.IsNullOrWhiteSpace(entry)))
        {
            if (!IPAddress.TryParse(entry.Trim(), out var address))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:TrustedProxies contains '{entry}', which is not an IP address. "
                    + "List each proxy address explicitly; ranges and host names are rejected "
                    + "because a wrong entry widens who may choose the address the rate limit "
                    + "partitions on.");
            }

            options.KnownProxies.Add(address);
        }

        return options;
    }
}
