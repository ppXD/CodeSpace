using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Core.Services.Sessions.Journal.Describers;
using CodeSpace.Core.Services.Tasks.Timeline;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the journal walk — the chronological replay. Pins that it describes the timeline spine's events (in the
/// projector's already-merged order) into steps, stamps a STRICTLY-INCREASING <see cref="JournalStep.Seq"/> (the delta
/// cursor), conflates a foreign run to null exactly like the timeline projector, and — through the registry — turns even
/// an UNKNOWN-source event into a step (never dropped). Composed with the REAL describer registry; the timeline is faked.
/// </summary>
[Trait("Category", "Unit")]
public class JournalWalkTests
{
    private static readonly IJournalStepDescriberRegistry Registry = new JournalStepDescriberRegistry(
        new IJournalStepDescriber[] { new SupervisorStepDescriber(), new ToolStepDescriber(), new AgentEventStepDescriber(), new LifecycleStepDescriber() },
        new FallbackStepDescriber());

    private static RunTimelineEvent Event(string id, string sourceKey, string kind = "k", string title = "t") => new()
    {
        Id = id, Kind = kind, Title = title, Severity = TimelineSeverity.Info, Level = TimelineLevel.Detail,
        OccurredAt = DateTimeOffset.UtcNow, Order = 0, SourceKey = sourceKey,
    };

    private static JournalWalk WalkOver(IReadOnlyList<RunTimelineEvent>? events, JournalFacts? facts = null) =>
        new(new FakeTimeline(events), Registry, new FakeFacts(facts ?? JournalFacts.Empty));

    [Fact]
    public async Task Walks_the_events_into_steps_in_order_each_with_a_distinct_cursor()
    {
        var walk = WalkOver(new[]
        {
            Event("supervisor-1", "supervisor", title: "Supervisor planned the work"),
            Event("tool-1", "tool-calls", title: "Called git.open_pr"),
            Event("agent-1", "agent-events", title: "edited auth.ts"),
        });

        var steps = await walk.WalkAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        steps.ShouldNotBeNull();
        steps!.Select(s => s.Kind).ShouldBe(new[] { JournalStepKinds.Decision, JournalStepKinds.Tool, JournalStepKinds.Agent }, "each event is classified + kept in the spine's order");
        steps.Select(s => s.Id).ShouldBe(new[] { "supervisor-1", "tool-1", "agent-1" }, "the step id carries through from the event");
        steps.Select(s => s.Cursor).ShouldAllBe(c => c.Length > 0, "every step carries a cursor");
        steps.Select(s => s.Cursor).Distinct().Count().ShouldBe(3, "each step's cursor is distinct");
    }

    [Fact]
    public async Task A_steps_cursor_is_stable_when_an_earlier_event_backfills()
    {
        // THE fix for the positional-cursor fragility (adversarial-review CONFIRMED): a step's cursor is a function of its
        // OWN event, not its walk position — so an earlier event backfilling mid-timeline must NOT change any existing
        // step's cursor (a positional counter would renumber them all + break a ?since= delta).
        var later = Event("tool-1", "tool-calls") with { OccurredAt = DateTimeOffset.UtcNow };

        var beforeBackfill = (await WalkOver(new[] { later }).WalkAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))!;

