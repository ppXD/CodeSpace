using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the P2-3 pure mapping <c>RealSupervisorActionExecutor.BuildAgentTask</c> — a supervisor turn context
/// (its optional <see cref="SupervisorAgentProfile"/> + tool allow-list + conversation) folded into the
/// <see cref="AgentTask"/> envelope each spawned agent inherits. Two halves:
/// <list type="bullet">
///   <item>a FULL profile maps every field (repo / harness / model / persona / credential / runner / MCP /
///         autonomy / tools / approval-conversation), mirroring <c>AgentCodeNode</c>'s config→task map;</item>
///   <item>an ABSENT profile produces EXACTLY today's bare task (Goal + codex-cli + Standard, everything else
///         null/default) — the byte-identical guarantee that keeps every pre-P2-3 supervisor spawn/crash/bound/
///         E2E test green. The persona-merge itself is DB-backed and proven at the integration tier.</item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
[Collection("DefaultHarnessEnvMutation")]   // a null-profile build reads the unset default harness — serialize with the env-mutating AgentHarnessDefaultsTests
public class SupervisorBuildAgentTaskTests
{
    private const string SubtaskId = "s1";

    [Fact]
    public void An_absent_profile_produces_the_byte_identical_bare_task()
    {
        // No profile, no tools, no conversation — exactly what a pre-P2-3 supervisor resolves to.
        var task = Build(new SupervisorTurnContext { Goal = "ship the feature" });

        // Every field must equal the EXACT pre-P2-3 bare task: Goal + Harness="codex-cli" + Autonomy=Standard,
        // everything else null/default. A drift here breaks an existing supervisor test.
        task.Goal.ShouldBe("ship the feature", "the goal falls back to the supervisor goal with no plan/instruction");
        task.Harness.ShouldBe("codex-cli", "the null-profile harness is the supervisor's codex-cli default");
        task.Autonomy.ShouldBe(AgentAutonomyLevel.Standard, "the null-profile autonomy is the safe Standard default");

        task.Model.ShouldBeNull();
        task.AgentDefinitionId.ShouldBeNull();
        task.ModelCredentialId.ShouldBeNull();
        task.Tools.ShouldBeNull("no allow-list → the harness default (the tri-state null)");
        task.RunnerKind.ShouldBeNull();
        task.RepositoryId.ShouldBeNull("no repo → analysis-only");
        task.ApprovalConversationId.ShouldBeNull("no supervisor conversation → no approval surface");
        task.EnableMcpEndpoint.ShouldBeNull("no MCP opt-in → defer to the ambient flag");
        task.PushProducedBranch.ShouldBeNull("no push opt-in → the spawn emits null, which now means push-by-default downstream (a guard is the explicit opt-out, not this)");

        // Permissions are DERIVED from the (default Standard) autonomy via AgentAutonomyPolicy — which for
        // Standard is value-equal to the AgentTask record's own default (no network, workspace write), so the
        // bare task is still field-for-field what pre-P2-3 produced. Environment / TimeoutSeconds stay the
        // record defaults (BuildAgentTask never touches them); a value-equal ShouldBe on the whole task is
        // unusable because the default Environment is a distinct Dictionary instance, so we pin them explicitly.
        task.Permissions.ShouldBe(AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Standard), "Standard autonomy derives the no-network/workspace-write posture (value-equal to pre-P2-3's untouched default)");
        task.Environment.ShouldBeEmpty("untouched → the record default empty environment");
        task.TimeoutSeconds.ShouldBe(3600, "untouched → the record default timeout (1h)");
        task.Workspace.ShouldBeNull("no profile → no related repos → null Workspace → byte-identical single-repo execution (S7)");

