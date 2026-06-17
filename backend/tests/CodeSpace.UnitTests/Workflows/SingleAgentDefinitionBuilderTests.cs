using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Projection.Builders.SingleAgent;
using CodeSpace.Messages.Agents;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the single-agent projection builder: the emitted graph is <c>trigger.manual → agent.code →
/// builtin.terminal</c>, it ALWAYS passes the REAL <see cref="DefinitionValidator"/> (against the real node
/// manifests, including the agent.code output-key existence check the terminal's refs hit), and the
/// <see cref="ResolvedAgentProfile"/> + seed goal map onto the SAME agent.code config keys
/// <see cref="AgentCodeNode"/> reads — so a snapshot single-agent run executes identically to an authored
/// agent.code node. Validation is asserted across a bare profile, a fully-populated profile, and the seed-repo
/// fallback so a relaxed mapping can't slip through.
/// </summary>
[Trait("Category", "Unit")]
public class SingleAgentDefinitionBuilderTests
{
    private static readonly SingleAgentDefinitionBuilder Builder = new();

    /// <summary>The REAL validator over the REAL node runtimes the builder emits — so the output-key check runs against agent.code's actual OutputSchema, not a stub.</summary>
    private static DefinitionValidator RealValidator() => new(new NodeRegistry(new INodeRuntime[]
    {
        new TriggerManualNode(),
        new AgentCodeNode(),
        new TerminalNode(),
    }));

