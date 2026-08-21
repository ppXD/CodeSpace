using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;
using CodeSpace.UnitTests.Infrastructure;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the supervisor plan beat's authoring model call. Pins the NON-hashed outcome fold (WriteModelUsage /
/// ReadModelUsage round-trip; a null usage leaves the outcome byte-identical so the idempotency key can't drift) and the
/// facts source that reads it back onto the PLAN beat. A pre-capture plan (no folded usage) contributes nothing. No DB.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorPlanModelCallFactsSourceTests
{
    [Fact]
    public void WriteModelUsage_round_trips_and_a_null_usage_is_byte_identical()
    {
        var outcome = "{\"count\":4,\"planned\":true}";

        SupervisorOutcome.WriteModelUsage(outcome, null).ShouldBe(outcome, "no usage → byte-identical (never adjacent to the hashed payload, but keep the outcome clean)");

        var written = SupervisorOutcome.WriteModelUsage(outcome, new SupervisorModelUsage { Model = "metis-coder-plus", InputTokens = 16902, OutputTokens = 1062 });
        var read = SupervisorOutcome.ReadModelUsage(written);

        read.ShouldNotBeNull();
        read!.Model.ShouldBe("metis-coder-plus");
        read.InputTokens.ShouldBe(16902);
        read.OutputTokens.ShouldBe(1062);
        written.ShouldContain("\"count\"", customMessage: "the original outcome fields survive alongside modelUsage");
    }

    [Fact]
    public void ReadModelUsage_is_null_on_a_pre_capture_or_malformed_outcome()
    {
        SupervisorOutcome.ReadModelUsage("{\"count\":4}").ShouldBeNull("a pre-capture outcome has no modelUsage");
        SupervisorOutcome.ReadModelUsage(null).ShouldBeNull();
        SupervisorOutcome.ReadModelUsage("not json").ShouldBeNull();
    }

    [Fact]
    public async Task Attaches_the_plan_beat_model_call_from_the_decision_outcome()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        var outcome = SupervisorOutcome.WriteModelUsage("{\"count\":4}", new SupervisorModelUsage { Model = "metis-coder-plus", InputTokens = 1000, OutputTokens = 200 });
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, "{}", outcome);

        var facts = await new SupervisorPlanModelCallFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        var decision = log.Rows.Single();
        var call = facts[SupervisorDecisionTimelineMap.EventId(decision)].ModelCall;

        call.ShouldNotBeNull();
        call!.Model.ShouldBe("metis-coder-plus");
        call.Tokens.ShouldBe(1200, "input + output");
    }

    [Fact]
    public async Task Only_a_plan_decision_with_captured_usage_contributes()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Spawn, "{}", SupervisorOutcome.WriteModelUsage("{}", new SupervisorModelUsage { Model = "m" }));   // wrong verb
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, "{}", "{\"count\":4}");                                                                     // plan, but pre-capture (no usage)

        (await new SupervisorPlanModelCallFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None))
            .ShouldBeEmpty("only a plan decision whose outcome carries captured usage contributes");
    }

    [Fact]
    public async Task Missing_usage_remains_silent_but_truncated_usage_is_explicit_and_never_a_model_call()
    {
        var missing = SupervisorPlanObservationTestData.Item(new SupervisorPlanObservationItemSpec { StoryOrder = 1 });
        var truncated = SupervisorPlanObservationTestData.Item(new SupervisorPlanObservationItemSpec
        {
            StoryOrder = 2,
            ModelUsageState = SupervisorPlanObservationLeafState.Truncated,
            ModelUsage = SupervisorPlanObservationTestData.Usage(new string('m', 200), modelBytes: 300),
        });
        var bundle = new FakeSupervisorPlanObservationPageBundle { Page = SupervisorPlanObservationTestData.Page(items: [missing, truncated]) };

        var facts = await new SupervisorPlanModelCallFactsSource(bundle).GatherAsync(missing.Metadata.SupervisorRunId, Guid.NewGuid(), CancellationToken.None);

        facts.ShouldNotContainKey(SupervisorDecisionTimelineMap.EventId(missing.Metadata.DecisionId), "pre-capture Missing stays no ModelCall and no warning");
        var fact = facts[SupervisorDecisionTimelineMap.EventId(truncated.Metadata.DecisionId)];
        fact.ModelCall.ShouldBeNull("the bounded model prefix is never presented as an exact model");
        fact.ObservationCoverage!.Single().Reason.ShouldBe(JournalObservationCoverageReason.TruncatedLeaf);
    }

    [Fact]
    public async Task Inconsistent_exact_usage_fails_closed_instead_of_promoting_a_prefix()
    {
        var item = SupervisorPlanObservationTestData.Item(new SupervisorPlanObservationItemSpec
        {
            ModelUsageState = SupervisorPlanObservationLeafState.Exact,
            ModelUsage = SupervisorPlanObservationTestData.Usage("prefix", modelBytes: 100),
        });
        var bundle = new FakeSupervisorPlanObservationPageBundle { Page = SupervisorPlanObservationTestData.Page(items: item) };

        var fact = (await new SupervisorPlanModelCallFactsSource(bundle).GatherAsync(item.Metadata.SupervisorRunId, Guid.NewGuid(), CancellationToken.None))
            [SupervisorDecisionTimelineMap.EventId(item.Metadata.DecisionId)];

        fact.ModelCall.ShouldBeNull();
        fact.ObservationCoverage!.Single().Reason.ShouldBe(JournalObservationCoverageReason.CorruptLeaf);
    }
}
