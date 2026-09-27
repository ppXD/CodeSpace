namespace CodeSpace.Messages.Agents;

/// <summary>
/// What one run asks the model-credential broker to front: the run's identity + fence, the decrypted upstream
/// credential the broker will hold on its behalf, and how long the lease answers without a renewal.
///
/// <para>A record rather than five parameters (Rule 1): the epoch is the load-bearing field and it must not be
/// possible to pass it positionally into the wrong slot. <see cref="Epoch"/> is the claiming worker's fence — the
/// broker refuses a renewal that does not present it, and a second <c>OpenAsync</c> at a HIGHER epoch (a reclaimed
/// run's next attempt) replaces the lease, so the superseded attempt's token stops being honoured immediately
/// rather than after its TTL.</para>
/// </summary>
public sealed record ModelCredentialLeaseRequest
{
    /// <summary>The agent run the lease belongs to — one live lease per run.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The run's team, taken from the loaded run row (never a forgeable envelope) — recorded so a lease can say whose key it is holding.</summary>
    public required Guid TeamId { get; init; }

    /// <summary>The claiming worker's fence epoch. A renewal presenting any other epoch is refused, which is what makes a reclaimed run's old worker unable to keep the key alive.</summary>
    public required long Epoch { get; init; }

    /// <summary>The decrypted credential the broker authenticates UPSTREAM with. It never leaves the broker's in-memory lease — the sandbox only ever sees the run token.</summary>
    public required ResolvedModelCredential Upstream { get; init; }

    /// <summary>How long the lease answers from now without a renewal. Sized by the caller off its own heartbeat cadence, so a worker that stops heartbeating stops paying for model calls.</summary>
    public required TimeSpan Ttl { get; init; }

    /// <summary>
    /// A per-run Unix socket to ALSO serve the lease on, or null for a TCP-only lease. A lease with a socket binds its
    /// TCP listener on loopback only: a child in a network namespace of its own reaches the broker through the socket,
    /// never through the host's TCP port. The path belongs to this one lease — its file is deleted when the lease
    /// closes (its directory never is, because a sandbox's bind pins that directory) — so a caller mints a fresh path
    /// for every open rather than reusing another lease's. A path a live lease on the same worker still serves is not
    /// bound again: the lease then comes back with no socket, and the broker says so in a Warning.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SocketPath { get; init; }
}
