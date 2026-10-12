using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Projects;
using CodeSpace.Messages.Commands.Variables;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using CodeSpace.Messages.Queries.Tasks;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// A resolved workflow secret that crosses a suspend into ANOTHER run — an <c>agent.run</c> goal referencing
/// <c>{{team|wf|project.X}}</c>, a <c>flow.subworkflow</c> child handed one in its inputs — must reach that run's
/// execution EXACTLY and never land at rest: not in the wait row, the agent's task, the child's request, the agent's
/// events / native records / result, the child's ledger or outputs, nor in any response a Viewer can read. Each test
/// proves both halves: the executing side saw the real value (the fake agent's capture file, the child's encrypted
/// node-output sidecar), and an ALL-TABLE + artifact canary sweep (<see cref="SecretCanarySweep"/>) plus a Viewer's
/// reads of every run surface find nothing.
///
/// <para>Real engine, real executor + <c>LocalProcessRunner</c> spawning <see cref="GoalCapturingFakeCli"/>, real
/// Postgres; jobs are recorded and driven by hand so each suspend boundary can be inspected. POSIX-only (the fake CLI
/// is a /bin/sh script — Rule 12.1).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SecretTaintAcrossSuspendsFlowTests
{
    private readonly PostgresFixture _fixture;

    public SecretTaintAcrossSuspendsFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_agent_goals_secrets_reach_the_agent_exactly_but_never_land_at_rest_or_reach_a_viewer()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new GoalCapturingFakeCli();
        var canaries = await SeedSecretsAsync();
        var workflowId = await CreateWorkflowAsync(canaries, AgentGoalDefinition());
        await SetWorkflowSecretAsync(canaries, workflowId);

        // A team-default credential with a model, so the executor re-persists the task with its RESOLVED model at
        // launch — the write that would put the opened (exact) task back into task_jsonb if it were not re-sealed.
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, canaries.TeamId, ResolvedModel, provider: "OpenAI");

        using var manual = ResolveJobClient().ManualExecution();
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, canaries.TeamId);

        await RunEngineAsync(runId);
        var agentRun = await LoadStagedAgentRunAsync(runId);

        agentRun.SealedTaskCiphertext.ShouldNotBeNull("the exact task rides the encrypted sealed column the executor launches from");
        await AssertNoCanaryAsync(canaries, "after the agent was staged", runId, agentRun.Id);
        (await ViewerAgentRunAsync(canaries, agentRun.Id))!.Goal.ShouldContain(PersistenceSecretRedactor.Marker, customMessage: "a Viewer still reads the goal — masked, not withheld");

        await ExecuteAgentAsync(agentRun.Id);

        foreach (var canary in canaries.All)
            cli.CapturedGoals.ShouldContain(canary, customMessage: "the agent must be launched with the EXACT goal — masking may never corrupt a value the platform itself needs");

        await RunEngineAsync(runId);

        (await LoadRunAsync(runId)).Status.ShouldBe(WorkflowRunStatus.Success, "the run resumes with the agent's (masked) result and completes");
        var finished = await LoadAgentRunAsync(agentRun.Id);
        finished.Status.ShouldBe(AgentRunStatus.Succeeded);
        JsonSerializer.Deserialize<AgentTask>(finished.TaskJson, AgentJson.Options)!.Model.ShouldBe(ResolvedModel, "precondition: the launch re-persisted the task with its resolved model");
        await AssertNoCanaryAsync(canaries, "after the agent echoed its goal into every stream and the run completed", runId, agentRun.Id);
    }

    [Fact]
    public async Task An_agent_with_a_local_acceptance_contract_still_grades_against_its_exact_goal()
    {
        // The local acceptance verifier re-reads the STORED contract and hashes it against the one the executor runs.
        // The stored task_jsonb is masked now, so a verifier still reading it would see a different goal and fail every
        // contract-bearing run whose goal embeds a secret — masking corrupting a value the platform itself needs.
        if (OperatingSystem.IsWindows()) return;

        using var cli = new GoalCapturingFakeCli();
        var canaries = await SeedSecretsAsync();
        var workflowId = await CreateWorkflowAsync(canaries, AgentGoalDefinition(acceptance: """{ "kind": "TestsPass", "command": ["sh", "-c", "exit 0"] }"""));
        await SetWorkflowSecretAsync(canaries, workflowId);

        using var manual = ResolveJobClient().ManualExecution();
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, canaries.TeamId);
        await RunEngineAsync(runId);
        var agentRunId = (await LoadStagedAgentRunAsync(runId)).Id;

        await ExecuteAgentAsync(agentRunId);

        var finished = await LoadAgentRunAsync(agentRunId);
        finished.Status.ShouldBe(AgentRunStatus.Succeeded, $"the contract graded against the exact goal; result: {finished.ResultJson}");
        finished.ResultJson.ShouldNotBeNull().ShouldNotContain("local-context-mismatch");
    }

    [Fact]
    public async Task A_subworkflows_inputs_reach_the_child_exactly_but_never_land_at_rest_or_reach_a_viewer()
    {
        var canaries = await SeedSecretsAsync();
        var childWorkflowId = await CreateWorkflowAsync(canaries, ChildDefinition(terminalInputs: """{ "status": "ok" }"""));
        var parentWorkflowId = await CreateWorkflowAsync(canaries, ParentDefinition(childWorkflowId));
        await SetWorkflowSecretAsync(canaries, parentWorkflowId);

        using var manual = ResolveJobClient().ManualExecution();
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentWorkflowId, canaries.TeamId);

        await RunEngineAsync(parentRunId);
        var childRunId = await LoadChildRunIdAsync(parentRunId);

        (await LoadRunRequestAsync(childRunId)).SealedPayloadCiphertext.ShouldNotBeNull("the child's exact inputs ride the request's encrypted sealed column");
        await AssertNoCanaryAsync(canaries, "after the child was staged", parentRunId, childRunId);

        await RunEngineAsync(childRunId);

        (await LoadRunAsync(childRunId)).Status.ShouldBe(WorkflowRunStatus.Success);
        var exact = await ReadSealedNodeOutputsAsync(childRunId, "emit");
        exact["t"].GetString().ShouldBe(canaries.Team, "the child must execute with the parent's EXACT team secret");
        exact["w"].GetString().ShouldBe(canaries.Workflow, "… and its exact wf secret, which the child's own variable rows cannot name");
        exact["p"].GetString().ShouldBe(canaries.Project, "… and its exact project secret");

        await RunEngineAsync(parentRunId);

        (await LoadRunAsync(parentRunId)).Status.ShouldBe(WorkflowRunStatus.Success);
        await AssertNoCanaryAsync(canaries, "after the child ran and the parent resumed", parentRunId, childRunId);
    }

    [Theory]
    [InlineData("""{ "echo": "{{trigger.wfToken}}" }""")]          // the PATH guard: the trigger key that carried the parent secret is tainted
    [InlineData("""{ "echo": "{{nodes.emit.outputs.w}}" }""")]     // the VALUE guard: an upstream node re-emitted the inherited value
    public async Task A_child_cannot_surface_an_inherited_parent_secret_as_its_output(string terminalInputs)
    {
        var canaries = await SeedSecretsAsync();
        var childWorkflowId = await CreateWorkflowAsync(canaries, ChildDefinition(terminalInputs));
        var parentWorkflowId = await CreateWorkflowAsync(canaries, ParentDefinition(childWorkflowId));
        await SetWorkflowSecretAsync(canaries, parentWorkflowId);

        using var manual = ResolveJobClient().ManualExecution();
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentWorkflowId, canaries.TeamId);

        await RunEngineAsync(parentRunId);
        var childRunId = await LoadChildRunIdAsync(parentRunId);
        await RunEngineAsync(childRunId);

        var child = await LoadRunAsync(childRunId);
        child.Status.ShouldBe(WorkflowRunStatus.Failure, "a parent's wf secret is still a secret inside the child, so the child's Terminal leak guard must refuse to publish it");
        child.Error.ShouldNotBeNull().ShouldContain("Terminal node 'end'");
        await AssertNoCanaryAsync(canaries, "after the child's terminal refused the inherited secret", parentRunId, childRunId);
    }

    [Fact]
    public async Task A_child_map_over_inherited_secrets_freezes_none_of_them()
    {
        // A Plain map freezes its resolved items in workflow_run_map_input; only a SecretDerived one re-resolves live.
        // The child's items binding names {{trigger.tokens}} — no secret head of the child's own — so it is the taint on
        // that inherited key that must classify it, and the inherited values that must mask every branch's records.
        var canaries = await SeedSecretsAsync();
        var childWorkflowId = await CreateWorkflowAsync(canaries, ChildMapDefinition());
        var parentWorkflowId = await CreateWorkflowAsync(canaries, ParentDefinition(childWorkflowId, """{ "inputs": { "tokens": [ "{{wf.WF_TOKEN}}", "{{project.billing.TOKEN}}" ] } }"""));
        await SetWorkflowSecretAsync(canaries, parentWorkflowId);

        using var manual = ResolveJobClient().ManualExecution();
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentWorkflowId, canaries.TeamId);
        await RunEngineAsync(parentRunId);
        var childRunId = await LoadChildRunIdAsync(parentRunId);
        await RunEngineAsync(childRunId);

        (await LoadRunAsync(childRunId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await LoadMapInputSensitivityAsync(childRunId)).ShouldBe("SecretDerived", "a map over an inherited secret stores no frozen array");
        await AssertNoCanaryAsync(canaries, "after the child mapped over its inherited secrets", parentRunId, childRunId);
    }

    [Fact]
    public async Task A_replay_of_a_sealed_child_runs_with_the_exact_inputs_and_stays_sealed()
    {
        var canaries = await SeedSecretsAsync();
        var childWorkflowId = await CreateWorkflowAsync(canaries, ChildDefinition(terminalInputs: """{ "status": "ok" }"""));
        var parentWorkflowId = await CreateWorkflowAsync(canaries, ParentDefinition(childWorkflowId));
        await SetWorkflowSecretAsync(canaries, parentWorkflowId);

        using var manual = ResolveJobClient().ManualExecution();
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentWorkflowId, canaries.TeamId);
        await RunEngineAsync(parentRunId);
        var childRunId = await LoadChildRunIdAsync(parentRunId);
        await RunEngineAsync(childRunId);

        var replayRunId = await ReplayAsync(canaries, childRunId);
        await RunEngineAsync(replayRunId);

        (await LoadRunAsync(replayRunId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await LoadRunRequestAsync(replayRunId)).SealedPayloadCiphertext.ShouldNotBeNull("a fork carries the sealed copy over, never just the masked column");
        (await ReadSealedNodeOutputsAsync(replayRunId, "emit"))["w"].GetString().ShouldBe(canaries.Workflow, "the replay re-runs with the EXACT inherited value, not the mask");
        await AssertNoCanaryAsync(canaries, "after the replay", childRunId, replayRunId);
    }

    [Fact]
    public async Task A_supervisors_resolved_goal_is_masked_in_the_plan_a_viewer_reads()
    {
        // agent.supervisor reads its goal from the RESOLVED config exactly like agent.run, and its plan row takes that
        // goal (or the model restating it) verbatim. The row is a projection for humans — the turn executes from its
        // tape — so it is masked with the node scope's secrets.
        var canaries = await SeedSecretsAsync();
        using (var script = _fixture.BeginScope()) script.Resolve<SupervisorDecisionScript>().PlanThenStop();
        var workflowId = await CreateWorkflowAsync(canaries, SupervisorDefinition("""{ "goal": "Ship the release with {{team.DEPLOY_TOKEN}}" }"""));

        using var manual = ResolveJobClient().ManualExecution();
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, canaries.TeamId);
        await RunEngineAsync(runId);

        var goal = await LoadWorkPlanGoalAsync(runId);
        goal.ShouldNotContain(canaries.Team);
        goal.ShouldBe($"Ship the release with {PersistenceSecretRedactor.Marker}");

        using var viewer = _fixture.BeginScopeAs(canaries.ViewerId, canaries.TeamId);
        JsonSerializer.Serialize(await viewer.Resolve<IMediator>().Send(new GetRunWorkPlanQuery { RunId = runId })).ShouldNotContain(canaries.Team);
    }

    [Fact]
    public async Task An_agent_run_staged_before_sealing_still_executes_from_its_public_task()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new GoalCapturingFakeCli();
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        const string legacyGoal = "Summarize the release notes";

        Guid agentRunId;
        using (var scope = await WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId))
            agentRunId = (await scope.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = legacyGoal, Harness = "codex-cli", TimeoutSeconds = 300 }, teamId, null, null, iterationKey: "", CancellationToken.None)).Id;

        (await LoadAgentRunAsync(agentRunId)).SealedTaskCiphertext.ShouldBeNull("a task embedding no secret is stored exactly as every pre-sealing row was");

        await ExecuteAgentAsync(agentRunId);

        (await LoadAgentRunAsync(agentRunId)).Status.ShouldBe(AgentRunStatus.Succeeded);
        cli.CapturedGoals.ShouldContain(legacyGoal, customMessage: "an unsealed row launches from task_jsonb, unchanged");
    }

    [Theory]
    [InlineData(true)]    // approved: the review.completed beat carries the verdict's words
    [InlineData(false)]   // flagged: so do the warning event and the result's reviewFeedback
    public async Task An_output_reviewer_quoting_the_goal_leaves_its_secrets_in_no_verdict_sink(bool approve)
    {
        // The critic is shown the EXACT goal, and a model restates the task it judges. Its call's capture is masked by
        // the critic scope; the executor then writes the verdict's words into the ledger, the timeline and the result
        // after that scope is gone, so the verdict itself must be masked.
        if (OperatingSystem.IsWindows()) return;

        using var cli = new GoalCapturingFakeCli();
        var canaries = await SeedSecretsAsync();
        var (_, reviewerModelId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, canaries.TeamId, "critic-model", provider: DeterministicCriticLlmClient.ProviderTag);
        var goal = $"{SecretGoal} {DeterministicCriticLlmClient.QuoteGoalMarker}" + (approve ? "" : $" {DeterministicCriticLlmClient.RejectMarker}");
        var workflowId = await CreateWorkflowAsync(canaries, AgentGoalDefinition(goal: goal, review: $", \"outputReviewMode\": 1, \"reviewerModelId\": \"{reviewerModelId}\""));
        await SetWorkflowSecretAsync(canaries, workflowId);
        var script = ResolveCriticScript();
        script.Reset();

        using var manual = ResolveJobClient().ManualExecution();
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, canaries.TeamId);
        await RunEngineAsync(runId);
        var agentRunId = (await LoadStagedAgentRunAsync(runId)).Id;

        await ExecuteAgentAsync(agentRunId);

        var quoted = script.Quoted.ShouldHaveSingleItem("the reviewer ran once and quoted the goal it was shown");
        foreach (var canary in canaries.All) quoted.ShouldContain(canary, customMessage: "the reviewer was shown the EXACT goal, so its verdict carried the real values");
        (await LoadAgentRunAsync(agentRunId)).Status.ShouldBe(approve ? AgentRunStatus.Succeeded : AgentRunStatus.NeedsReview);
        (await ViewerReadsAsync(canaries, runId, agentRunId)).ShouldContain("The task was: ", customMessage: "the verdict's words still reach the Viewer — masked, not dropped");
        await AssertNoCanaryAsync(canaries, "after the reviewer quoted the goal into its verdict", runId, agentRunId);
    }

    [Theory]
    [InlineData("test '{{team.DEPLOY_TOKEN}}' != '[REDACTED]'", true)]                          // passes ONLY with the exact value
    [InlineData("test -n '{{team.DEPLOY_TOKEN}}' && exit 1", false)]                             // prints nothing: the argv line alone carries it
    [InlineData("echo calling with Authorization: Bearer {{team.DEPLOY_TOKEN}}; exit 1", false)]  // echoes it into the stdout tail
    public async Task An_acceptance_argv_carrying_a_secret_grades_with_it_and_keeps_only_the_mask(string check, bool passes)
    {
        // The grader's evidence repeats the argv and both output tails, then is stored as a team artifact a Viewer can
        // download and clipped onto the result. The task is sealed, so the argv runs with the real value; the evidence
        // must keep only the mask.
        if (OperatingSystem.IsWindows()) return;

        using var cli = new GoalCapturingFakeCli();
        var canaries = await SeedSecretsAsync();
        var workflowId = await CreateWorkflowAsync(canaries, AgentGoalDefinition(acceptance: $$"""{ "kind": "TestsPass", "command": ["sh", "-c", "{{check}}"] }"""));
        await SetWorkflowSecretAsync(canaries, workflowId);

        using var manual = ResolveJobClient().ManualExecution();
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, canaries.TeamId);
        await RunEngineAsync(runId);
        var agentRunId = (await LoadStagedAgentRunAsync(runId)).Id;

        await ExecuteAgentAsync(agentRunId);

        var finished = await LoadAgentRunAsync(agentRunId);
        finished.Status.ShouldBe(passes ? AgentRunStatus.Succeeded : AgentRunStatus.Failed, $"the check ran with the exact argv; result: {finished.ResultJson}");
        var evidenceId = JsonDocument.Parse(finished.ResultJson!).RootElement.GetProperty("acceptanceEvidenceId").GetGuid();

        using (var viewer = _fixture.BeginScopeAs(canaries.ViewerId, canaries.TeamId))
        {
            var evidence = System.Text.Encoding.UTF8.GetString((await viewer.Resolve<IMediator>().Send(new CodeSpace.Messages.Queries.Artifacts.GetArtifactQuery { ArtifactId = evidenceId })).ShouldNotBeNull().Bytes);
            evidence.ShouldContain("$ sh -c ", customMessage: "the evidence is still there for the Viewer");
            evidence.ShouldContain(PersistenceSecretRedactor.Marker, customMessage: "the argv line keeps its shape, masked");
            evidence.ShouldNotContain(canaries.Team, customMessage: "the evidence a Viewer downloads holds only the mask");
        }

        await RunEngineAsync(runId);
        await AssertNoCanaryAsync(canaries, "after the acceptance check graded and the run resumed", runId, agentRunId);
    }

    [Theory]
    [InlineData(true)]    // a map over the inherited secrets: SecretDerived, so no frozen array
    [InlineData(false)]   // a Terminal publishing an inherited secret: the leak guard refuses it
    public async Task A_child_resumed_through_its_variable_snapshot_still_treats_inherited_secrets_as_secrets(bool map)
    {
        // A plain team variable gives every run a variable snapshot, so a resume rebuilds the child's scope on the
        // REPLAY path, not the fresh one its first walk took. The nodes under test run only after the gate resumes.
        var canaries = await SeedSecretsAsync();
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IVariableService>().SetAsync(VariableScope.Team, canaries.TeamId, canaries.TeamId, "REGION", VariableValueType.String, JsonSerializer.SerializeToElement("eu-west-1"), null, canaries.OwnerId, CancellationToken.None);

        var gateWorkflowId = await CreateWorkflowAsync(canaries, PassThroughDefinition());
        var childWorkflowId = await CreateWorkflowAsync(canaries, ChildResumingDefinition(gateWorkflowId, map));
        var parentWorkflowId = await CreateWorkflowAsync(canaries, ParentDefinition(childWorkflowId, """{ "inputs": { "tokens": [ "{{wf.WF_TOKEN}}", "{{project.billing.TOKEN}}" ], "wfToken": "{{wf.WF_TOKEN}}" } }"""));
        await SetWorkflowSecretAsync(canaries, parentWorkflowId);

        using var manual = ResolveJobClient().ManualExecution();
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentWorkflowId, canaries.TeamId);
        await RunEngineAsync(parentRunId);
        var childRunId = await LoadChildRunIdAsync(parentRunId);
        await RunEngineAsync(childRunId);
        await RunEngineAsync(await LoadChildRunIdAsync(childRunId));
        await RunEngineAsync(childRunId);

        (await HasRecordAsync(childRunId, WorkflowRunRecordTypes.RunReplayed)).ShouldBeTrue("fixture check: the resumed walk rebuilt its scope on the replay path");
        var child = await LoadRunAsync(childRunId);

        if (map)
        {
            child.Status.ShouldBe(WorkflowRunStatus.Success);
            (await LoadMapInputSensitivityAsync(childRunId)).ShouldBe("SecretDerived", "a map over an inherited secret stores no frozen array on the resumed walk either");
        }
        else
        {
            child.Status.ShouldBe(WorkflowRunStatus.Failure, "the resumed walk's Terminal guard still knows the inherited key is a secret");
            child.Error.ShouldNotBeNull().ShouldContain("Terminal node 'end'");
        }

        await AssertNoCanaryAsync(canaries, "after the child resumed through its replay scope", parentRunId, childRunId);
    }

    private const string ResolvedModel = "gpt-taint-test-model";

    // ── Definitions ──────────────────────────────────────────────────────────────────────────────────────────

    private const string SecretGoal = "Deploy with bearer {{team.DEPLOY_TOKEN}}, sign with {{wf.WF_TOKEN}} and fetch with {{project.billing.TOKEN}}";

    private static WorkflowDefinition AgentGoalDefinition(string? acceptance = null, string goal = SecretGoal, string review = "") => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.run", Inputs = WorkflowsTestSeed.EmptyJson(),
                    Config = WorkflowsTestSeed.Json($$"""{ "goal": "{{goal}}", "harness": "codex-cli", "runnerKind": "local", "timeoutSeconds": 300 {{review}}""" + (acceptance is null ? "" : $", \"acceptance\": {acceptance}") + " }") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "summary": "{{nodes.agent.outputs.summary}}" }""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "agent" }, new() { From = "agent", To = "end" } },
    };

    private const string SecretInputs = """{ "inputs": { "token": "{{team.DEPLOY_TOKEN}}", "wfToken": "{{wf.WF_TOKEN}}", "projToken": "{{project.billing.TOKEN}}" } }""";

    private static WorkflowDefinition ParentDefinition(Guid childWorkflowId, string subInputs = SecretInputs) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sub", TypeKey = "flow.subworkflow", Config = WorkflowsTestSeed.Json($$"""{ "workflowId": "{{childWorkflowId}}" }"""),
                    Inputs = WorkflowsTestSeed.Json(subInputs) },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "status": "ok" }""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "sub" }, new() { From = "sub", To = "end" } },
    };

    private static WorkflowDefinition ChildDefinition(string terminalInputs) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "emit", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "t": "{{trigger.token}}", "w": "{{trigger.wfToken}}", "p": "{{trigger.projToken}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(terminalInputs) },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "emit" }, new() { From = "emit", To = "end" } },
    };

    private static WorkflowDefinition ChildMapDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.tokens}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "echo", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "value": "{{item}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "map" }, new() { From = "map", To = "end" }, new() { From = "ms", To = "echo" } },
    };

    /// <summary>A child that first parks on a sub-workflow <c>gate</c>, so everything after it runs on the RESUMED walk — whose scope a run with a variable snapshot rebuilds on the replay path — then either maps over its inherited tokens or publishes one from its Terminal.</summary>
    private static WorkflowDefinition ChildResumingDefinition(Guid gateWorkflowId, bool map) => new()
    {
        SchemaVersion = 1,
        Nodes = map
            ? new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "gate", TypeKey = "flow.subworkflow", Config = WorkflowsTestSeed.Json($$"""{ "workflowId": "{{gateWorkflowId}}" }"""), Inputs = WorkflowsTestSeed.Json("""{ "inputs": {} }""") },
                new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.tokens}}" }""") },
                new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "echo", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "value": "{{item}}" }""") },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
            }
            : new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "gate", TypeKey = "flow.subworkflow", Config = WorkflowsTestSeed.Json($$"""{ "workflowId": "{{gateWorkflowId}}" }"""), Inputs = WorkflowsTestSeed.Json("""{ "inputs": {} }""") },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "echo": "{{trigger.wfToken}}" }""") },
            },
        Edges = map
            ? new List<EdgeDefinition> { new() { From = "start", To = "gate" }, new() { From = "gate", To = "map" }, new() { From = "map", To = "end" }, new() { From = "ms", To = "echo" } }
            : new List<EdgeDefinition> { new() { From = "start", To = "gate" }, new() { From = "gate", To = "end" } },
    };

    private static WorkflowDefinition PassThroughDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "status": "ok" }""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
    };

    private static WorkflowDefinition SupervisorDefinition(string supervisorConfig) => new()
    {
        SchemaVersion = 1,
        CompletionMode = WorkflowDefinition.CompletionModeShadow,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(supervisorConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "sup" }, new() { From = "sup", To = "end" } },
    };

    // ── Seeding ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One unique canary per secret scope a goal or an input can reference, plus a Viewer who must never read any of them.</summary>
    private sealed record Canaries(Guid TeamId, Guid OwnerId, Guid ViewerId, string Team, string Workflow, string Project)
    {
        public IReadOnlyList<string> All => [Team, Workflow, Project];
    }

    private async Task<Canaries> SeedSecretsAsync()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var canaries = new Canaries(teamId, ownerId, await SeedViewerAsync(teamId), Canary("team"), Canary("wf"), Canary("project"));

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IVariableService>().SetAsync(VariableScope.Team, teamId, teamId, "DEPLOY_TOKEN", VariableValueType.Secret, JsonSerializer.SerializeToElement(canaries.Team), null, ownerId, CancellationToken.None);

        using (var scope = _fixture.BeginScopeAs(ownerId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            var projectId = await mediator.Send(new CreateProjectCommand { Name = "Billing" });
            await mediator.Send(new SetProjectVariableCommand { ProjectId = projectId, Name = "TOKEN", ValueType = VariableValueType.Secret, Value = JsonSerializer.SerializeToElement(canaries.Project) });
        }

        return canaries;
    }

    private async Task SetWorkflowSecretAsync(Canaries canaries, Guid workflowId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IVariableService>().SetAsync(VariableScope.Workflow, workflowId, canaries.TeamId, "WF_TOKEN", VariableValueType.Secret, JsonSerializer.SerializeToElement(canaries.Workflow), null, canaries.OwnerId, CancellationToken.None);
    }

    private static string Canary(string scope) => $"CANARY-taint-{scope}-{Guid.NewGuid():N}";

    private async Task<Guid> SeedViewerAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var viewerId = Guid.NewGuid();

        db.User.Add(new User { Id = viewerId, Email = $"viewer-{viewerId:N}@test.local", Name = $"viewer-{viewerId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = viewerId, Role = TeamRole.Viewer, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();

        return viewerId;
    }

    private async Task<Guid> CreateWorkflowAsync(Canaries canaries, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(canaries.OwnerId, canaries.TeamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "taint-" + Guid.NewGuid().ToString("N")[..8], Description = null, Definition = definition,
            Activations = new List<WorkflowActivationInput>(), Enabled = true,
        });
    }

    // ── Driving ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task ExecuteAgentAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(agentRunId, CancellationToken.None);
    }

    private async Task<Guid> ReplayAsync(Canaries canaries, Guid runId)
    {
        using var scope = _fixture.BeginScopeAs(canaries.OwnerId, canaries.TeamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ReplayRunCommand { OriginalRunId = runId });
    }

    private CriticReviewScript ResolveCriticScript()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<CriticReviewScript>();
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(run => run.Id == runId);
    }

    private async Task<WorkflowRunRequest> LoadRunRequestAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = await db.WorkflowRun.AsNoTracking().Where(run => run.Id == runId).Select(run => run.RunRequestId).SingleAsync();

        return await db.WorkflowRunRequest.AsNoTracking().SingleAsync(request => request.Id == requestId);
    }

    private async Task<AgentRun> LoadAgentRunAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(run => run.Id == agentRunId);
    }

    private async Task<AgentRun> LoadStagedAgentRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(run => run.WorkflowRunId == runId);
    }

    private async Task<string> LoadWorkPlanGoalAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkPlan.AsNoTracking().Where(plan => plan.WorkflowRunId == runId).OrderByDescending(plan => plan.Version).Select(plan => plan.Goal).FirstAsync();
    }

    private async Task<string> LoadMapInputSensitivityAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunMapInput.AsNoTracking().Where(input => input.RunId == runId).Select(input => input.Sensitivity).SingleAsync();
    }

    private async Task<Guid> LoadChildRunIdAsync(Guid parentRunId)
    {
        using var scope = _fixture.BeginScope();
        var token = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(wait => wait.RunId == parentRunId && wait.WaitKind == WorkflowWaitKinds.Subworkflow).Select(wait => wait.Token).SingleAsync();

        return Guid.Parse(token);
    }

    /// <summary>The exact outputs a node produced, from the encrypted recovery sidecar the engine writes ONLY when it masked the public copy — so finding the canary here proves both that the node ran with the real value and that its ledger row was masked.</summary>
    private async Task<IReadOnlyDictionary<string, JsonElement>> ReadSealedNodeOutputsAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(candidate => candidate.Id == runId);
        var recordId = await db.WorkflowRunRecord.AsNoTracking().Where(record => record.RunId == runId && record.NodeId == nodeId && record.RecordType == WorkflowRunRecordTypes.NodeCompleted).Select(record => record.Id).SingleAsync();

        return (await scope.Resolve<IWorkflowSensitivePayloadStore>().ReadNodeOutputsAsync(recordId, runId, run.TeamId, CancellationToken.None)).ShouldNotBeNull("the node's outputs carried a secret, so its public row was masked and the exact copy sealed beside it");
    }

    private async Task<CodeSpace.Messages.Dtos.Agents.AgentRunSummary?> ViewerAgentRunAsync(Canaries canaries, Guid agentRunId)
    {
        using var viewer = _fixture.BeginScopeAs(canaries.ViewerId, canaries.TeamId);
        return await viewer.Resolve<IMediator>().Send(new GetAgentRunQuery { AgentRunId = agentRunId });
    }

    /// <summary>Everything a Viewer can read about these runs, serialized — the run detail (pending wait + request payload included), its ledger and timeline, the pending-wait observation, and for an agent run its summary and event log.</summary>
    private async Task<string> ViewerReadsAsync(Canaries canaries, Guid runId, Guid? agentRunId)
    {
        using var viewer = _fixture.BeginScopeAs(canaries.ViewerId, canaries.TeamId);
        var mediator = viewer.Resolve<IMediator>();
        var reads = new List<object?>
        {
            await mediator.Send(new GetWorkflowRunQuery { RunId = runId }),
            await mediator.Send(new GetWorkflowRunPendingWaitObservationQuery { RunId = runId }),
            await mediator.Send(new GetRunRecordsQuery { RunId = runId }),
            await mediator.Send(new GetRunTimelineQuery { RunId = runId }),
        };

        if (agentRunId is { } id)
        {
            reads.Add(await mediator.Send(new GetAgentRunQuery { AgentRunId = id }));
            reads.Add(await mediator.Send(new ListAgentRunEventsQuery { AgentRunId = id }));
        }

        return JsonSerializer.Serialize(reads);
    }

    /// <summary>
    /// No canary anywhere at rest (every table, every artifact of the team) and in nothing a Viewer reads about either
    /// run. <paramref name="otherRunId"/> is the second run of the pair — the child, or the agent run.
    /// </summary>
    private async Task AssertNoCanaryAsync(Canaries canaries, string moment, Guid runId, Guid otherRunId)
    {
        var agentRunId = await IsAgentRunAsync(otherRunId) ? otherRunId : (Guid?)null;
        var viewerReads = await ViewerReadsAsync(canaries, runId, agentRunId) + (agentRunId is null ? await ViewerReadsAsync(canaries, otherRunId, null) : "");

        foreach (var canary in canaries.All)
        {
            (await SecretCanarySweep.FindAsync(_fixture, canaries.TeamId, canary)).ShouldBeEmpty($"{moment}: a resolved secret froze into a table or artifact");
            viewerReads.ShouldNotContain(canary, customMessage: $"{moment}: a Viewer read a resolved secret");
        }
    }

    private async Task<bool> HasRecordAsync(Guid runId, string recordType)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().AnyAsync(record => record.RunId == runId && record.RecordType == recordType);
    }

    private async Task<bool> IsAgentRunAsync(Guid id)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().AnyAsync(run => run.Id == id);
    }
}
