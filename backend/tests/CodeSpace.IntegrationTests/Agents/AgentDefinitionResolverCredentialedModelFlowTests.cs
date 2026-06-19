using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The dispatch-time resolution of a picked credentialed model (<c>AgentTask.ModelCredentialModelId</c>) against real
/// Postgres: the row EXPANDS into the loose model + credential on BOTH an inline run and a persona run (where it takes
/// node-level precedence), a foreign / revoked / missing row FAILS CLOSED (a clean node failure, never a silent
/// fallback), and an absent reference leaves the task byte-identical. This is the operator-authoring half of the
/// credential-rooted catalog — the executor downstream uses the expanded model + credential exactly as before.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentDefinitionResolverCredentialedModelFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentDefinitionResolverCredentialedModelFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_picked_credentialed_model_expands_into_model_and_credential_on_an_inline_run()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic");
        var rowId = await AddModelAsync(credId, "claude-opus-4-8");

        // Inline run (no persona) authoring ONLY the picked model — no loose model/credential.
        var task = new AgentTask { Goal = "g", Harness = "claude-code", ModelCredentialModelId = rowId };

        var resolved = await ResolveAsync(task, teamId);

        resolved.Model.ShouldBe("claude-opus-4-8", "the row's model id is stamped");
        resolved.ModelCredentialId.ShouldBe(credId, "the row's backing credential is stamped from the same choice");
        resolved.ModelCredentialModelId.ShouldBe(rowId, "the reference is kept as provenance");
    }

    [Fact]
    public async Task A_picked_credentialed_model_overrides_a_loose_model_and_wins_over_a_persona_on_both_axes()
    {
        var teamId = await SeedTeamAsync();
        var pickedCred = await SeedCredentialAsync(teamId, "Anthropic");
        var rowId = await AddModelAsync(pickedCred, "claude-opus-4-8");
        var personaCred = await SeedCredentialAsync(teamId, "Anthropic");
        var personaId = await SeedPersonaAsync(teamId, model: "claude-haiku-4-5", modelCredentialId: personaCred);

        // A loose model, a persona model, AND a persona credential all exist — the explicit pick must win on BOTH the
        // model axis and the credential axis (so the persona merge can't clobber the expanded values back).
        var task = new AgentTask { Goal = "g", Harness = "claude-code", Model = "loose-model", AgentDefinitionId = personaId, ModelCredentialModelId = rowId };

        var resolved = await ResolveAsync(task, teamId);

        resolved.Model.ShouldBe("claude-opus-4-8", "the picked model beats both the loose model and the persona's model");
        resolved.ModelCredentialId.ShouldBe(pickedCred, "the picked model's credential beats the persona's own credential");
    }

    [Fact]
    public async Task A_picked_but_disabled_model_fails_closed()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic");
        var rowId = await AddModelAsync(credId, "claude-opus-4-8");
        await DisableModelAsync(rowId);

        // A disabled model is "not part of the usable pool" — a pinned-then-disabled model fails rather than runs.
        var task = new AgentTask { Goal = "g", Harness = "claude-code", ModelCredentialModelId = rowId };

        await Should.ThrowAsync<AgentDefinitionResolutionException>(() => ResolveAsync(task, teamId));
    }

    [Fact]
    public async Task A_picked_model_under_a_non_active_but_undeleted_credential_fails_closed()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic");
        var rowId = await AddModelAsync(credId, "claude-opus-4-8");
        await SetCredentialStatusAsync(credId, CredentialStatus.Expired);   // status-only, DeletedDate stays null

        // Isolates the Status == Active clause: an Expired/Error credential fails closed even when not soft-deleted.
        var task = new AgentTask { Goal = "g", Harness = "claude-code", ModelCredentialModelId = rowId };

        await Should.ThrowAsync<AgentDefinitionResolutionException>(() => ResolveAsync(task, teamId));
    }

    [Fact]
    public async Task A_run_with_no_picked_model_is_byte_identical()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic");

        var task = new AgentTask { Goal = "g", Harness = "claude-code", Model = "gpt-5.4", ModelCredentialId = credId };

        var resolved = await ResolveAsync(task, teamId);

        resolved.Model.ShouldBe("gpt-5.4", "no pick → the loose model is untouched");
        resolved.ModelCredentialId.ShouldBe(credId);
        resolved.ModelCredentialModelId.ShouldBeNull();
    }

    [Fact]
    public async Task A_picked_model_under_a_foreign_team_fails_closed()
    {
        var ownerTeam = await SeedTeamAsync();
        var otherTeam = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(ownerTeam, "Anthropic");
        var rowId = await AddModelAsync(credId, "claude-opus-4-8");

        // Resolving the owner team's model under another team must throw — never resolve a cross-team model.
        var task = new AgentTask { Goal = "g", Harness = "claude-code", ModelCredentialModelId = rowId };

        await Should.ThrowAsync<AgentDefinitionResolutionException>(() => ResolveAsync(task, otherTeam));
    }

    [Fact]
    public async Task A_picked_model_whose_credential_was_revoked_fails_closed()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic");
        var rowId = await AddModelAsync(credId, "claude-opus-4-8");
        await RevokeCredentialAsync(credId);

        // The operator picked this exact model; its credential is now gone → fail closed, do NOT silently fall back.
        var task = new AgentTask { Goal = "g", Harness = "claude-code", ModelCredentialModelId = rowId };

        await Should.ThrowAsync<AgentDefinitionResolutionException>(() => ResolveAsync(task, teamId));
    }

    [Fact]
    public async Task A_missing_model_row_fails_closed()
    {
        var teamId = await SeedTeamAsync();

        var task = new AgentTask { Goal = "g", Harness = "claude-code", ModelCredentialModelId = Guid.NewGuid() };

        await Should.ThrowAsync<AgentDefinitionResolutionException>(() => ResolveAsync(task, teamId));
    }

    // ─── Helpers ───

    private async Task<AgentTask> ResolveAsync(AgentTask task, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentDefinitionResolver>().ResolveAsync(task, teamId, CancellationToken.None);
    }

    private async Task<Guid> AddModelAsync(Guid credId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = id, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task RevokeCredentialAsync(Guid credId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var cred = await db.ModelCredential.FindAsync(credId);
        cred!.Status = CredentialStatus.Revoked;
        cred.DeletedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task SetCredentialStatusAsync(Guid credId, CredentialStatus status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var cred = await db.ModelCredential.FindAsync(credId);
        cred!.Status = status;   // DeletedDate stays null — isolates the Status clause
        await db.SaveChangesAsync();
    }

    private async Task DisableModelAsync(Guid rowId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var row = await db.ModelCredentialModel.FindAsync(rowId);
        row!.Enabled = false;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, string provider)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = provider, DisplayName = provider + " cred",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("sk-x"), Status = CredentialStatus.Active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedPersonaAsync(Guid teamId, string model, Guid? modelCredentialId = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.AgentDefinition.Add(new AgentDefinition
        {
            Id = id, TeamId = teamId, Slug = $"persona-{id:N}"[..20], Name = "Persona",
            SystemPrompt = "You are a helper.", Model = model, ModelCredentialId = modelCredentialId,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"acm-{userId:N}@test.local", Name = $"acm-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"acm-{teamId:N}", Name = "Resolver Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
