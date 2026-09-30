using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Harnesses;
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
/// <para>The fallback is bounded by <see cref="AgentTask.AllowedHarnessKinds"/> — the run's harness allow-list, when it
/// has one. A repair therefore only ever lands on a kind the OPERATOR admitted; when none of them drives the provider,
/// the authored (admitted) kind is kept and the credential resolver surfaces the real error. Null / empty = unbounded,
/// which is every non-supervisor path and every task envelope persisted before that field existed.</para>
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
///
/// <para>The ONE exception is the run's allowed model pool (<see cref="AgentTask.AllowedModelIds"/>, the model analogue of
/// the harness allow-list): a bounded task runs on a POOLED ROW — its named model's pooled row, else (no name, or a name
/// no pooled row carries, such as a planner-authored model outside the pool) the pool's default row — reported as
/// <see cref="HarnessReconciliation.PooledModel"/> for the executor to run and persist, and the harness is reconciled
/// against THAT row's provider. An unbounded task is reconciled exactly as before.</para>
/// </summary>
public interface IHarnessModelReconciler
{
    /// <summary>Resolve the harness KIND to ACTUALLY run for <paramref name="task"/>: the authored one when it can drive the model's provider (from the pinned credential or, failing a pin, the named model's pool row), else a registered, run-ADMITTED harness that can (the always-runnable fallback). The caller resolves the kind to an adapter, so the registry stays the single owner of kind→adapter.</summary>
    Task<HarnessReconciliation> ReconcileAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken);
}

