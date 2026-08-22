using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Tasks.Timeline;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Tasks.Timeline;

/// <summary>
/// The fan-out projector (Rule 18.3 — the concern's identity abstraction's impl, at the concern root). Autofac
/// resolves EVERY <see cref="IRunTimelineSource"/> in the request scope into the injected <c>IEnumerable</c>, so a
/// new event source is purely a dropped source — the projector never names a concrete one. The pipeline is a flat
/// sequence (Rule 4/5): tenancy precheck → fan out (per-source try/catch so one broken source degrades, never
/// 500s) → concat + stable-sort by OccurredAt. READ-ONLY — no writes, no engine mutation.
/// </summary>
public sealed class RunTimelineProjector : IRunTimelineProjector, IScopedDependency
{
    private readonly IWorkflowRunObservationIdentityBundle _identity;
    private readonly IEnumerable<IRunTimelineSource> _sources;
    private readonly ILogger<RunTimelineProjector> _logger;

    public RunTimelineProjector(IWorkflowRunObservationIdentityBundle identity, IEnumerable<IRunTimelineSource> sources, ILogger<RunTimelineProjector> logger)
    {
        _identity = identity;
        _sources = sources;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RunTimelineEvent>?> ProjectAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var belongs = await RunBelongsToTeamAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (!belongs) return null;

        var context = new RunTimelineContext { RunId = runId, TeamId = teamId };

        var contributed = await FanOutAsync(context, cancellationToken).ConfigureAwait(false);

        return Merge(contributed);
    }

    /// <summary>The exact body-blind team/run precheck. The handler's status envelope reuses the same request-scoped observation, so both checks cost one query without leaking a foreign run's existence.</summary>
    private async Task<bool> RunBelongsToTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _identity.GetAsync(teamId, runId, cancellationToken).ConfigureAwait(false);

        return run != null;
    }

    /// <summary>Fire every source for the run; a source that throws is logged + skipped so it degrades to fewer events rather than 500ing the whole projection.</summary>
    private async Task<List<RunTimelineEvent>> FanOutAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var all = new List<RunTimelineEvent>();

        foreach (var source in _sources)
            all.AddRange(await ContributeSafelyAsync(source, context, cancellationToken).ConfigureAwait(false));

        return all;
    }

    private async Task<IReadOnlyList<RunTimelineEvent>> ContributeSafelyAsync(IRunTimelineSource source, RunTimelineContext context, CancellationToken cancellationToken)
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
            _logger.LogWarning(ex, "Run timeline source {SourceKey} failed for run {RunId}; degrading to fewer events", source.SourceKey, context.RunId);

            return Array.Empty<RunTimelineEvent>();
        }
    }

    /// <summary>Stable-sort the merged events by OccurredAt, tie-broken by SourceKey then the source's numeric Order (so two events in one tick keep their true ledger order, never the lexical id order) then Id — deterministic, independent of source-iteration order.</summary>
    private static IReadOnlyList<RunTimelineEvent> Merge(List<RunTimelineEvent> events) =>
        events
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.SourceKey, StringComparer.Ordinal)
            .ThenBy(e => e.Order)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
}
