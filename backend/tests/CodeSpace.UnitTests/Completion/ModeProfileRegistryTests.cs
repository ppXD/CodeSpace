using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: the P4 mode-profile vocabulary — the first cell of the conformance matrix. Pins: the three agent
/// lanes are registered with TOTAL stage maps (every stage declared, never silently unmapped) and their honest
/// qualifications; the generic graph is DELIBERATELY unregistered (fail-close downstream); the classifier derives
/// the mode from the launch-stamped projection kind first, else the definition's own node shape, with the
/// supervisor node outranking a map outranking a plain agent, and unparseable json reading generic.
/// </summary>
[Trait("Category", "Unit")]
public class ModeProfileRegistryTests
{
    private static readonly ModeProfileRegistry Registry = new();

    [Theory]
    [InlineData(RunModeKeys.Supervisor, ProtocolReadiness.Shadow, PerformanceQualification.Shadow)]
    [InlineData(RunModeKeys.PlanMap, ProtocolReadiness.Open, PerformanceQualification.Unmeasured)]
    [InlineData(RunModeKeys.SingleAgent, ProtocolReadiness.Shadow, PerformanceQualification.Shadow)]
    public void The_registered_lanes_declare_total_stage_maps(string mode, ProtocolReadiness readiness, PerformanceQualification performance)
    {
        var profile = Registry.Resolve(mode).ShouldNotBeNull();

        profile.Readiness.ShouldBe(readiness);
        profile.Performance.ShouldBe(performance, "the two axes are orthogonal — a protocol can be enforceable before any performance number stands");
        profile.Stages.Keys.ShouldBe(Enum.GetValues<CompletionStage>(), ignoreOrder: true, customMessage: "every stage declared — a new stage must break here, never sit silently unmapped");
    }

    [Fact]
    public void A_generic_graph_has_no_conformance_story()
    {
        Registry.Resolve(RunModeKeys.Generic).ShouldBeNull("deliberate — an arbitrary graph's runs must park Unsupported under Enforced, not terminalize a Success nothing qualified");
        Registry.Resolve("no-such-mode").ShouldBeNull();
    }

    [Fact]
    public void Stage_requiredness_never_offers_a_model_authored_na()
    {
        Enum.GetNames<StageRequiredness>().ShouldBe(new[] { "Required", "OperatorAuthorizedNotApplicable", "ServerPolicyAuthorizedNotApplicable" },
            ignoreOrder: true, customMessage: "Lock Clause 4 — a model proposal can never set a stage N/A; the vocabulary must not even contain the member");
    }

    [Fact]
    public void The_supervisor_lane_owes_the_full_chain_and_single_agent_owes_no_plan_or_integration()
    {
        Registry.Resolve(RunModeKeys.Supervisor)!.Stages.Values.ShouldAllBe(r => r == StageRequiredness.Required);

        var single = Registry.Resolve(RunModeKeys.SingleAgent)!;
        single.Stages[CompletionStage.Plan].ShouldBe(StageRequiredness.ServerPolicyAuthorizedNotApplicable, "one unit has no plan — authorized off, never silently absent");
        single.Stages[CompletionStage.Integrate].ShouldBe(StageRequiredness.ServerPolicyAuthorizedNotApplicable);
        single.Stages[CompletionStage.Verify].ShouldBe(StageRequiredness.Required);
    }

    [Theory]
    [InlineData("supervisor", RunModeKeys.Supervisor)]
    [InlineData("single-agent", RunModeKeys.SingleAgent)]
    [InlineData("plan-map-synth", RunModeKeys.PlanMap)]
    [InlineData("plan-map-dynamic", RunModeKeys.PlanMap)]
    [InlineData("coordinated-loop", RunModeKeys.PlanMap)]
    [InlineData("some-future-kind", RunModeKeys.Generic)]
    public void The_projection_kind_wins_when_stamped(string projectionKind, string expected)
    {
        RunModeClassifier.Derive(projectionKind, new WorkflowDefinition { Nodes = [], Edges = [] }).ShouldBe(expected);
    }

    [Fact]
    public void An_authored_definition_derives_from_its_node_shape()
    {
        RunModeClassifier.Derive(null, Definition("trigger.manual", "agent.supervisor", "flow.map", "agent.run", "builtin.terminal"))
            .ShouldBe(RunModeKeys.Supervisor, "a supervisor node IS a supervisor run whatever else the graph carries");
        RunModeClassifier.Derive(null, Definition("trigger.manual", "flow.map", "agent.run", "builtin.terminal")).ShouldBe(RunModeKeys.PlanMap);
        RunModeClassifier.Derive(null, Definition("trigger.manual", "agent.run", "builtin.terminal")).ShouldBe(RunModeKeys.SingleAgent);
        RunModeClassifier.Derive(null, Definition("trigger.manual", "llm.complete", "builtin.terminal")).ShouldBe(RunModeKeys.Generic);
    }

    [Fact]
    public void Unparseable_definition_json_reads_generic_which_fails_closed_downstream()
    {
        RunModeClassifier.DeriveFromJson(null, "{not json").ShouldBe(RunModeKeys.Generic);
        RunModeClassifier.DeriveFromJson(null, null).ShouldBe(RunModeKeys.Generic);
    }

    private static WorkflowDefinition Definition(params string[] typeKeys) => new()
    {
        Nodes = typeKeys.Select((k, i) => new NodeDefinition { Id = $"n{i}", TypeKey = k, Config = System.Text.Json.JsonDocument.Parse("{}").RootElement, Inputs = System.Text.Json.JsonDocument.Parse("{}").RootElement }).ToList(),
        Edges = [],
    };
}
