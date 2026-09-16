namespace CodeSpace.Messages.Agents;

/// <summary>
/// What a RE-ATTACHING worker asks the broker to re-open: the address a still-running agent already holds, plus the
/// credential to front behind it again. Nothing here is minted — the port, the route and the bearer all come off the
/// run's durable handle, because the detached CLI's base URL was frozen at launch and a fresh one would reach it
/// never.
///
/// <para>The sibling of <see cref="ModelCredentialLeaseRequest"/> and deliberately NOT a variant of it: an open MINTS
/// an address and may choose any free port, a re-bind RESTORES one and must take the port it is given or fail. Folding
/// them into one request with three nullable fields would make "mint" and "restore" a runtime reading of which fields
/// happened to be set, on the one path where guessing wrong hands a live run a port its agent does not call.</para>
///
/// <para><see cref="Epoch"/> is the RE-ATTACH's fence, not the launch's — the reclaim bumped it, and the lease must be
/// installed at the epoch whose heartbeat is about to renew it. Same init-only shape as its sibling, for the same
/// reason: the epoch and the port are both bare numbers, and a positional list is where they get swapped.</para>
/// </summary>
public sealed record ModelCredentialRebindRequest
{
    /// <summary>The agent run whose address is being re-opened.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The run's team, taken from the loaded run row — recorded so the restored lease can say whose key it is holding, exactly as the original did.</summary>
    public required Guid TeamId { get; init; }

    /// <summary>The RE-ATTACHING worker's fence epoch. The restored lease is keyed on it, so this pass's heartbeat renews it and a superseded worker's cannot.</summary>
    public required long Epoch { get; init; }

    /// <summary>The TCP port the launch bound, read off the run's durable handle. The broker binds THIS port or refuses — an ephemeral substitute would answer on an address nothing calls.</summary>
    public required int Port { get; init; }

    /// <summary>The route segment the launch minted, read off the same handle. Installed verbatim: the agent's base URL carries it, so a fresh id would 401 every call it makes.</summary>
    public required string PathId { get; init; }

    /// <summary>The per-run bearer the launch minted, read off the same handle. Installed verbatim, for the same reason — the agent's environment already holds it.</summary>
    public required string RunToken { get; init; }

    /// <summary>The decrypted credential the restored lease authenticates UPSTREAM with, re-resolved by this worker. It never leaves the broker's memory, here exactly as at launch.</summary>
    public required ResolvedModelCredential Upstream { get; init; }

    /// <summary>How long the restored lease answers without a renewal — the same window an open uses, sized off the heartbeat that is about to start renewing it.</summary>
    public required TimeSpan Ttl { get; init; }
}
