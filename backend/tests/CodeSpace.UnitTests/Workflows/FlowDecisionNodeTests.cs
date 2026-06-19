using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <c>flow.decision</c> (Decision substrate D1) — the first pass raises a typed <see cref="DecisionRequest"/> and parks
/// on a BOUNDED Decision wait (a mandatory deadline + default, so it never hangs); the resumed pass maps the
/// <see cref="DecisionAnswer"/> to outputs identically whether a human, a policy, a supervisor, or the timeout answered.
/// </summary>
[Trait("Category", "Unit")]
public class FlowDecisionNodeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Guid RunId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task First_pass_parks_on_a_decision_wait_with_the_typed_envelope()
    {
        var config = """
            {
              "question": "Which migration path?",
              "decisionType": "choose_one",
              "options": [ { "id": "a", "label": "Path A" }, { "id": "b", "label": "Path B", "isSideEffecting": true } ],
              "recommendedOption": "a",
              "blockingReason": "the schema diverged",
              "riskLevel": "low",
              "policy": "supervisor_first",
              "timeoutSeconds": 120,
              "defaultAction": "a"
            }
            """;

        var result = await new FlowDecisionNode().RunAsync(Context(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        result.SuspendUntil.ShouldNotBeNull();
        result.SuspendUntil!.Kind.ShouldBe(WorkflowWaitKinds.Decision);
        result.SuspendUntil.DeadlineAt.ShouldNotBeNull("a decision is ALWAYS bounded — it can never hang forever (AC4)");
        result.SuspendUntil.DeadlineAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(60));
        result.SuspendUntil.TimeoutPayload.ShouldNotBeNull("the default-on-timeout answer must be staged with the wait");

        var req = JsonSerializer.Deserialize<DecisionRequest>(result.SuspendUntil.Payload, Json)!;
        req.Question.ShouldBe("Which migration path?");
        req.DecisionType.ShouldBe(DecisionTypes.ChooseOne);
        req.Options.Count.ShouldBe(2);
        req.Options[1].IsSideEffecting.ShouldBeTrue();
        req.RecommendedOption.ShouldBe("a");
        req.BlockingReason.ShouldBe("the schema diverged");
        req.RiskLevel.ShouldBe(DecisionRiskLevels.Low);
        req.Policy.ShouldBe(DecisionPolicies.SupervisorFirst);
        req.Scope.ShouldBe(DecisionScopes.Node);
        req.RequesterType.ShouldBe(DecisionRequesterTypes.WorkflowNode);
        req.ResumeBackend.ShouldBe(DecisionResumeBackends.WorkflowWait);
        req.Status.ShouldBe(DecisionStatuses.Pending);
        req.RootTraceId.ShouldBe(RunId, "rootTraceId is stamped from the run id (AC5 — every event is traceable from the first slice)");
        req.NodeId.ShouldBe("decide");
        req.DedupeKey.ShouldContain(RunId.ToString("N"), Case.Insensitive);
        req.Id.ShouldNotBe(Guid.Empty);

        var timeout = JsonSerializer.Deserialize<DecisionAnswer>(result.SuspendUntil.TimeoutPayload!.Value, Json)!;
        timeout.AnsweredBy.ShouldBe(DecisionAnsweredByKinds.Timeout);
        timeout.SelectedOptions.ShouldBe(new[] { "a" }, "the default-on-timeout applies the configured defaultAction");
        timeout.TimedOut.ShouldBeTrue();
        timeout.Rationale.ShouldNotBeNullOrWhiteSpace("even the timeout default records a rationale (AC3)");
    }

    [Fact]
    public async Task A_decision_with_no_timeout_config_is_still_bounded_by_the_default_deadline()
    {
        var result = await new FlowDecisionNode().RunAsync(Context("""{ "question": "ok?" }""", resume: null), CancellationToken.None);

        result.SuspendUntil!.DeadlineAt.ShouldNotBeNull("the mandatory deadline is always set, even with no timeoutSeconds config");
    }

    [Fact]
    public async Task Resumed_pass_surfaces_the_answer_as_outputs()
    {
        var answer = new DecisionAnswer
        {
            DecisionId = Guid.NewGuid(),
            AnsweredBy = DecisionAnsweredByKinds.Human,
            SelectedOptions = new[] { "b" },
            FreeText = "B is safer",
        };

        var result = await new FlowDecisionNode().RunAsync(Context("{}", resume: JsonSerializer.SerializeToElement(answer, Json)), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["selectedOption"].GetString().ShouldBe("b");
        result.Outputs["selectedOptions"].GetArrayLength().ShouldBe(1);
        result.Outputs["freeText"].GetString().ShouldBe("B is safer");
        result.Outputs["answeredBy"].GetString().ShouldBe(DecisionAnsweredByKinds.Human);
        result.Outputs["timedOut"].GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Resumed_pass_with_the_timeout_default_marks_timed_out_and_carries_the_rationale()
    {
        var timeoutAnswer = new DecisionAnswer
        {
            DecisionId = Guid.NewGuid(),
            AnsweredBy = DecisionAnsweredByKinds.Timeout,
            SelectedOptions = new[] { "a" },
            Rationale = "deadline",
            TimedOut = true,
        };

        var result = await new FlowDecisionNode().RunAsync(Context("{}", resume: JsonSerializer.SerializeToElement(timeoutAnswer, Json)), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["timedOut"].GetBoolean().ShouldBeTrue();
        result.Outputs["answeredBy"].GetString().ShouldBe(DecisionAnsweredByKinds.Timeout);
        result.Outputs["rationale"].GetString().ShouldBe("deadline");
    }

    [Fact]
    public async Task A_malformed_resume_payload_degrades_to_a_clean_empty_answer()
    {
        // A foreign/garbage resume payload must not crash the resumed walk — it degrades to a "no answer".
        var result = await new FlowDecisionNode().RunAsync(Context("{}", resume: JsonDocument.Parse("\"not-an-object\"").RootElement), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["selectedOption"].ValueKind.ShouldBe(JsonValueKind.Null);
        result.Outputs["timedOut"].GetBoolean().ShouldBeFalse();
    }

    private static NodeRunContext Context(string config, JsonElement? resume) => new()
    {
        Inputs = new Dictionary<string, JsonElement>(),
        Config = JsonDocument.Parse(config).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse(config).RootElement,
        Scope = new NodeRunScope
        {
            Trigger = new Dictionary<string, JsonElement>(),
            Sys = new Dictionary<string, JsonElement> { [SystemScopeKeys.WorkflowRunId] = JsonSerializer.SerializeToElement(RunId.ToString()) },
        },
        NodeId = "decide",
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
        ResumePayload = resume,
    };
}
