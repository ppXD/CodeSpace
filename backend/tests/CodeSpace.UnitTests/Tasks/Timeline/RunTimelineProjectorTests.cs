using CodeSpace.Core.Services.Tasks.Timeline;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using CodeSpace.UnitTests.Tasks.Phases;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

/// <summary>
/// The fan-out projector: it prechecks tenancy (foreign run → null, 404-conflate), merges every source's events
/// OccurredAt-sorted, and a single source that throws degrades to fewer events (the others still contribute) rather
/// than sinking the whole projection. A cancelled token propagates; any other fault degrades. Proves the merge +
/// the per-source fault isolation independent of any concrete source.
/// </summary>
[Trait("Category", "Unit")]
public class RunTimelineProjectorTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_foreign_run_returns_null_without_consulting_sources()
    {
        var thrower = new ThrowingSource();
        var projector = Build(runExists: false, thrower);

        var result = await projector.ProjectAsync(RunId, TeamId, CancellationToken.None);

        result.ShouldBeNull("a run that isn't the team's resolves to null — 404-conflate, no existence leak");
        thrower.Called.ShouldBeFalse("the precheck short-circuits before any source fires for a foreign run");
    }

    [Fact]
    public async Task Merges_and_sorts_across_sources_by_occurred_at()
    {
        var a = new StaticSource("a", Ev("b", T0.AddMinutes(5)), Ev("a", T0.AddMinutes(1)));
        var b = new StaticSource("b", Ev("c", T0.AddMinutes(3)));

        var projector = Build(runExists: true, a, b);

        var events = await projector.ProjectAsync(RunId, TeamId, CancellationToken.None);

        events.ShouldNotBeNull();
        events!.Select(e => e.Id).ShouldBe(new[] { "a", "c", "b" }, "the merged list is chronologically sorted across sources");
    }

    [Fact]
    public async Task Same_timestamp_events_keep_ledger_order_not_lexical_id_order()
    {
        // record-2 then record-10 in one tick: lexical id order would put "record-10" FIRST. The numeric Order pins it.
        var src = new StaticSource("run-record", Ev("record-2", T0, order: 2), Ev("record-10", T0, order: 10));

        var projector = Build(runExists: true, src);

        var events = await projector.ProjectAsync(RunId, TeamId, CancellationToken.None);

        events.ShouldNotBeNull();
        events!.Select(e => e.Id).ShouldBe(new[] { "record-2", "record-10" }, "the numeric Order tie-break keeps the true ledger order on a same-timestamp collision");
    }

    [Fact]
    public async Task A_throwing_source_degrades_the_others_still_contribute()
    {
        var healthy = new StaticSource("healthy", Ev("ok", T0));
        var broken = new ThrowingSource();

        var projector = Build(runExists: true, broken, healthy);

        var events = await projector.ProjectAsync(RunId, TeamId, CancellationToken.None);

        events.ShouldNotBeNull();
        events!.Select(e => e.Id).ShouldBe(new[] { "ok" }, "the broken source is caught + skipped; the healthy source's event still lands — never a 500");
    }

    [Fact]
    public async Task A_cancelled_token_propagates_cancellation_rather_than_degrading()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var projector = Build(runExists: true, new CancelingSource());

        await Should.ThrowAsync<OperationCanceledException>(() => projector.ProjectAsync(RunId, TeamId, cts.Token));
    }

    [Fact]
    public async Task A_genuine_fault_still_degrades_even_under_a_live_token()
    {
        var healthy = new StaticSource("healthy", Ev("ok", T0));
        var faulting = new CancelingSource();   // throws OCE, but the projection's own token is NOT cancelled

        var projector = Build(runExists: true, faulting, healthy);

        var events = await projector.ProjectAsync(RunId, TeamId, CancellationToken.None);

        events.ShouldNotBeNull();
        events!.Select(e => e.Id).ShouldBe(new[] { "ok" }, "an OCE for a reason OTHER than our cancellation still degrades gracefully");
    }

    private static RunTimelineProjector Build(bool runExists, params IRunTimelineSource[] sources)
    {
        var detail = runExists ? RunDetailFixtures.Run(WorkflowRunStatus.Running) : null;
        var workflows = new StubWorkflowService(RunId, TeamId, detail);

        return new RunTimelineProjector(workflows, sources, NullLogger<RunTimelineProjector>.Instance);
    }

    private static RunTimelineEvent Ev(string id, DateTimeOffset at, long order = 0) => new()
    {
        Id = id,
        Kind = "test",
        Title = id,
        Severity = TimelineSeverity.Info,
        OccurredAt = at,
        Order = order,
        SourceKey = "test",
    };

    private sealed class StaticSource : IRunTimelineSource
    {
        private readonly RunTimelineEvent[] _events;

        public StaticSource(string key, params RunTimelineEvent[] events) { SourceKey = key; _events = events; }

        public string SourceKey { get; }

        public Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RunTimelineEvent>>(_events);
    }

    private sealed class ThrowingSource : IRunTimelineSource
    {
        public bool Called { get; private set; }

        public string SourceKey => "throwing";

        public Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
        {
            Called = true;
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class CancelingSource : IRunTimelineSource
    {
        public string SourceKey => "canceling";

        public Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }
}