/// <summary>The harness KIND to run, whether it was REPAIRED away from the authored one, and a human-facing note for the timeline when it was — plus, for a task bounded to an allowed model pool, the pooled row it runs on (null when unbounded, or when the pool resolves nothing any more) and a note when the authored model had to move or cannot be honoured.</summary>
public sealed record HarnessReconciliation(string HarnessKind, bool Repaired, string? Note, ModelDispatchRef? PooledModel = null, string? PoolNote = null);

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
        var pooled = await ResolvePooledModelAsync(task, teamId, cancellationToken).ConfigureAwait(false);
        var poolNote = DescribePoolBound(task, pooled);

        // A bounded task runs on its pooled row, so the harness follows THAT row's provider, not the authored model's.
        var provider = pooled?.Provider ?? await ResolveModelProviderAsync(task, teamId, cancellationToken).ConfigureAwait(false);

        // No provider to reconcile against (no pin AND no pooled model name) → return the authored kind verbatim (the
        // caller's registry resolves it; a genuinely-unregistered kind surfaces there, unchanged).
        if (provider is null) return new HarnessReconciliation(task.Harness, false, null, pooled, poolNote);

        // The repair chooses from the registry CLAMPED to the run's harness allow-list (null/empty = the whole registry,
        // which is every non-supervisor path and every pre-field task envelope). Without this clamp the run-time repair
        // was the allow-list's hole: an agent the spawn correctly stamped with an admitted kind got repaired onto an
        // UNADMITTED one here whenever the admitted kind could not drive the model's provider. When nothing admitted can
        // drive it, Reconcile returns the authored kind unchanged — the honest floor, and the authored kind is the
        // admitted one, so the floor stays inside the list too.
        var pool = AgentHarnessPool.Clamp(_harnesses.All, task.AllowedHarnessKinds);

        return Reconcile(task.Harness, provider, pool, AgentHarnessDefaults.DefaultHarness) with { PooledModel = pooled, PoolNote = poolNote };
    }

    /// <summary>
    /// The allowed-pool row a bounded task runs on: its named model's pooled row, else — no name, or a name no pooled row
    /// carries — the pool's default row, ranked by the same agent-plane precedence the supervisor's pool-bound default uses
    /// (names repeat across credentials, so both lookups are over the pool's ROWS). Null for an unbounded task, and when
    /// nothing in the pool resolves any more.
    /// </summary>
    private async Task<ModelDispatchRef?> ResolvePooledModelAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken)
    {
        if (task.AllowedModelIds is not { Count: > 0 } pool) return null;

        var named = string.IsNullOrWhiteSpace(task.Model) ? null : await _modelSelector.ResolveDispatchAsync(teamId, task.Model, pool, cancellationToken).ConfigureAwait(false);

        return named ?? await _modelSelector.ResolvePoolDefaultAsync(teamId, pool, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why a bounded task's model did not run as authored — it was outside the pool, or nothing in the pool resolves any more. Null when unbounded, when the named model is pooled, and when no model was named (the pool's default is then simply the model it runs).</summary>
    private static string? DescribePoolBound(AgentTask task, ModelDispatchRef? pooled)
    {
        if (task.AllowedModelIds is not { Count: > 0 }) return null;
        if (pooled is null) return "None of this run's allowed models resolves to an enabled model under an active credential, so the agent cannot run inside its allowed model pool.";
        if (string.IsNullOrWhiteSpace(task.Model) || string.Equals(task.Model.Trim(), pooled.ModelId, StringComparison.OrdinalIgnoreCase)) return null;

        return $"Model '{task.Model}' is not in this run's allowed model pool; running the pool's default, '{pooled.ModelId}', instead.";
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

        if (!string.IsNullOrWhiteSpace(task.Model))
        {
            var dispatch = await _modelSelector.ResolveDispatchAsync(teamId, task.Model, allowedRowIds: null, cancellationToken).ConfigureAwait(false);

            return dispatch?.Provider;
        }

        // No pin AND no model name (the AUTO case) — derive the provider the un-pinned run will actually get (the team's
        // top default pool row), so the harness follows the model instead of the codex floor. This is the "always-codex"
        // fix: an Anthropic-only auto team now reconciles to claude-code. Null (no enabled pool model) keeps the floor.
        return await _modelSelector.ResolveTeamDefaultProviderAsync(teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pure decision: keep the authored kind if its harness drives <paramref name="provider"/>; else the FIRST
    /// registered harness that does (deterministic — registration order is stable), the always-runnable fallback;
    /// else the authored kind (nothing drives it → the resolver throws the truly-unrunnable error, the honest floor).
    /// Internal + static so it is unit-pinned across the harness × provider matrix without a DB.
    /// </summary>
    internal static HarnessReconciliation Reconcile(string authoredKind, string provider, IReadOnlyList<IAgentHarness> pool, string defaultHarnessKind)
    {
        var authored = pool.FirstOrDefault(h => string.Equals(h.Kind, authoredKind, StringComparison.OrdinalIgnoreCase));

        if (authored is not null && Drives(authored, provider)) return new HarnessReconciliation(authoredKind, false, null);

        // The DEFAULT harness wins among the drivers, else Kind order — DETERMINISTIC regardless of DI/registration order.
        // The default-first tie-break decides the genuinely-ambiguous case: "Custom" is OpenAI-wire and BOTH codex-cli and
        // claude-code drive it, so a Custom auto model derives the default (codex-cli, the OpenAI harness), not the
        // alphabetically-first claude-code. (`defaultHarnessKind` is passed in so this stays a pure function.)
        var compatible = pool.Where(h => Drives(h, provider))
            .OrderByDescending(h => string.Equals(h.Kind, defaultHarnessKind, StringComparison.OrdinalIgnoreCase))
            .ThenBy(h => h.Kind, StringComparer.Ordinal)
            .FirstOrDefault();

        if (compatible is null) return new HarnessReconciliation(authoredKind, false, null);

        return new HarnessReconciliation(compatible.Kind, true,
            $"Authored harness '{authoredKind}' cannot drive model-credential provider '{provider}'; reconciled to '{compatible.Kind}' so the agent still runs.");
    }

    /// <summary>A harness drives a provider iff it projects credentials (<see cref="IModelCredentialProjector"/>) and lists the provider as supported. A harness that needs no model key drives nothing here (it has no credential to mismatch).</summary>
    private static bool Drives(IAgentHarness harness, string provider) =>
        harness is IModelCredentialProjector projector
        && projector.SupportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);
}
