using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using Microsoft.AspNetCore.DataProtection;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The rule for a workflow secret riding an agent's task: the stager names the values the task embeds, the store
/// persists a masked task with the exact copy sealed beside it, and the executor opens the exact copy back — with the
/// values still named, so the run's redactor keeps treating them as secrets. Values never serialize on their own.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentTaskSecretsTests
{
    private const string Secret = "CANARY-goal-7d2e91b4c0";

    private static readonly WorkflowSensitivePayloadSealer Sealer = new(new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider()));

    private static AgentTask Task(string goal) => new() { Goal = goal, Harness = "codex-cli" };

    [Fact]
    public void Embed_names_only_the_scope_values_the_task_carries_and_long_enough_to_be_needles()
    {
        var scope = new PersistenceSecretRedactor([Secret, "absent-scope-secret", "short1"]);

        var embedded = AgentTaskSecrets.Embed(Task($"Deploy with {Secret} and pin short1"), scope);

        embedded.WorkflowSecrets.ShouldBe([Secret],
            "a scope value the task does not embed never rides it, and a value under the needle length stays out of both the mask and the stream redactor");
    }

    [Fact]
    public void Embed_with_no_scope_or_nothing_embedded_returns_the_task_unchanged()
    {
        var task = Task("Nothing secret here");

        AgentTaskSecrets.Embed(task, null).ShouldBeSameAs(task);
        AgentTaskSecrets.Embed(task, new PersistenceSecretRedactor([Secret])).ShouldBeSameAs(task);
    }

    [Fact]
    public void Named_secrets_are_never_serialized_with_the_task()
    {
        var json = JsonSerializer.Serialize(Task("goal") with { WorkflowSecrets = [Secret] }, AgentJson.Options);

        json.ShouldNotContain(Secret);
        json.ShouldNotContain("workflowSecrets", Case.Insensitive);
    }

    [Fact]
    public void A_sealed_task_persists_masked_and_opens_to_the_exact_task_still_naming_its_secrets()
    {
        var task = Task($"Call the deploy API with bearer token {Secret}") with { WorkflowSecrets = [Secret] };

        var sealedTask = AgentTaskSecrets.Seal(task, Sealer);
        var run = new AgentRun { Id = Guid.NewGuid(), TaskJson = sealedTask.PublicJson, SealedTaskCiphertext = sealedTask.Ciphertext, Harness = "codex-cli" };

        run.TaskJson.ShouldNotContain(Secret);
        JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!.Goal.ShouldBe("Call the deploy API with bearer token [REDACTED]");

        var opened = AgentTaskSecrets.Open(run, Sealer)!;

        opened.Goal.ShouldBe(task.Goal, "the agent executes with the exact value");
        opened.WorkflowSecrets.ShouldBe([Secret], "the opened task still names what the run's redactor must mask");
    }

    [Fact]
    public void A_task_naming_no_secret_persists_exactly_as_before_and_opens_from_its_public_column()
    {
        var sealedTask = AgentTaskSecrets.Seal(Task("Plain goal"), sealer: null);
        var run = new AgentRun { Id = Guid.NewGuid(), TaskJson = sealedTask.PublicJson, Harness = "codex-cli" };

        sealedTask.Ciphertext.ShouldBeNull();
        sealedTask.PublicJson.ShouldBe(JsonSerializer.Serialize(Task("Plain goal"), AgentJson.Options));
        AgentTaskSecrets.Open(run, sealer: null)!.Goal.ShouldBe("Plain goal");
        AgentTaskSecrets.Open(run, sealer: null)!.WorkflowSecrets.ShouldBeNull();
    }

    [Fact]
    public void A_sealed_row_cannot_be_opened_without_a_sealer_rather_than_running_the_masked_goal()
    {
        var sealedTask = AgentTaskSecrets.Seal(Task($"goal {Secret}") with { WorkflowSecrets = [Secret] }, Sealer);
        var run = new AgentRun { Id = Guid.NewGuid(), TaskJson = sealedTask.PublicJson, SealedTaskCiphertext = sealedTask.Ciphertext, Harness = "codex-cli" };

        Should.Throw<InvalidOperationException>(() => AgentTaskSecrets.Open(run, sealer: null));
    }
}
