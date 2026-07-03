using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the facts gatherer — the merge seam every <see cref="IJournalFactsSource"/> feeds. Pins that it runs EVERY
/// source and unions their per-step facts, and that two sources contributing to the SAME step id compose field-wise
/// (<see cref="JournalStepFacts.Merge"/>) rather than clobber — so a decision that has BOTH a rationale (one source) and,
/// in a future slice, agent cards (another) ends with both. Driven over hand-written fake sources — no database — so the
/// merge is proven before the second real source exists.
/// </summary>
[Trait("Category", "Unit")]
public class JournalFactsGathererTests
{
    [Fact]
    public async Task Unions_facts_from_every_source()
    {
        var gatherer = new JournalFactsGatherer(new IJournalFactsSource[]
        {
            Source(("supervisor-1", new JournalStepFacts { Rationale = "why-1" })),
            Source(("supervisor-2", new JournalStepFacts { Rationale = "why-2" })),
        });

        var facts = await gatherer.GatherAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        facts.For("supervisor-1")!.Rationale.ShouldBe("why-1");
        facts.For("supervisor-2")!.Rationale.ShouldBe("why-2");
    }

    [Fact]
    public async Task On_a_same_step_collision_the_later_source_wins()
    {
        // The gatherer folds later sources onto earlier ones via existing.Merge(contributed) — so a LATER source wins a
        // same-field collision. Both sources set Rationale to DIFFERENT non-null values (the ONLY shape that distinguishes
        // the correct fold-orientation from its inverse): if the gatherer flipped to contributed.Merge(existing), or Merge
        // itself were inverted, this would return "first" and fail. Pins the merge SEAM the "drop-a-source" claim rests on.
        var gatherer = new JournalFactsGatherer(new IJournalFactsSource[]
        {
            Source(("supervisor-1", new JournalStepFacts { Rationale = "first" })),
            Source(("supervisor-1", new JournalStepFacts { Rationale = "second" })),
        });

        var facts = await gatherer.GatherAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        facts.For("supervisor-1")!.Rationale.ShouldBe("second", "the later-registered source wins the same-field collision — the gatherer folds existing.Merge(contributed)");
    }

    [Fact]
    public async Task An_empty_later_source_does_not_clobber_an_earlier_set_field()
    {
        // The complementary case: a later source that sets NOTHING for the step must leave the earlier fact intact — so a
        // gap-filling source (rationale) and a silent one (an agent-cards source on a step with no agents) coexist.
        var gatherer = new JournalFactsGatherer(new IJournalFactsSource[]
        {
            Source(("supervisor-1", new JournalStepFacts { Rationale = "first" })),
            Source(("supervisor-1", new JournalStepFacts())),   // contributes to the same id but sets nothing
        });

        var facts = await gatherer.GatherAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        facts.For("supervisor-1")!.Rationale.ShouldBe("first", "the empty later contribution does NOT erase the earlier set field");
    }

    [Fact]
    public async Task Two_sources_setting_DIFFERENT_fields_compose_onto_one_step()
    {
        // The end-to-end genericity property: a rationale source and an agent-cards source both contribute to the SAME
        // step id but set DIFFERENT fields — the merged bundle carries BOTH. This is the "independent enrichers stack"
        // guarantee at the gatherer level (JournalStepFacts.Merge proves it in isolation; this proves the fold applies it).
        var cards = new[] { new JournalAgentCard { AgentRunId = Guid.NewGuid(), Label = "task", Status = AgentRunStatus.Running } };

        var gatherer = new JournalFactsGatherer(new IJournalFactsSource[]
        {
            Source(("supervisor-1", new JournalStepFacts { Rationale = "why it spawned" })),
            Source(("supervisor-1", new JournalStepFacts { Agents = cards })),
        });

        var facts = await gatherer.GatherAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var stepFacts = facts.For("supervisor-1")!;
        stepFacts.Rationale.ShouldBe("why it spawned", "the rationale source's contribution survives");
        stepFacts.Agents.ShouldBe(cards, "the agent-cards source's contribution survives — independent sources stack onto one step");
    }

    [Fact]
    public async Task No_sources_or_no_facts_yields_the_empty_bundle()
    {
        (await new JournalFactsGatherer(Array.Empty<IJournalFactsSource>()).GatherAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))
            .ByStepId.ShouldBeEmpty("no sources → no facts, and every step stays bare");
    }

    private static IJournalFactsSource Source(params (string StepId, JournalStepFacts Facts)[] facts) =>
        new FakeSource(facts.ToDictionary(f => f.StepId, f => f.Facts));

    private sealed class FakeSource : IJournalFactsSource
    {
        private readonly IReadOnlyDictionary<string, JournalStepFacts> _facts;
        public FakeSource(IReadOnlyDictionary<string, JournalStepFacts> facts) => _facts = facts;
        public Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult(_facts);
    }
}
