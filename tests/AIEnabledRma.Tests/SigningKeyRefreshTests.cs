using System.Security.Cryptography;
using AIEnabledRma.Mcp;
using Microsoft.IdentityModel.Tokens;

namespace AIEnabledRma.Tests;

/// <summary>
/// An unknown key id means the identity provider rotated its signing keys, and the transport has to
/// fetch the new set instead of refusing every token until somebody restarts it.
/// </summary>
/// <remarks>
/// The real sequence this stands in for: compose boots, Keycloak imports the realm and mints key A,
/// the transport caches A, Keycloak is re-imported and mints key B, and the next machine token names
/// B. Before this resolver existed that token was a 401 — reproduced against the compose stack — and
/// the only cure was <c>docker compose restart mcp-http</c>. The keys here are stand-ins for the
/// realm's, because what is under test is which key set reaches validation, not the issuer.
/// </remarks>
public sealed class SigningKeyRefreshTests
{
    private static readonly SecurityKey KeyA = NewKey("key-a");
    private static readonly SecurityKey KeyB = NewKey("key-b");

    [Fact]
    public void A_known_key_id_uses_the_cached_set()
    {
        var keySet = new FakeKeySet([KeyA], [KeyA, KeyB]);
        var keys = new RefreshingSigningKeys(keySet, new FixedClock());

        var resolved = keys.Resolve("key-a");

        Assert.Same(KeyA, Assert.Single(resolved));
        Assert.Equal(0, keySet.Refetches);
    }

    [Fact]
    public void An_unknown_key_id_refetches_and_validates_with_the_new_set()
    {
        var keySet = new FakeKeySet([KeyA], [KeyA, KeyB]);
        var keys = new RefreshingSigningKeys(keySet, new FixedClock());

        var resolved = keys.Resolve("key-b").ToArray();

        Assert.Equal(1, keySet.Refetches);
        Assert.Contains(KeyB, resolved);
    }

    [Fact]
    public void A_token_with_no_key_id_does_not_refetch()
    {
        var keySet = new FakeKeySet([KeyA], [KeyA, KeyB]);
        var keys = new RefreshingSigningKeys(keySet, new FixedClock());

        var resolved = keys.Resolve(null);

        Assert.Same(KeyA, Assert.Single(resolved));
        Assert.Equal(0, keySet.Refetches);
    }

    /// <summary>
    /// The key id comes from the caller, so unknown ids have to be cheaper than a discovery request
    /// each. Within the interval the request fails validation without touching the identity provider.
    /// </summary>
    [Fact]
    public void A_second_unknown_key_id_within_the_interval_does_not_refetch_again()
    {
        var clock = new FixedClock();
        var keySet = new FakeKeySet([KeyA], [KeyA]);
        var keys = new RefreshingSigningKeys(keySet, clock);

        keys.Resolve("key-b");
        clock.Advance(RefreshingSigningKeys.MinimumRefetchInterval - TimeSpan.FromSeconds(1));
        var second = keys.Resolve("key-c");

        Assert.Equal(1, keySet.Refetches);
        Assert.Same(KeyA, Assert.Single(second));
    }

    [Fact]
    public void An_unknown_key_id_after_the_interval_refetches_again()
    {
        var clock = new FixedClock();
        var keySet = new FakeKeySet([KeyA], [KeyA, KeyB]);
        var keys = new RefreshingSigningKeys(keySet, clock);

        keys.Resolve("key-b");
        clock.Advance(RefreshingSigningKeys.MinimumRefetchInterval);
        keys.Resolve("key-c");

        Assert.Equal(2, keySet.Refetches);
    }

    [Fact]
    public void A_refetch_that_fails_leaves_the_token_refused_rather_than_throwing()
    {
        var keySet = new FakeKeySet([KeyA], [KeyA]) { ThrowOnRefetch = true };
        var keys = new RefreshingSigningKeys(keySet, new FixedClock());

        var resolved = keys.Resolve("key-b");

        Assert.Same(KeyA, Assert.Single(resolved));
        Assert.Equal(1, keySet.Refetches);
    }

    private static SecurityKey NewKey(string keyId)
    {
        var rsa = RSA.Create(2048);

        return new RsaSecurityKey(rsa) { KeyId = keyId };
    }

    private sealed class FakeKeySet(IReadOnlyCollection<SecurityKey> held, IReadOnlyCollection<SecurityKey> refetched)
        : ISigningKeySet
    {
        public bool ThrowOnRefetch { get; init; }

        public int Refetches { get; private set; }

        public IReadOnlyCollection<SecurityKey> Current => held;

        public IReadOnlyCollection<SecurityKey> Refetch()
        {
            Refetches++;

            if (ThrowOnRefetch)
            {
                throw new InvalidOperationException("the identity provider is unreachable");
            }

            return refetched;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
