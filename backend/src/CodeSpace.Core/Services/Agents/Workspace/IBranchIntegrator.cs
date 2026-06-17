using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Integrates K parallel agent contributions into ONE branch on a single repository (SOTA #3 — make multi-agent
/// fan-out INTEGRATE, not narrate). A SIBLING capability (Rule 7 / ISP — NOT a widening of
/// <see cref="IWorkspaceProvider"/> / <see cref="IWorkspaceHandle"/>): the workspace seam materialises ONE clone for
/// ONE agent; integration is the distinct concern of combining MANY agents' patches. Resolved by <see cref="Kind"/>
/// the same way the provider + runner are, so a future in-pod integrator plugs in unchanged.
///
/// <para><b>Patch-based + base-anchored.</b> The durable, always-present integration input is each contribution's
/// unified-diff patch + the base SHA it was rooted at (a pushed branch is opt-in / absent on a re-attached run). The
/// integrator checks out the shared base before applying so a 3-way apply resolves against the right pre-image, and
/// REFUSES any contribution whose recorded base disagrees — a stale-base patch otherwise applies "cleanly" onto a
/// moved tree and silently grafts incoherent work.</para>
///
/// <para><b>Fail-safe, never fail-silent.</b> Integration is all-or-nothing: it auto-integrates ONLY a fully-clean
/// apply set and publishes a run-id-derived reviewable branch (a plain push when absent; a reuse-as-no-op only when an
/// existing branch's tree byte-equals ours, else refused as "advanced" — it NEVER force-overwrites foreign work); on
/// ANY conflict / base mismatch / multi-repo set / truncated-or-missing patch it ABORTS the git state (resets the
/// clone to base), pushes nothing, and returns an <see cref="IntegrationResult"/> naming what could not be applied —
/// the original K agent branches/patches remain intact for human review. It NEVER produces a corrupt half-merge and
/// NEVER pushes to the base/a protected branch.</para>
///
/// <para><b>Tenancy.</b> The caller passes an already-team-resolved repo URL + token and team-scoped artifact ids;
/// the integrator resolves offloaded patches through the team-scoped offloader, so a cross-team artifact id resolves
/// to nothing and that contribution is recorded unintegrable.</para>
/// </summary>
public interface IBranchIntegrator
{
    /// <summary>Stable tag matching the sandbox runner / workspace provider it pairs with — "local" (v0). Matches the key a registry resolves by.</summary>
    string Kind { get; }

    /// <summary>
    /// Integrate the <paramref name="request"/>'s ordered contributions into one branch, returning a fail-safe
    /// <see cref="IntegrationResult"/> (Clean + a pushed branch, or Conflicted/Empty with per-contribution detail).
    /// Does NOT throw on a conflict or an unintegrable contribution — those are reported in the result; only a genuine
    /// infrastructure failure (the integration push itself failing for auth/network) surfaces, with the token redacted.
    /// </summary>
    Task<IntegrationResult> IntegrateAsync(IntegrationRequest request, CancellationToken cancellationToken);
}
