using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
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
/// <para>Only a PINNED credential can mismatch the harness: the no-pin path resolves a team-default credential FROM
/// the harness's own providers, so it is compatible by construction. So the reconciler only acts when
/// <see cref="AgentTask.ModelCredentialId"/> is set. It reads only the credential's PROVIDER (no decrypt). When no
/// registered harness can drive the provider at all (a genuinely-unrunnable model — nothing to fall back to), it
/// leaves the authored harness so the credential resolver surfaces its precise, already-tested error — the honest
/// floor, not a silent wrong run.</para>
///
/// <para>SCOPE: this reconciles the harness↔credential-PROVIDER axis only. The model NAME (<see cref="AgentTask.Model"/>)
/// ↔ credential consistency is the dispatch layer's job — the supervisor's <c>ApplyDispatchModelAsync</c> resolves the
/// model name AND its credential from the SAME credentialed pool row, so they are consistent by construction and a
/// harness repair makes the whole (harness, model, credential) triple runnable. A hand-authored task that pins a
/// credential whose provider doesn't match its <see cref="AgentTask.Model"/> name is an upstream authoring error this
/// layer deliberately does NOT mask (blanking the model here would drop a VALID operator model choice in the common
/// consistent case); P1's catalog-informed authoring is what keeps the two in step.</para>
/// </summary>
public interface IHarnessModelReconciler
{
    /// <summary>Resolve the harness KIND to ACTUALLY run for <paramref name="task"/>: the authored one when it can drive the pinned credential's provider, else a registered harness that can (the always-runnable fallback). The caller resolves the kind to an adapter, so the registry stays the single owner of kind→adapter.</summary>
    Task<HarnessReconciliation> ReconcileAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken);
}

/// <summary>The harness KIND to run, whether it was REPAIRED away from the authored one, and a human-facing note for the timeline when it was.</summary>
public sealed record HarnessReconciliation(string HarnessKind, bool Repaired, string? Note);

public sealed class HarnessModelReconciler : IHarnessModelReconciler, IScopedDependency
{
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly CodeSpaceDbContext _db;

    public HarnessModelReconciler(IAgentHarnessRegistry harnesses, CodeSpaceDbContext db)
    {
        _harnesses = harnesses;
        _db = db;
    }

    public async Task<HarnessReconciliation> ReconcileAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken)
    {
        // Only a PINNED credential can mismatch the harness. No pin → return the authored kind verbatim (the caller's
        // registry resolves it; a genuinely-unregistered kind surfaces there, unchanged).
        if (task.ModelCredentialId is not { } id) return new HarnessReconciliation(task.Harness, false, null);

        // Provider only — no decrypt; mirrors the credential resolver's active-row predicate so we reconcile against
        // the SAME credential it will resolve. A missing / foreign / inactive id leaves the authored harness so the
        // resolver throws its precise not-active-for-this-team error rather than us masking it.
        var provider = await _db.ModelCredential.AsNoTracking()
            .Where(c => c.Id == id && c.TeamId == teamId && c.DeletedDate == null && c.Status == CredentialStatus.Active)
            .Select(c => c.Provider)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (provider is null) return new HarnessReconciliation(task.Harness, false, null);

        return Reconcile(task.Harness, provider, _harnesses.All);
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
