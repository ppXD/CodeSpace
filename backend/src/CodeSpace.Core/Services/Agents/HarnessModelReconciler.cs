using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The stable base under the (future) intelligent allocation: it GUARANTEES a runnable (harness × model-credential)
/// pair for every agent run, whatever the planner / supervisor / operator authored. A harness only drives certain
/// model providers (<see cref="IModelCredentialProjector.SupportedProviders"/>); when an agent's authored harness
/// cannot drive its PINNED credential's provider — the impossible pairing that otherwise fails every agent at
/// execution time with "provider this harness cannot drive" — this reconciler repairs it to a registered harness
/// that CAN, so the agent still runs. It NEVER fails on a mismatch; it falls back. The brain stays free to author
/// any (harness, model); this layer makes a wrong pairing harmless instead of fatal.
///
/// <para>It needs the model's PROVIDER, and finds it from whichever the task carries: a PINNED credential
/// (<see cref="AgentTask.ModelCredentialId"/> — the supervisor / hand-authored pin path) reads that credential's
/// provider; else a loose MODEL NAME (<see cref="AgentTask.Model"/> with no pin — the planner path) resolves the
/// provider of the pool row that backs that name. Either way it reads only the PROVIDER (no decrypt). When the task
/// carries neither, or the named model isn't in the pool, or no registered harness can drive the provider at all (a
/// genuinely-unrunnable model — nothing to fall back to), it leaves the authored harness so the downstream credential
/// resolver surfaces its precise, already-tested error — the honest floor, not a silent wrong run.</para>
///
/// <para>SCOPE: this reconciles the harness↔model-PROVIDER axis. It does NOT touch the model NAME — the planner's loose
/// name stays verbatim (the no-pin resolver picks a credential from the now-compatible harness's providers), and the
/// supervisor's <c>ApplyDispatchModelAsync</c> already resolves the model name AND its credential from the SAME pool
/// row, so a pin repair makes the whole (harness, model, credential) triple runnable. Blanking the model here would
/// drop a VALID operator choice in the common consistent case, so this layer never does; it only swaps the harness for
/// one that can drive the model's provider.</para>
/// </summary>
public interface IHarnessModelReconciler
{
    /// <summary>Resolve the harness KIND to ACTUALLY run for <paramref name="task"/>: the authored one when it can drive the model's provider (from the pinned credential or, failing a pin, the named model's pool row), else a registered harness that can (the always-runnable fallback). The caller resolves the kind to an adapter, so the registry stays the single owner of kind→adapter.</summary>
    Task<HarnessReconciliation> ReconcileAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken);
}

/// <summary>The harness KIND to run, whether it was REPAIRED away from the authored one, and a human-facing note for the timeline when it was.</summary>
public sealed record HarnessReconciliation(string HarnessKind, bool Repaired, string? Note);

public sealed class HarnessModelReconciler : IHarnessModelReconciler, IScopedDependency
{
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly IModelPoolSelector _modelSelector;
    private readonly CodeSpaceDbContext _db;

    public HarnessModelReconciler(IAgentHarnessRegistry harnesses, IModelPoolSelector modelSelector, CodeSpaceDbContext db)
    {
        _harnesses = harnesses;
        _modelSelector = modelSelector;
        _db = db;
    }

    public async Task<HarnessReconciliation> ReconcileAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken)
    {
        var provider = await ResolveModelProviderAsync(task, teamId, cancellationToken).ConfigureAwait(false);

        // No provider to reconcile against (no pin AND no pooled model name) → return the authored kind verbatim (the
        // caller's registry resolves it; a genuinely-unregistered kind surfaces there, unchanged).
        if (provider is null) return new HarnessReconciliation(task.Harness, false, null);

        return Reconcile(task.Harness, provider, _harnesses.All);
    }

    /// <summary>
    /// The model's PROVIDER, from whichever the task carries. A PINNED credential is authoritative — read its provider
    /// (no decrypt), mirroring the credential resolver's active-row predicate so we reconcile against the SAME row it
    /// will resolve. Failing a pin, a loose model NAME (the planner path) resolves the provider of the pool row backing
    /// it. Null = nothing to reconcile against (the resolver then picks a harness-provider default — the honest floor).
    /// </summary>
    private async Task<string?> ResolveModelProviderAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken)
    {
        if (task.ModelCredentialId is { } id)
            return await _db.ModelCredential.AsNoTracking()
                .Where(c => c.Id == id && c.TeamId == teamId && c.DeletedDate == null && c.Status == CredentialStatus.Active)
                .Select(c => c.Provider)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(task.Model)) return null;

        var dispatch = await _modelSelector.ResolveDispatchAsync(teamId, task.Model, allowedRowIds: null, cancellationToken).ConfigureAwait(false);

        return dispatch?.Provider;
    }

    /// <summary>
    /// The pure decision: keep the authored kind if its harness drives <paramref name="provider"/>; else the FIRST
    /// registered harness that does (deterministic — registration order is stable), the always-runnable fallback;
    /// else the authored kind (nothing drives it → the resolver throws the truly-unrunnable error, the honest floor).
    /// Internal + static so it is unit-pinned across the harness × provider matrix without a DB.
    /// </summary>
    internal static HarnessReconciliation Reconcile(string authoredKind, string provider, IReadOnlyList<IAgentHarness> pool)
    {
        var authored = pool.FirstOrDefault(h => string.Equals(h.Kind, authoredKind, StringComparison.OrdinalIgnoreCase));

        if (authored is not null && Drives(authored, provider)) return new HarnessReconciliation(authoredKind, false, null);

        // Order by Kind so the fallback is DETERMINISTIC regardless of DI/registration order — two runs of the same
        // mismatch always repair to the same harness (today codex-cli + claude-code overlap only on "Custom", which the
        // authored harness already drives, so this never branches; it future-proofs a third overlapping harness).
        var compatible = pool.Where(h => Drives(h, provider)).OrderBy(h => h.Kind, StringComparer.Ordinal).FirstOrDefault();

        if (compatible is null) return new HarnessReconciliation(authoredKind, false, null);

        return new HarnessReconciliation(compatible.Kind, true,
            $"Authored harness '{authoredKind}' cannot drive model-credential provider '{provider}'; reconciled to '{compatible.Kind}' so the agent still runs.");
    }

    /// <summary>A harness drives a provider iff it projects credentials (<see cref="IModelCredentialProjector"/>) and lists the provider as supported. A harness that needs no model key drives nothing here (it has no credential to mismatch).</summary>
    private static bool Drives(IAgentHarness harness, string provider) =>
        harness is IModelCredentialProjector projector
        && projector.SupportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);
}
