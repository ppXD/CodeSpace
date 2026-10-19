using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.RepositoryBinding;

/// <summary>
/// Moves a bound repository from one provider connection to another connection to the same host
/// (<see cref="ProviderInstances.ProviderInstanceHost"/>), onto a credential of the new connection. The repository is the same row —
/// its id, project links, triggers and history stay — and what changes is whom it is bound through: its credential and its connection,
/// always together, and the hooks that deliver its events, which a connection owns. Split in two, as a bind is, because the caller owns
/// the unit of work between staging and dispatch.
///
/// <para>Deliberately narrow: only the move that changes the connection (a move within one connection is just a new credential, and
/// needs no hook), so the doors that accept it — a relink, a Disconnect that names a target, a connect's carry-forward, the repair
/// sweep — share one definition of what the move touches.</para>
/// </summary>
public interface IRepositoryRehoming
{
    /// <summary>
    /// For each of <paramref name="repositories"/> (on a connection other than <paramref name="to"/>'s) that cannot move onto the credential's
    /// connection, why — worded for the person who asked; a repository that can is not in the answer. The connection already holds a live row
    /// for the same project (a repository is bound once per connection, which the database enforces — and so is a second one in this
    /// same batch), or the credential's granted scopes do not cover reading it and registering the hook the connection's webhook
    /// scope needs: a move that left the repository unable to receive events would trade a visible failure for a silent one. Reads
    /// nothing from the provider.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> RefusalsAsync(IReadOnlyList<Repository> repositories, Credential to, CancellationToken cancellationToken);

    /// <summary>
    /// Re-point <paramref name="repository"/> at <paramref name="to"/> and the connection it belongs to, as tracked changes, and stage
    /// what its hooks need there — the way a bind does: a per-repository connection gets a fresh Pending hook, a connection-wide one an
    /// owner hook if none covers the owner yet; the repository's hooks under its old connection are retired (a hook still registered is
    /// removed and deleted at the provider once the move commits; the rest are cancelled). Lifting a disconnect's Error is the caller's,
    /// as it is for a move that stays on one connection. Call <see cref="RefusalsAsync"/> first.
    /// </summary>
    Task<RehomeStaged> RehomeAsync(Repository repository, Credential to, CancellationToken cancellationToken);

    /// <summary>Hand what <see cref="RehomeAsync"/> staged to the registrars, and ask the provider to delete the hooks it retired, once the caller's unit of work has committed.</summary>
    Task DispatchAfterCommitAsync(RehomeStaged staged, CancellationToken cancellationToken);
}

/// <summary>What one <see cref="IRepositoryRehoming.RehomeAsync"/> left for after the commit: the hooks to register, and the retired ones to delete at the provider.</summary>
public sealed record RehomeStaged
{
    public static readonly RehomeStaged Nothing = new();

    /// <summary>The Pending per-repository hook staged for the new connection, when its webhook scope is per repository.</summary>
    public Guid? RepositoryHookId { get; init; }

    /// <summary>The owner hook staged (or revived) on the new connection, when its webhook scope is connection-wide and no live hook covered the owner already.</summary>
    public Guid? ConnectionHookId { get; init; }

    /// <summary>Hooks the repository had registered under its old connection, to delete at the provider through the new credential.</summary>
    public RetiredRemoteHooks? Retired { get; init; }
}

/// <summary>The provider-side hooks a move retired, with what is needed to delete them as the credential that took the repository over.</summary>
public sealed record RetiredRemoteHooks(ProviderInstance Instance, Credential Credential, RemoteRepository Repository, IReadOnlyList<string> HookExternalIds);
