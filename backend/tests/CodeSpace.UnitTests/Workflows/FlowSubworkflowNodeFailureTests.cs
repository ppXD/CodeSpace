using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Failures;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// What a <c>flow.subworkflow</c> node does with the payload the child's completion resumes it with: a success maps the child's outputs, a failure
/// is the node's own sentence — unchanged — and, when the child ended on a provider failure a person can repair, that failure beside it. A payload
/// stamped before children carried theirs, one carrying nothing, and one carrying anything that is not a provider connection failure all fail the
/// node with the sentence alone, and nothing here ever throws: a payload that cannot be read must never fail the run worse than the child already did.
/// </summary>
[Trait("Category", "Unit")]
public class FlowSubworkflowNodeFailureTests
{
    private static readonly Guid ChildRun = Guid.NewGuid();
    private static readonly Guid Credential = Guid.NewGuid();

    private static Task<NodeResult> ResumeWithAsync(string payload) => new FlowSubworkflowNode().RunAsync(new NodeRunContext
    {
        NodeId = "sub",
        Config = new Dictionary<string, JsonElement> { ["workflowId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()) },
        Inputs = new Dictionary<string, JsonElement>(),
        RawConfig = JsonDocument.Parse("{}").RootElement.Clone(),
        RawInputs = JsonDocument.Parse("{}").RootElement.Clone(),
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
        ResumePayload = JsonDocument.Parse(payload).RootElement.Clone(),
    }, CancellationToken.None);

    private static string FailedPayload(string failureMember = "") => $$$"""{"status":"Failure","outputs":{},"error":"Node 'list' failed."{{{failureMember}}}}""";

    private static string Failure(string code = FailureCodes.CredentialNeedsReconnect) =>
        $$$""","failure":{"code":"{{{code}}}","details":{"credentialId":"{{{Credential}}}"},"repositoryId":"{{{Guid.NewGuid()}}}","origin":{"runId":"{{{ChildRun}}}","nodeId":"list"}}""";

    [Fact]
    public async Task A_child_that_ended_on_a_provider_failure_fails_the_node_with_its_own_sentence_and_the_failure_beside_it()
    {
        var result = await ResumeWithAsync(FailedPayload(Failure()));

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldBe("Sub-workflow run failed: Node 'list' failed.", "the sentence is the node's own and is exactly what it always was");
        result.CarriedFailure.ShouldNotBeNull();
        result.CarriedFailure.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect);
        result.CarriedFailure.DetailGuid("credentialId").ShouldBe(Credential);
        result.CarriedFailure.Origin.ShouldBe(new FailureOrigin { RunId = ChildRun, NodeId = "list" });
        result.Retryable.ShouldBeTrue("carrying a failure never changes whether the node retries");
        result.Cause.ShouldBeNull("there is no exception: the failure was read back, not thrown");
    }

    [Fact]
    public async Task The_time_the_child_recorded_the_failure_rides_through_the_node_untouched()
    {
        var at = new DateTimeOffset(2026, 10, 19, 4, 29, 49, TimeSpan.Zero);
        var failure = $$$""","failure":{"code":"provider_unauthorized","origin":{"runId":"{{{ChildRun}}}","nodeId":"list","at":"2026-10-19T04:29:49+00:00"}}""";

        (await ResumeWithAsync(FailedPayload(failure))).CarriedFailure!.Origin.ShouldBe(new FailureOrigin { RunId = ChildRun, NodeId = "list", At = at }, "when the child failed, not when the node woke up");
    }

    [Theory]
    [InlineData("")]                                                                       // a payload stamped before children carried their failures
    [InlineData(",\"failure\":null")]
    [InlineData(",\"failure\":\"a string\"")]
    [InlineData(",\"failure\":{}")]
    [InlineData(",\"failure\":{\"code\":\"\"}")]
    [InlineData(",\"failure\":{\"code\":\"credential_disconnected\",\"origin\":\"not an object\"}")]   // the whole failure is unreadable, so none is carried — never a throw
    public async Task A_payload_with_no_readable_failure_fails_the_node_with_the_sentence_alone(string failureMember)
    {
        var result = await ResumeWithAsync(FailedPayload(failureMember));

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldBe("Sub-workflow run failed: Node 'list' failed.");
        result.CarriedFailure.ShouldBeNull();
    }

    [Theory]
    [InlineData(FailureCodes.Internal)]
    [InlineData(FailureCodes.RunBudgetExhausted)]
    [InlineData("a_code_nobody_declared")]
    public async Task A_failure_that_is_not_a_provider_connection_is_not_carried(string code)
    {
        var result = await ResumeWithAsync(FailedPayload(Failure(code)));

        result.Status.ShouldBe(NodeStatus.Failure);
        result.CarriedFailure.ShouldBeNull("only the failures a person can repair outside the run are written down, wherever they are read back");
    }

    [Theory]
    [InlineData(FailureCodes.CredentialDisconnected)]
    [InlineData(FailureCodes.CredentialNeedsReconnect)]
    [InlineData(FailureCodes.ProviderUnauthorized)]
    [InlineData(FailureCodes.GitCredentialRejected)]
    [InlineData(FailureCodes.ProviderBaseUrlRedirects)]
    [InlineData(FailureCodes.OAuthExchangeFailed)]
    public async Task Every_provider_connection_failure_is_carried(string code)
    {
        (await ResumeWithAsync(FailedPayload(Failure(code)))).CarriedFailure!.Code.ShouldBe(code);
    }

    [Fact]
    public async Task A_child_without_an_error_sentence_still_fails_the_node_with_the_sentence_it_always_had()
    {
        var result = await ResumeWithAsync("""{"status":"Cancelled","outputs":{},"error":null}""");

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldBe("Sub-workflow run failed: see the child run");
        result.CarriedFailure.ShouldBeNull();
    }

    [Fact]
    public async Task A_child_that_succeeded_maps_its_outputs_and_carries_nothing_even_if_a_failure_rides_the_payload()
    {
        var result = await ResumeWithAsync($$$"""{"status":"Success","outputs":{"result":"ok"},"error":null{{{Failure()}}}}""");

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["result"].GetString().ShouldBe("ok");
        result.CarriedFailure.ShouldBeNull();
    }

    [Fact]
    public void The_failure_a_payload_carries_is_the_shape_a_ledger_record_keeps_it_in()
    {
        // The completion hook writes it with PersistedFailureJson and the node reads it with PersistedFailureJson: the same key, the same object.
        PersistedFailureJson.PayloadKey.ShouldBe("failure");
        PersistedFailureJson.Read(FailedPayload(Failure()))!.Origin!.RunId.ShouldBe(ChildRun);
    }
}
