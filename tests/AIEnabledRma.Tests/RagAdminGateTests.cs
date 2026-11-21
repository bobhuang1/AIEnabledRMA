using System.Net;
using AIEnabledRma.Rag;
using Microsoft.AspNetCore.Http;

namespace AIEnabledRma.Tests;

/// <summary>
/// The decisions <see cref="RagAdminGate"/> makes, tested directly. The endpoint tests boot a
/// host, but a remote caller, an unconfigured key, and a key that differs only in case are all
/// cheaper and clearer to pin here.
/// </summary>
public sealed class RagAdminGateTests
{
    private const string Key = "correct-horse";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_key_loopback_may_pass_and_a_remote_caller_may_not(string? configuredKey)
    {
        Assert.True(RagAdminGate.IsAllowed(Context(IPAddress.Loopback), configuredKey));
        Assert.False(RagAdminGate.IsAllowed(Context(IPAddress.Parse("203.0.113.7")), configuredKey));
    }

    [Fact]
    public void A_caller_with_no_address_is_refused_rather_than_assumed_local() =>
        Assert.False(RagAdminGate.IsAllowed(Context(remote: null), configuredKey: null));

    [Fact]
    public void Once_a_key_is_configured_the_header_decides_and_the_address_does_not()
    {
        Assert.False(RagAdminGate.IsAllowed(Context(IPAddress.Loopback), Key));
        Assert.False(RagAdminGate.IsAllowed(Context(IPAddress.Parse("203.0.113.7")), Key));

        Assert.True(RagAdminGate.IsAllowed(Context(IPAddress.Loopback, Key), Key));
        Assert.True(RagAdminGate.IsAllowed(Context(IPAddress.Parse("203.0.113.7"), Key), Key));
    }

    [Fact]
    public void The_key_is_compared_exactly_not_case_insensitively() =>
        Assert.False(RagAdminGate.IsAllowed(Context(IPAddress.Loopback, Key.ToUpperInvariant()), Key));

    // The shipped appsettings carries an empty key and an absent setting reads as null, so both
    // shapes have to be refused; they reach the gate by different routes.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_keyless_host_refuses_to_start_outside_development(string? configuredKey)
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => RagAdminGate.EnsureStartupAllowed(configuredKey, isDevelopment: false));

        Assert.Contains(RagAdminGate.ApiKeyConfigurationKey, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The example env file's key is published in this repository, so it admits every reader of the
    /// repository to /diagnostics, /retrieve and /rag/reload. It is present and non-empty, which is
    /// all the older check required.
    /// </summary>
    [Theory]
    [InlineData("replace-with-a-long-random-key")]
    [InlineData("Replace-Me")]
    public void A_placeholder_key_refuses_to_start_outside_development(string configuredKey)
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => RagAdminGate.EnsureStartupAllowed(configuredKey, isDevelopment: false));

        Assert.Contains(RagAdminGate.ApiKeyConfigurationKey, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_keyless_host_may_start_in_development() =>
        Assert.Null(Record.Exception(
            () => RagAdminGate.EnsureStartupAllowed(configuredKey: null, isDevelopment: true)));

    [Fact]
    public void A_configured_key_is_enough_to_start_anywhere() =>
        Assert.Null(Record.Exception(
            () => RagAdminGate.EnsureStartupAllowed(Key, isDevelopment: false)));

    private static HttpContext Context(IPAddress? remote, string? suppliedKey = null)
    {
        var context = new DefaultHttpContext();

        // DefaultHttpContext carries a settable connection, which is what lets the loopback and
        // remote branches be exercised without binding a socket.
        context.Connection.RemoteIpAddress = remote;

        if (suppliedKey is not null)
        {
            context.Request.Headers[RagAdminGate.HeaderName] = suppliedKey;
        }

        return context;
    }
}
