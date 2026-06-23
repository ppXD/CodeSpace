using CodeSpace.Core.Services.Tasks.Projection.Builders.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the supervisor projection builder: the emitted graph is the minimal lane shape
/// (<c>trigger.manual → agent.supervisor → builtin.terminal</c>), it ALWAYS passes the REAL
/// <see cref="DefinitionValidator"/> over the real node manifests, and the <c>agent.supervisor</c> node Config
/// carries the goal + the mapped agentProfile + the caps→bounds (maxParallelism / maxRounds / maxTotalSpawns
/// from <c>Route.Caps</c>) + the approvalPolicy on the EXACT camelCase keys the node's ConfigSchema reads. The
/// build is PURE (no lane check, no clamp) — the builder lifts bytes; the node clamps + gates at execution.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDefinitionBuilderTests
{
    private static readonly SupervisorDefinitionBuilder Builder = new();

    /// <summary>The REAL validator over the REAL node runtimes the builder emits. AgentSupervisorNode's scope factory is unused for manifest reading, so a null is safe.</summary>
    private static DefinitionValidator RealValidator() => new(new NodeRegistry(new INodeRuntime[]
    {
        new TriggerManualNode(),
        new AgentSupervisorNode(null!),
        new TerminalNode(),
    }));

    private static TaskBuildContext Context(ResolvedAgentProfile? profile = null, RouteCaps? caps = null) => new()
    {
        Seed = new TaskLaunchSeed { Goal = "Ship the whole feature", SurfaceKind = "chat", TeamId = Guid.NewGuid() },
        Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.Supervisor, Caps = caps ?? new RouteCaps() },
        AgentProfile = profile,
    };

    [Fact]
    public void Reports_the_supervisor_projection_kind()
    {
        Builder.ProjectionKind.ShouldBe(TaskProjectionKinds.Supervisor);
    }

    [Fact]
    public void Emits_the_manual_supervisor_terminal_graph()
    {
        var def = Builder.Build(Context());

        var byId = def.Nodes.ToDictionary(n => n.Id, n => n.TypeKey);
        byId["start"].ShouldBe("trigger.manual");
        byId["sup"].ShouldBe("agent.supervisor");
        byId["end"].ShouldBe("builtin.terminal");

        def.Edges.Select(e => (e.From, e.To)).ShouldBe(new[] { ("start", "sup"), ("sup", "end") }, ignoreOrder: true);
    }

    [Fact]
    public void Output_passes_the_real_validator_for_a_bare_context()
    {
        var result = RealValidator().Validate(Builder.Build(Context()));

        result.IsValid.ShouldBeTrue(customMessage: "a bare supervisor definition must pass DefinitionValidator: " + string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Output_passes_the_real_validator_for_a_full_context()
    {
        var profile = new ResolvedAgentProfile { RepositoryId = Guid.NewGuid(), Harness = "claude-code", Model = "claude-sonnet", RunnerKind = "local", AutonomyLevel = "Trusted", EnableMcp = true };
        var caps = new RouteCaps { MaxParallelism = 5, MaxRounds = 6, MaxTotalSpawns = 20, RequiresApproval = true };

        RealValidator().Validate(Builder.Build(Context(profile, caps))).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Supervisor_config_carries_the_seed_goal()
    {
        var sup = Builder.Build(Context()).Nodes.Single(n => n.Id == "sup");

        sup.Config.GetProperty("goal").GetString().ShouldBe("Ship the whole feature", "the seed goal maps onto the supervisor's goal config");
    }

    [Fact]
    public void Supervisor_config_folds_the_caps_into_the_bounds()
    {
        var caps = new RouteCaps { MaxParallelism = 5, MaxRounds = 6, MaxTotalSpawns = 20, MaxCostUsd = 12.50m };

        var sup = Builder.Build(Context(caps: caps)).Nodes.Single(n => n.Id == "sup");

        sup.Config.GetProperty("maxParallelism").GetInt32().ShouldBe(5, "the route's parallelism cap folds onto the supervisor bound");
        sup.Config.GetProperty("maxRounds").GetInt32().ShouldBe(6);
        sup.Config.GetProperty("maxTotalSpawns").GetInt32().ShouldBe(20);
        sup.Config.GetProperty("maxCostUsd").GetDecimal().ShouldBe(12.50m, "the route's cost cap folds onto the supervisor bound as a JSON number (SOTA #4)");
    }

    [Theory]
    [InlineData(true, "spawns")]    // RequiresApproval → spawns are human-gated
    [InlineData(false, "none")]     // the deep preset's default → autonomous
    public void Approval_policy_reflects_the_caps_requires_approval(bool requiresApproval, string expectedPolicy)
    {
        var sup = Builder.Build(Context(caps: new RouteCaps { RequiresApproval = requiresApproval })).Nodes.Single(n => n.Id == "sup");

        sup.Config.GetProperty("approvalPolicy").GetString().ShouldBe(expectedPolicy);
    }

    [Fact]
    public void Supervisor_config_maps_the_agent_profile_field_for_field()
    {
        var repoId = Guid.NewGuid();
        var credId = Guid.NewGuid();
        var profile = new ResolvedAgentProfile { RepositoryId = repoId, Harness = "claude-code", Model = "claude-sonnet", ModelCredentialId = credId, RunnerKind = "local", EnableMcp = true, AutonomyLevel = "Trusted" };

        var agentProfile = Builder.Build(Context(profile)).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        agentProfile.GetProperty("repositoryId").GetString().ShouldBe(repoId.ToString());
        agentProfile.GetProperty("harness").GetString().ShouldBe("claude-code");
        agentProfile.GetProperty("model").GetString().ShouldBe("claude-sonnet");
        agentProfile.GetProperty("modelCredentialId").GetString().ShouldBe(credId.ToString());
        agentProfile.GetProperty("runnerKind").GetString().ShouldBe("local");
        agentProfile.GetProperty("enableMcp").GetBoolean().ShouldBeTrue();
        agentProfile.GetProperty("autonomyLevel").GetString().ShouldBe("Trusted");
    }

    [Fact]
    public void Supervisor_config_stamps_each_related_repo_so_a_deep_multi_repo_launch_is_not_a_silent_drop()
    {
        // The deep (supervisor) path's analogue of the agent.code node's relatedRepositories — each spawned agent
        // clones these alongside the primary. Emitted in the SAME {repositoryId, alias?, access?} shape (via the ONE
        // shared serializer) the supervisor's SupervisorAgentProfile re-parses.
        var api = Guid.NewGuid();
        var web = Guid.NewGuid();
        var profile = new ResolvedAgentProfile
        {
            RepositoryId = Guid.NewGuid(),
            RelatedRepositories = new[]
            {
                new WorkspaceRepositorySpec { RepositoryId = api, Alias = "api", Access = WorkspaceAccess.Write },
                new WorkspaceRepositorySpec { RepositoryId = web, Alias = "web", Access = WorkspaceAccess.Read },
            },
        };

        var agentProfile = Builder.Build(Context(profile)).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        var related = agentProfile.GetProperty("relatedRepositories").EnumerateArray().ToList();
        related.Select(e => e.GetProperty("repositoryId").GetString()).ShouldBe(new[] { api.ToString(), web.ToString() }, "both related repos reach the supervisor config in authored order");
        related[0].GetProperty("access").GetString().ShouldBe("write");
        related[1].GetProperty("access").GetString().ShouldBe("read");
        related[0].GetProperty("alias").GetString().ShouldBe("api");
    }

    [Fact]
    public void A_profile_with_no_related_repos_omits_relatedRepositories_byte_identical()
    {
        var profile = new ResolvedAgentProfile { RepositoryId = Guid.NewGuid(), Harness = "codex-cli" };

        var agentProfile = Builder.Build(Context(profile)).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        agentProfile.TryGetProperty("relatedRepositories", out _).ShouldBeFalse(
            "no related repos ⇒ no key — a single-repo supervisor spawn is byte-identical to the pre-multi-repo-launch shape");
    }

    [Fact]
    public void A_bare_context_omits_the_agent_profile_and_the_caps_keys()
    {
        var sup = Builder.Build(Context()).Nodes.Single(n => n.Id == "sup");

        sup.Config.TryGetProperty("agentProfile", out _).ShouldBeFalse("an absent profile omits the nested object — a bare codex-cli/no-repo supervisor");
        sup.Config.TryGetProperty("maxParallelism", out _).ShouldBeFalse("an unset cap omits the key — the node falls to its SupervisorLane default");
        sup.Config.TryGetProperty("maxRounds", out _).ShouldBeFalse();
        sup.Config.TryGetProperty("maxTotalSpawns", out _).ShouldBeFalse();
        sup.Config.TryGetProperty("maxCostUsd", out _).ShouldBeFalse("an unset cost cap omits the key — the run has no cost budget (SOTA #4 default)");

        // approvalPolicy is always present — it defaults to 'none' (the autonomous, pre-approval posture).
        sup.Config.GetProperty("approvalPolicy").GetString().ShouldBe("none");
    }

    [Fact]
    public void Terminal_surfaces_the_supervisor_outputs()
    {
        var end = Builder.Build(Context()).Nodes.Single(n => n.Id == "end");

        end.Inputs.GetProperty("status").GetString().ShouldBe("{{nodes.sup.outputs.status}}");
        end.Inputs.GetProperty("decision").GetString().ShouldBe("{{nodes.sup.outputs.decision}}");
        end.Inputs.GetProperty("reason").GetString().ShouldBe("{{nodes.sup.outputs.reason}}");
        end.Inputs.GetProperty("turns").GetString().ShouldBe("{{nodes.sup.outputs.turns}}");
    }
}