        var earlier = Event("agent-1", "agent-events") with { OccurredAt = later.OccurredAt.AddSeconds(-5) };
        var afterBackfill = (await WalkOver(new[] { earlier, later }).WalkAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))!;

        afterBackfill.Single(s => s.Id == "tool-1").Cursor
            .ShouldBe(beforeBackfill.Single(s => s.Id == "tool-1").Cursor, "the later event's cursor is unchanged by an earlier event appearing before it");
    }

    [Fact]
    public async Task A_foreign_run_walks_to_null()
    {
        var steps = await WalkOver(null).WalkAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        steps.ShouldBeNull("a run that isn't the team's conflates to null, exactly like the timeline projector — no existence leak");
    }

    [Fact]
    public async Task A_run_with_no_events_walks_to_an_empty_list()
    {
        var steps = await WalkOver(Array.Empty<RunTimelineEvent>()).WalkAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        steps.ShouldNotBeNull("an empty run is the team's — it walks to an empty list, distinct from a foreign run's null");
        steps!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preserves_the_projectors_order_verbatim_even_for_same_tick_events()
    {
        // The walk NEVER re-sorts — the timeline projector is the single ordering authority (its Merge tie-breaks
        // same-OccurredAt events by SourceKey → Order → Id). Feed events sharing one instant in a fixed order and assert
        // the walk keeps THAT order + numbers it, so the journal can't drift from the Activity tab's order.
        var instant = DateTimeOffset.UtcNow;
        var events = new[] { "a", "b", "c", "d" }.Select(id => Event(id, "run-record") with { OccurredAt = instant }).ToList();

        var steps = (await WalkOver(events).WalkAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))!;

        steps.Select(s => s.Id).ShouldBe(new[] { "a", "b", "c", "d" }, "the walk keeps the projector's order verbatim, tie-break and all");
        steps.Select(s => s.Cursor).Distinct().Count().ShouldBe(4, "same-tick events still get distinct cursors (the sort key includes the id tie-break)");
    }

    [Fact]
    public async Task Is_deterministic_across_two_walks()
    {
        var events = new[] { Event("supervisor-1", "supervisor"), Event("tool-1", "tool-calls") };
        var walk = WalkOver(events);

        var first = (await walk.WalkAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))!;
        var second = (await walk.WalkAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))!;

        first.Select(s => (s.Id, s.Kind, s.Cursor)).ShouldBe(second.Select(s => (s.Id, s.Kind, s.Cursor)), "same events → identical steps + cursors (replay-stable)");
    }

    [Fact]
    public async Task An_unknown_source_event_still_becomes_a_step()
    {
        // Genericity through the whole walk: a future source no describer claims is still walked into a step (via the
        // fallback), Seq-assigned like any other — never silently dropped from the journal.
        var walk = WalkOver(new[] { Event("future-1", "some-future-source-2027", title: "a new beat") });

        var steps = (await walk.WalkAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))!;

        steps.ShouldHaveSingleItem();
        steps[0].Kind.ShouldBe(JournalStepKinds.Event);
        steps[0].Title.ShouldBe("a new beat");
        steps[0].Cursor.ShouldNotBeNullOrEmpty("even a fallback step gets a cursor");
    }

    [Fact]
    public async Task Enriches_a_step_with_the_facts_gathered_for_its_id_leaving_the_rest_bare()
    {
        // The enrichment seam: the walk folds each step's gathered facts (rationale/agents/diffstat — the reads a pure
        // describer can't do) onto the step keyed by the SAME id. A step with no facts stays bare, so enrichment is
        // additive and never fabricated.
        var facts = new JournalFacts
        {
            ByStepId = new Dictionary<string, JournalStepFacts> { ["supervisor-1"] = new() { Rationale = "Spawned to unblock the build · Evidence: CI red" } },
        };

        var walk = WalkOver(new[]
        {
            Event("supervisor-1", "supervisor", title: "Supervisor spawned 1 agent"),
            Event("tool-1", "tool-calls", title: "Called git.open_pr"),
        }, facts);

        var steps = (await walk.WalkAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))!;

        steps.Single(s => s.Id == "supervisor-1").Rationale.ShouldBe("Spawned to unblock the build · Evidence: CI red", "the decision step carries the rationale gathered for its id");
        steps.Single(s => s.Id == "tool-1").Rationale.ShouldBeNull("a step with no gathered facts stays bare — enrichment is never fabricated");
    }

    private sealed class FakeTimeline : IRunTimelineProjector
    {
        private readonly IReadOnlyList<RunTimelineEvent>? _events;
        public FakeTimeline(IReadOnlyList<RunTimelineEvent>? events) => _events = events;
        public Task<IReadOnlyList<RunTimelineEvent>?> ProjectAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult(_events);
    }

    private sealed class FakeFacts : IJournalFactsGatherer
    {
        private readonly JournalFacts _facts;
        public FakeFacts(JournalFacts facts) => _facts = facts;
        public Task<JournalFacts> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult(_facts);
    }
}
