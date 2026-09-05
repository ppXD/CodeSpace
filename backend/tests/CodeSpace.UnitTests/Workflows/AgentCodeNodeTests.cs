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
    public void Type_key_is_agent_code() => new AgentCodeNode().TypeKey.ShouldBe("agent.run");

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

    [Theory]
    [InlineData(900, 900)]   // a positive value is the wall-clock cap, in seconds
    [InlineData(0, null)]    // explicit 0 ⇒ NO wall-clock (infinite — the operator's "no timeout" choice)
    [InlineData(-1, null)]   // explicit negative ⇒ infinite too
    public async Task TimeoutSeconds_maps_positive_to_a_cap_and_non_positive_to_infinite(int configured, int? expected)
    {
        var config = new Dictionary<string, JsonElement>(RequiredConfig()) { ["timeoutSeconds"] = Num(configured) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);
        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options);

        task!.TimeoutSeconds.ShouldBe(expected);
    }

    [Fact]
    public async Task TimeoutSeconds_absent_falls_back_to_the_bounded_1h_default_never_infinite()
    {
        // An UNSET config must never be unbounded — only an EXPLICIT non-positive value is infinite. So a launch that
        // doesn't touch the timeout (or a programmatic/supervisor agent) stays bounded at the 1h default.
        var config = new Dictionary<string, JsonElement>(RequiredConfig());
        config.Remove("timeoutSeconds");

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);
        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options);

        task!.TimeoutSeconds.ShouldBe(3600);
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
    public async Task Base_ref_input_clones_the_single_repo_at_that_ref()
    {
        // Session branch continuity (S4b): a baseRef input pins the prior turn's produced branch — a single-repo run
        // then gets an explicit workspace whose primary Ref is that branch, so the agent starts on earlier work
        // (the workspace provider clones --branch <ref>; absent baseRef stays the null-workspace default-branch path).
        var repo = Guid.NewGuid();
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = Str(repo.ToString()),
            ["baseRef"] = Str("run-1/x"),
        };

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;

        task.Workspace.ShouldNotBeNull("a pinned baseRef needs an explicit workspace so the clone uses it");
        var primary = task.Workspace!.Repositories.Single();
        primary.RepositoryId.ShouldBe(repo);
        primary.Ref.ShouldBe("run-1/x", "the agent clones the primary repo at the prior turn's branch");
        primary.RefSoftFallback.ShouldBeFalse("a baseRef with NO baseRefFromSession marker is HARD — an author-pinned ref fails loud if gone, never silently rewritten");
    }

    [Fact]
    public async Task A_session_base_ref_marks_the_primary_soft_so_a_pruned_branch_falls_back()
    {
        // Correction-4: when the launch projection set baseRef from the SESSION base-refs map, it also sets
        // baseRefFromSession:true → the primary's ref is SOFT, so a pruned prior branch falls back to the default.
        var repo = Guid.NewGuid();
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = Str(repo.ToString()),
            ["baseRef"] = Str("run-1/x"),
            ["baseRefFromSession"] = JsonSerializer.SerializeToElement(true),
        };

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        var primary = task.Workspace!.Repositories.Single();
        primary.Ref.ShouldBe("run-1/x");
        primary.RefSoftFallback.ShouldBeTrue("a session-inherited baseRef is SOFT — the clone falls back to the default branch if the prior branch was pruned");
    }

    [Fact]
    public async Task Related_repository_ref_input_clones_that_repo_at_its_own_branch()
    {
        // Session per-repo branch continuity (S4b-2): a relatedRepositories[].ref input pins the prior turn's produced
        // branch for THAT repo — the node folds it onto the related spec's Ref so the agent clones each repo at its own
        // branch (the consumer half of the primary baseRef round-trip above; no cross-repo bleed).
        var web = Guid.NewGuid();
        var api = Guid.NewGuid();
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = Str(web.ToString()),
            ["baseRef"] = Str("run-1/web"),
            ["relatedRepositories"] = JsonSerializer.SerializeToElement(new[]
            {
                new { repositoryId = api.ToString(), alias = "api", access = "write", @ref = "run-1/api" },
            }),
        };

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;

        task.Workspace!.Repositories.Single(r => r.IsPrimary).Ref.ShouldBe("run-1/web", "the primary clones at its own prior branch");
        task.Workspace.Repositories.Single(r => r.RepositoryId == api).Ref.ShouldBe("run-1/api", "the related repo clones at ITS own prior branch — per-repo continuity end-to-end through the node");
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
        task.Goal.ShouldBe("", "the node carries the raw (empty) goal — the resolver routes the persona to its own SystemPrompt channel at dispatch");
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
    public async Task A_folded_under_claim_maps_exactly_like_a_normal_success()
    {
        // D4b: the executor folds a self-reported FAILURE whose acceptance check PASSED to Succeeded, keeping the
        // agent's own error + non-zero exit reason for the journal. The node must read the STATUS, not the leftovers
        // of the claim — otherwise the objectively-delivered work fails the node (and burns a retry) anyway.
        var resume = JsonDocument.Parse("""
            {"status":"Succeeded","error":"I could not finish the task.","exitReason":"non-zero-exit","contradiction":"under_claim","acceptancePassed":true,"summary":"Gave up before verifying.","changedFiles":["src/a.ts"],"branch":"agent/fix-billing"}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(new(), resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "the objective check passed — the node binds the work like any other success, never a retryable failure");
        result.Error.ShouldBeNull("the agent's own failure text must not leak into the node's error on a folded success");
        result.Outputs["status"].GetString().ShouldBe("Succeeded");
        result.Outputs["branch"].GetString().ShouldBe("agent/fix-billing");
        result.Outputs["changedFiles"].GetArrayLength().ShouldBe(1);
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

    [Theory]
    [InlineData("Failed", null, true)]                  // a crashed / non-zero-exit agent — a fresh respawn may survive
    [InlineData("Failed", "non-zero-exit", true)]       // an ordinary harness death — transient candidate
    [InlineData("TimedOut", null, true)]                // a wall-clock kill — transient by nature
    [InlineData("NeedsReview", null, false)]            // human-owed verdict — a respawn cannot change it
    [InlineData("NeedsReview", "output-flagged", false)] // the critic disapproved — that IS the verdict, a respawn cannot argue with it
    [InlineData("NeedsReview", "stalled", true)]         // the IDLE watchdog killed a silent process — the same kind of fact as its wall-clock sibling above, which has always retried
    [InlineData("Cancelled", null, false)]              // the user's own stop — never override it with a respawn
    [InlineData("Failed", "acceptance-failed", false)]  // a fail-closed verdict — same code + same check would fail again
    [InlineData("Failed", "harness-reported-failure", true)]  // exit-0-but-harness-Error — a fresh respawn may survive
    [InlineData("Failed", "resource-exhausted", false)]  // a cgroup ceiling killed the subtree — a respawn runs at the SAME ceiling and dies identically
    public async Task A_resumed_failure_carries_the_retry_verdict_for_the_engine(string status, string? exitReason, bool expectedRetryable)
    {
        var resume = JsonDocument.Parse($$"""{"status":"{{status}}","error":"x"{{(exitReason is null ? "" : $@",""exitReason"":""{exitReason}""")}}}""").RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(new(), resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Retryable.ShouldBe(expectedRetryable, "the node's verdict tells the retry policy whether a fresh agent could change the outcome");
    }

    [Fact]
    public void The_two_watchdogs_are_classified_alike_because_neither_can_see_why_the_process_went_quiet()
    {
        // The asymmetry this closes, stated as the invariant rather than as two separate rows: the wall-clock
        // watchdog and the idle watchdog both terminate a process on a timer, and neither can tell an agent stuck
        // at an unanswerable prompt from one working quietly through a long build. Treating only one of them as a
        // terminal verdict cost the quick lane its whole run (no error edge) and cost a map branch every sibling's
        // finished work (default terminate mode).
        AgentAcceptanceContract.StalledExitReason.ShouldBe("stalled",
            "the producer stamps this literal and the retry verdict keys on it — a rename that touches only one side silently restores the old behaviour");
    }

    [Fact]
    public async Task P3_1_a_grader_infra_timeout_is_retryable_despite_the_fail_closed_acceptance_exit_reason()
    {
        // The grader's OWN wall-clock firing on a legitimately slow suite ("tests-timed-out") is an environment
        // fact, not a code defect — unlike a genuine "tests-failed-exit-1" verdict, it must NOT burn the retry
        // budget as if the agent's code were broken.
        var resume = JsonDocument.Parse("""
            {"status":"Failed","error":"x","exitReason":"acceptance-failed","acceptanceDetail":"tests-timed-out","changedFiles":["src/a.ts"]}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(new(), resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Retryable.ShouldBeTrue("a grader infra fault is retryable — a fresh respawn deserves the same chance a crash/timeout gets");
    }

    [Fact]
    public async Task P3_1_a_genuine_acceptance_failure_stays_non_retryable_even_with_work_present()
    {
        // A real "the tests genuinely failed" verdict is UNCHANGED by P3.1 — still deterministic, still non-retryable
        // — even though work (changedFiles) is present, because "tests-failed-exit-1" is not infra-classified.
        var resume = JsonDocument.Parse("""
            {"status":"Failed","error":"x","exitReason":"acceptance-failed","acceptanceDetail":"tests-failed-exit-1","changedFiles":["src/a.ts"]}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(new(), resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Retryable.ShouldBeFalse("the same code + the same check would fail again — a respawn cannot change a genuine verdict");
    }

    [Theory]
    [InlineData("setup-failed: npm ERR! missing script")]
    [InlineData("setup-timed-out")]
    public async Task P3_1_part_2_a_setup_command_infra_fault_is_retryable_despite_the_fail_closed_acceptance_exit_reason(string acceptanceDetail)
    {
        // The contract's OWN setup step (installing deps, a build) failing/timing out means the CHECK never ran at
        // all — the verdict was never reached, so this is infra exactly like a grader clone/timeout fault, not a
        // genuine "the tests failed" verdict. A fresh respawn deserves the same chance a crash/timeout gets.
        var resume = JsonDocument.Parse($$"""
            {"status":"Failed","error":"x","exitReason":"acceptance-failed","acceptanceDetail":"{{acceptanceDetail}}","changedFiles":["src/a.ts"]}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(new(), resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Retryable.ShouldBeTrue("a setup infra fault is retryable — the check itself never got a chance to run");
    }

    [Fact]
    public async Task P2_3_a_respawn_carrying_a_prior_sessionId_warm_resumes_instead_of_cold_starting()
    {
        // The engine threads the retiring resume payload forward as PriorAttemptPayload right before a retry
        // clears ResumePayload and re-runs the node fresh (WorkflowEngine.ExecuteNodeAsync). A prior attempt that
        // captured a resumable session must have that session stamped onto the FRESH AgentTask, not discarded.
        var priorAttempt = JsonDocument.Parse("""
            {"status":"Failed","error":"gateway 429","exitReason":"non-zero-exit","sessionId":"sess-123","sessionTranscript":"{\"role\":\"user\"}"}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, priorAttemptPayload: priorAttempt), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended, "a respawn still stages a fresh AgentRun wait — only its task envelope carries the resume hint");

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.ResumeFromSessionId.ShouldBe("sess-123");
        task.RestoredTranscript.ShouldBe("{\"role\":\"user\"}");
    }

    [Fact]
    public async Task D3_a_deterministic_acceptance_failure_becomes_RETRYABLE_when_a_stronger_model_exists()
    {
        // The reachability fix: a non-infra acceptance failure is deterministic (the SAME model reproduces the
        // verdict) and so was finalized with retryable:false — which meant the engine never wrote a
        // PriorAttemptPayload and the respawn half could never run. A resolved proposal naming a stronger model
        // makes the respawn a different experiment, so the failure is retryable again.
        var resume = JsonDocument.Parse("""
            {"status":"Failed","exitReason":"acceptance-failed","acceptanceDetail":"tests-failed-exit-1","changedFiles":["a.cs"],
             "proposedEscalation":{"from":"claude-haiku-4-5","to":"claude-sonnet-4-5","reason":"the prior round claimed success but its acceptance check failed (tests-failed-exit-1)"}}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Retryable.ShouldBeTrue("a stronger credentialed model exists — the respawn runs the same task on a better model, not the same experiment twice");
    }

    [Fact]
    public async Task D3_a_deterministic_acceptance_failure_stays_terminal_when_nothing_stronger_exists()
    {
        // The other arm, and the one that protects the budget: the trigger fired, the team credentialed nothing
        // above the model that just failed, so a respawn would re-burn the identical model. Terminal, as today.
        var resume = JsonDocument.Parse("""
            {"status":"Failed","exitReason":"acceptance-failed","acceptanceDetail":"tests-failed-exit-1","changedFiles":["a.cs"],
             "proposedEscalation":{"from":"claude-haiku-4-5","to":null,"reason":"the prior round claimed success but its acceptance check failed (tests-failed-exit-1)"}}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Retryable.ShouldBeFalse("a null pick means there is nothing better to try — respawning would only re-bill the same model");
    }

    [Fact]
    public async Task D3_a_respawn_carrying_a_proposal_asks_the_executor_to_re_resolve_it()
    {
        // The node has no DB, so it forwards the FLOOR and the reason, never the answer: the pool can change
        // between attempts, and the executor re-resolves at launch.
        var priorAttempt = JsonDocument.Parse("""
            {"status":"Failed","exitReason":"acceptance-failed","acceptanceDetail":"tests-failed-exit-1","changedFiles":["a.cs"],
             "proposedEscalation":{"from":"claude-haiku-4-5","to":"claude-sonnet-4-5","reason":"the prior round claimed success but its acceptance check failed (tests-failed-exit-1)"}}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, priorAttemptPayload: priorAttempt), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;

        task.Escalation.ShouldNotBeNull("the respawn must reach higher, not re-run the same model blind");
        task.Escalation!.From.ShouldBe("claude-haiku-4-5", "the tier floor travels");
        task.Escalation.To.ShouldBeNull("the answer does NOT travel — the executor re-resolves against the pool as it is at launch");
        task.Escalation.Reason.ShouldContain("claimed success");
    }

    [Theory]
    // No proposal at all — the finished attempt's evidence said nothing about its model.
    [InlineData("""{"status":"Failed","exitReason":"acceptance-failed","acceptanceDetail":"tests-failed-exit-1","changedFiles":["a.cs"]}""")]
    // A proposal that found nothing stronger: there is no model to move to, so the respawn must not claim one.
    [InlineData("""{"status":"Failed","exitReason":"acceptance-failed","changedFiles":["a.cs"],"proposedEscalation":{"from":"m","to":null,"reason":"r"}}""")]
    // Malformed / half-written proposals degrade to "no hint" rather than failing the node.
    [InlineData("""{"status":"Failed","exitReason":"acceptance-failed","changedFiles":["a.cs"],"proposedEscalation":{"to":"stronger"}}""")]
    [InlineData("""{"status":"Failed","exitReason":"acceptance-failed","changedFiles":["a.cs"],"proposedEscalation":"nonsense"}""")]
    public async Task D3_a_respawn_without_a_usable_proposal_asks_for_no_escalation(string priorAttemptJson)
    {
        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, priorAttemptPayload: JsonDocument.Parse(priorAttemptJson).RootElement), CancellationToken.None);

        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.Escalation
            .ShouldBeNull("escalation costs real money — it fires only on a resolved proposal naming a real, stronger model");
    }

    [Fact]
    public async Task P2_3_a_respawn_carrying_a_prior_transcript_artifact_ref_threads_it_through()
    {
        var artifactId = Guid.NewGuid();
        var priorAttempt = JsonDocument.Parse($$"""
            {"status":"TimedOut","error":"timed out","sessionId":"sess-456","sessionTranscriptArtifactId":"{{artifactId}}"}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, priorAttemptPayload: priorAttempt), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.ResumeFromSessionId.ShouldBe("sess-456");
        task.RestoredTranscriptArtifactId.ShouldBe(artifactId);
    }

    [Fact]
    public async Task P2_3_a_first_pass_with_no_prior_attempt_cold_starts_byte_identical()
    {
        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, priorAttemptPayload: null), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.ResumeFromSessionId.ShouldBeNull();
        task.RestoredTranscript.ShouldBeNull();
        task.RestoredTranscriptArtifactId.ShouldBeNull();
    }

    [Fact]
    public async Task P2_3_a_prior_attempt_with_no_captured_session_cold_starts_byte_identical()
    {
        // The retiring payload exists (this WAS a respawn) but the prior attempt never captured a session
        // (e.g. it died before any session-bearing event) — nothing to warm-resume from, so it cold-starts exactly
        // like a first pass, not a half-populated task.
        var priorAttempt = JsonDocument.Parse("""{"status":"Failed","error":"crashed before any output","exitReason":"non-zero-exit"}""").RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, priorAttemptPayload: priorAttempt), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.ResumeFromSessionId.ShouldBeNull();
        task.RestoredTranscript.ShouldBeNull();
        task.RestoredTranscriptArtifactId.ShouldBeNull();
    }

    // ── Retry world-state conservation: attempt N's pushed tree is attempt N+1's starting tree ──

    [Fact]
    public async Task A_respawn_clones_the_branch_the_prior_attempt_pushed_instead_of_the_default_branch()
    {
        // The conservation law the warm-resume half alone breaks: restoring the conversation while the workspace
        // re-clones the DEFAULT branch hands the agent a "warm transcript, cold tree" — it believes edits exist that
        // its sandbox does not contain. The prior attempt's PUSHED branch (the payload's `branch`, which the executor
        // only ever sets from a successful push) becomes the retry's clone ref, exactly as the supervisor lane pins
        // its retry to the prior attempt's manifest branch.
        var repoId = Guid.NewGuid();
        var inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = Str(repoId.ToString()) };
        var priorAttempt = JsonDocument.Parse("""
            {"status":"Failed","error":"timed out","exitReason":"timed-out","sessionId":"sess-1","branch":"codespace/agent/attempt-1","changedFiles":["src/a.ts"]}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs, priorAttempt), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        var primary = task.Workspace.ShouldNotBeNull("a pinned retry ref needs an EXPLICIT one-repo workspace — a null Workspace clones the default branch").Primary!;

        primary.RepositoryId.ShouldBe(repoId);
        primary.Ref.ShouldBe("codespace/agent/attempt-1", "the retry starts from the tree its own prior attempt produced");
        primary.RefSoftFallback.ShouldBeFalse("HARD like the supervisor lane's manifest pin — a vanished produced branch must fail loud, never silently re-clone the default branch");
        task.Goal.ShouldBe("Fix the tests", "the work IS present in this tree — no honest-redo hint");
    }

    [Fact]
    public async Task A_respawn_repins_each_multi_repo_sibling_to_its_own_pushed_branch()
    {
        // Multi-repo: the payload carries a per-repo produced branch, so EVERY writable repo that pushed is repinned
        // (the primary from the top-level mirror, each sibling from its own entry) — one seam, single- and multi-repo
        // identical. A read-only sibling that pushed nothing keeps its authored ref.
        var primaryId = Guid.NewGuid();
        var webId = Guid.NewGuid();
        var docsId = Guid.NewGuid();

        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = Str(primaryId.ToString()),
            ["relatedRepositories"] = JsonDocument.Parse($$"""
                [{"repositoryId":"{{webId}}","alias":"web","access":"write"},{"repositoryId":"{{docsId}}","alias":"docs","access":"read"}]
                """).RootElement,
        };

        var priorAttempt = JsonDocument.Parse($$"""
            {"status":"Failed","error":"timed out","sessionId":"sess-2","branch":"codespace/agent/api",
             "repositoryResults":[{"alias":"repo","repositoryId":"{{primaryId}}","producedBranch":"codespace/agent/api"},
                                  {"alias":"web","repositoryId":"{{webId}}","producedBranch":"codespace/agent/web"},
                                  {"alias":"docs","repositoryId":"{{docsId}}","producedBranch":null}]}
            """).RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs, priorAttempt), CancellationToken.None);

        var repos = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.Workspace!.Repositories;

        repos.Single(r => r.RepositoryId == primaryId).Ref.ShouldBe("codespace/agent/api", "the primary clones its own prior branch");
        repos.Single(r => r.RepositoryId == webId).Ref.ShouldBe("codespace/agent/web", "each sibling clones ITS OWN prior branch — never the primary's");
        repos.Single(r => r.RepositoryId == docsId).Ref.ShouldBeNull("a repo that pushed nothing keeps its authored ref (the default branch)");
    }

    [Theory]
    // Work existed but NOTHING was pushed: a failed push / a publish-policy skip leaves `branch` null, which is
    // exactly how the payload says "not pushed" — so there is no ref to point at.
    [InlineData("""{"status":"Failed","error":"push failed","sessionId":"s","changedFiles":["src/a.ts"]}""")]
    // No branch and no work at all.
    [InlineData("""{"status":"Failed","error":"crashed early","sessionId":"s"}""")]
    // A blank branch is not a ref either.
    [InlineData("""{"status":"Failed","error":"x","sessionId":"s","branch":"","repositoryResults":[]}""")]
    public async Task A_respawn_with_no_pushed_branch_keeps_the_default_clone_and_says_so_honestly(string priorAttemptJson)
    {
        // The other half of the conservation law: when the prior attempt's work was NOT preserved, the resumed
        // conversation implies edits the tree does not have — so the goal must SAY so, in the same words the
        // supervisor lane uses. Pointing at a branch that was never pushed would fail the clone outright.
        var inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = Str(Guid.NewGuid().ToString()) };

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs, JsonDocument.Parse(priorAttemptJson).RootElement), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;

        task.Workspace.ShouldBeNull("nothing to pin — the single-repo run keeps its null Workspace (the default-branch clone)");
        task.Goal.ShouldBe($"Fix the tests\n\n{AgentRetryContinuity.HonestNoContinuityHint}", "the agent is TOLD its restored conversation's changes are not in this tree");
    }

    [Fact]
    public async Task A_respawn_with_a_pushed_branch_but_no_restored_session_pins_the_tree_without_the_hint()
    {
        // World-state continuity is independent of conversation continuity (the supervisor resolves its manifest pin
        // whether or not a resumable session exists). With no restored conversation there is no false belief to
        // correct, so the honest-redo line — whose text asserts a restored conversation — must NOT fire.
        var repoId = Guid.NewGuid();
        var inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = Str(repoId.ToString()) };
        var priorAttempt = JsonDocument.Parse("""{"status":"Failed","error":"killed","branch":"codespace/agent/attempt-1"}""").RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs, priorAttempt), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;

        task.Workspace!.Primary!.Ref.ShouldBe("codespace/agent/attempt-1", "the produced tree is conserved even on a cold-start respawn");
        task.ResumeFromSessionId.ShouldBeNull();
        task.Goal.ShouldBe("Fix the tests", "no conversation was restored, so the restored-conversation hint would be a lie");
    }

    [Fact]
    public async Task A_respawn_prefers_the_prior_attempts_branch_over_the_launch_base_ref_and_drops_the_base_pin()
    {
        // The prior attempt's own produced branch is MORE specific than the launch's authored base — and the base
        // PIN must go with it: hard-checking-out the original base commit would throw away the very work this pin
        // exists to conserve. The supervisor lane's ref-wins-over-pin precedent, applied to the quick lane.
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = Str(Guid.NewGuid().ToString()),
            ["baseRef"] = Str("release/2.0"),
            ["pinnedSha"] = Str("aaaa1111bbbb2222"),
        };
        var priorAttempt = JsonDocument.Parse("""{"status":"Failed","error":"x","sessionId":"s","branch":"codespace/agent/attempt-1"}""").RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs, priorAttempt), CancellationToken.None);

        var primary = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.Workspace!.Primary!;

        primary.Ref.ShouldBe("codespace/agent/attempt-1");
        primary.PinnedSha.ShouldBeNull("the immutable base pin cannot survive a repin onto the branch built ON that base — it would discard the work");
    }

    [Fact]
    public async Task A_respawn_with_no_pushed_branch_leaves_the_launch_base_ref_and_pin_untouched()
    {
        // The negative path must not quietly widen: a retry with nothing to conserve keeps the launch's authored
        // base ref, its soft-fallback flag, its recovery anchor and its base pin exactly as the first pass had them.
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = Str(Guid.NewGuid().ToString()),
            ["baseRef"] = Str("codespace/session/prior-turn"),
            ["baseRefFromSession"] = Bool(true),
            ["baseRefRecoverySha"] = Str("cccc3333"),
            ["pinnedSha"] = Str("aaaa1111bbbb2222"),
        };
        var priorAttempt = JsonDocument.Parse("""{"status":"Failed","error":"x","sessionId":"s"}""").RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs, priorAttempt), CancellationToken.None);

        var primary = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.Workspace!.Primary!;

        primary.Ref.ShouldBe("codespace/session/prior-turn");
        primary.RefSoftFallback.ShouldBeTrue();
        primary.RefRecoverySha.ShouldBe("cccc3333");
        primary.PinnedSha.ShouldBe("aaaa1111bbbb2222");
    }

    [Fact]
    public async Task A_first_pass_with_no_prior_attempt_keeps_its_authored_workspace_byte_identical()
    {
        var inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = Str(Guid.NewGuid().ToString()) };

        var task = JsonSerializer.Deserialize<AgentTask>(
            (await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs, priorAttemptPayload: null), CancellationToken.None)).SuspendUntil!.Payload,
            AgentJson.Options)!;

        task.Workspace.ShouldBeNull("a single-repo first pass keeps Workspace null — the resolver derives it from RepositoryId");
        task.Goal.ShouldBe("Fix the tests");
    }

    [Fact]
    public async Task A_respawn_of_a_repo_less_run_has_no_tree_to_conserve()
    {
        // An analysis-only run (no primary repo) has no workspace to pin; a branch on the payload cannot invent one.
        var priorAttempt = JsonDocument.Parse("""{"status":"Failed","error":"x","sessionId":"s","branch":"codespace/agent/attempt-1"}""").RootElement;

        var task = JsonSerializer.Deserialize<AgentTask>(
            (await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs: null, priorAttempt), CancellationToken.None)).SuspendUntil!.Payload,
            AgentJson.Options)!;

        task.Workspace.ShouldBeNull("no repo, no tree — nothing to repin");
        task.ResumeFromSessionId.ShouldBe("s", "the conversation still resumes; only the world-state pin is inapplicable");
        task.Goal.ShouldBe("Fix the tests", "an analysis-only run never had a tree to lose — the git-flavoured honest-redo line would assert a fact about work this run was never able to produce");
    }

    [Theory]
    [InlineData(null)]                                     // no repo input at all
    [InlineData("")]                                       // picked then cleared
    public async Task A_repo_less_respawn_is_never_told_its_git_changes_were_lost(string? rawRepositoryId)
    {
        // The same rule with NOTHING on the payload to conserve: an absent repository is not a lost tree. The hint's
        // wording ("its git changes were NOT preserved in this workspace") would be nonsense for a run whose
        // workspace never held a repo, and it would land on every retry of every analysis-only agent.
        var inputs = rawRepositoryId is null ? null : new Dictionary<string, JsonElement> { ["repositoryId"] = Str(rawRepositoryId) };
        var priorAttempt = JsonDocument.Parse("""{"status":"Failed","error":"crashed","sessionId":"s"}""").RootElement;

        var task = JsonSerializer.Deserialize<AgentTask>(
            (await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs, priorAttempt), CancellationToken.None)).SuspendUntil!.Payload,
            AgentJson.Options)!;

        task.Goal.ShouldBe("Fix the tests", "no repository ⇒ no world-state claim either way");
        task.Workspace.ShouldBeNull();
    }

    [Fact]
    public async Task A_respawn_whose_primary_push_failed_repins_the_sibling_but_is_still_told_the_truth()
    {
        // The honesty decision follows the PRIMARY, exactly like the supervisor's workspaceHasPriorWork: a sibling's
        // successful push is still conserved (its own branch is repinned), but the agent's primary repo re-clones the
        // default branch, so the restored conversation MUST be told its changes are not there. An "any repo pushed"
        // read would suppress the line precisely where the primary lost its work — the worst place to go quiet.
        var primaryId = Guid.NewGuid();
        var webId = Guid.NewGuid();

        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = Str(primaryId.ToString()),
            ["relatedRepositories"] = JsonDocument.Parse($$"""[{"repositoryId":"{{webId}}","alias":"web","access":"write"}]""").RootElement,
        };

        // The primary's push FAILED (its entry carries publishError and no branch); the sibling's succeeded.
        var priorAttempt = JsonDocument.Parse($$"""
            {"status":"Failed","error":"x","sessionId":"s",
             "repositoryResults":[{"alias":"repo","repositoryId":"{{primaryId}}","producedBranch":null,"publishError":"403 forbidden"},
                                  {"alias":"web","repositoryId":"{{webId}}","producedBranch":"codespace/agent/web"}]}
            """).RootElement;

        var task = JsonSerializer.Deserialize<AgentTask>(
            (await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null, inputs, priorAttempt), CancellationToken.None)).SuspendUntil!.Payload,
            AgentJson.Options)!;

        var repos = task.Workspace!.Repositories;
        repos.Single(r => r.RepositoryId == webId).Ref.ShouldBe("codespace/agent/web", "the sibling's pushed work is still conserved — a failed primary push must not throw it away");
        repos.Single(r => r.RepositoryId == primaryId).Ref.ShouldBeNull("the primary never pushed, so it clones its default branch");
        task.Goal.ShouldBe($"Fix the tests\n\n{AgentRetryContinuity.HonestNoContinuityHint}", "the primary's tree does NOT carry the restored conversation's edits, and the agent is told so");
    }

    [Fact]
    public void The_honest_no_continuity_hint_is_pinned_verbatim()
    {
        // Rule 8-style pin: the quick lane (this node) and the supervisor lane read this ONE const, so both tell the
        // agent the same thing about a tree that does not carry its prior work. The literal is pinned because the
        // supervisor's own behaviour test asserts this exact wording — a reword must be a visible decision.
        AgentRetryContinuity.HonestNoContinuityHint.ShouldBe(
            "Note: your prior attempt's conversation is restored, but its git changes were NOT preserved in this workspace (no pushed branch was found to continue from) — you must redo any relevant file changes from scratch.");
    }

    [Fact]
    public void The_fail_closed_acceptance_exit_reason_is_pinned()
    {
        // The node's retry verdict and the acceptance re-grade key on this literal — a rename would silently turn
        // every deterministic verdict failure back into a billed respawn loop. Hard-pin (Rule 8).
        AgentAcceptanceContract.FailClosedExitReason.ShouldBe("acceptance-failed");
    }

    [Fact]
    public async Task Resumed_needs_review_run_does_not_proceed_as_a_success()
    {
        // Slice A1 consumer contract: a run re-graded to NeedsReview (it raised a decision still unanswered) is NOT
        // a clean success — the node must NOT consume it as if the work finished. It maps to a clean node failure so
        // the workflow doesn't proceed on un-reviewed work (the decision itself stays answerable via the queue).
        var resume = JsonDocument.Parse("""{"status":"NeedsReview","summary":"I need a decision on the API shape."}""").RootElement;

        var result = await new AgentCodeNode().RunAsync(BuildContext(new(), resume), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure, "NeedsReview is a non-success terminal — the node fails cleanly rather than emitting success outputs");
        result.Error.ShouldContain("NeedsReview");
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
        // Absent → null → push is DEFAULT-ON for a non-empty diff (the deleted env gate's replacement); this pins
        // that the node itself stays byte-identical (still emits null when unset) — the meaning of null downstream
        // is EvaluatePublishGuardsAsync's concern, not this node's.
        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null), CancellationToken.None);

        JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!.PushProducedBranch
            .ShouldBeNull("no pushBranch config → the node emits null (no behaviour change to the node itself)");
    }

    // ── mode (the model-authored intent) → base permissions + push, composing under the existing precedence ─────

    [Theory]
    // research → analysis base: network OFF and no produced branch (push false), but the TIER's write scope, so the
    // agent can write the deliverable files its own oracle grades (PlannerSchema pairs research/analysis kinds with
    // ArtifactPresent / LlmJudge / CitationsResolve / ArtifactSchema over deliverable PATHS — a forced ReadOnly made
    // every one of those contracts unsatisfiable). Nothing it writes is published; that is the actual boundary.
    [InlineData("research", AgentNetworkAccess.Off, AgentWriteScope.Workspace, false)]
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
    public async Task Mode_research_lowers_the_network_but_never_raises_the_tiers_write_scope()
    {
        // The two halves of research's new base, on the tier that makes each falsifiable:
        //  • Trusted GRANTS network — research must still turn it OFF (a mode may always LOWER privilege), while the
        //    tier's Workspace write scope stands so the agent can write the deliverables it is graded on;
        //  • Confined grants NO write — research must not manufacture one (a mode never raises the ceiling), which is
        //    the same clamp-safety invariant Mode_code_does_not_override_a_low_autonomy_tier pins from the other side.
        var trusted = new Dictionary<string, JsonElement> { ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["mode"] = Str("research"), ["autonomyLevel"] = Str("Trusted") };

        var trustedTask = JsonSerializer.Deserialize<AgentTask>((await new AgentCodeNode().RunAsync(BuildContext(trusted, resume: null), CancellationToken.None)).SuspendUntil!.Payload, AgentJson.Options)!;
        trustedTask.Permissions.Network.ShouldBe(AgentNetworkAccess.Off, "research reads the tree it was given, never the internet — it lowers the Trusted tier's network");
        trustedTask.Permissions.WriteScope.ShouldBe(AgentWriteScope.Workspace, "research writes its report into its own workspace, or its deliverable contract can never be satisfied");
        trustedTask.PushProducedBranch.ShouldBe(false, "nothing research writes is published — that, not read-only, is the boundary");

        var confined = new Dictionary<string, JsonElement> { ["goal"] = Str("g"), ["harness"] = Str("codex-cli"), ["mode"] = Str("research"), ["autonomyLevel"] = Str("Confined") };

        JsonSerializer.Deserialize<AgentTask>((await new AgentCodeNode().RunAsync(BuildContext(confined, resume: null), CancellationToken.None)).SuspendUntil!.Payload, AgentJson.Options)!
            .Permissions.WriteScope.ShouldBe(AgentWriteScope.ReadOnly, "the operator's Confined ceiling still caps the write scope — a mode never lifts the tier");
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

    [Theory]
    // A shape-derived contract on a research run: the acceptance force must NOT reach past the mode's no-publish base.
    [InlineData("research", false)]
    // The same contract on a code / document run still forces publish — a contract implies a gradable branch (F4).
    [InlineData("code", true)]
    public async Task An_acceptance_never_forces_a_research_run_to_publish_a_branch(string mode, bool expectedPush)
    {
        // The refutation: every answer / research launch now carries an LlmJudge acceptance, and the F4 force-push
        // read `acceptance != null` alone — so a repo-bound "explain how X works" published a branch whose only
        // content was the DELIVERABLE.md it was told to write, contradicting the mode's own no-publish boundary.
        var config = new Dictionary<string, JsonElement>
        {
            ["goal"] = Str("Explain how the retry loop works"),
            ["harness"] = Str("codex-cli"),
            ["mode"] = Str(mode),
            ["acceptance"] = JsonDocument.Parse("""{"kind":"LlmJudge","command":["DELIVERABLE.md"],"rubric":{"criteria":[{"id":"goal","requirement":"answers the question"}]}}""").RootElement,
        };

        var result = await new AgentCodeNode().RunAsync(BuildContext(config, resume: null), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Acceptance.ShouldNotBeNull("the contract still grades the run — only the PUBLISH decision changes");
        task.PushProducedBranch.ShouldBe(expectedPush,
            customMessage: $"mode={mode} with a bound acceptance must resolve push to {expectedPush} — research publishes nothing, whatever grades it");
    }

    [Fact]
    public async Task Absent_mode_is_byte_identical_to_today_deferring_to_the_tier_and_the_push_flag()
    {
        // Regression pin: a node with no mode resolves EXACTLY as before this knob existed — Standard tier base
        // (Workspace write, no network) and null push (defer to the deployment flag).
        var result = await new AgentCodeNode().RunAsync(BuildContext(RequiredConfig(), resume: null), CancellationToken.None);

        var task = JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
        task.Permissions.ShouldBe(new AgentPermissions(), "no mode → the tier-derived baseline, byte-identical to before the mode knob existed");
        task.PushProducedBranch.ShouldBeNull("no mode and no explicit pushBranch → the node still emits null (push is default-on downstream, not this node's concern)");
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

    private static NodeRunContext BuildContext(Dictionary<string, JsonElement> config, JsonElement? resume, Dictionary<string, JsonElement>? inputs = null, JsonElement? priorAttemptPayload = null) => new()
    {
        Inputs = inputs ?? new Dictionary<string, JsonElement>(),
        Config = config,
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
        ResumePayload = resume,
        PriorAttemptPayload = priorAttemptPayload,
    };
}
