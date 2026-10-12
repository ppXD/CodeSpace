using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The workflow secrets an agent's task embeds become needles of the run's redactor — the agent was handed them in
/// its goal, so its events, native records, result, error and MCP ledger can echo them like an injected key. A run
/// whose task embeds none keeps its redactor and its handle fingerprint exactly as before, which is what lets a run
/// launched before this change re-attach after it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunWorkflowSecretRedactorTests
{
    private const string Secret = "CANARY-wf-secret-51c8e2";

    private static readonly ResolvedModelCredential Credential = new() { Provider = "Anthropic", ApiKey = "sk-live-9f3a7c21" };

    [Fact]
    public void Workflow_secrets_become_needles_beside_the_credential()
    {
        var subject = AgentRunExecutor.WithWorkflowSecrets(AgentRunExecutor.BuildRunRedactor(new Dictionary<string, string>(), Credential), [Secret]);

        subject.Redact($"calling deploy with {Secret} using sk-live-9f3a7c21").ShouldBe("calling deploy with *** using ***");
    }

    [Fact]
    public void A_task_embedding_no_workflow_secret_keeps_the_redactor_and_its_fingerprint()
    {
        var baseline = AgentRunExecutor.BuildRunRedactor(new Dictionary<string, string>(), Credential);

        AgentRunExecutor.WithWorkflowSecrets(baseline, null).ShouldBeSameAs(baseline);
        AgentRunExecutor.WithWorkflowSecrets(baseline, []).ShouldBeSameAs(baseline);
        AgentRunExecutor.WithWorkflowSecrets(baseline, ["short"]).Fingerprint.ShouldBe(baseline.Fingerprint, "a value under the needle length stays readable and does not change the stamped fingerprint");
    }

    [Fact]
    public void The_fold_is_a_function_of_the_secret_set_so_a_reattach_rebuilds_the_launch_fingerprint()
    {
        var baseline = AgentRunExecutor.BuildRunRedactor(new Dictionary<string, string>(), Credential);

        var launch = AgentRunExecutor.WithWorkflowSecrets(baseline, [Secret, "CANARY-second-0b9d7a44"]);
        var reattach = AgentRunExecutor.WithWorkflowSecrets(baseline, ["CANARY-second-0b9d7a44", Secret, Secret]);

        launch.Fingerprint.ShouldNotBe(baseline.Fingerprint, "a fingerprint blind to the workflow secrets would let a re-attach that lost them re-tail unmasked");
        reattach.Fingerprint.ShouldBe(launch.Fingerprint);
    }
}
