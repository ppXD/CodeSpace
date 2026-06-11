using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The DB-dependent branches of <see cref="AgentDefinitionResolver"/> against real Postgres: the persona
/// load (team-scoped, soft-delete-aware), the merge into the task, and the typed not-found failure. The
/// pure precedence rules are unit-pinned in <c>AgentDefinitionResolverTests</c>; this tier proves the
/// load + tenancy + provenance.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentDefinitionResolverFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentDefinitionResolverFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task No_persona_returns_the_task_unchanged()
    {
        var teamId = await SeedTeamAsync();
        var task = InlineTask(goal: "Just do it", model: "gpt-5.4");

        var resolved = await ResolveAsync(task, teamId);

        resolved.Goal.ShouldBe("Just do it");
        resolved.Model.ShouldBe("gpt-5.4");
        resolved.AgentDefinitionId.ShouldBeNull("the pure-inline path is byte-for-byte unchanged — zero regression");
    }

    [Fact]
    public async Task Persona_prompt_prepends_the_goal_and_its_model_fills_in()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, "You are a reviewer.", model: "gpt-5.4");
        var task = InlineTask(goal: "Review PR #42.", model: null, agentDefinitionId: id);

        var resolved = await ResolveAsync(task, teamId);

        resolved.Goal.ShouldBe("You are a reviewer.\n\nReview PR #42.");
        resolved.Model.ShouldBe("gpt-5.4", "no inline model → the persona's model");
        resolved.AgentDefinitionId.ShouldBe(id, "provenance is preserved on the merged task");
    }

    [Fact]
    public async Task Inline_model_override_wins_over_the_persona()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, "You are a reviewer.", model: "gpt-5.4");
        var task = InlineTask(goal: "Review.", model: "gpt-5.3-codex", agentDefinitionId: id);

        var resolved = await ResolveAsync(task, teamId);

        // Both axes at once: the model override wins AND the persona prompt still prepends the goal.
        resolved.Goal.ShouldBe("You are a reviewer.\n\nReview.", "the persona prompt still prepends even when the node overrides the model");
        resolved.Model.ShouldBe("gpt-5.3-codex", "a non-blank inline model overrides the persona's");
    }

    [Fact]
    public async Task Persona_with_no_goal_runs_on_the_system_prompt_alone()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, "You are a reviewer.", model: null);
        var task = InlineTask(goal: "", model: null, agentDefinitionId: id);

        var resolved = await ResolveAsync(task, teamId);

        resolved.Goal.ShouldBe("You are a reviewer.");
        resolved.Model.ShouldBeNull("neither node nor persona set a model → null = harness default");
    }

    [Fact]
    public async Task A_missing_persona_throws_a_typed_resolution_error()
    {
        var teamId = await SeedTeamAsync();
        var task = InlineTask(goal: "x", model: null, agentDefinitionId: Guid.NewGuid());

        await Should.ThrowAsync<AgentDefinitionResolutionException>(() => ResolveAsync(task, teamId));
    }

    [Fact]
    public async Task A_persona_from_another_team_is_not_resolvable()
    {
        var teamA = await SeedTeamAsync();
        var teamB = await SeedTeamAsync();
        var idInA = await SeedPersonaAsync(teamA, "Team A persona.", model: null);

        // Resolving team A's persona id under team B must fail — never a cross-team read.
        await Should.ThrowAsync<AgentDefinitionResolutionException>(() => ResolveAsync(InlineTask(goal: "x", model: null, agentDefinitionId: idInA), teamB));
    }

    [Fact]
    public async Task A_soft_deleted_persona_is_not_resolvable()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, "Doomed.", model: null);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var row = await db.AgentDefinition.SingleAsync(a => a.Id == id);
            row.DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await Should.ThrowAsync<AgentDefinitionResolutionException>(() => ResolveAsync(InlineTask(goal: "x", model: null, agentDefinitionId: id), teamId));
    }

    [Fact]
    public async Task A_persona_with_an_empty_prompt_and_no_goal_throws_nothing_to_run()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, systemPrompt: "", model: null);

        var ex = await Should.ThrowAsync<AgentDefinitionResolutionException>(() => ResolveAsync(InlineTask(goal: "", model: null, agentDefinitionId: id), teamId));
        ex.Message.ShouldContain("nothing to run");
    }

    [Fact]
    public async Task Persona_tools_union_with_the_nodes_tools()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, "You are a reviewer.", model: null, toolsJson: "[\"Read\",\"Grep\"]");
        var task = InlineTask(goal: "Review.", model: null, agentDefinitionId: id) with { Tools = new[] { "Grep", "Bash" } };

        (await ResolveAsync(task, teamId)).Tools.ShouldBe(new[] { "Read", "Grep", "Bash" },
            customMessage: "the run's tools are the UNION of the persona's and the node's — supplement, never narrow");
    }

    [Fact]
    public async Task Persona_tools_fill_in_when_the_node_supplies_none()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, "You are a reviewer.", model: null, toolsJson: "[\"Read\"]");

        // task.Tools is null (node sets none) → the persona's tools are used as-is.
        (await ResolveAsync(InlineTask(goal: "Review.", model: null, agentDefinitionId: id), teamId)).Tools.ShouldBe(new[] { "Read" });
    }

    [Fact]
    public async Task A_persona_with_no_tools_leaves_the_run_on_the_harness_default()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, "You are a reviewer.", model: null);   // ToolsJson null = default

        (await ResolveAsync(InlineTask(goal: "Review.", model: null, agentDefinitionId: id), teamId)).Tools
            .ShouldBeNull("neither persona nor node set tools → null = inherit the harness default");
    }

    [Fact]
    public async Task Persona_model_credential_fills_in_when_the_node_pins_none()
    {
        var teamId = await SeedTeamAsync();
        var personaCred = Guid.NewGuid();
        var id = await SeedPersonaAsync(teamId, "You are a reviewer.", model: null, modelCredentialId: personaCred);

        // task.ModelCredentialId is null (node pins none) → the persona's default reference is carried through.
        (await ResolveAsync(InlineTask(goal: "Review.", model: null, agentDefinitionId: id), teamId)).ModelCredentialId
            .ShouldBe(personaCred, "no node override → the persona's default model credential");
    }

    [Fact]
    public async Task Node_model_credential_override_wins_over_the_persona()
    {
        var teamId = await SeedTeamAsync();
        var personaCred = Guid.NewGuid();
        var nodeCred = Guid.NewGuid();
        var id = await SeedPersonaAsync(teamId, "You are a reviewer.", model: null, modelCredentialId: personaCred);

        var task = InlineTask(goal: "Review.", model: null, agentDefinitionId: id) with { ModelCredentialId = nodeCred };

        (await ResolveAsync(task, teamId)).ModelCredentialId.ShouldBe(nodeCred, "a node-pinned credential overrides the persona's default");
    }

    private static AgentTask InlineTask(string goal, string? model, Guid? agentDefinitionId = null) => new()
    {
        Goal = goal,
        Harness = "codex-cli",
        Model = model,
        AgentDefinitionId = agentDefinitionId,
    };

    private async Task<AgentTask> ResolveAsync(AgentTask task, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentDefinitionResolver>().ResolveAsync(task, teamId, CancellationToken.None);
    }

    private async Task<Guid> SeedPersonaAsync(Guid teamId, string systemPrompt, string? model, string? toolsJson = null, Guid? modelCredentialId = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = "persona-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Persona",
            SystemPrompt = systemPrompt,
            Model = model,
            ModelCredentialId = modelCredentialId,
            ToolsJson = toolsJson,
            Origin = AgentDefinitionOrigin.Authored,
            CreatedDate = now,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now,
            LastModifiedBy = SystemUsers.SeederId,
        };
        db.AgentDefinition.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"res-{userId:N}@test.local", Name = $"res-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"res-team-{teamId:N}", Name = "Resolver Team", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return teamId;
    }
}
