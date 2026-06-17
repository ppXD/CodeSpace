namespace CodeSpace.Messages.Agents;

/// <summary>
/// A declarative request to integrate K parallel agent contributions into ONE branch on a single repository
/// (SOTA #3 — make multi-agent fan-out INTEGRATE, not narrate side-by-side). Provider-neutral and a pure data
/// noun (Rule 18.1): the repo URL + token are resolved upstream (the same way <see cref="WorkspaceRequest"/> is),
/// and each contribution carries the durable, base-anchored input the integrator needs.
///
/// <para><b>Base-anchored by construction:</b> the request carries the single shared <see cref="BaseSha"/> the K
/// agents were expected to branch from, and EACH <see cref="BranchContribution"/> carries the base it actually
/// recorded. The integrator checks out <see cref="BaseSha"/> before applying and REFUSES any contribution whose
/// own base disagrees — a stale-base patch otherwise applies "cleanly" onto a moved tree and silently grafts
/// incoherent work.</para>
/// </summary>
public sealed record IntegrationRequest
{
    /// <summary>The team that owns this integration — the tenancy boundary. Used to resolve offloaded patches through the team-scoped artifact store, so a cross-team artifact id resolves to nothing (and that contribution is recorded unintegrable).</summary>
    public required Guid TeamId { get; init; }

    /// <summary>HTTPS (or file://) clone URL of the repository to integrate into.</summary>
    public required string RepositoryUrl { get; init; }

    /// <summary>The branch / ref to clone for context. Null → the remote's default branch. The integration NEVER pushes here — it always pushes a run-id-derived reviewable branch.</summary>
    public string? BaseRef { get; init; }

    /// <summary>The shared base revision ALL contributions must be rooted at — the commit the integrator checks out before applying. The integrity anchor: a contribution whose own base differs is refused.</summary>
    public required string BaseSha { get; init; }

    /// <summary>Access token for HTTPS auth (clone + the integration-branch push). Null → an anonymous clone with no push (a read-only integration that reports but cannot write a branch).</summary>
    public string? Token { get; init; }

    /// <summary>Basic-auth username paired with <see cref="Token"/> — provider-specific ("x-access-token", "oauth2"). Ignored when <see cref="Token"/> is null.</summary>
    public string? TokenUsername { get; init; }

    /// <summary>The deterministic, run-unique name for the integrated branch the integrator publishes (run-id-derived so a replay reuses the same branch — a re-run that reproduces the identical tree is a no-op, never a fork). NEVER equal to <see cref="BaseRef"/> / a protected branch.</summary>
    public required string IntegrationBranch { get; init; }

    /// <summary>Clone depth. 0 (default) → a full clone — a 3-way apply needs the base history the agents' shallow Depth=1 clones lack.</summary>
    public int Depth { get; init; }

    /// <summary>The ordered agent contributions to apply, in the spawn order they were produced (apply order is deterministic for replay).</summary>
    public required IReadOnlyList<BranchContribution> Contributions { get; init; }
}

/// <summary>
/// ONE agent's work product as an integration input — the durable, always-present, base-anchored representation
/// (the unified-diff patch + the base it was rooted at). A pure projection of the agent's terminal
/// <c>AgentRunResult</c>; the patch may be inline (<see cref="Patch"/>) or offloaded (<see cref="PatchArtifactId"/>,
/// resolved by the team-scoped artifact offloader before applying).
/// </summary>
public sealed record BranchContribution
{
    /// <summary>A short, human-readable label for this contribution in the result + logs (e.g. the agent run id or a subtask title) — never a secret.</summary>
    public required string Label { get; init; }

    /// <summary>The repository this agent worked in. The integrator refuses a set whose contributions span multiple source repositories (a multi-repo fan-out has no single repo to integrate into).</summary>
    public Guid SourceRepositoryId { get; init; }

    /// <summary>The base revision THIS contribution's patch is rooted at — must equal the request's <see cref="IntegrationRequest.BaseSha"/> or the contribution is refused (the moved-base integrity guard). Null when the agent recorded no base (a re-attached run with no surviving clone) → refused as unintegrable.</summary>
    public string? BaseSha { get; init; }

    /// <summary>The inline unified diff (git format) of the agent's changes vs its base. Empty when offloaded (see <see cref="PatchArtifactId"/>) or when the agent changed nothing. A patch carrying the truncation marker (a diff over the inline cap) is refused — the truncated text cannot be applied.</summary>
    public string Patch { get; init; } = "";

    /// <summary>When the diff was offloaded (larger than the artifact inline threshold), the artifact-store id holding the full unified diff; the integrator resolves it (team-scoped) before applying. Null when the diff is inline or absent.</summary>
    public Guid? PatchArtifactId { get; init; }

    /// <summary>The branch the agent's sandbox pushed, if any (informational — patch-based integration is the durable mechanism; a pushed branch is the human-review fallback when this contribution can't be integrated).</summary>
    public string? ProducedBranch { get; init; }
}
