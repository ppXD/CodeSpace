using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Phases;

/// <summary>
/// The fan-out projector: it prechecks tenancy (foreign run → null, 404-conflate), merges every source's rows
/// Order-sorted, and a single source that throws degrades to fewer phases (the others still contribute) rather than
/// sinking the whole projection. Proves the merge + the per-source fault isolation independent of any concrete source.
/// </summary>
[Trait("Category", "Unit")]
public class RunPhaseProjectorTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();

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
    public async Task Merges_and_order_sorts_across_sources()
    {
        var early = new StaticSource("early", Phase("b", order: 5), Phase("a", order: 1));
        var late = new StaticSource("late", Phase("c", order: 3));

        var projector = Build(runExists: true, early, late);

        var phases = await projector.ProjectAsync(RunId, TeamId, CancellationToken.None);

        phases.ShouldNotBeNull();
        phases!.Select(p => p.Id).ShouldBe(new[] { "a", "c", "b" }, "the merged list is stable-sorted by Order across sources");
    }

    [Fact]
    public async Task A_throwing_source_degrades_the_others_still_contribute()
    {
        var healthy = new StaticSource("healthy", Phase("ok", order: 1));
        var broken = new ThrowingSource();

        var projector = Build(runExists: true, broken, healthy);

        var phases = await projector.ProjectAsync(RunId, TeamId, CancellationToken.None);

        phases.ShouldNotBeNull();
        phases!.Select(p => p.Id).ShouldBe(new[] { "ok" }, "the broken source is caught + skipped; the healthy source's phase still lands — never a 500");
    }

    [Fact]
    public async Task A_cancelled_token_propagates_cancellation_rather_than_degrading()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var canceller = new CancelingSource();
        var projector = Build(runExists: true, canceller);

        // A cancelled GET /phases must surface the cancellation (the request aborts) — NOT swallow it as "degrade
        // to fewer phases", which would keep querying the DB for a client that's already gone.
        await Should.ThrowAsync<OperationCanceledException>(() => projector.ProjectAsync(RunId, TeamId, cts.Token));
    }

    [Fact]
    public async Task A_genuine_source_fault_still_degrades_even_under_a_live_token()
    {
        // The cancellation rethrow must NOT regress the graceful-degrade contract: a real source fault (an
        // OperationCanceledException NOT caused by OUR token, or any other exception) is still caught + skipped.
        var healthy = new StaticSource("healthy", Phase("ok", order: 1));
        var faulting = new CancelingSource();   // throws OCE, but the projection's own token is NOT cancelled

        var projector = Build(runExists: true, faulting, healthy);

        var phases = await projector.ProjectAsync(RunId, TeamId, CancellationToken.None);

        phases.ShouldNotBeNull();
        phases!.Select(p => p.Id).ShouldBe(new[] { "ok" },
            "a source that throws OperationCanceledException for a reason OTHER than our cancellation still degrades gracefully — only OUR cancelled token rethrows");
    }

    private static RunPhaseProjector Build(bool runExists, params IRunPhaseSource[] sources)
    {
        var detail = runExists ? RunDetailFixtures.Run(WorkflowRunStatus.Running) : null;
        var workflows = new StubWorkflowService(RunId, TeamId, detail);

        return new RunPhaseProjector(workflows, sources, NullLogger<RunPhaseProjector>.Instance);
    }

    private static RunPhase Phase(string id, int order) => new()
    {
        Id = id,
        Label = id,
        Kind = "test",
        Status = PhaseStatus.Pending,
        Order = order,
        SourceKey = "test",
    };

    private sealed class StaticSource : IRunPhaseSource
    {
        private readonly RunPhase[] _phases;

        public StaticSource(string key, params RunPhase[] phases) { SourceKey = key; _phases = phases; }

        public string SourceKey { get; }

        public Task<IReadOnlyList<RunPhase>> ContributeAsync(RunPhaseContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RunPhase>>(_phases);
    }

    private sealed class ThrowingSource : IRunPhaseSource
    {
        public bool Called { get; private set; }

        public string SourceKey => "throwing";

        public Task<IReadOnlyList<RunPhase>> ContributeAsync(RunPhaseContext context, CancellationToken cancellationToken)
        {
            Called = true;
            throw new InvalidOperationException("boom");
        }
    }

    /// <summary>A source that always throws <see cref="OperationCanceledException"/> — under a cancelled token the projector must rethrow it (cancellation propagates); under a live token it is just another fault that degrades gracefully.</summary>
    private sealed class CancelingSource : IRunPhaseSource
    {
        public string SourceKey => "canceling";

        public Task<IReadOnlyList<RunPhase>> ContributeAsync(RunPhaseContext context, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }
}
