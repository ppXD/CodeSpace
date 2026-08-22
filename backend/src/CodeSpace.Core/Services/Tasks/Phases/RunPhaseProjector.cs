using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Tasks.Phases;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Tasks.Phases;

/// <summary>
/// The fan-out projector (Rule 18.3 — the concern's identity abstraction's impl, at the concern root). Autofac
/// resolves EVERY <see cref="IRunPhaseSource"/> in the request scope into the injected <c>IEnumerable</c>, so a new
/// run shape is purely a dropped source — the projector never names a concrete source. The pipeline is a flat
/// sequence (Rule 4/5): tenancy precheck → fan out (per-source try/catch so one broken source degrades, never
/// 500s) → concat + stable-sort by Order. READ-ONLY — no writes, no engine mutation.
/// </summary>
public sealed class RunPhaseProjector : IRunPhaseProjector, IScopedDependency
{
    private readonly IWorkflowRunObservationIdentityBundle _identity;
    private readonly IEnumerable<IRunPhaseSource> _sources;
    private readonly ILogger<RunPhaseProjector> _logger;

    public RunPhaseProjector(IWorkflowRunObservationIdentityBundle identity, IEnumerable<IRunPhaseSource> sources, ILogger<RunPhaseProjector> logger)
    {
        _identity = identity;
        _sources = sources;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RunPhase>?> ProjectAsync(Guid runId, Guid teamId, CancellationToken cancellationToken, bool mergeLineage = true)
    {
        var belongs = await RunBelongsToTeamAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (!belongs) return null;

        var context = new RunPhaseContext { RunId = runId, TeamId = teamId, MergeLineage = mergeLineage };

        var contributed = await FanOutAsync(context, cancellationToken).ConfigureAwait(false);

        return Merge(contributed);
    }

    /// <summary>The exact body-blind team/run precheck. The handler's status envelope reuses the same request-scoped observation, so both checks cost one query without leaking a foreign run's existence. Attempt-only versus lineage-merged remains a source projection concern and never changes identity.</summary>
    private async Task<bool> RunBelongsToTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _identity.GetAsync(teamId, runId, cancellationToken).ConfigureAwait(false);

        return run != null;
    }

    /// <summary>Fire every source for the run; a source that throws is logged + skipped so it degrades to fewer phases rather than 500ing the whole projection.</summary>
    private async Task<List<RunPhase>> FanOutAsync(RunPhaseContext context, CancellationToken cancellationToken)
    {
        var all = new List<RunPhase>();

        foreach (var source in _sources)
            all.AddRange(await ContributeSafelyAsync(source, context, cancellationToken).ConfigureAwait(false));

        return all;
    }

    private async Task<IReadOnlyList<RunPhase>> ContributeSafelyAsync(IRunPhaseSource source, RunPhaseContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await source.ContributeAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run phase source {SourceKey} failed for run {RunId}; degrading to fewer phases", source.SourceKey, context.RunId);

            return Array.Empty<RunPhase>();
        }
    }

    /// <summary>Stable-sort the merged rows by Order, tie-broken by StartedAt then SourceKey — a deterministic UI order independent of source-iteration order.</summary>
    private static IReadOnlyList<RunPhase> Merge(List<RunPhase> phases) =>
        phases
            .OrderBy(p => p.Order)
            .ThenBy(p => p.StartedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(p => p.SourceKey, StringComparer.Ordinal)
            .ToList();
}
