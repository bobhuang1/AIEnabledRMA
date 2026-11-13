using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace AIEnabledRma.Tests;

/// <summary>
/// Makes the test host look like a request from a given address. TestServer reports no remote
/// address at all, which matters to the two places that decide on the caller's address: the RAG
/// admin gate, which trusts loopback and nothing unidentified, and the wizard's write limiter,
/// which partitions on it.
/// </summary>
internal sealed class PeerAddressStartupFilter(IPAddress peer) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = peer;
                return nextMiddleware(context);
            });

            next(app);
        };
}
