using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Skills;
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

    [Fact]
    public async Task Bound_skills_resolve_onto_the_task_and_drop_a_soft_deleted_one()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, "You are a reviewer.", model: null);
        var s1 = await SeedSkillAsync(teamId, "tdd", "Use when implementing.", "Write the test first.");
        var s2 = await SeedSkillAsync(teamId, "debugging", "Use when stuck.", "Form a hypothesis.");
        await BindSkillsAsync(teamId, id, new[] { s1, s2 });

        var resolved = await ResolveAsync(InlineTask(goal: "Review.", model: null, agentDefinitionId: id), teamId);

        resolved.Skills.ShouldNotBeNull();
        // No re-sort in the assertion: pin the DETERMINISTIC emitted order. Both were bound in one Set call (same
        // timestamp), so the slug tiebreak decides → alphabetical. A regression that dropped the tiebreak would flake here.
        resolved.Skills!.Select(sk => sk.Slug).ShouldBe(new[] { "debugging", "tdd" }, customMessage: "stable order = bind time then slug");
        resolved.Skills!.Single(sk => sk.Slug == "tdd").Body.ShouldBe("Write the test first.", "the body is carried so the harness can project the SKILL.md");

        // Soft-delete one skill → the join reads active only, so it drops out of the next resolve.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.SkillDefinition.SingleAsync(x => x.Id == s1)).DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var after = await ResolveAsync(InlineTask(goal: "Review.", model: null, agentDefinitionId: id), teamId);
        after.Skills!.Select(sk => sk.Slug).ShouldBe(new[] { "debugging" });
    }

    [Fact]
    public async Task A_persona_with_no_bound_skills_leaves_skills_null()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, "You are a reviewer.", model: null);

        (await ResolveAsync(InlineTask(goal: "Review.", model: null, agentDefinitionId: id), teamId)).Skills
            .ShouldBeNull("no bindings → null skills, so the task_json stays byte-identical to a skill-less run");
    }

    [Fact]
    public async Task A_store_snapshot_persona_id_is_not_runnable()
    {
        var teamId = await SeedTeamAsync();

        // A Library STORE snapshot id is reachable to a client and could be threaded into a run via AgentDefinitionId;
        // resolving it must fail closed (never freeze a non-runnable snapshot's prompt/model into a live run).
        var storeId = await SeedPersonaAsync(teamId, "Library snapshot prompt.", model: null, scope: DefinitionScope.Store);

        await Should.ThrowAsync<AgentDefinitionResolutionException>(() => ResolveAsync(InlineTask(goal: "x", model: null, agentDefinitionId: storeId), teamId));
    }

    [Fact]
    public async Task Bound_skills_resolve_drops_a_store_scoped_skill()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, "You are a reviewer.", model: null);
        var working = await SeedSkillAsync(teamId, "tdd", "Use when implementing.", "Write the test first.");
        var store = await SeedStoreSkillAsync(teamId, "debugging");

        // Bind BOTH directly (bypassing the Working-only write guard) so the run-time join is what's under test:
        // a Store-scoped skill must drop out of the resolved task — defense-in-depth even if a bad binding exists.
        await InsertBindingAsync(id, working);
        await InsertBindingAsync(id, store);

        var resolved = await ResolveAsync(InlineTask(goal: "Review.", model: null, agentDefinitionId: id), teamId);

        resolved.Skills!.Select(sk => sk.Slug).ShouldBe(new[] { "tdd" }, customMessage: "the run-time skill join is Working-scoped, so the Store snapshot skill never reaches the task");
    }

    private async Task<Guid> SeedSkillAsync(Guid teamId, string slug, string description, string body)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        db.SkillDefinition.Add(new SkillDefinition { Id = id, TeamId = teamId, Slug = slug, Name = slug, Description = description, Body = body, Origin = SkillDefinitionOrigin.Authored, CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task BindSkillsAsync(Guid teamId, Guid agentId, IReadOnlyList<Guid> skillIds)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentSkillBindingService>().SetForAgentAsync(teamId, agentId, skillIds, SystemUsers.SeederId, CancellationToken.None);
    }

    private async Task<Guid> SeedStoreSkillAsync(Guid teamId, string slug)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.SkillDefinition.Add(new SkillDefinition { Id = id, TeamId = teamId, Slug = slug, Name = slug, Description = "snapshot", Body = "snapshot body", Origin = SkillDefinitionOrigin.Imported, Scope = DefinitionScope.Store, PackId = null, SourcePath = $"skills/{slug}/SKILL.md", CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task InsertBindingAsync(Guid agentId, Guid skillId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentSkillBinding.Add(new AgentSkillBinding { Id = Guid.NewGuid(), AgentDefinitionId = agentId, SkillDefinitionId = skillId, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
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

    // ── ResolveSlugAsync (P3): the brain-authored slug → team AgentDefinitionId lookup ─────────────────

    [Fact]
    public async Task ResolveSlugAsync_resolves_a_known_slug_and_normalizes_a_display_name_to_it()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedPersonaAsync(teamId, "Reviewer.", model: null, slug: "security-reviewer");

        (await ResolveSlugAsync("security-reviewer", teamId)).ShouldBe(id, "an exact slug resolves to its persona id");
        (await ResolveSlugAsync("Security Reviewer", teamId)).ShouldBe(id, "a display name is normalized via DeriveSlug to the same handle and resolves");
    }

    [Fact]
    public async Task ResolveSlugAsync_returns_null_for_an_unknown_slug()
    {
        var teamId = await SeedTeamAsync();

        (await ResolveSlugAsync("nope-not-here", teamId)).ShouldBeNull("an unknown slug returns null — the caller decides fail-closed");
    }

    [Fact]
    public async Task ResolveSlugAsync_is_team_scoped_and_soft_delete_aware()
    {
        var teamA = await SeedTeamAsync();
        var teamB = await SeedTeamAsync();
        var idInA = await SeedPersonaAsync(teamA, "Team A.", model: null, slug: "shared-slug");

        (await ResolveSlugAsync("shared-slug", teamB)).ShouldBeNull("a slug only resolves within its own team — never a cross-team read");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.AgentDefinition.SingleAsync(a => a.Id == idInA)).DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        (await ResolveSlugAsync("shared-slug", teamA)).ShouldBeNull("a soft-deleted persona's slug no longer resolves");
    }

    [Fact]
    public async Task ResolveSlugAsync_resolves_to_the_working_persona_ignoring_a_store_snapshot_of_the_same_handle()
    {
        var teamId = await SeedTeamAsync();

        // A STORE snapshot and a WORKING persona legally share a handle (team-slug uniqueness is Working-only).
        // The @-mention must resolve to the runnable bench persona — and must NOT throw on the two-row match.
        var workingId = await SeedPersonaAsync(teamId, "Bench reviewer.", model: null, slug: "code-reviewer", scope: DefinitionScope.Working);
        await SeedPersonaAsync(teamId, "Library snapshot.", model: null, slug: "code-reviewer", scope: DefinitionScope.Store);

        (await ResolveSlugAsync("code-reviewer", teamId)).ShouldBe(workingId, "a @-mention resolves to the WORKING persona, never the Library store snapshot");
    }

    [Fact]
    public async Task ResolveSlugAsync_returns_null_when_only_a_store_snapshot_owns_the_handle()
    {
        var teamId = await SeedTeamAsync();
        await SeedPersonaAsync(teamId, "Library snapshot only.", model: null, slug: "snapshot-only", scope: DefinitionScope.Store);

        (await ResolveSlugAsync("snapshot-only", teamId)).ShouldBeNull("a store snapshot is not runnable — its handle must not resolve for an @-mention");
    }

    private async Task<Guid?> ResolveSlugAsync(string slug, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentDefinitionResolver>().ResolveSlugAsync(slug, teamId, CancellationToken.None);
    }

    private async Task<Guid> SeedPersonaAsync(Guid teamId, string systemPrompt, string? model, string? toolsJson = null, Guid? modelCredentialId = null, string? slug = null, DefinitionScope scope = DefinitionScope.Working)
    {
        using var s = _fixture.BeginScope();
        var db = s.Resolve<CodeSpaceDbContext>();

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug ?? "persona-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Persona",
            SystemPrompt = systemPrompt,
            Model = model,
            ModelCredentialId = modelCredentialId,
            ToolsJson = toolsJson,
            Origin = scope == DefinitionScope.Store ? AgentDefinitionOrigin.Imported : AgentDefinitionOrigin.Authored,
            Scope = scope,
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
