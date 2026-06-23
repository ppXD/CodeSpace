using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows;
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
    private readonly IWorkflowService _workflows;
    private readonly IEnumerable<IRunTimelineSource> _sources;
    private readonly ILogger<RunTimelineProjector> _logger;

    public RunTimelineProjector(IWorkflowService workflows, IEnumerable<IRunTimelineSource> sources, ILogger<RunTimelineProjector> logger)
    {
        _workflows = workflows;
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

    /// <summary>The team-scope precheck — <c>GetRunAsync</c> is team-scoped (foreign / absent → null), so a non-null run is the team's. No existence is leaked: a foreign run is indistinguishable from an absent one.</summary>
    private async Task<bool> RunBelongsToTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _workflows.GetRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

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

    /// <summary>Stable-sort the merged events by OccurredAt, tie-broken by SourceKey then Id — a deterministic chronological order independent of source-iteration order.</summary>
    private static IReadOnlyList<RunTimelineEvent> Merge(List<RunTimelineEvent> events) =>
        events
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.SourceKey, StringComparer.Ordinal)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
}
