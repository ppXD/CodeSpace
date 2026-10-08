using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Review;
using CodeSpace.UnitTests.Review;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: a model-authored rubric never chooses its own judge. <see cref="AcceptanceRubric.JudgeModelId"/> is an
/// operator knob — a pinned row replaces the independence-aware judge pick — and neither the supervisor decision
/// schema nor the planner wire offers it, but the schema's <c>additionalProperties:false</c> is advisory, so a reply
/// naming it bound into the frozen ledger payload and the judge ran on the row the model chose (PROBE_P5). Both model
/// boundaries now bind the rubric without the pin; an operator's own rubric keeps it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelAuthoredJudgePinTests
{
    private static readonly Guid Pinned = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static string JudgedAcceptance => "{\"kind\":\"LlmJudge\",\"command\":[\"REPORT.md\"],\"rubric\":{\"criteria\":[{\"id\":\"c1\",\"requirement\":\"names the root cause\"}],\"threshold\":1,\"judgeModelId\":\"" + Pinned + "\"}}";

    [Theory]
    [InlineData(false)]   // the bytes the projector freezes from a fresh reply
    [InlineData(true)]    // a row stored before the boundary existed, the pin still in its bytes
    public void A_supervisor_planned_rubric_reaches_the_judge_without_its_pin(bool storedBeforeTheBoundary)
    {
        var reply = JsonDocument.Parse("""{"kind":"plan","plan":{"goal":"g","subtasks":[{"id":"s1","title":"t","instruction":"write REPORT.md","acceptance":""" + JudgedAcceptance + "}]}}").RootElement;

        JsonSchemaValidator.Validate(reply, SupervisorDecisionSchema.ResponseSchema).ShouldBeEmpty("fixture check: the server's schema check lets the pin through — additionalProperties is advisory");

        var decision = SupervisorDecisionProjector.Project(reply.Deserialize<SupervisorModelDecision>(SupervisorDecisionSchema.Options)!);

        decision.PayloadJson.ShouldNotContain("judgeModelId", Case.Insensitive, "the canonical bytes the ledger freezes never carry the pin");

        var stored = storedBeforeTheBoundary ? WithPin(decision.PayloadJson) : decision.PayloadJson;

        if (storedBeforeTheBoundary) stored.ShouldContain(Pinned.ToString(), Case.Insensitive, "fixture check: the legacy row really carries the pin");

        var spec = RealSupervisorActionExecutor.ResolvePlannedSubtasks(new SupervisorTurnContext { Goal = "g", PriorDecisions = new[] { Prior(1, decision.Kind, stored) } })["s1"].Acceptance!;

        spec.Rubric!.JudgeModelId.ShouldBeNull("the spawned unit's acceptance — what its judge grades by — carries no model-chosen judge");
        spec.Rubric.Criteria.ShouldHaveSingleItem().Id.ShouldBe("c1", "everything else the model authored still grades the unit");
        spec.Rubric.Threshold.ShouldBe(1);
        SupervisorOutcome.ReadPlanSubtasks(stored).Single().Acceptance!.Rubric!.JudgeModelId.ShouldBeNull("the per-unit fold reads the same slot");
    }

    [Fact]
    public void A_stop_rubric_reaches_the_stop_gate_without_its_pin()
    {
        var reply = JsonDocument.Parse("""{"kind":"stop","stop":{"outcome":"completed","summary":"shipped","acceptance":""" + JudgedAcceptance + "}}").RootElement;
        var decision = SupervisorDecisionProjector.Project(reply.Deserialize<SupervisorModelDecision>(SupervisorDecisionSchema.Options)!);

        SupervisorTurnService.ReadStopAcceptance(WithPin(decision.PayloadJson))!.Rubric!.JudgeModelId.ShouldBeNull();
    }

    [Fact]
    public void A_planner_authored_rubric_binds_without_its_pin()
    {
        var spec = PlannerAcceptanceDraft.Bind(JsonDocument.Parse($$$"""{"formatVersion":2,"kind":"LlmJudge","artifactPaths":["REPORT.md"],"rubric":{"criteria":[{"id":"c1","requirement":"r"}],"judgeModelId":"{{{Pinned}}}"}}""").RootElement, out var defect);

        defect.ShouldBeNull("the pin is dropped, not refused — the rest of the oracle is sound");
        spec!.Rubric!.JudgeModelId.ShouldBeNull();
        spec.Rubric.Criteria.ShouldHaveSingleItem().Id.ShouldBe("c1");
    }

    [Fact]
    public void An_operators_rubric_keeps_its_judge_pin()
    {
        var rubric = new AcceptanceRubric { Criteria = new[] { new AcceptanceRubricCriterion { Id = "c1", Requirement = "r" } }, JudgeModelId = Pinned };
        var task = new AgentTask { Goal = "g", Harness = "test", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "REPORT.md" }, Kind = Messages.Agents.Benchmark.BenchmarkGradingKind.LlmJudge, Rubric = rubric } };

        JsonSerializer.Deserialize<AgentTask>(JsonSerializer.Serialize(task, AgentJson.Options), AgentJson.Options)!.Acceptance!.Rubric!.JudgeModelId.ShouldBe(Pinned, "node config and AgentTask.Acceptance are the operator's own contract");
    }

    [Fact]
    public async Task The_judge_auto_picks_its_row_once_the_pin_is_gone()
    {
        // PROBE_P5's last leg, inverted: the judge consults its independence-aware pick instead of a model-chosen row.
        var pool = new StubReviewerPool();
        var judge = new LlmRubricJudge(new SingleClientRegistry(new InstructionFollowingJudge()), pool);
        var rubric = PlannerAcceptanceDraft.Bind(JsonDocument.Parse($$$"""{"formatVersion":2,"kind":"LlmJudge","artifactPaths":["REPORT.md"],"rubric":{"criteria":[{"id":"c1","requirement":"r"}],"judgeModelId":"{{{Pinned}}}"}}""").RootElement, out _)!.Rubric!;

        await judge.JudgeAsync(new RubricJudgeRequest { Rubric = rubric, Artifact = "MEETS[c1]", TeamId = Guid.NewGuid() }, CancellationToken.None);

        pool.Resolved.ShouldBe(new[] { StubReviewerPool.AutoPickedRow });
    }

    private static SupervisorPriorDecision Prior(int sequence, string kind, string payloadJson) =>
        new() { Id = Guid.NewGuid(), Sequence = sequence, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = kind, PayloadJson = payloadJson, OutcomeJson = "{}" };

    /// <summary>The payload as a pre-boundary projector stored it: every rubric carries the pin.</summary>
    private static string WithPin(string payloadJson)
    {
        var root = JsonNode.Parse(payloadJson)!;

        Inject(root);

        return root.ToJsonString(AgentJson.Options);

        static void Inject(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["rubric"] is JsonObject rubric) rubric["judgeModelId"] = Pinned.ToString();

                    foreach (var (_, child) in obj.ToList()) Inject(child);
                    break;

                case JsonArray array:
                    foreach (var child in array) Inject(child);
                    break;
            }
        }
    }
}
