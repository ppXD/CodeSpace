using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.UnitTests.Fleet;
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

    private static readonly WorkflowSensitivePayloadSealer Sealer = new(new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider()), FixedFleetCapabilities.AllCurrent);

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

    [Fact]
    public void While_a_previous_version_worker_is_live_a_secret_bearing_task_persists_as_the_previous_build_wrote_it()
    {
        var task = Task($"Call the deploy API with bearer token {Secret}") with { WorkflowSecrets = [Secret] };
        var sealer = new WorkflowSensitivePayloadSealer(new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider()), FixedFleetCapabilities.WithPreviousVersionWorker);

        var written = AgentTaskSecrets.Seal(task, sealer);

        // The previous build's AgentRunService wrote exactly this expression into task_jsonb and its executor
        // deserialized task_jsonb to launch; WorkflowSecrets is [JsonIgnore], so the list never reaches the column.
        written.PublicJson.ShouldBe(CodeSpace.Core.Persistence.PersistedText.SanitizeJson(JsonSerializer.Serialize(task, AgentJson.Options)));
        written.Ciphertext.ShouldBeNull();
        JsonSerializer.Deserialize<AgentTask>(written.PublicJson, AgentJson.Options)!.Goal.ShouldBe(task.Goal, "the previous build launches the exact goal, never the mask");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_task_a_current_worker_rewrites_onto_its_own_row_keeps_the_shape_it_was_opened_from(bool openedFromSealedRow)
    {
        // The executor re-persists the opened task with its resolved model while the run is Running. A previous-version
        // worker never launches a Running row, so the fleet gate has nothing to protect there; the row's shape is what
        // the launch's redactor fingerprint was built from, and a re-attach rebuilds it from that shape.
        var protector = new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider());
        var stagedWhileAllCurrent = new WorkflowSensitivePayloadSealer(protector, FixedFleetCapabilities.AllCurrent);
        var previousVersionWorkerLive = new WorkflowSensitivePayloadSealer(protector, FixedFleetCapabilities.WithPreviousVersionWorker);
        var staged = AgentTaskSecrets.Seal(Task($"Deploy with {Secret}") with { WorkflowSecrets = openedFromSealedRow ? [Secret] : null }, stagedWhileAllCurrent);
        var row = new AgentRun { Id = Guid.NewGuid(), TaskJson = staged.PublicJson, SealedTaskCiphertext = staged.Ciphertext, Harness = "codex-cli" };
        var opened = AgentTaskSecrets.Open(row, previousVersionWorkerLive)!;

        var written = AgentTaskSecrets.Reseal(opened with { Model = "resolved-model" }, previousVersionWorkerLive);

        (written.Ciphertext is not null).ShouldBe(openedFromSealedRow);
        written.PublicJson.Contains(Secret).ShouldBe(!openedFromSealedRow);
        var reopened = AgentTaskSecrets.Open(new AgentRun { Id = row.Id, TaskJson = written.PublicJson, SealedTaskCiphertext = written.Ciphertext, Harness = "codex-cli" }, previousVersionWorkerLive)!;
        reopened.Model.ShouldBe("resolved-model");
        reopened.Goal.ShouldBe($"Deploy with {Secret}");
        reopened.WorkflowSecrets.ShouldBe(opened.WorkflowSecrets, "a re-attach rebuilds the launch's redactor from exactly these");
    }
}

