using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentCodeNodeTests
{
    [Fact]
    public void Type_key_is_agent_code() => new AgentCodeNode().TypeKey.ShouldBe("agent.code");

    [Fact]
    public async Task First_pass_suspends_on_an_agent_run_wait_carrying_the_task_envelope()
    {
        var config = new Dictionary<string, JsonElement>
        {
            ["goal"] = Str("Fix the failing billing tests"),
            ["harness"] = Str("codex-cli"),
            ["model"] = Str("gpt-5.3-codex"),
            ["runnerKind"] = Str("local"),
            ["timeoutSeconds"] = Num(900),
            ["readOnly"] = Bool(true),
        };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        result.SuspendUntil!.Kind.ShouldBe(WorkflowWaitKinds.AgentRun);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil.Payload, AgentJson.Options);
        task!.Goal.ShouldBe("Fix the failing billing tests");
        task.Harness.ShouldBe("codex-cli");
        task.Model.ShouldBe("gpt-5.3-codex");
        task.RunnerKind.ShouldBe("local");
        task.TimeoutSeconds.ShouldBe(900);
        task.Permissions.WriteScope.ShouldBe(AgentWriteScope.ReadOnly);
        task.AgentDefinitionId.ShouldBeNull("an inline run carries no persona — the pure-inline path the resolver returns unchanged (zero regression)");
    }

    [Fact]
    public async Task Repository_input_is_carried_onto_the_task()
    {
        var repoId = Guid.NewGuid();
        var inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = Str(repoId.ToString()) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.RepositoryId.ShouldBe(repoId);
    }

    [Theory]
    [InlineData(null)]   // input absent entirely
    [InlineData("")]     // picked then cleared
    public async Task No_repository_input_is_a_no_repo_run(string? raw)
    {
        var inputs = raw is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement> { ["repositoryId"] = Str(raw) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.RepositoryId.ShouldBeNull();
    }

    [Fact]
    public async Task Malformed_repository_input_fails_the_node()
    {
        var inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = Str("not-a-uuid") };

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("repositoryId");
    }

    [Theory]
    [InlineData("goal")]      // no persona → goal is required
    [InlineData("harness")]   // always required (a persona is harness-agnostic)
    public async Task First_pass_fails_when_a_required_config_field_is_missing(string omit)
    {
        // model is intentionally NOT here — it's optional now (blank → persona's model → harness default).
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("g"), ["harness"] = Str("codex-cli") };
        config.Remove(omit);

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain(omit);
    }

    [Fact]
    public async Task Model_is_optional_so_a_node_can_defer_to_the_persona_or_harness_default()
    {
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("Fix it"), ["harness"] = Str("codex-cli") };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.Model
            .ShouldBeNull("a blank model is no longer a hard failure — it flows through as null for the resolver / harness to default");
    }

    [Fact]
    public async Task Persona_reference_lets_goal_and_model_be_omitted_and_carries_the_id()
    {
        var agentId = Guid.NewGuid();
        // Only harness + the persona reference — no goal, no model. The persona supplies the prompt + model
        // (composed by the dispatch-time resolver); the node just carries the reference.
        var config = new Dictionary<string, JsonElement> { ["harness"] = Str("codex-cli"), ["agentDefinitionId"] = Str(agentId.ToString()) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.AgentDefinitionId.ShouldBe(agentId);
        task.Goal.ShouldBe("", "the node carries the raw (empty) goal — the resolver composes persona prompt + goal at dispatch");
        task.Model.ShouldBeNull();
    }

    [Fact]
    public async Task Malformed_agent_definition_id_fails_the_node()
    {
        var config = new Dictionary<string, JsonElement> { ["harness"] = Str("codex-cli"), ["agentDefinitionId"] = Str("not-a-uuid") };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("agentDefinitionId");
    }

    [Fact]
    public async Task Resumed_success_maps_the_result_onto_outputs()
    {
        var resume = JsonDocument.Parse("""
            {"status":"Succeeded","summary":"Fixed the tests.","changedFiles":["src/a.ts","src/b.ts"],"branch":"agent/fix-billing"}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(new(), resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["status"].GetString().ShouldBe("Succeeded");
        result.Outputs["summary"].GetString().ShouldBe("Fixed the tests.");
        result.Outputs["branch"].GetString().ShouldBe("agent/fix-billing");
        result.Outputs["changedFiles"].GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Resumed_failure_fails_the_node_with_the_error()
    {
        var resume = JsonDocument.Parse("""{"status":"Failed","error":"patch did not apply"}""").RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(new(), resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("patch did not apply");
    }

    private static JsonElement Str(string s) => JsonSerializer.SerializeToElement(s);
    private static JsonElement Num(int n) => JsonSerializer.SerializeToElement(n);
    private static JsonElement Bool(bool b) => JsonSerializer.SerializeToElement(b);

    private static Dictionary<string, JsonElement> RequiredConfig() => new()
    {
        ["goal"] = Str("Fix the tests"),
        ["harness"] = Str("codex-cli"),
        ["model"] = Str("gpt-5.3-codex"),
    };

    private static NodeRunContext BuildContext(Dictionary<string, JsonElement> config, JsonElement? resume, Dictionary<string, JsonElement>? inputs = null) => new()
    {
        Inputs = inputs ?? new Dictionary<string, JsonElement>(),
        Config = config,
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
        ResumePayload = resume,
    };
}
