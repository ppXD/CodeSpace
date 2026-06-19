using Autofac;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Commands.ModelCredentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.ModelCredentials;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Full-pipeline coverage for the credential's MODEL-LIST management surface, driven through MediatR (so the
/// team-membership authorization behaviour + the thin handlers + <c>ModelCredentialService</c> all run) on real
/// Postgres. The load-bearing assertion is the security one: a foreign team's credential is invisible and
/// immutable on every model-list operation. Also pins the manual-add provenance, the per-credential duplicate
/// guard, and the blank-id guard.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CredentialedModelManagementFlowTests
{
    private readonly PostgresFixture _fixture;

    public CredentialedModelManagementFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Add_then_list_returns_the_manual_model()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);

        var rowId = await SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "  claude-opus-4-8  ", DisplayName = "Opus 4.8" });

        var list = await SendAsync(userId, teamId, new ListCredentialedModelsQuery { ModelCredentialId = credId });
        var model = list.ShouldHaveSingleItem();
        model.Id.ShouldBe(rowId);
        model.ModelId.ShouldBe("claude-opus-4-8", "the model id is trimmed on the way in");
        model.DisplayName.ShouldBe("Opus 4.8");
        model.Enabled.ShouldBeTrue("a freshly added model is usable by default");

        // The manual provenance is persisted (not surfaced on the pick DTO, but stored for the refresh upsert).
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking().SingleAsync(m => m.Id == rowId)).Source.ShouldBe(ModelSource.Manual);
    }

    [Fact]
    public async Task A_duplicate_model_on_one_credential_is_rejected()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);

        await SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "gpt-5.4" });

        await Should.ThrowAsync<InvalidOperationException>(() =>
            SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "gpt-5.4" }));
    }

    [Fact]
    public async Task A_blank_model_id_is_rejected()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);

        await Should.ThrowAsync<ArgumentException>(() =>
            SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "   " }));
    }

    [Fact]
    public async Task Remove_takes_the_model_off_the_list()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);

        var keep = await SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "claude-opus-4-8" });
        var drop = await SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "claude-haiku-4-5" });

        await SendAsync(userId, teamId, new RemoveCredentialedModelCommand { ModelCredentialId = credId, ModelRowId = drop });

        var list = await SendAsync(userId, teamId, new ListCredentialedModelsQuery { ModelCredentialId = credId });
        list.ShouldHaveSingleItem().Id.ShouldBe(keep);
    }

    [Fact]
    public async Task A_model_row_cannot_be_removed_through_a_sibling_credential_in_the_same_team()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credA = await AddCredentialAsync(userId, teamId);
        var credB = await AddCredentialAsync(userId, teamId);

        var rowInA = await SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credA, ModelId = "claude-opus-4-8" });

        // Same team, but the row lives under credA — addressing it via credB's route must NOT delete it (the
        // remove is scoped to (row id AND credential id), so a guessed row id can't cross credentials).
        await Should.ThrowAsync<KeyNotFoundException>(() =>
            SendAsync(userId, teamId, new RemoveCredentialedModelCommand { ModelCredentialId = credB, ModelRowId = rowInA }));

        (await SendAsync(userId, teamId, new ListCredentialedModelsQuery { ModelCredentialId = credA })).ShouldHaveSingleItem().Id.ShouldBe(rowInA);
    }

    [Fact]
    public async Task Case_distinct_model_ids_coexist_on_one_credential()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);

        // Wire model ids are case-sensitive — the duplicate guard is byte-exact, so these are two distinct models.
        await SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "gpt-5.4" });
        await SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "GPT-5.4" });

        var list = await SendAsync(userId, teamId, new ListCredentialedModelsQuery { ModelCredentialId = credId });
        var ids = list.Select(m => m.ModelId).ToList();
        ids.Count.ShouldBe(2, "case-distinct ids are two separate models");
        ids.ShouldContain("gpt-5.4");
        ids.ShouldContain("GPT-5.4");
    }

    [Fact]
    public async Task Listing_a_credential_with_no_models_is_empty()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);

        (await SendAsync(userId, teamId, new ListCredentialedModelsQuery { ModelCredentialId = credId })).ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_teams_credential_is_invisible_and_immutable()
    {
        var (ownerUserId, ownerTeamId) = await SeedTeamAsync();
        var (otherUserId, otherTeamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(ownerUserId, ownerTeamId);
        var rowId = await SendAsync(ownerUserId, ownerTeamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "claude-opus-4-8" });

        // The other team is a member of its OWN team but the credential belongs to a foreign team → not found on
        // every model-list operation (the service scopes to the caller's current team).
        await Should.ThrowAsync<KeyNotFoundException>(() => SendAsync(otherUserId, otherTeamId, new ListCredentialedModelsQuery { ModelCredentialId = credId }));
        await Should.ThrowAsync<KeyNotFoundException>(() => SendAsync(otherUserId, otherTeamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "evil" }));
        await Should.ThrowAsync<KeyNotFoundException>(() => SendAsync(otherUserId, otherTeamId, new RemoveCredentialedModelCommand { ModelCredentialId = credId, ModelRowId = rowId }));

        // The owner's list is untouched by the foreign attempts.
        (await SendAsync(ownerUserId, ownerTeamId, new ListCredentialedModelsQuery { ModelCredentialId = credId })).ShouldHaveSingleItem().Id.ShouldBe(rowId);
    }

    // ─── Helpers (mirror ModelCredentialFlowTests) ───

    private async Task<Guid> AddCredentialAsync(Guid userId, Guid teamId) =>
        await SendAsync(userId, teamId, new AddModelCredentialCommand { Provider = "Anthropic", DisplayName = "Team Anthropic", ApiKey = "sk-x", BaseUrl = "https://api.anthropic.com" });

    private async Task<TResult> SendAsync<TResult>(Guid userId, Guid teamId, IRequest<TResult> request)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(request);
    }

    private async Task<(Guid UserId, Guid TeamId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"cmm-{userId:N}@test.local", Name = $"cmm-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"cmm-{teamId:N}", Name = "Model Mgmt Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (userId, teamId);
    }
}
