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
    public async Task Related_repositories_input_projects_a_multi_repo_workspace()
    {
        var web = Guid.NewGuid();
        var api = Guid.NewGuid();
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = Str(web.ToString()),
            ["relatedRepositories"] = JsonSerializer.SerializeToElement(new[] { new { repositoryId = api.ToString(), alias = "api", access = "write" } }),
        };

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;

        task.RepositoryId.ShouldBe(web, "the primary stays on the legacy field too");
        task.Workspace.ShouldNotBeNull();
        task.Workspace!.Repositories.Count.ShouldBe(2);

        var primary = task.Workspace.Repositories.Single(r => r.IsPrimary);
        primary.RepositoryId.ShouldBe(web);
        primary.Access.ShouldBe(WorkspaceAccess.Write);

        var related = task.Workspace.Repositories.Single(r => !r.IsPrimary);
        related.RepositoryId.ShouldBe(api);
        related.Alias.ShouldBe("api");
        related.Access.ShouldBe(WorkspaceAccess.Write);
    }

    [Fact]
    public async Task No_related_repositories_keeps_the_workspace_null_byte_identical()
    {
        var inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = Str(Guid.NewGuid().ToString()) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.Workspace
            .ShouldBeNull("no related repos → null Workspace → the resolver derives the single-repo workspace (byte-identical)");
    }

    [Fact]
    public async Task Related_repositories_without_a_primary_repository_fails_the_node()
    {
        // Fail loud rather than silently drop the authored multi-repo intent (e.g. an expression-bound primary that
        // resolved empty at runtime). Without a primary the workspace has nowhere to anchor.
        var inputs = new Dictionary<string, JsonElement>
        {
            ["relatedRepositories"] = JsonSerializer.SerializeToElement(new[] { new { repositoryId = Guid.NewGuid().ToString(), access = "write" } }),
        };

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("relatedRepositories");
        result.Error.ShouldContain("primary");
    }

    [Fact]
    public async Task Related_repositories_default_to_read_and_skip_a_malformed_entry()
    {
        var web = Guid.NewGuid();
        var api = Guid.NewGuid();
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = Str(web.ToString()),
            // api: no access → defaults to Read. Plus a non-object + an idless entry that must be SKIPPED, not throw.
            ["relatedRepositories"] = JsonSerializer.SerializeToElement(new object[]
            {
                new { repositoryId = api.ToString() },
                "not-an-object",
                new { alias = "ghost" },   // no repositoryId → skipped
            }),
        };

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Workspace!.Repositories.Count.ShouldBe(2, "only the primary + the one well-formed related repo (malformed entries skipped)");
        task.Workspace.Repositories.Single(r => !r.IsPrimary).Access.ShouldBe(WorkspaceAccess.Read, "a related repo with no authored access defaults to read-only context");
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
    public async Task Tools_config_is_carried_onto_the_task_and_absent_tools_stay_null()
    {
        var withTools = new Dictionary<string, JsonElement>
        {
            ["goal"] = Str("Fix it"), ["harness"] = Str("codex-cli"),
            ["tools"] = JsonSerializer.SerializeToElement(new[] { "Read", "Grep" }),
        };
        var withoutTools = new Dictionary<string, JsonElement> { ["goal"] = Str("Fix it"), ["harness"] = Str("codex-cli") };

        var a = await new AgentCodeNode().RunAsync(BuildContext(withTools, resume: null), CancellationToken.None);
        var b = await new AgentCodeNode().RunAsync(BuildContext(withoutTools, resume: null), CancellationToken.None);

        JsonSerializer.Deserialize<AgentTask>(a.SuspendUntil!.Payload, AgentJson.Options)!.Tools.ShouldBe(new[] { "Read", "Grep" });
        JsonSerializer.Deserialize<AgentTask>(b.SuspendUntil!.Payload, AgentJson.Options)!.Tools
            .ShouldBeNull("no tools config → null = inherit the harness default (the resolver/harness decide), not an empty list");
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
    public async Task Model_credential_id_node_override_is_carried_onto_the_task_as_a_reference()
    {
        var credId = Guid.NewGuid();
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("Fix it"), ["harness"] = Str("codex-cli"), ["modelCredentialId"] = Str(credId.ToString()) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.ModelCredentialId.ShouldBe(credId);
        // The staged envelope carries only the REFERENCE — never a secret. The key is decrypted + injected at execution.
        task.Environment.ShouldBeEmpty("staging freezes a credential reference, never the key or any injected env");
    }

    [Fact]
    public async Task No_model_credential_id_leaves_the_task_to_fall_back_at_resolve_time()
    {
        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null), CancellationToken.None);

        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.ModelCredentialId
            .ShouldBeNull("no node override → the resolver fills the persona default, else a team/operator key");
    }

    [Fact]
    public async Task Malformed_model_credential_id_fails_the_node()
    {
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("Fix it"), ["harness"] = Str("codex-cli"), ["modelCredentialId"] = Str("not-a-uuid") };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("modelCredentialId");
    }

    // ── approvalConversationId (the MCP tool-approval surface reference) ──────────

    [Fact]
    public async Task Approval_conversation_id_is_carried_onto_the_task_from_config()
    {
        var conversationId = Guid.NewGuid();
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("Fix it"), ["harness"] = Str("codex-cli"), ["approvalConversationId"] = Str(conversationId.ToString()) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.ApprovalConversationId.ShouldBe(conversationId);
    }

    [Fact]
    public async Task A_picked_credentialed_model_is_carried_onto_the_task_as_a_reference()
    {
        var modelRowId = Guid.NewGuid();
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("Fix it"), ["harness"] = Str("codex-cli"), ["modelCredentialModelId"] = Str(modelRowId.ToString()) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        // The node stays PURE — it carries the reference; the dispatch-time resolver expands it into model + credential.
        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.ModelCredentialModelId.ShouldBe(modelRowId);
    }

    [Fact]
    public async Task A_malformed_credentialed_model_id_fails_the_node()
    {
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("Fix it"), ["harness"] = Str("codex-cli"), ["modelCredentialModelId"] = Str("not-a-uuid") };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("modelCredentialModelId");
    }

    [Fact]
    public async Task An_unset_credentialed_model_is_omitted_from_the_staged_task_json()
    {
        // Byte-identity: [JsonIgnore(WhenWritingNull)] keeps an unset reference OUT of the persisted task_json, so an
        // existing run's envelope is unchanged by the new field.
        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        result.SuspendUntil!.Payload.GetRawText().ShouldNotContain("modelCredentialModelId");
    }

    [Theory]
    [InlineData(null)]          // absent entirely → no approval surface
    [InlineData("")]            // picked then cleared
    [InlineData("not-a-uuid")]  // malformed → optional config, degrades to null (NOT a node failure)
    public async Task No_or_malformed_approval_conversation_id_is_null_and_never_fails_the_node(string? raw)
    {
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("Fix it"), ["harness"] = Str("codex-cli") };
        if (raw is not null) config["approvalConversationId"] = Str(raw);

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended, "approvalConversationId is optional config, not a safety-critical input — a missing/malformed value degrades to null rather than failing the node");
        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.ApprovalConversationId.ShouldBeNull();
    }

    [Fact]
    public void Approval_conversation_id_round_trips_through_the_suspend_payload_json()
    {
        // The task envelope is the persisted suspend payload re-read on reattach — the reference must survive serialize→deserialize.
        var conversationId = Guid.NewGuid();
        var task = new AgentTask { Goal = "g", Harness = "codex-cli", ApprovalConversationId = conversationId };

        var json = JsonSerializer.SerializeToElement(task, AgentJson.Options);
        var roundTripped = JsonSerializer.Deserialize<AgentTask>(json, AgentJson.Options)!;

        roundTripped.ApprovalConversationId.ShouldBe(conversationId);
    }

    [Fact]
    public void A_task_with_no_approval_conversation_id_round_trips_as_null()
    {
        var task = new AgentTask { Goal = "g", Harness = "codex-cli" };

        var roundTripped = JsonSerializer.Deserialize<AgentTask>(JsonSerializer.SerializeToElement(task, AgentJson.Options), AgentJson.Options)!;

        roundTripped.ApprovalConversationId.ShouldBeNull();
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

    [Fact]
    public async Task Resumed_multi_repo_run_surfaces_the_change_set_outputs()
    {
        // A multi-repo run's resume payload carries repositoryResults + changeSetId; the node surfaces them so a
        // downstream git.open_change_set can bind the per-repo branches.
        var resume = JsonDocument.Parse("""
            {"status":"Succeeded","summary":"Coordinated change.","changedFiles":["web.txt"],"branch":"codespace/run-x","changeSetId":"cs-abc",
             "repositoryResults":[
               {"alias":"web","repositoryId":"11111111-1111-1111-1111-111111111111","producedBranch":"codespace/run-x","baseSha":"base-web"},
               {"alias":"api","repositoryId":"22222222-2222-2222-2222-222222222222","producedBranch":"codespace/run-x","baseSha":"base-api"}]}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(new(), resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["changeSetId"].GetString().ShouldBe("cs-abc");
        result.Outputs["repositoryResults"].GetArrayLength().ShouldBe(2);
        result.Outputs["repositoryResults"][0].GetProperty("alias").GetString().ShouldBe("web");
        result.Outputs["repositoryResults"][0].GetProperty("producedBranch").GetString().ShouldBe("codespace/run-x");
    }

    [Fact]
    public async Task Resumed_single_repo_run_omits_the_change_set_outputs()
    {
        // The ACTUAL single-repo resume payload the notifier builds carries an EMPTY repositoryResults array + a null
        // changeSetId (AgentRunResult defaults them so). The node must NOT add those keys — the single-repo output bag
        // stays byte-identical (no repositoryResults / changeSetId).
        var resume = JsonDocument.Parse("""
            {"status":"Succeeded","summary":"Done.","changedFiles":["a.ts"],"branch":"agent/x","repositoryResults":[],"changeSetId":null}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(new(), resume), CancellationToken.None);

        result.Outputs.ContainsKey("repositoryResults").ShouldBeFalse("an empty change set must not add a repositoryResults key");
        result.Outputs.ContainsKey("changeSetId").ShouldBeFalse("a null change-set id must not add a changeSetId key");
    }

    // ── Autonomy tier → permissions ─────────────────────────────────────────────

    [Theory]
    [InlineData("Confined", AgentNetworkAccess.Off, AgentWriteScope.ReadOnly)]
    [InlineData("Standard", AgentNetworkAccess.Off, AgentWriteScope.Workspace)]
    [InlineData("Trusted", AgentNetworkAccess.On, AgentWriteScope.Workspace)]
    [InlineData("Unleashed", AgentNetworkAccess.On, AgentWriteScope.Workspace)]
    public async Task Autonomy_level_derives_the_permissions_and_is_carried_as_provenance(string level, AgentNetworkAccess network, AgentWriteScope writeScope)
    {
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["autonomyLevel"] = Str(level) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Autonomy.ShouldBe(Enum.Parse<AgentAutonomyLevel>(level), "the chosen tier is carried as provenance");
        task.Permissions.Network.ShouldBe(network);
        task.Permissions.WriteScope.ShouldBe(writeScope);
    }

    [Fact]
    public async Task No_autonomy_and_no_overrides_is_standard_matching_the_historical_default()
    {
        // Regression: a pre-existing node (no autonomyLevel, no network/readOnly) must behave EXACTLY as before
        // the dial existed — Standard tier = Network Off + WriteScope Workspace.
        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Autonomy.ShouldBe(AgentAutonomyLevel.Standard);
        task.Permissions.ShouldBe(new AgentPermissions());
    }

    [Theory]
    // Override layers on top of the tier, and ONLY the overridden field changes (the other inherits the tier).
    [InlineData("Trusted", "readOnly", true, AgentNetworkAccess.On, AgentWriteScope.ReadOnly)]    // readOnly override; network inherits Trusted
    [InlineData("Confined", "network", true, AgentNetworkAccess.On, AgentWriteScope.ReadOnly)]     // network override; writeScope inherits Confined
    [InlineData("Trusted", "network", false, AgentNetworkAccess.Off, AgentWriteScope.Workspace)]   // an explicit false override still wins
    public async Task Explicit_overrides_layer_over_the_tier(string level, string overrideKey, bool overrideValue, AgentNetworkAccess network, AgentWriteScope writeScope)
    {
        var config = new Dictionary<string, JsonElement>
        {
            ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["autonomyLevel"] = Str(level), [overrideKey] = Bool(overrideValue),
        };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Permissions.Network.ShouldBe(network);
        task.Permissions.WriteScope.ShouldBe(writeScope);
    }

    [Fact]
    public async Task Legacy_network_readonly_without_a_tier_keep_their_exact_prior_meaning()
    {
        // The pre-dial config form (network/readOnly as plain booleans, no autonomyLevel) must resolve identically:
        // Standard base + the two overrides → exactly what the old inline construction produced.
        var config = new Dictionary<string, JsonElement>
        {
            ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["network"] = Bool(true), ["readOnly"] = Bool(true),
        };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Permissions.Network.ShouldBe(AgentNetworkAccess.On);
        task.Permissions.WriteScope.ShouldBe(AgentWriteScope.ReadOnly);
    }

    [Fact]
    public async Task Unrecognized_autonomy_level_falls_back_to_standard()
    {
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["autonomyLevel"] = Str("bogus") };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Autonomy.ShouldBe(AgentAutonomyLevel.Standard, "an unknown tier degrades to the safe default, never throws");
    }

    // ── pushBranch config → the per-run push opt-in on the task ─────────────────

    [Theory]
    [InlineData(true, true)]    // explicit opt-in → carried through so the fan-out branch agent publishes its branch
    [InlineData(false, false)]  // explicit opt-out → carried as false (defers to the ambient flag at the executor gate)
    public async Task PushBranch_config_is_carried_onto_the_task(bool configured, bool expected)
    {
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["pushBranch"] = Bool(configured) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.PushProducedBranch.ShouldBe(expected);
    }

    [Fact]
    public async Task No_push_branch_config_leaves_the_task_deferring_to_the_ambient_flag()
    {
        // Absent → null → the executor's ShouldPushProducedBranch defers entirely to the env flag, so an ordinary
        // authored node is byte-identical to before this knob existed.
        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null), CancellationToken.None);

        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.PushProducedBranch
            .ShouldBeNull("no pushBranch config → null = defer to the deployment-wide flag (no behaviour change)");
    }

    // ── mode (the model-authored intent) → base permissions + push, composing under the existing precedence ─────

    [Theory]
    // research → analysis-only base: ReadOnly write scope, network off, and no produced branch (push false).
    [InlineData("research", AgentNetworkAccess.Off, AgentWriteScope.ReadOnly, false)]
    // code → the tier-derived base (Standard = workspace write, no network) AND publishes its own branch (push true).
    [InlineData("code", AgentNetworkAccess.Off, AgentWriteScope.Workspace, true)]
    public async Task Mode_authors_the_base_permissions_and_push(string mode, AgentNetworkAccess network, AgentWriteScope writeScope, bool push)
    {
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["mode"] = Str(mode) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Permissions.Network.ShouldBe(network);
        task.Permissions.WriteScope.ShouldBe(writeScope);
        task.PushProducedBranch.ShouldBe(push);
    }

    [Fact]
    public async Task Mode_code_does_not_override_a_low_autonomy_tier_so_the_ceiling_clamp_still_bounds_it()
    {
        // mode=code is a BASE — it derives the tier's write posture, it never RAISES the tier. A Confined ceiling
        // (the clamp at the launch choke point) therefore still caps the agent to ReadOnly even when the model
        // authored mode=code. This is what makes mode clamp-safe.
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["mode"] = Str("code"), ["autonomyLevel"] = Str("Confined") };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Permissions.WriteScope.ShouldBe(AgentWriteScope.ReadOnly, "mode=code derives the Confined tier's read-only posture — it never lifts the autonomy ceiling");
    }

    [Fact]
    public async Task Explicit_read_only_override_wins_over_mode_code()
    {
        // The explicit per-field override is the HIGHEST precedence layer — it must override the mode=code base.
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["mode"] = Str("code"), ["readOnly"] = Bool(true) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.Permissions.WriteScope
            .ShouldBe(AgentWriteScope.ReadOnly, "an explicit readOnly override is the top precedence layer — it wins over the mode base");
    }

    [Fact]
    public async Task Explicit_push_branch_override_wins_over_mode_research()
    {
        // mode=research's base is push=false, but an explicit pushBranch=true is the higher precedence layer.
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["mode"] = Str("research"), ["pushBranch"] = Bool(true) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.PushProducedBranch
            .ShouldBe(true, "an explicit pushBranch override wins over the mode=research base");
    }

    [Fact]
    public async Task Absent_mode_is_byte_identical_to_today_deferring_to_the_tier_and_the_push_flag()
    {
        // Regression pin: a node with no mode resolves EXACTLY as before this knob existed — Standard tier base
        // (Workspace write, no network) and null push (defer to the deployment flag).
        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Permissions.ShouldBe(new AgentPermissions(), "no mode → the tier-derived baseline, byte-identical to before the mode knob existed");
        task.PushProducedBranch.ShouldBeNull("no mode and no explicit pushBranch → null = defer to the deployment-wide flag");
    }

    [Fact]
    public async Task Unrecognized_mode_degrades_to_unset_and_never_throws()
    {
        // An unknown mode value (the responseSchema enum is the hard bound; the prompt is soft) degrades to the
        // safe Unset default — today's behaviour — rather than failing the node, mirroring ReadAutonomyLevel.
        var config = new Dictionary<string, JsonElement> { ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["mode"] = Str("bogus") };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended, "an unknown mode degrades to Unset (today's behaviour), never throws");
        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Permissions.ShouldBe(new AgentPermissions());
        task.PushProducedBranch.ShouldBeNull();
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