        // S6: an EXPLICIT 0, never null — null would let an Improve output-review imply an in-run revise round,
        // stacking with the supervisor's own retry loop (the supervisor IS its units' revision mechanism).
        task.MaxReviseRounds.ShouldBe(0, "supervisor units never self-revise — the supervisor's retry loop owns their revision");
    }

    [Fact]
    public void A_full_profile_maps_every_field_onto_the_spawned_task()
    {
        var repoId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var context = new SupervisorTurnContext
        {
            Goal = "ship the feature",
            ConversationId = conversationId,
            SpawnedAgentTools = new[] { "Read", "Grep" },
            AgentProfile = new SupervisorAgentProfile
            {
                RepositoryId = repoId,
                Harness = "claude-code",
                Model = "claude-opus",
                AgentDefinitionId = personaId,
                ModelCredentialId = credentialId,
                RunnerKind = "local",
                EnableMcp = true,
                PushBranch = true,
                AutonomyLevel = "trusted",   // case-insensitive parse
            },
        };

        var task = Build(context);

        task.Goal.ShouldBe("ship the feature");
        task.Harness.ShouldBe("claude-code", "the profile harness overrides the codex-cli default");
        task.Model.ShouldBe("claude-opus");
        task.AgentDefinitionId.ShouldBe(personaId, "the persona reference is carried (the resolver merges it at dispatch)");
        task.ModelCredentialId.ShouldBe(credentialId);
        task.Tools.ShouldBe(new[] { "Read", "Grep" }, "the reused AllowedTools are threaded into Tools");
        task.RunnerKind.ShouldBe("local");
        task.RepositoryId.ShouldBe(repoId, "the profile repo is cloned by the executor");
        task.Autonomy.ShouldBe(AgentAutonomyLevel.Trusted, "the profile autonomy parses case-insensitively");
        task.ApprovalConversationId.ShouldBe(conversationId, "the supervisor's conversation becomes the approval surface");
        task.EnableMcpEndpoint.ShouldBe(true);
        task.PushProducedBranch.ShouldBe(true, "the profile's push opt-in threads to the spawned task so each branch agent publishes its own branch");
    }

    [Theory]
    [InlineData(7200, 7200)]  // positive → caps the run
    [InlineData(0, null)]     // explicit 0 → NO wall-clock (the operator's "no timeout" choice)
    [InlineData(-5, null)]    // any non-positive → unbounded, mirroring the agent.code node's vocabulary
    public void A_profile_timeout_maps_onto_the_spawned_task_in_the_agent_code_vocabulary(int authored, int? expected)
    {
        var task = Build(new SupervisorTurnContext { Goal = "g", AgentProfile = new SupervisorAgentProfile { TimeoutSeconds = authored } });

        task.TimeoutSeconds.ShouldBe(expected);
    }

    // ── S7-A: multi-repo — the authored related repos project onto a Workspace via the SHARED authoring底層 ──

    [Fact]
    public void Authored_related_repositories_project_onto_a_multi_repo_workspace()
    {
        var primary = Guid.NewGuid();
        var related = Guid.NewGuid();

        var context = new SupervisorTurnContext
        {
            Goal = "ship across the frontend + backend",
            AgentProfile = new SupervisorAgentProfile
            {
                RepositoryId = primary,
                RelatedRepositories = JsonDocument.Parse($$"""[{"repositoryId":"{{related}}","alias":"api","access":"write"}]""").RootElement,
            },
        };

        var task = Build(context);

        task.RepositoryId.ShouldBe(primary, "the primary still drives the legacy single-repo field");
        task.Workspace.ShouldNotBeNull("authored related repos project onto a multi-repo workspace through the SAME AgentWorkspaceAuthoring the agent.code node uses — no second parse");
        task.Workspace!.Repositories.Count.ShouldBe(2, "primary + one related repo");
        task.Workspace.Repositories.ShouldContain(r => r.RepositoryId == primary && r.IsPrimary);

        var api = task.Workspace.Repositories.Single(r => r.RepositoryId == related);
        api.Alias.ShouldBe("api");
        api.Access.ShouldBe(WorkspaceAccess.Write, "the authored write access flows through the shared parse (which the supervisor never re-implements)");
    }

    [Fact]
    public void A_profile_with_a_primary_but_no_related_repos_leaves_the_workspace_null()
    {
        var task = Build(new SupervisorTurnContext { Goal = "g", AgentProfile = new SupervisorAgentProfile { RepositoryId = Guid.NewGuid() } });

        task.RepositoryId.ShouldNotBeNull();
        task.Workspace.ShouldBeNull("no related repos → null Workspace → the executor derives the single-repo workspace from RepositoryId → BYTE-IDENTICAL single-repo spawn");
    }

    [Theory]
    [InlineData("\"not-an-array\"")]   // a non-array value
    [InlineData("[]")]                  // an empty array
    [InlineData("[{\"alias\":\"orphan\"}]")]   // an idless entry — skipped
    public void A_malformed_or_empty_related_repositories_value_degrades_to_single_repo(string relatedJson)
    {
        var context = new SupervisorTurnContext
        {
            Goal = "g",
            AgentProfile = new SupervisorAgentProfile { RepositoryId = Guid.NewGuid(), RelatedRepositories = JsonDocument.Parse(relatedJson).RootElement },
        };

        Build(context).Workspace.ShouldBeNull("a non-array / empty / all-idless relatedRepositories is lenient → no related → single-repo (never a stranded spawn)");
    }

    [Theory]
    [InlineData("confined", AgentAutonomyLevel.Confined)]
    [InlineData("Standard", AgentAutonomyLevel.Standard)]
    [InlineData("TRUSTED", AgentAutonomyLevel.Trusted)]
    [InlineData("unleashed", AgentAutonomyLevel.Unleashed)]
    [InlineData("nonsense", AgentAutonomyLevel.Standard)]   // unrecognised → safe Standard default
    [InlineData(null, AgentAutonomyLevel.Standard)]          // unset → safe Standard default
    public void The_profile_autonomy_parses_case_insensitively_with_a_safe_default(string? raw, AgentAutonomyLevel expected)
    {
        var task = Build(new SupervisorTurnContext { Goal = "g", AgentProfile = new SupervisorAgentProfile { AutonomyLevel = raw } });

        task.Autonomy.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Confined", AgentNetworkAccess.Off, AgentWriteScope.ReadOnly)]   // read-only, no network — the sandbox posture the schema promises
    [InlineData("Standard", AgentNetworkAccess.Off, AgentWriteScope.Workspace)]  // workspace write, no network (the safe default)
    [InlineData("Trusted", AgentNetworkAccess.On, AgentWriteScope.Workspace)]    // + network
    [InlineData("Unleashed", AgentNetworkAccess.On, AgentWriteScope.Workspace)]
    public void The_profile_autonomy_derives_the_sandbox_permissions(string raw, AgentNetworkAccess expectedNetwork, AgentWriteScope expectedWriteScope)
    {
        // Permissions must be DERIVED from the resolved autonomy (mirroring AgentCodeNode.ResolvePermissions →
        // AgentAutonomyPolicy.Derive), not left at the AgentTask record default — otherwise Trusted/Unleashed
        // would run with NO network and Confined with WRITE access, making the autonomy dial inert on the sandbox.
        var task = Build(new SupervisorTurnContext { Goal = "g", AgentProfile = new SupervisorAgentProfile { AutonomyLevel = raw } });

        task.Permissions.Network.ShouldBe(expectedNetwork, $"{raw} autonomy must derive the right network posture");
        task.Permissions.WriteScope.ShouldBe(expectedWriteScope, $"{raw} autonomy must derive the right write scope");
    }

    [Fact]
    public void A_blank_harness_or_model_in_the_profile_falls_back_to_the_defaults()
    {
        // Whitespace must degrade to the codex-cli default / the null model sentinel, never carry a blank.
        var task = Build(new SupervisorTurnContext { Goal = "g", AgentProfile = new SupervisorAgentProfile { Harness = "   ", Model = "  ", RunnerKind = "" } });

        task.Harness.ShouldBe("codex-cli", "a blank harness falls back to the default");
        task.Model.ShouldBeNull("a blank model degrades to the null harness-default sentinel");
        task.RunnerKind.ShouldBeNull("a blank runner degrades to null = the executor default");
    }

    [Fact]
    public void The_instruction_falls_back_planned_then_goal()
    {
        // Planned instruction wins over the goal when the subtask is in the plan lookup.
        var subtasks = new Dictionary<string, SupervisorPlannedSubtask>
        {
            [SubtaskId] = new() { Id = SubtaskId, Title = "T", Instruction = "do the planned thing" },
        };

        RealSupervisorActionExecutor.BuildAgentTask(subtasks, SubtaskId, revisedInstruction: null, new SupervisorTurnContext { Goal = "the goal" })
            .Goal.ShouldBe("do the planned thing", "the planned instruction wins over the supervisor goal");

        // A revised instruction (retry) wins over both.
        RealSupervisorActionExecutor.BuildAgentTask(subtasks, SubtaskId, revisedInstruction: "the revised thing", new SupervisorTurnContext { Goal = "the goal" })
            .Goal.ShouldBe("the revised thing", "the revised retry instruction wins over the planned one");
    }

    [Fact]
    public void DisplayTitle_is_the_clean_instruction_never_a_role_prefix_or_handoff_fold()
    {
        var subtasks = new Dictionary<string, SupervisorPlannedSubtask>
        {
            [SubtaskId] = new() { Id = SubtaskId, Title = "T", Instruction = "do the planned thing" },
        };
        var staging = new DependencyStagingResult { Ref = "codespace/agent/producer", GoalFoldText = "Your producer already committed work on branch `x`." };

        var task = RealSupervisorActionExecutor.BuildAgentTask(subtasks, SubtaskId, revisedInstruction: null, new SupervisorTurnContext { Goal = "the goal" }, spec: new SupervisorAgentDispatch { SubtaskId = SubtaskId, Role = "security reviewer" }, staging: staging);

        task.Goal.ShouldStartWith(staging.GoalFoldText!, customMessage: "the MODEL still sees the role framing + handoff fold, exactly as before");
        task.DisplayTitle.ShouldBe("do the planned thing", "the CARD title is the clean instruction — never the role prefix or the handoff paragraph");
    }

    // ── L4 arc B: a model-authored per-agent dispatch overrides + is clamped ───────────

    [Fact]
    public void A_dispatch_role_folds_into_the_agents_goal()
    {
        BuildWithSpec(new SupervisorTurnContext { Goal = "the goal" }, new SupervisorAgentDispatch { SubtaskId = SubtaskId, Role = "security reviewer" })
            .Goal.ShouldBe("As the security reviewer, the goal", "the role's only sink is the goal prompt — there is no AgentTask.Role field");
    }

    [Fact]
    public void A_dispatch_goal_override_replaces_the_instruction_and_keeps_the_role()
    {
        BuildWithSpec(new SupervisorTurnContext { Goal = "the goal" }, new SupervisorAgentDispatch { SubtaskId = SubtaskId, GoalOverride = "do X instead", Role = "impl" })
            .Goal.ShouldBe("As the impl, do X instead");
    }

    [Fact]
    public void A_dispatch_overrides_the_harness_and_model_requests()
    {
        var ctx = new SupervisorTurnContext { Goal = "g", AgentProfile = new SupervisorAgentProfile { Harness = "codex-cli", Model = "profile-model", AutonomyLevel = "standard" } };

        var task = BuildWithSpec(ctx, new SupervisorAgentDispatch { SubtaskId = SubtaskId, Harness = "claude-code", Model = "claude-opus" });

        task.Harness.ShouldBe("claude-code", "the per-agent harness request wins over the profile");
        task.Model.ShouldBe("claude-opus");
    }

    [Theory]
    [InlineData("standard", "unleashed", AgentAutonomyLevel.Standard)]   // request ABOVE the ceiling → clamped down
    [InlineData("trusted", "confined", AgentAutonomyLevel.Confined)]     // request BELOW the ceiling → honored (a model may lower its own autonomy)
    [InlineData("standard", null, AgentAutonomyLevel.Standard)]          // no request → the ceiling
    [InlineData("trusted", "trusted", AgentAutonomyLevel.Trusted)]       // equal → the ceiling
    [InlineData("confined", "unleashed", AgentAutonomyLevel.Confined)]   // can never escalate past a confined ceiling
    public void A_dispatch_autonomy_is_clamped_to_the_profile_ceiling(string profileTier, string? requested, AgentAutonomyLevel expected)
    {
        var ctx = new SupervisorTurnContext { Goal = "g", AgentProfile = new SupervisorAgentProfile { AutonomyLevel = profileTier } };

        var task = BuildWithSpec(ctx, new SupervisorAgentDispatch { SubtaskId = SubtaskId, AutonomyLevel = requested });

        task.Autonomy.ShouldBe(expected);
        task.Permissions.ShouldBe(AgentAutonomyPolicy.Derive(expected), "permissions derive from the CLAMPED autonomy, never the raw request");
    }

    [Fact]
    public void A_dispatch_targets_a_subset_of_the_bound_repos()
    {
        var primary = Guid.NewGuid(); var api = Guid.NewGuid(); var sdk = Guid.NewGuid();

        var task = BuildWithSpec(BoundContext(primary, api, sdk),
            new SupervisorAgentDispatch { SubtaskId = SubtaskId, TargetRepos = JsonDocument.Parse($$"""[{"repositoryId":"{{api}}","alias":"api","access":"write"}]""").RootElement });

        task.Workspace!.Repositories.Count.ShouldBe(2, "primary + the one targeted related repo (sdk dropped)");
        task.Workspace.Repositories.ShouldContain(r => r.RepositoryId == api);
        task.Workspace.Repositories.ShouldNotContain(r => r.RepositoryId == sdk, "a related repo the agent did not target is excluded");
    }

    [Fact]
    public void A_dispatch_primary_override_within_bound_becomes_the_agents_primary()
    {
        var primary = Guid.NewGuid(); var api = Guid.NewGuid(); var sdk = Guid.NewGuid();

        // api is bound WRITE, so it is a valid writable primary override.
        BuildWithSpec(BoundContext(primary, api, sdk), new SupervisorAgentDispatch { SubtaskId = SubtaskId, RepositoryId = api })
            .RepositoryId.ShouldBe(api, "the per-agent primary override (a write-bound repo) becomes the agent's primary");
    }

    [Fact]
    public void A_primary_override_to_a_related_repo_is_cloned_once_not_duplicated()
    {
        var primary = Guid.NewGuid(); var api = Guid.NewGuid(); var sdk = Guid.NewGuid();

        // Override the primary to the write-bound RELATED repo `api` (TargetRepos left unset). The chosen primary must be
        // cloned ONCE (as primary), never also as a related mount, and the operator's profile primary is replaced.
        var task = BuildWithSpec(BoundContext(primary, api, sdk), new SupervisorAgentDispatch { SubtaskId = SubtaskId, RepositoryId = api });

        task.RepositoryId.ShouldBe(api);
        task.Workspace!.Repositories.Count(r => r.RepositoryId == api).ShouldBe(1, "the overridden primary is cloned ONCE — never duplicated as both primary and related");
        task.Workspace.Repositories.Single(r => r.RepositoryId == api).IsPrimary.ShouldBeTrue();
        task.Workspace.Repositories.ShouldNotContain(r => r.RepositoryId == primary, "a primary override REPLACES the operator's profile primary (not auto-added)");
    }

    [Fact]
    public void A_dispatch_to_an_unbound_repo_fails_closed_through_the_clamp()
    {
        var ctx = BoundContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var unbound = Guid.NewGuid();

        Should.Throw<SupervisorRepoAccessException>(() => BuildWithSpec(ctx,
            new SupervisorAgentDispatch { SubtaskId = SubtaskId, TargetRepos = JsonDocument.Parse($$"""[{"repositoryId":"{{unbound}}"}]""").RootElement }));
    }

    [Fact]
    public void A_dispatch_read_to_write_upgrade_fails_closed_through_the_clamp()
    {
        var primary = Guid.NewGuid(); var api = Guid.NewGuid(); var sdk = Guid.NewGuid();   // sdk is bound READ-only

        Should.Throw<SupervisorRepoAccessException>(() => BuildWithSpec(BoundContext(primary, api, sdk),
            new SupervisorAgentDispatch { SubtaskId = SubtaskId, TargetRepos = JsonDocument.Parse($$"""[{"repositoryId":"{{sdk}}","access":"write"}]""").RootElement }));
    }

    [Fact]
    public void An_authored_dispatch_model_is_stamped_raw()
    {
        // BuildTaskWithGoal is a pure projection — it stamps the authored model NAME verbatim. The pool gate (the model
        // must resolve to a credentialed row in the allowed pool) runs POST-resolution in CreateResolvedAgentRunAsync,
        // where the effective (incl. persona-filled) model is known; it's exercised at the integration tier.
        BuildWithSpec(new SupervisorTurnContext { Goal = "g" }, new SupervisorAgentDispatch { SubtaskId = SubtaskId, Model = "claude-opus-4-8" })
            .Model.ShouldBe("claude-opus-4-8");
    }

    [Fact]
    public void The_dispatch_model_wins_over_the_profile_default_then_falls_back_to_it()
    {
        var ctx = new SupervisorTurnContext { Goal = "g", AgentProfile = new SupervisorAgentProfile { Model = "profile-model" } };

        BuildWithSpec(ctx, new SupervisorAgentDispatch { SubtaskId = SubtaskId, Model = "authored-model" }).Model.ShouldBe("authored-model", "an authored per-agent model wins");
        BuildWithSpec(ctx, new SupervisorAgentDispatch { SubtaskId = SubtaskId }).Model.ShouldBe("profile-model", "no authored model → the profile default is stamped");
    }

    [Fact]
    public void Passing_a_null_dispatch_is_byte_identical_to_the_no_dispatch_overload()
    {
        var ctx = new SupervisorTurnContext { Goal = "g", AgentProfile = new SupervisorAgentProfile { RepositoryId = Guid.NewGuid(), Harness = "claude-code", AutonomyLevel = "trusted" } };

        var withNull = RealSupervisorActionExecutor.BuildAgentTask(EmptySubtasks, SubtaskId, revisedInstruction: null, ctx, spec: null);
        var without = RealSupervisorActionExecutor.BuildAgentTask(EmptySubtasks, SubtaskId, revisedInstruction: null, ctx);

        JsonSerializer.Serialize(withNull, AgentJson.Options).ShouldBe(JsonSerializer.Serialize(without, AgentJson.Options),
            "spec=null builds the EXACT same task as the no-spec path — a no-dispatch spawn is byte-identical");
    }

    [Fact]
    public void A_contract_bearing_subtask_forces_the_push_opt_in()
    {
        // F4 on the SUPERVISOR lane (P0): the fold grades a contracted unit's oracle on its produced branch — a
        // stock profile (push off) would fail every contracted unit "no-branch-or-repo" even with perfect work.
        var subtasks = new Dictionary<string, SupervisorPlannedSubtask>
        {
            ["s1"] = new() { Id = "s1", Title = "contracted", Instruction = "do it", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "report.md" } } },
            ["s2"] = new() { Id = "s2", Title = "plain", Instruction = "do it too" },
        };
        var ctx = new SupervisorTurnContext { Goal = "g" };

        RealSupervisorActionExecutor.BuildAgentTask(subtasks, "s1", revisedInstruction: null, ctx).PushProducedBranch
            .ShouldBe(true, "a per-unit contract implies a GRADABLE branch — the publish opt-in is forced ON");
        RealSupervisorActionExecutor.BuildAgentTask(subtasks, "s2", revisedInstruction: null, ctx).PushProducedBranch
            .ShouldBeNull("a contract-less subtask keeps the profile/ambient behavior — byte-identical");
    }

    // ── S1 handoff: a dependency-staging override threads a clone ref + goal fold ──────

    [Fact]
    public void A_staging_ref_becomes_the_pinned_single_repo_clone_ref()
    {
        var repoId = Guid.NewGuid();
        var ctx = new SupervisorTurnContext { Goal = "g", AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId } };
        var staging = new DependencyStagingResult { Ref = "codespace/agent/producer" };

        var task = RealSupervisorActionExecutor.BuildAgentTask(EmptySubtasks, SubtaskId, revisedInstruction: null, ctx, staging: staging);

        task.Workspace.ShouldNotBeNull("a pinned ref needs an explicit single-repo Workspace spec — the executor no longer derives the default branch from bare RepositoryId");
        var primary = task.Workspace!.Repositories.Single();
        primary.RepositoryId.ShouldBe(repoId);
        primary.IsPrimary.ShouldBeTrue();
        primary.Ref.ShouldBe("codespace/agent/producer", "the dependent clones the producer's own branch, never the repository default");
    }

    [Fact]
    public void A_staging_goal_fold_is_prepended_before_the_instruction_and_role()
    {
        var subtasks = new Dictionary<string, SupervisorPlannedSubtask> { [SubtaskId] = new() { Id = SubtaskId, Title = "T", Instruction = "finish the feature" } };
        var staging = new DependencyStagingResult { Ref = "b", GoalFoldText = "You are building on prior work already on branch `b`." };

        var task = RealSupervisorActionExecutor.BuildAgentTask(subtasks, SubtaskId, revisedInstruction: null, new SupervisorTurnContext { Goal = "g" }, spec: new SupervisorAgentDispatch { SubtaskId = SubtaskId, Role = "implementer" }, staging: staging);

        task.Goal.ShouldBe("You are building on prior work already on branch `b`.\n\nAs the implementer, finish the feature", "the handoff block precedes the role-wrapped instruction");
    }

    [Fact]
    public void No_staging_leaves_the_workspace_and_goal_byte_identical()
    {
        var repoId = Guid.NewGuid();
        var ctx = new SupervisorTurnContext { Goal = "g", AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId } };

        var withNullStaging = RealSupervisorActionExecutor.BuildAgentTask(EmptySubtasks, SubtaskId, revisedInstruction: null, ctx, staging: null);
        var withNoOverride = RealSupervisorActionExecutor.BuildAgentTask(EmptySubtasks, SubtaskId, revisedInstruction: null, ctx, staging: DependencyStagingResult.NoOverride);
        var withoutParam = RealSupervisorActionExecutor.BuildAgentTask(EmptySubtasks, SubtaskId, revisedInstruction: null, ctx);

        withNullStaging.Workspace.ShouldBeNull("no staging override → the byte-identical default-branch derivation from bare RepositoryId");
        JsonSerializer.Serialize(withNullStaging, AgentJson.Options).ShouldBe(JsonSerializer.Serialize(withNoOverride, AgentJson.Options), "an explicit NoOverride is byte-identical to omitting staging entirely");
        JsonSerializer.Serialize(withNullStaging, AgentJson.Options).ShouldBe(JsonSerializer.Serialize(withoutParam, AgentJson.Options));
    }

    private static AgentTask Build(SupervisorTurnContext context) =>
        RealSupervisorActionExecutor.BuildAgentTask(EmptySubtasks, SubtaskId, revisedInstruction: null, context);

    private static AgentTask BuildWithSpec(SupervisorTurnContext context, SupervisorAgentDispatch spec) =>
        RealSupervisorActionExecutor.BuildAgentTask(EmptySubtasks, spec.SubtaskId, spec.GoalOverride, context, spec);

    // ── S1: the launch base pin — every spawn materializes the SAME immutable base. ──────────

    [Fact]
    public void The_profiles_launch_pin_materializes_on_every_spawned_workspace()
    {
        var repoId = Guid.NewGuid();
        var context = new SupervisorTurnContext { Goal = "ship", AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId, PinnedSha = "abc123def456" } };

        var first = Build(context);
        var second = Build(context);

        first.Workspace.ShouldNotBeNull("a pin needs an explicit spec — a null Workspace would clone the default TIP and silently drop it");
        first.Workspace!.Repositories.Single().PinnedSha.ShouldBe("abc123def456");
        second.Workspace!.Repositories.Single().PinnedSha.ShouldBe("abc123def456", "two parallel spawns materialize the SAME immutable base — the exact drift S1 kills");
    }

    [Fact]
    public void A_dependency_handoff_ref_outranks_the_launch_pin()
    {
        var repoId = Guid.NewGuid();
        var context = new SupervisorTurnContext { Goal = "ship", AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId, PinnedSha = "abc123def456" } };

        var task = RealSupervisorActionExecutor.BuildTaskWithGoal("continue on the dependency's branch", context, primaryRef: "codespace/agent/dep");

        var primary = task.Workspace!.Repositories.Single();
        primary.Ref.ShouldBe("codespace/agent/dep");
        primary.PinnedSha.ShouldBeNull("continuing work rides the prior attempt's branch — the launch base cannot express that continuation (the session-soft rule, applied to handoffs)");
    }

    [Fact]
    public void A_dispatch_primary_override_never_inherits_the_profiles_pin()
    {
        var primary = Guid.NewGuid();
        var api = Guid.NewGuid();
        var context = BoundContext(primary, apiWrite: api, sdkRead: Guid.NewGuid());
        context = context with { AgentProfile = context.AgentProfile! with { PinnedSha = "abc123def456" } };

        var task = BuildWithSpec(context, new SupervisorAgentDispatch { SubtaskId = SubtaskId, RepositoryId = api });

        task.RepositoryId.ShouldBe(api);
        task.Workspace!.Repositories.Single(r => r.IsPrimary).PinnedSha.ShouldBeNull("the vector was resolved for the LAUNCH's primary — a different repo must not wear another repo's commit");
    }

    /// <summary>A context whose profile binds a primary (write) + api (write) + sdk (read) — the operator bound set the per-agent clamp validates against.</summary>
    private static SupervisorTurnContext BoundContext(Guid primary, Guid apiWrite, Guid sdkRead) => new()
    {
        Goal = "ship",
        AgentProfile = new SupervisorAgentProfile
        {
            RepositoryId = primary,
            RelatedRepositories = JsonDocument.Parse($$"""[{"repositoryId":"{{apiWrite}}","alias":"api","access":"write"},{"repositoryId":"{{sdkRead}}","alias":"sdk","access":"read"}]""").RootElement,
        },
    };

    private static readonly IReadOnlyDictionary<string, SupervisorPlannedSubtask> EmptySubtasks = new Dictionary<string, SupervisorPlannedSubtask>();
}
