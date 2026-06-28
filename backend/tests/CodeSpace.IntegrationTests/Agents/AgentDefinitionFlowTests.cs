using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Ecosystem PR2 — the Agents-library CRUD vertical end-to-end through the mediator + DB.
///
/// <para>An Agent persona (<c>agent_definition</c>) is the canonical "Agent" noun an <c>agent.code</c>
/// node / a chat @-mention references. This suite proves the operator-facing contract: name → @-handle
/// derivation, the tools tri-state (null = harness default / empty / specific), per-team handle
/// uniqueness with reuse after soft-delete, cross-team isolation on every verb, and — the keystone of
/// the format-preserving design — that editing an IMPORTED persona never clobbers its import-owned
/// fields (skills / MCP / verbatim frontmatter / origin / provenance / handle).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentDefinitionFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentDefinitionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Create_then_Get_round_trips_the_persona_with_a_derived_handle()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid id;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            id = await scope.Resolve<IMediator>().Send(new CreateAgentDefinitionCommand
            {
                Name = "Backend Architect",
                Description = "Use PROACTIVELY for system design.",
                SystemPrompt = "You are a senior backend architect.",
                Model = "claude-opus-4-8",
                DefaultAutonomy = "guarded",
                Tools = new[] { "Read", "Grep", "Bash" },
            });
        }

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var loaded = await verify.Resolve<IMediator>().Send(new GetAgentDefinitionQuery { AgentDefinitionId = id });

        loaded.ShouldNotBeNull();
        loaded!.Name.ShouldBe("Backend Architect");
        loaded.Slug.ShouldBe("backend-architect",
            customMessage: "the @-mention handle is DERIVED from the name — operators never type it. If this changes, every agent.code / chat reference to the handle breaks");
        loaded.Description.ShouldBe("Use PROACTIVELY for system design.");
        loaded.SystemPrompt.ShouldBe("You are a senior backend architect.");
        loaded.Model.ShouldBe("claude-opus-4-8");
        loaded.DefaultAutonomy.ShouldBe("guarded");
        loaded.Tools.ShouldBe(new[] { "Read", "Grep", "Bash" });
        loaded.Origin.ShouldBe(AgentDefinitionOrigin.Authored, "an API-authored persona is Authored, not Imported");
        loaded.TeamId.ShouldBe(teamId);
    }

    [Fact]
    public async Task Create_preserves_the_tools_tri_state()
    {
        // null (inherit the harness's default toolset), empty (explicitly no tools), and a specific
        // allow-list are three DISTINCT states — collapsing null↔empty changes which tools the harness
        // grants, so the round-trip must preserve each exactly.
        var (teamId, userId) = await SeedTeamAsync();

        async Task<IReadOnlyList<string>?> RoundTripAsync(IReadOnlyList<string>? tools)
        {
            Guid id;
            using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
                id = await scope.Resolve<IMediator>().Send(new CreateAgentDefinitionCommand { Name = $"Tooled {Guid.NewGuid():N}", Tools = tools });

            using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
            return (await verify.Resolve<IMediator>().Send(new GetAgentDefinitionQuery { AgentDefinitionId = id }))!.Tools;
        }

        (await RoundTripAsync(null)).ShouldBeNull("null tools means 'inherit the harness default' — it must NOT come back as an empty list");
        (await RoundTripAsync(Array.Empty<string>())).ShouldBeEmpty("an empty list means 'no tools' — it must NOT come back as null");
        (await RoundTripAsync(new[] { "Read", "Grep" })).ShouldBe(new[] { "Read", "Grep" });
    }

    [Fact]
    public async Task Create_with_a_colliding_handle_is_refused_with_an_actionable_error()
    {
        var (teamId, userId) = await SeedTeamAsync();

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new CreateAgentDefinitionCommand { Name = "Code Reviewer" });

        // Second persona, same name → same derived handle → uq_agent_definition_team_slug fires; the
        // service translates it to a friendly error naming BOTH the handle and the original name.
        var act = async () => await mediator.Send(new CreateAgentDefinitionCommand { Name = "Code Reviewer" });
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("code-reviewer", Case.Insensitive, customMessage: "the error must name the derived handle that's already taken");
        ex.Message.ShouldContain("Code Reviewer", customMessage: "the error must name the original input so the operator knows which create collided");
    }

    [Fact]
    public async Task Create_succeeds_when_only_a_store_snapshot_owns_the_handle()
    {
        var (teamId, userId) = await SeedTeamAsync();

        // A Library STORE snapshot owns "code-reviewer". Team-slug uniqueness is Working-only, so authoring a
        // runnable persona of the same name must NOT be refused — the snapshot and the bench persona coexist.
        await SeedStoreSnapshotAsync(teamId, userId, "code-reviewer");

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var id = await scope.Resolve<IMediator>().Send(new CreateAgentDefinitionCommand { Name = "Code Reviewer" });

        id.ShouldNotBe(Guid.Empty, "a store snapshot's handle must not block authoring a working persona of the same name");
    }

    [Theory]
    [InlineData("$$$")]
    [InlineData("   ")]
    public async Task Create_rejects_a_name_that_yields_no_handle(string name)
    {
        var (teamId, userId) = await SeedTeamAsync();
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        var act = async () => await scope.Resolve<IMediator>().Send(new CreateAgentDefinitionCommand { Name = name });
        await act.ShouldThrowAsync<Exception>(
            customMessage: "a name with no handle-able characters must be rejected (blank → ArgumentException, punctuation-only → InvalidOperationException) — never persisted with an empty handle");
    }

    [Fact]
    public async Task Handle_is_reusable_after_the_persona_is_soft_deleted()
    {
        var (teamId, userId) = await SeedTeamAsync();

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var firstId = await mediator.Send(new CreateAgentDefinitionCommand { Name = "Reviewer" });

        // Active duplicate is refused…
        await Should.ThrowAsync<InvalidOperationException>(() => mediator.Send(new CreateAgentDefinitionCommand { Name = "Reviewer" }));

        await mediator.Send(new DeleteAgentDefinitionCommand { AgentDefinitionId = firstId });

        // …but once soft-deleted the handle frees up (partial unique index excludes deleted rows).
        // A throw here would fail the test, so the successful create IS the "reusable" assertion.
        var secondId = await mediator.Send(new CreateAgentDefinitionCommand { Name = "Reviewer" });
        secondId.ShouldNotBe(firstId, "the re-created persona is a new row, not a revival of the deleted one");
    }

    [Fact]
    public async Task A_persona_is_invisible_and_immutable_to_another_team_on_every_verb()
    {
        var (teamA, userA) = await SeedTeamAsync();
        var (teamB, userB) = await SeedTeamAsync();

        Guid idInA;
        using (var scope = _fixture.BeginScopeAs(userA, teamA, Roles.Admin))
        {
            idInA = await scope.Resolve<IMediator>().Send(new CreateAgentDefinitionCommand { Name = "Team A Secret Agent", SystemPrompt = "original" });
        }

        using var attacker = _fixture.BeginScopeAs(userB, teamB, Roles.Admin);
        var mediator = attacker.Resolve<IMediator>();

        // Read — must not confirm existence.
        (await mediator.Send(new GetAgentDefinitionQuery { AgentDefinitionId = idInA }))
            .ShouldBeNull("a foreign team's Get MUST return null — returning the row is a cross-team leak");

        // Update — must not mutate.
        await Should.ThrowAsync<KeyNotFoundException>(() => mediator.Send(new UpdateAgentDefinitionCommand { AgentDefinitionId = idInA, Name = "Hijacked" }),
            customMessage: "a foreign team's Update MUST throw not-found — silently mutating another tenant's persona is a critical breach");

        // Delete — must not remove.
        await Should.ThrowAsync<KeyNotFoundException>(() => mediator.Send(new DeleteAgentDefinitionCommand { AgentDefinitionId = idInA }),
            customMessage: "a foreign team's Delete MUST throw not-found — a silent no-op delete would let an attacker probe / destroy another tenant's data");

        // Ground truth: A's persona is untouched.
        using var verify = _fixture.BeginScopeAs(userA, teamA, Roles.Admin);
        var stillThere = await verify.Resolve<IMediator>().Send(new GetAgentDefinitionQuery { AgentDefinitionId = idInA });
        stillThere!.Name.ShouldBe("Team A Secret Agent", "all of team B's verbs threw, so A's persona must be byte-for-byte unchanged");
        stillThere.SystemPrompt.ShouldBe("original");
    }

    [Fact]
    public async Task List_returns_only_this_teams_active_working_personas_oldest_first_excluding_store_snapshots()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var (otherTeam, otherUser) = await SeedTeamAsync();

        Guid keep1, keep2, deleted;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var m = scope.Resolve<IMediator>();
            keep1 = await m.Send(new CreateAgentDefinitionCommand { Name = "First" });
            keep2 = await m.Send(new CreateAgentDefinitionCommand { Name = "Second" });
            deleted = await m.Send(new CreateAgentDefinitionCommand { Name = "Doomed" });
            await m.Send(new DeleteAgentDefinitionCommand { AgentDefinitionId = deleted });
        }
        using (var scope = _fixture.BeginScopeAs(otherUser, otherTeam, Roles.Admin))
        {
            await scope.Resolve<IMediator>().Send(new CreateAgentDefinitionCommand { Name = "Other Team Agent" });
        }

        // A STORE snapshot in THIS team lives in the Library, not on the bench — the list must exclude it.
        await SeedStoreSnapshotAsync(teamId, userId, "store-snapshot");

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var list = await verify.Resolve<IMediator>().Send(new ListAgentDefinitionsQuery());

        list.Select(a => a.Id).ShouldBe(new[] { keep1, keep2 },
            customMessage: "List must return exactly this team's ACTIVE WORKING personas, ordered created-date ASC, excluding the soft-deleted one, the other team's, and any store snapshot");
    }

    [Fact]
    public async Task Updating_an_imported_persona_replaces_authored_fields_but_preserves_import_owned_fields()
    {
        // The format-preserving keystone: an imported persona carries skills / MCP / verbatim
        // frontmatter / provenance / origin / handle that the import slice owns. Editing its prompt
        // or model in the library MUST leave all of that intact — otherwise a re-sync would diverge
        // and the lossless-import promise is broken.
        var (teamId, userId) = await SeedTeamAsync();
        var packId = Guid.NewGuid();
        var id = await SeedImportedAgentAsync(teamId, userId, packId);

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            await scope.Resolve<IMediator>().Send(new UpdateAgentDefinitionCommand
            {
                AgentDefinitionId = id,
                Name = "Renamed In Library",
                SystemPrompt = "locally tweaked prompt",
                Model = "claude-haiku-4-5",
                Tools = new[] { "Read" },
            });
        }

        using var verify = _fixture.BeginScope();
        var row = await verify.Resolve<CodeSpaceDbContext>().AgentDefinition.AsNoTracking().SingleAsync(a => a.Id == id);

        // Authored surface changed…
        row.Name.ShouldBe("Renamed In Library");
        row.SystemPrompt.ShouldBe("locally tweaked prompt");
        row.Model.ShouldBe("claude-haiku-4-5");

        // …import-owned fields untouched.
        row.Origin.ShouldBe(AgentDefinitionOrigin.Imported, "Update must NOT flip an imported persona to Authored");
        row.Slug.ShouldBe("imported-reviewer", "the @-handle is immutable post-create — a rename must not change it");
        row.PackId.ShouldBe(packId, "import provenance must survive a library edit so re-sync still works");
        row.SourcePath.ShouldBe("agents/imported-reviewer.md");
        row.McpServersJson.ShouldContain("github", customMessage: "imported MCP servers must survive an authored edit");
        row.RawFrontmatterJson.ShouldContain("custom_future_key", customMessage: "the verbatim frontmatter blob must survive untouched — it's the lossless-forward-compat source");
    }

    [Fact]
    public async Task Instantiate_from_store_copies_content_into_a_new_working_unbound_persona_linked_to_the_snapshot()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var snapshotId = await SeedStoreSnapshotAsync(teamId, userId, "code-reviewer");

        Guid newId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            newId = await scope.Resolve<IMediator>().Send(new InstantiateAgentFromStoreCommand { SourceDefinitionId = snapshotId });

        newId.ShouldNotBe(snapshotId, "instantiate creates a NEW row, not a reference to the snapshot");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var copy = await db.AgentDefinition.AsNoTracking().SingleAsync(a => a.Id == newId);

        // A runnable bench copy…
        copy.Scope.ShouldBe(DefinitionScope.Working);
        copy.Origin.ShouldBe(AgentDefinitionOrigin.Imported);
        copy.Slug.ShouldBe("code-reviewer", "the snapshot's handle is free on the bench, so the copy keeps it");

        // …carrying the snapshot's content verbatim (compare the tool list against the snapshot's STORED value, since
        // the jsonb column canonicalizes whitespace on round-trip — what matters is the copy matches the snapshot)…
        var snapshot = await db.AgentDefinition.AsNoTracking().SingleAsync(a => a.Id == snapshotId);
        copy.SystemPrompt.ShouldBe("You are code-reviewer.");
        copy.Model.ShouldBe("claude-opus-4-8");
        copy.Description.ShouldBe(snapshot.Description);
        copy.ToolsJson.ShouldBe(snapshot.ToolsJson, "the tool allow-list is copied — it governs what the instantiated agent may run");
        copy.ToolsJson.ShouldContain("Read");
        copy.McpServersJson.ShouldContain("github");
        copy.RawFrontmatterJson.ShouldContain("custom_future_key");

        // …with provenance back to the snapshot, but no pack (so it never re-appears in the Library)…
        copy.SourceDefinitionId.ShouldBe(snapshotId);
        copy.SourceVersion.ShouldBe("v-code-reviewer", "the copy captures the snapshot's content version as the LHS of a future sync");
        copy.PackId.ShouldBeNull();

        // …unbound…
        (await db.AgentSkillBinding.CountAsync(b => b.AgentDefinitionId == newId)).ShouldBe(0, "a from-store copy starts with no skill bindings");

        // …and it lands on the bench while the snapshot stays in the Library.
        using var benchScope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var bench = await benchScope.Resolve<IMediator>().Send(new ListAgentDefinitionsQuery());
        bench.Select(a => a.Id).ShouldContain(newId);
        bench.Select(a => a.Id).ShouldNotContain(snapshotId, "the snapshot stays in the Library, off the bench");
    }

    [Fact]
    public async Task Instantiate_from_store_auto_disambiguates_a_handle_a_bench_persona_already_owns()
    {
        var (teamId, userId) = await SeedTeamAsync();

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            await scope.Resolve<IMediator>().Send(new CreateAgentDefinitionCommand { Name = "Code Reviewer" });   // bench already owns "code-reviewer"

        var snapshotId = await SeedStoreSnapshotAsync(teamId, userId, "code-reviewer");

        Guid newId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            newId = await scope.Resolve<IMediator>().Send(new InstantiateAgentFromStoreCommand { SourceDefinitionId = snapshotId });

        using var verify = _fixture.BeginScope();
        var copy = await verify.Resolve<CodeSpaceDbContext>().AgentDefinition.AsNoTracking().SingleAsync(a => a.Id == newId);
        copy.Slug.ShouldBe("code-reviewer-2", "the handle is taken on the bench, so instantiate suffixes rather than dead-ending");
    }

    [Fact]
    public async Task Instantiate_from_store_rejects_a_non_store_or_foreign_id()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var (otherTeam, otherUser) = await SeedTeamAsync();

        // A WORKING bench persona is not a store snapshot → not instantiable.
        Guid workingId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            workingId = await scope.Resolve<IMediator>().Send(new CreateAgentDefinitionCommand { Name = "Bench Agent" });

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            await Should.ThrowAsync<KeyNotFoundException>(() => scope.Resolve<IMediator>().Send(new InstantiateAgentFromStoreCommand { SourceDefinitionId = workingId }));

        // A foreign team's store snapshot → not accessible (no cross-team read).
        var foreignSnapshot = await SeedStoreSnapshotAsync(otherTeam, otherUser, "foreign-snap");
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            await Should.ThrowAsync<KeyNotFoundException>(() => scope.Resolve<IMediator>().Send(new InstantiateAgentFromStoreCommand { SourceDefinitionId = foreignSnapshot }));
    }

    /// <summary>Inserts a STORE snapshot (Origin=Imported, Scope=Store) with real content directly — the Library shape that must never surface on the bench list. Returns its id.</summary>
    private async Task<Guid> SeedStoreSnapshotAsync(Guid teamId, Guid userId, string slug)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        db.AgentDefinition.Add(new AgentDefinition
        {
            Id = id,
            TeamId = teamId,
            Slug = slug,
            Name = slug,
            Description = $"{slug} description",
            SystemPrompt = $"You are {slug}.",
            Model = "claude-opus-4-8",
            ToolsJson = "[\"Read\",\"Grep\"]",
            McpServersJson = "[{\"name\":\"github\"}]",
            RawFrontmatterJson = "{\"name\":\"" + slug + "\",\"custom_future_key\":7}",
            Origin = AgentDefinitionOrigin.Imported,
            Scope = DefinitionScope.Store,
            ContentVersion = "v-" + slug,
            PackId = null,   // pack provenance is irrelevant to the scope filter under test
            SourcePath = $"agents/{slug}.md",
            CreatedDate = now,
            CreatedBy = userId,
            LastModifiedDate = now,
            LastModifiedBy = userId,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedImportedAgentAsync(Guid teamId, Guid userId, Guid packId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = "imported-reviewer",
            Name = "Imported Reviewer",
            SystemPrompt = "imported prompt",
            Model = "claude-sonnet-4-6",
            McpServersJson = "[{\"name\":\"github\"}]",
            RawFrontmatterJson = "{\"name\":\"imported-reviewer\",\"custom_future_key\":42}",
            Origin = AgentDefinitionOrigin.Imported,
            PackId = packId,
            SourcePath = "agents/imported-reviewer.md",
            CreatedDate = now,
            CreatedBy = userId,
            LastModifiedDate = now,
            LastModifiedBy = userId,
        };
        db.AgentDefinition.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-user-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-team-{teamId:N}", Name = "Agent Test Team", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
