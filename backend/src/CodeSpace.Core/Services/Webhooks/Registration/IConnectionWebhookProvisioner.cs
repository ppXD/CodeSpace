using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Creates and retires the group / organization hooks a connection-wide scope needs. Separate from
/// the dispatcher and the registrar because those own one row's walk through its lifecycle, and
/// this owns which rows should exist at all — the question bind and a scope switch both ask.
/// </summary>
public interface IConnectionWebhookProvisioner
{
    /// <summary>
    /// Make sure this connection has a hook covering <paramref name="ownerPath"/>, staged in the
    /// caller's unit of work. Returns the id to dispatch once that unit of work has committed, or
    /// null when a live hook already covers the owner and there is nothing to register.
    ///
    /// <para>"Covers" reads the owner path as the tree it is: a hook on any ANCESTOR of the owner
    /// already covers it, because a group hook fires for every project in the group and its
    /// subgroups. Registering a second one under a subgroup would not be redundancy — it would be
    /// every push there delivered twice, and every push there starting two runs. The reverse is
    /// handled in the same call: staging a hook on an ancestor retires the narrower ones it now
    /// swallows, so the invariant holds whichever order an operator binds in.</para>
    ///
    /// <para>A hook that failed earlier and is past its backoff is revived here rather than left
    /// for a sweep: the next bind under that owner is both the earliest moment we learn the
    /// operator still wants it and a moment we are already writing.</para>
    /// </summary>
    Task<Guid?> EnsureForOwnerAsync(ProviderInstance instance, Guid credentialId, string ownerPath, CancellationToken cancellationToken);

    /// <summary>
    /// Retire every hook this connection has: delete it at the provider best-effort, then take the
    /// local rows out of the lifecycle. Called when a connection leaves connection-wide scope — the
    /// group hook must stop delivering BEFORE per-repository hooks start, so that no event is
    /// delivered twice.
    /// </summary>
    Task<int> RetireAllAsync(ProviderInstance instance, CancellationToken cancellationToken);
}
