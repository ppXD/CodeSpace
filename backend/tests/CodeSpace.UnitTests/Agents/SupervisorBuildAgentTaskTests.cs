using CodeSpace.Core.Services.Agents;
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

        // Permissions are DERIVED from the (default Standard) autonomy via AgentAutonomyPolicy — which for
        // Standard is value-equal to the AgentTask record's own default (no network, workspace write), so the
        // bare task is still field-for-field what pre-P2-3 produced. Environment / TimeoutSeconds stay the
        // record defaults (BuildAgentTask never touches them); a value-equal ShouldBe on the whole task is
        // unusable because the default Environment is a distinct Dictionary instance, so we pin them explicitly.
        task.Permissions.ShouldBe(AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Standard), "Standard autonomy derives the no-network/workspace-write posture (value-equal to pre-P2-3's untouched default)");
        task.Environment.ShouldBeEmpty("untouched → the record default empty environment");
        task.TimeoutSeconds.ShouldBe(1800, "untouched → the record default timeout");
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

    private static AgentTask Build(SupervisorTurnContext context) =>
        RealSupervisorActionExecutor.BuildAgentTask(EmptySubtasks, SubtaskId, revisedInstruction: null, context);

    private static readonly IReadOnlyDictionary<string, SupervisorPlannedSubtask> EmptySubtasks = new Dictionary<string, SupervisorPlannedSubtask>();
}
