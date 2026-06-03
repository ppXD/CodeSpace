using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// The probed credential's effective <see cref="RepositoryRole"/> on a repository — reported by the
/// provider, NOT judged here. Whether that role is ENOUGH is decided generically by the act-as-user gate,
/// which compares it against the role the node's declared capability needs (per-provider data on the
/// module). This keeps the provider answering only "what role does this actor have", never "is it enough".
/// </summary>
public sealed record RepositoryActorAccess
{
    /// <summary>
    /// The actor's effective role, or <c>null</c> when the probe was INCONCLUSIVE (transient error /
    /// couldn't determine). The gate treats null as "don't block" so a flaky probe never refuses a
    /// legitimate click — the write path stays the backstop.
    /// </summary>
    public RepositoryRole? Role { get; init; }

    /// <summary>
    /// Optional provider-native name for the role (e.g. GitLab "Reporter") used to explain WHY a click was
    /// refused. Null → the gate falls back to the neutral <see cref="RepositoryRole"/> name (which already
    /// matches GitHub's own UI terms).
    /// </summary>
    public string? RoleLabel { get; init; }

    /// <summary>Probe was inconclusive (transient / unexpected) — the gate degrades to "allow".</summary>
    public static RepositoryActorAccess Inconclusive { get; } = new() { Role = null };

    /// <summary>The actor's determined role, with an optional provider-native label.</summary>
    public static RepositoryActorAccess Of(RepositoryRole role, string? roleLabel = null) => new() { Role = role, RoleLabel = roleLabel };
}
