using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AIEnabledRma.Mcp;

/// <summary>
/// The key set tokens are validated against, with the one operation the transport adds: fetch it
/// again because a token arrived carrying a key id nobody has seen.
/// </summary>
internal interface ISigningKeySet
{
    /// <summary>The keys held now, without a network call.</summary>
    IReadOnlyCollection<SecurityKey> Current { get; }

    /// <summary>Retrieves the key set again and returns what it produced.</summary>
    IReadOnlyCollection<SecurityKey> Refetch();
}

/// <summary>
/// The production key set: the identity provider's discovery document and its keys, retrieved through
/// a fresh <see cref="ConfigurationManager{T}"/> every time a refetch is asked for.
/// </summary>
/// <remarks>
/// A refetch deliberately builds a new manager rather than calling <c>RequestRefresh</c> on the
/// existing one. <c>RequestRefresh</c> only marks the cache stale; the manager's own refresh throttle
/// then served the previous document, and the transport went on trying the previous key. That was
/// measured against the compose stack: the resolver was reached, refetched, and got back the key id
/// of the realm that had just been replaced, so the rotated token was still a 401. A new manager has
/// no cache to serve and fetches on its first read.
///
/// The managers created here are not disposed: the JWT handler holds the manager it was configured
/// with and reads it on every request, so disposing anything it captured would break later requests.
/// One extra manager per signing-key rotation is bounded by how often the realm is re-imported.
/// </remarks>
internal sealed class ConfigurationSigningKeySet(Func<ConfigurationManager<OpenIdConnectConfiguration>> create)
    : ISigningKeySet
{
    private ConfigurationManager<OpenIdConnectConfiguration> configuration = create();

    /// <remarks>
    /// Both members block. Validation is called synchronously by the JWT handler, which already
    /// blocks on this cache for its first request, so this adds a second caller rather than a new
    /// kind of wait.
    /// </remarks>
    public IReadOnlyCollection<SecurityKey> Current => Read(configuration);

    public IReadOnlyCollection<SecurityKey> Refetch()
    {
        var replacement = create();
        var keys = Read(replacement);

        Interlocked.Exchange(ref configuration, replacement);

        return keys;
    }

    private static IReadOnlyCollection<SecurityKey> Read(ConfigurationManager<OpenIdConnectConfiguration> manager) =>
        manager
            .GetConfigurationAsync(CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult()
            .SigningKeys
            .ToArray();
}

/// <summary>
/// Resolves signing keys for a token, refetching when the token names a key the cache does not hold.
/// </summary>
/// <remarks>
/// A Keycloak realm that is re-imported — the compose stack does that on every boot — mints new
/// signing keys. The transport caches the old set, so a valid token minted after the re-import names
/// a key id that is not in the cache and every request answered 401 until someone restarted the
/// process. Refetching on an unknown key id makes the first request after a rotation succeed.
///
/// Refetches are throttled, because the key id comes from the caller: without a floor, a stream of
/// tokens with invented key ids would turn into a stream of discovery requests to the identity
/// provider. Within the interval an unknown key id just fails validation, which is also what happens
/// when the refetch does not produce the key it was looking for.
/// </remarks>
internal sealed class RefreshingSigningKeys(ISigningKeySet keys, TimeProvider? time = null)
{
    /// <summary>Shortest gap between two on-demand refetches.</summary>
    internal static readonly TimeSpan MinimumRefetchInterval = TimeSpan.FromSeconds(30);

    private readonly ISigningKeySet keys = keys;
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly Lock gate = new();
    private DateTimeOffset lastRefetch = DateTimeOffset.MinValue;

    /// <summary>
    /// The keys to validate this token with. Unknown key id and no recent refetch: the freshly
    /// retrieved set. Otherwise what the cache holds.
    /// </summary>
    public IEnumerable<SecurityKey> Resolve(string? keyId)
    {
        var held = keys.Current;

        if (string.IsNullOrEmpty(keyId) || Holds(held, keyId))
        {
            return held;
        }

        lock (gate)
        {
            var now = time.GetUtcNow();

            if (now - lastRefetch < MinimumRefetchInterval)
            {
                return held;
            }

            lastRefetch = now;
        }

        try
        {
            return keys.Refetch();
        }
        catch (Exception)
        {
            // The error that got here was "no key with this id"; surfacing a discovery failure
            // instead would hide it behind a network problem. The handler refuses the token either
            // way, and the next request can try again.
            return held;
        }
    }

    private static bool Holds(IEnumerable<SecurityKey> held, string keyId) =>
        held.Any(key => string.Equals(key.KeyId, keyId, StringComparison.Ordinal));
}
