using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.ModelCredentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.ModelCredentials;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Full-pipeline coverage for the model-credential CRUD surface, driven through MediatR (so the
/// team-membership authorization behaviour, the handlers, and <c>ModelCredentialService</c> all run) on real
/// Postgres. The load-bearing assertions are the security ones: the key is encrypted at rest, only a masked
/// hint is ever read back, and a foreign team's credential is invisible + immutable.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ModelCredentialFlowTests
{
    private readonly PostgresFixture _fixture;

    public ModelCredentialFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Add_then_list_returns_a_masked_summary_and_encrypts_at_rest()
    {
        const string key = "sk-ant-supersecret-9f3a";
        var (userId, teamId) = await SeedTeamAsync();

        var id = await SendAsync(userId, teamId, new AddModelCredentialCommand { Provider = "Anthropic", DisplayName = "Team Anthropic", ApiKey = key, BaseUrl = "https://api.anthropic.com" });

        // Encrypted at rest — the stored column is not the plaintext, but decrypts back to it.
        using (var scope = _fixture.BeginScope())
        {
            var row = await scope.Resolve<CodeSpaceDbContext>().ModelCredential.AsNoTracking().SingleAsync(c => c.Id == id);
            row.EncryptedApiKey.ShouldNotBeNull();
            row.EncryptedApiKey!.ShouldNotContain("supersecret");
            scope.Resolve<IPayloadEncryptor>().Decrypt(row.EncryptedApiKey).ShouldBe(key);
        }

        var list = await SendAsync(userId, teamId, new ListModelCredentialsQuery());
        var summary = list.ShouldHaveSingleItem();
        summary.Provider.ShouldBe("Anthropic");
        summary.DisplayName.ShouldBe("Team Anthropic");
        summary.BaseUrl.ShouldBe("https://api.anthropic.com");
        summary.KeyHint.ShouldBe("····9f3a");
        summary.KeyHint!.ShouldNotContain("supersecret");
        summary.KeyUnreadable.ShouldBeFalse("a decryptable key is not flagged unreadable");
    }

    [Fact]
    public async Task A_keyless_credential_lists_with_no_hint()
    {
        var (userId, teamId) = await SeedTeamAsync();

        await SendAsync(userId, teamId, new AddModelCredentialCommand { Provider = "Ollama", DisplayName = "Local", ApiKey = null, BaseUrl = "http://localhost:11434" });

        var summary = (await SendAsync(userId, teamId, new ListModelCredentialsQuery())).ShouldHaveSingleItem();
        summary.KeyHint.ShouldBeNull();
        summary.KeyUnreadable.ShouldBeFalse("a genuinely keyless provider is not flagged unreadable");
        summary.BaseUrl.ShouldBe("http://localhost:11434");
    }

    [Fact]
    public async Task A_credential_whose_key_cannot_be_decrypted_still_lists_with_a_null_hint()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var id = await SendAsync(userId, teamId, new AddModelCredentialCommand { Provider = "Anthropic", DisplayName = "Stale key", ApiKey = "sk-rotated-away", BaseUrl = "https://api.anthropic.com" });

        // Simulate a Data Protection key-ring change (rotation / loss / the #725 Postgres key-ring migration): the
        // stored ciphertext can no longer be decrypted. The list MUST still render — one unreadable secret cannot 500
        // the whole credentials page, because the operator needs that very list to re-enter the dead keys.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var row = await db.ModelCredential.SingleAsync(c => c.Id == id);
            row.EncryptedApiKey = "not-a-readable-protected-payload";
            await db.SaveChangesAsync();
        }

        var summary = (await SendAsync(userId, teamId, new ListModelCredentialsQuery())).ShouldHaveSingleItem();
        summary.DisplayName.ShouldBe("Stale key");
        summary.BaseUrl.ShouldBe("https://api.anthropic.com");
        summary.Status.ShouldBe(CredentialStatus.Active);
        summary.KeyHint.ShouldBeNull("an undecryptable key yields no hint rather than throwing the whole list");
        summary.KeyUnreadable.ShouldBeTrue("a stored-but-undecryptable key is flagged for re-entry, not shown as keyless");
    }

    [Fact]
    public async Task Update_rotates_the_key_only_when_one_is_supplied()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var id = await SendAsync(userId, teamId, new AddModelCredentialCommand { Provider = "OpenAI", DisplayName = "Old name", ApiKey = "sk-old-aaaa" });

        // No key supplied → display name + base url change, key kept.
        await SendAsync(userId, teamId, new UpdateModelCredentialCommand { Id = id, DisplayName = "New name", BaseUrl = "https://gw/v1" });
        (await DecryptKeyAsync(id)).ShouldBe("sk-old-aaaa", "a blank key on update keeps the existing one");

        // Key supplied → rotated.
        await SendAsync(userId, teamId, new UpdateModelCredentialCommand { Id = id, DisplayName = "New name", ApiKey = "sk-new-bbbb" });
        (await DecryptKeyAsync(id)).ShouldBe("sk-new-bbbb", "a non-blank key rotates it");

        var summary = (await SendAsync(userId, teamId, new ListModelCredentialsQuery())).ShouldHaveSingleItem();
        summary.DisplayName.ShouldBe("New name");
        summary.KeyHint.ShouldBe("····bbbb");
    }

    [Fact]
    public async Task Revoke_soft_deletes_clears_the_key_and_drops_from_the_list()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var id = await SendAsync(userId, teamId, new AddModelCredentialCommand { Provider = "OpenAI", DisplayName = "Doomed", ApiKey = "sk-doomed-cccc" });

        await SendAsync(userId, teamId, new RevokeModelCredentialCommand { Id = id });

        (await SendAsync(userId, teamId, new ListModelCredentialsQuery())).ShouldBeEmpty("a revoked credential is gone from the list");

        using var scope = _fixture.BeginScope();
        var row = await scope.Resolve<CodeSpaceDbContext>().ModelCredential.AsNoTracking().SingleAsync(c => c.Id == id);
        row.Status.ShouldBe(CredentialStatus.Revoked);
        row.DeletedDate.ShouldNotBeNull();
        row.EncryptedApiKey.ShouldBeNull("revoke also drops the key material");
    }

    [Fact]
    public async Task Setting_a_default_model_marks_one_and_clears_the_previous()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await SendAsync(userId, teamId, new AddModelCredentialCommand { Provider = "OpenAI", DisplayName = "GW", ApiKey = "sk", BaseUrl = "https://gw/v1" });
        var m1 = await SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "model-a" });
        var m2 = await SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "model-b" });

        await SendAsync(userId, teamId, new SetDefaultCredentialedModelCommand { ModelCredentialId = credId, ModelRowId = m1 });
        var afterFirst = await SendAsync(userId, teamId, new ListCredentialedModelsQuery { ModelCredentialId = credId });
        afterFirst.Single(m => m.Id == m1).IsDefault.ShouldBeTrue();
        afterFirst.Single(m => m.Id == m2).IsDefault.ShouldBeFalse();

        // Re-marking moves the star — at most ONE default per credential, the previous is cleared.
        await SendAsync(userId, teamId, new SetDefaultCredentialedModelCommand { ModelCredentialId = credId, ModelRowId = m2 });
        var afterMove = await SendAsync(userId, teamId, new ListCredentialedModelsQuery { ModelCredentialId = credId });
        afterMove.Single(m => m.Id == m1).IsDefault.ShouldBeFalse("the previous default is cleared");
        afterMove.Single(m => m.Id == m2).IsDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task Setting_a_disabled_model_as_default_is_rejected()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await SendAsync(userId, teamId, new AddModelCredentialCommand { Provider = "OpenAI", DisplayName = "GW", ApiKey = "sk" });
        var rowId = await SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "model-x" });

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var row = await db.ModelCredentialModel.SingleAsync(m => m.Id == rowId);
            row.Enabled = false;
            await db.SaveChangesAsync();
        }

        // A hidden (disabled) model can't be the auto-default — it's not in the usable pool, so the run could never use it.
        await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(userId, teamId, new SetDefaultCredentialedModelCommand { ModelCredentialId = credId, ModelRowId = rowId }));
    }

    [Fact]
    public async Task A_foreign_teams_credential_is_invisible_and_not_mutable()
    {
        var (userA, teamA) = await SeedTeamAsync();
        var (userB, teamB) = await SeedTeamAsync();
        var idInB = await SendAsync(userB, teamB, new AddModelCredentialCommand { Provider = "OpenAI", DisplayName = "B's key", ApiKey = "sk-team-b" });

        (await SendAsync(userA, teamA, new ListModelCredentialsQuery())).ShouldBeEmpty("team A can't see team B's credential");

        await Should.ThrowAsync<KeyNotFoundException>(() => SendAsync(userA, teamA, new UpdateModelCredentialCommand { Id = idInB, DisplayName = "hijack", ApiKey = "sk-evil" }));
        await Should.ThrowAsync<KeyNotFoundException>(() => SendAsync(userA, teamA, new RevokeModelCredentialCommand { Id = idInB }));

        // B's credential is untouched.
        (await DecryptKeyAsync(idInB)).ShouldBe("sk-team-b");
    }

    private async Task<TResult> SendAsync<TResult>(Guid userId, Guid teamId, IRequest<TResult> request)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(request);
    }

    private async Task<string> DecryptKeyAsync(Guid id)
    {
        using var scope = _fixture.BeginScope();
        var row = await scope.Resolve<CodeSpaceDbContext>().ModelCredential.AsNoTracking().SingleAsync(c => c.Id == id);
        return scope.Resolve<IPayloadEncryptor>().Decrypt(row.EncryptedApiKey!);
    }

    private async Task<(Guid UserId, Guid TeamId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"mc-{userId:N}@test.local", Name = $"mc-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"mc-{teamId:N}", Name = "MC Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (userId, teamId);
    }
}