    private static TaskBuildContext Context(TaskLaunchSeed seed, ResolvedAgentProfile? profile) => new()
    {
        Seed = seed,
        Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.SingleAgent },
        AgentProfile = profile,
    };

    private static TaskLaunchSeed Seed(Guid? repositoryId = null) => new()
    {
        Goal = "Fix the failing login test",
        SurfaceKind = "chat",
        TeamId = Guid.NewGuid(),
        RepositoryId = repositoryId,
    };

    [Fact]
    public void Reports_the_single_agent_projection_kind()
    {
        Builder.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);
    }

    [Fact]
    public void Emits_the_fixed_manual_to_agent_to_terminal_graph()
    {
        var def = Builder.Build(Context(Seed(), profile: null));

        def.Nodes.Select(n => n.TypeKey).ShouldBe(new[] { "trigger.manual", "agent.code", "builtin.terminal" });
        def.Edges.Select(e => (e.From, e.To)).ShouldBe(new[] { ("start", "agent"), ("agent", "done") });
    }

    [Fact]
    public void Output_passes_the_real_validator_for_a_bare_profile()
    {
        var result = RealValidator().Validate(Builder.Build(Context(Seed(), profile: null)));

        result.IsValid.ShouldBeTrue(customMessage: "a bare-profile single-agent definition must pass DefinitionValidator: " + string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Bare_profile_emits_only_goal_and_the_default_harness()
    {
        var config = AgentConfigOf(Builder.Build(Context(Seed(), profile: null)));

        config.GetProperty("goal").GetString().ShouldBe("Fix the failing login test");
        config.GetProperty("harness").GetString().ShouldBe("codex-cli", customMessage: "a null harness folds to the agent.code catalog default");

        // No optional knobs leak — an absent key inherits the node's own default, matching a bare authored node.
        config.TryGetProperty("model", out _).ShouldBeFalse();
        config.TryGetProperty("autonomyLevel", out _).ShouldBeFalse();
        config.TryGetProperty("agentDefinitionId", out _).ShouldBeFalse();
        AgentInputsOf(Builder.Build(Context(Seed(), profile: null))).TryGetProperty("repositoryId", out _).ShouldBeFalse();
    }

    [Fact]
    public void Full_profile_maps_every_field_onto_the_agent_code_config()
    {
        var agentDefId = Guid.NewGuid();
        var credId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        var profile = new ResolvedAgentProfile
        {
            RepositoryId = repoId,
            Harness = "claude-code",
            Model = "claude-sonnet",
            AgentDefinitionId = agentDefId,
            ModelCredentialId = credId,
            RunnerKind = "local",
            AutonomyLevel = "Trusted",
            AllowedTools = new[] { "Read", "Grep" },
        };

        var def = Builder.Build(Context(Seed(), profile));
        var config = AgentConfigOf(def);

        config.GetProperty("harness").GetString().ShouldBe("claude-code");
        config.GetProperty("model").GetString().ShouldBe("claude-sonnet");
        config.GetProperty("agentDefinitionId").GetString().ShouldBe(agentDefId.ToString());
        config.GetProperty("modelCredentialId").GetString().ShouldBe(credId.ToString());
        config.GetProperty("runnerKind").GetString().ShouldBe("local");
        config.GetProperty("autonomyLevel").GetString().ShouldBe("Trusted");
        config.GetProperty("tools").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "Read", "Grep" });

        // repositoryId binds as the node's INPUT (matching agent.code's InputSchema), not config.
        AgentInputsOf(def).GetProperty("repositoryId").GetString().ShouldBe(repoId.ToString());

        RealValidator().Validate(def).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Repository_falls_back_to_the_seed_when_the_profile_has_none()
    {
        var seedRepo = Guid.NewGuid();

        // Profile names no repo; the seed does → the node's bound repositoryId comes from the seed.
        var def = Builder.Build(Context(Seed(seedRepo), new ResolvedAgentProfile { Harness = "codex-cli" }));

        AgentInputsOf(def).GetProperty("repositoryId").GetString().ShouldBe(seedRepo.ToString());
    }

    [Fact]
    public void Profile_repository_wins_over_the_seed_repository()
    {
        var profileRepo = Guid.NewGuid();
        var seedRepo = Guid.NewGuid();

        var def = Builder.Build(Context(Seed(seedRepo), new ResolvedAgentProfile { RepositoryId = profileRepo }));

        AgentInputsOf(def).GetProperty("repositoryId").GetString().ShouldBe(profileRepo.ToString());
    }

    [Fact]
    public void Profile_related_repositories_project_onto_the_agent_code_relatedRepositories_input()
    {
        var web = Guid.NewGuid();
        var api = Guid.NewGuid();

        var def = Builder.Build(Context(Seed(), new ResolvedAgentProfile
        {
            RepositoryId = web,
            RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = api, Access = WorkspaceAccess.Write } },
        }));

        var inputs = AgentInputsOf(def);
        inputs.GetProperty("repositoryId").GetString().ShouldBe(web.ToString());

        var related = inputs.GetProperty("relatedRepositories");
        related.GetArrayLength().ShouldBe(1, "the projection lane carries the related repos onto the SAME input the editor + AgentCodeNode use");
        related[0].GetProperty("repositoryId").GetString().ShouldBe(api.ToString());
        related[0].GetProperty("alias").GetString().ShouldBe("api");
        related[0].GetProperty("access").GetString().ShouldBe("write");

        RealValidator().Validate(def).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void No_related_repositories_omits_the_input_byte_identical()
    {
        // A profile with no related repos must NOT add a relatedRepositories key — a single-repo projection is unchanged.
        var def = Builder.Build(Context(Seed(), new ResolvedAgentProfile { RepositoryId = Guid.NewGuid() }));

        AgentInputsOf(def).TryGetProperty("relatedRepositories", out _).ShouldBeFalse();
    }

    [Fact]
    public void Related_repository_with_read_access_and_blank_alias_emits_read_and_omits_the_alias()
    {
        // The projection lane must agree with the editor + node on the defaults: a blank alias is emitted as null
        // (the node re-derives repo-N) and Read access is emitted as "read" — else the projected workspace diverges.
        var api = Guid.NewGuid();
        var def = Builder.Build(Context(Seed(), new ResolvedAgentProfile
        {
            RepositoryId = Guid.NewGuid(),
            RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "  ", RepositoryId = api, Access = WorkspaceAccess.Read } },
        }));

        var entry = AgentInputsOf(def).GetProperty("relatedRepositories")[0];
        entry.GetProperty("repositoryId").GetString().ShouldBe(api.ToString());
        entry.GetProperty("access").GetString().ShouldBe("read", "Read access is emitted as 'read'");
        (entry.GetProperty("alias").ValueKind == JsonValueKind.Null).ShouldBeTrue("a blank alias is emitted null so the node re-derives repo-N");
    }

    [Fact]
    public void Terminal_surfaces_the_agent_result_outputs()
    {
        var inputs = TerminalInputsOf(Builder.Build(Context(Seed(), profile: null)));

        inputs.GetProperty("status").GetString().ShouldBe("{{nodes.agent.outputs.status}}");
        inputs.GetProperty("summary").GetString().ShouldBe("{{nodes.agent.outputs.summary}}");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static JsonElement AgentConfigOf(WorkflowDefinition def) => def.Nodes.Single(n => n.Id == "agent").Config;
    private static JsonElement AgentInputsOf(WorkflowDefinition def) => def.Nodes.Single(n => n.Id == "agent").Inputs;
    private static JsonElement TerminalInputsOf(WorkflowDefinition def) => def.Nodes.Single(n => n.Id == "done").Inputs;
}
