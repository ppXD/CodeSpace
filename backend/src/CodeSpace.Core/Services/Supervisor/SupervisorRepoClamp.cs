using System.Text.Json;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The per-agent repo PRIVILEGE GATE (L4 arc B): clamps a model-authored repo subset (a spawn dispatch's
/// <c>targetRepos</c>) to the operator's bound set. The model PROPOSES which repos an agent touches; the server
/// GRANTS only what the operator already bound — every authored repo must be the operator's primary or one of its
/// bound related repos, and access may only DOWNGRADE (write→read is always safe; read→write is rejected). So the
/// model can never reach an unbound repo nor escalate its own write access past the operator's grant. Reuses the
/// SHARED <see cref="AgentWorkspaceAuthoring.ParseRelatedRepositories"/> parse (no forked repo-JSON reading);
/// fail-closed — throws <see cref="SupervisorRepoAccessException"/> on the first out-of-set repo or illegal upgrade.
/// </summary>
public static class SupervisorRepoClamp
{
    /// <summary>
    /// Validate + return the model-authored RELATED-repo subset against the operator's bound set. Each authored repo
    /// MUST be the operator's primary or one of its bound related repos, and its requested access may not exceed the
    /// operator's grant. Throws <see cref="SupervisorRepoAccessException"/> on the first violation (fail-closed). An
    /// empty / absent authored subset returns empty (no related repos → the agent runs single-repo on the primary).
    ///
    /// <para>NOTE: this validates the RELATED subset only. A per-agent PRIMARY override (a dispatch's repositoryId) is a
    /// SEPARATE writable axis — clamp it with <see cref="ClampPrimary"/>, which a consumer (B3) MUST call too.</para>
    /// </summary>
    public static IReadOnlyList<WorkspaceRepositorySpec> IntersectWithBoundRepos(JsonElement authoredSubset, Guid? boundPrimaryId, IReadOnlyList<WorkspaceRepositorySpec> boundRelated)
    {
        var authored = AgentWorkspaceAuthoring.ParseRelatedRepositories(authoredSubset);

        if (authored.Count == 0) return authored;

        var granted = BuildGrantedAccess(boundPrimaryId, boundRelated);

        foreach (var repo in authored)
        {
            if (!granted.TryGetValue(repo.RepositoryId, out var grant))
                throw new SupervisorRepoAccessException($"Agent dispatch targets repository {repo.RepositoryId}, which the operator did not bind to this run.");

            if (repo.Access > grant)
                throw new SupervisorRepoAccessException($"Agent dispatch requests write access to repository {repo.RepositoryId}, but the operator granted read-only.");
        }

        // S1: the launch base pin is SERVER truth, never a model authoring — each granted entry takes the BOUND
        // spec's pin (a dispatched agent's mounts materialize the same base as its homogeneous siblings), and a
        // model-authored pinnedSha on the subset is discarded outright (a dispatch must not point a bound mount at
        // an arbitrary commit).
        var boundPins = boundRelated.Where(b => !string.IsNullOrWhiteSpace(b.PinnedSha)).ToDictionary(b => b.RepositoryId, b => b.PinnedSha);

        return authored.Select(repo => repo with { PinnedSha = boundPins.TryGetValue(repo.RepositoryId, out var pin) ? pin : null }).ToList();
    }

    /// <summary>
    /// Validate a model-authored per-agent PRIMARY repo override against the operator's bound set. The primary is the
    /// agent's WRITABLE cwd anchor (<see cref="WorkspaceSpec.FromAuthoredRepos"/> hardcodes the primary
    /// <see cref="WorkspaceAccess.Write"/>), so the requested override must be a repo the operator bound WITH WRITE
    /// access — its own primary, or a write-granted related repo. Null → the operator's primary (no override). Throws
    /// <see cref="SupervisorRepoAccessException"/> when the override is unbound OR only read-granted, so the model can
    /// never make an unbound (or read-only) repo its writable primary — the escalation the related-subset clamp alone
    /// would miss.
    /// </summary>
    public static Guid? ClampPrimary(Guid? requestedPrimaryId, Guid? boundPrimaryId, IReadOnlyList<WorkspaceRepositorySpec> boundRelated)
    {
        if (requestedPrimaryId is not { } requested) return boundPrimaryId;

        var granted = BuildGrantedAccess(boundPrimaryId, boundRelated);

        if (!granted.TryGetValue(requested, out var grant))
            throw new SupervisorRepoAccessException($"Agent dispatch sets primary repository {requested}, which the operator did not bind to this run.");

        if (grant < WorkspaceAccess.Write)
            throw new SupervisorRepoAccessException($"Agent dispatch sets primary repository {requested}, but the operator granted it read-only — a primary must be writable.");

        return requested;
    }

    /// <summary>The operator's bound repos → max granted access: the primary is writable; each related repo carries its operator-granted access. A repo bound as both takes the higher grant.</summary>
    private static Dictionary<Guid, WorkspaceAccess> BuildGrantedAccess(Guid? boundPrimaryId, IReadOnlyList<WorkspaceRepositorySpec> boundRelated)
    {
        var granted = new Dictionary<Guid, WorkspaceAccess>();

        if (boundPrimaryId is { } primary) granted[primary] = WorkspaceAccess.Write;

        foreach (var repo in boundRelated)
            granted[repo.RepositoryId] = granted.TryGetValue(repo.RepositoryId, out var existing) && existing > repo.Access ? existing : repo.Access;

        return granted;
    }
}

/// <summary>Raised when a model-authored agent dispatch targets a repo the operator did not bind, or requests access beyond the operator's grant — the per-agent repo privilege gate's fail-closed signal (L4 arc B).</summary>
public sealed class SupervisorRepoAccessException : Exception
{
    public SupervisorRepoAccessException(string message) : base(message) { }
}
