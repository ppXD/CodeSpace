using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;
using CodeSpace.UnitTests.Infrastructure;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the plan facts source — attaches the model-authored subtasks to the PLAN decision step, keyed by its timeline
/// event id, so the plan renders inline under "planned the work" (the causal spine plan → dispatch → agents). Pins that
/// ONLY a Plan decision contributes, the subtasks come off the PAYLOAD, a re-plan keys its own step, and an empty/non-plan
/// step adds nothing. Driven over the shared in-memory decision log — no database.
/// </summary>
[Trait("Category", "Unit")]
public class PlanFactsSourceTests
{
    [Fact]
    public async Task Keys_the_plan_subtasks_by_the_plan_decision_step_id()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, PlanPayload(("s1", "Research the market"), ("s2", "Write the report")), "{}");

        var facts = await new PlanFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        var decision = log.Rows.Single();
        facts.ShouldContainKey(SupervisorDecisionTimelineMap.EventId(decision));
        facts[SupervisorDecisionTimelineMap.EventId(decision)].Plan!.Select(s => (s.SubtaskId, s.Title))
            .ShouldBe(new[] { ("s1", "Research the market"), ("s2", "Write the report") }, "the plan's subtasks, in authored order, on the plan step");
    }

    [Fact]
    public async Task Only_a_plan_decision_contributes()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Spawn, PlanPayload(("s1", "x")), "{}");
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Merge, "{}", "{}");

        (await new PlanFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None))
            .ShouldBeEmpty("only a Plan verb authors the inline plan — a spawn / merge adds nothing");
    }

    [Fact]
    public async Task A_re_plan_attaches_its_own_subtasks_to_its_own_step()
    {
        // A re-plan is a LATER Plan decision — its subtasks key to ITS step id, so the journal shows each planning moment inline.
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, PlanPayload(("a", "First plan")), "{}");
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, PlanPayload(("b", "Second plan")), "{}");

        var facts = await new PlanFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        facts.Count.ShouldBe(2, "each Plan decision keys its own subtasks");
        facts.Values.SelectMany(f => f.Plan!).Select(s => s.Title).ShouldBe(new[] { "First plan", "Second plan" }, ignoreOrder: true);
    }

    [Fact]
    public async Task A_plan_with_no_subtasks_contributes_nothing()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, "{}", "{}");   // a plan whose payload authored no subtasks

        (await new PlanFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Truncated_subtasks_emit_coverage_without_promoting_prefixes_to_the_plan()
    {
        var item = SupervisorPlanObservationTestData.Item(new SupervisorPlanObservationItemSpec
        {
            SubtasksState = SupervisorPlanObservationLeafState.Truncated,
            SubtaskTotal = 25,
            SubtaskOmitted = 5,
            Subtasks = Enumerable.Range(1, 20).Select(i => SupervisorPlanObservationTestData.Subtask($"s{i}", $"prefix-{i}")).ToList(),
        });
        var bundle = new FakeSupervisorPlanObservationPageBundle { Page = SupervisorPlanObservationTestData.Page(items: item) };

        var facts = await new PlanFactsSource(bundle).GatherAsync(item.Metadata.SupervisorRunId, Guid.NewGuid(), CancellationToken.None);

        var fact = facts[SupervisorDecisionTimelineMap.EventId(item.Metadata.DecisionId)];
        fact.Plan.ShouldBeNull("bounded prefixes never become an apparently complete plan");
        var coverage = fact.ObservationCoverage.ShouldHaveSingleItem();
        coverage.SourceKind.ShouldBe(JournalObservationCoverageSourceKinds.SupervisorPlanSubtasks);
        coverage.Reason.ShouldBe(JournalObservationCoverageReason.TruncatedLeaf);
        coverage.ObservedCount.ShouldBe(20);
        coverage.OmittedCount.ShouldBe(5);
    }

    [Fact]
    public async Task Page_cap_is_explicit_on_the_oldest_observed_decision()
    {
        var first = SupervisorPlanObservationTestData.Item(new SupervisorPlanObservationItemSpec { StoryOrder = 501 });
        var second = SupervisorPlanObservationTestData.Item(new SupervisorPlanObservationItemSpec { StoryOrder = 502 });
        var bundle = new FakeSupervisorPlanObservationPageBundle { Page = SupervisorPlanObservationTestData.Page(true, first, second) };

        var facts = await new PlanFactsSource(bundle).GatherAsync(first.Metadata.SupervisorRunId, Guid.NewGuid(), CancellationToken.None);

        var boundary = facts[SupervisorDecisionTimelineMap.EventId(first.Metadata.DecisionId)].ObservationCoverage!
            .Single(value => value.Reason == JournalObservationCoverageReason.OlderItemsOmitted);
        boundary.ObservedCount.ShouldBe(2);
        boundary.OmittedCount.ShouldBe(1);
        boundary.OmittedCountIsLowerBound.ShouldBeTrue("limit+1 proves at least one older row without an unbounded COUNT");
    }

    [Fact]
    public async Task Corrupt_status_and_malformed_exact_leaf_fail_closed()
    {
        var corruptStatus = SupervisorPlanObservationTestData.Item(new SupervisorPlanObservationItemSpec { Status = SupervisorDecisionObservationStatus.LegacyUnknown, StoryOrder = 1 });
        var malformedExact = SupervisorPlanObservationTestData.Item(new SupervisorPlanObservationItemSpec
        {
            StoryOrder = 2,
            Subtasks = [SupervisorPlanObservationTestData.Subtask("truncated", "prefix", titleBytes: 99)],
        });
        var bundle = new FakeSupervisorPlanObservationPageBundle { Page = SupervisorPlanObservationTestData.Page(items: [corruptStatus, malformedExact]) };

        var facts = await new PlanFactsSource(bundle).GatherAsync(corruptStatus.Metadata.SupervisorRunId, Guid.NewGuid(), CancellationToken.None);

        facts.Values.ShouldAllBe(fact => fact.Plan == null);
        facts[SupervisorDecisionTimelineMap.EventId(corruptStatus.Metadata.DecisionId)].ObservationCoverage!.Single().Reason
            .ShouldBe(JournalObservationCoverageReason.CorruptDecisionStatus);
        facts[SupervisorDecisionTimelineMap.EventId(malformedExact.Metadata.DecisionId)].ObservationCoverage!.Single().Reason
            .ShouldBe(JournalObservationCoverageReason.CorruptLeaf);
    }

    [Fact]
    public async Task Observation_database_fault_propagates_as_infrastructure()
    {
        var error = new InvalidOperationException("plan-observation-db-fault");
        var bundle = new FakeSupervisorPlanObservationPageBundle { Page = null, Error = error };

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => new PlanFactsSource(bundle)
            .GatherAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        thrown.ShouldBeSameAs(error);
    }

    private static string PlanPayload(params (string id, string title)[] subtasks) =>
        System.Text.Json.JsonSerializer.Serialize(new { subtasks = subtasks.Select(s => new { id = s.id, title = s.title, instruction = s.title }) });
}
