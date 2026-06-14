using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// SAFETY pin over the REAL DI graph for the act-as-user git writes exposed on the tool fabric (git.open_pr /
/// git.pr_review). Proves the impersonation hole is closed end-to-end: invoking these via
/// <see cref="IAgentToolRegistry"/> with a model-supplied <c>actAsUserId</c> does NOT spend that user's stored
/// provider credential — the actor key is stripped before the node runs, so the write acts as the repo
/// CONNECTION credential.
///
/// <para>The decisive signal needs no node-output change: the named victim has NO linked provider identity. On
/// the engine respond path a wired actAsUserId for an unlinked user throws ActorIdentityRequiredException
/// (ActorCredentialProvider.RequireAsync → ActorIdentityResolver). So if the tool path honoured the
/// model-supplied id the call would ERROR; because it strips it, the connection credential resolves and the
/// call SUCCEEDS. A regression that stops stripping flips this test red.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentToolActorStrippingFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentToolActorStrippingFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Git_pr_review_via_the_tool_path_ignores_a_model_supplied_actAsUserId()
    {
        var seed = await SeedAsync();

        // The model names an UNLINKED victim. Stripped → connection credential → success.
        var input = JsonSerializer.SerializeToElement(new { repositoryId = seed.RepositoryId.ToString(), number = 5, verdict = "comment", body = "looks good", actAsUserId = seed.VictimUserId.ToString() });

        var result = await CallAsync("git.pr_review", input, seed.TeamId);

        result.IsError.ShouldBeFalse("a model-supplied actAsUserId must be STRIPPED on the tool path — the review acts as the repo connection credential, never spends the named user's stored token (no ActorIdentityRequiredException, no forged authorship)");
    }

    [Fact]
    public async Task Git_open_pr_via_the_tool_path_ignores_a_model_supplied_actAsUserId()
    {
        var seed = await SeedAsync();

        var input = JsonSerializer.SerializeToElement(new { repositoryId = seed.RepositoryId.ToString(), title = "t", sourceBranch = "feature", targetBranch = "main", actAsUserId = seed.VictimUserId.ToString() });

        var result = await CallAsync("git.open_pr", input, seed.TeamId);

        result.IsError.ShouldBeFalse("a model-supplied actAsUserId must be STRIPPED on the tool path — the open acts as the repo connection credential, never as the named user");
    }

    private async Task<AgentToolResult> CallAsync(string kind, JsonElement input, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var tool = scope.Resolve<IAgentToolRegistry>().Resolve(kind);
        tool.ShouldNotBeNull($"{kind} must project onto the tool fabric via DI");

        return await tool!.CallAsync(new AgentToolCall { Input = input, TeamId = teamId }, CancellationToken.None);
    }

    private async Task<SeedResult> SeedAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        string Pat(string token) => encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = token }));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var victim = new User { Id = Guid.NewGuid(), Email = $"v-{suffix}@x", Name = "victim" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = user.Id };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.Git, DisplayName = "instance",
            BaseUrl = $"https://git-{suffix}.local", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret")
        };
        var connection = new Credential
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService,
            AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = Pat("conn"), Status = CredentialStatus.Active
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = connection.Id,
            ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://git.local/acme/api", Status = RepositoryStatus.Active
        };

        db.User.Add(user);
        db.User.Add(victim);            // deliberately has NO UserProviderIdentity on this instance
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(connection);
        db.Repository.Add(repo);

        await db.SaveChangesAsync();

        return new SeedResult(team.Id, repo.Id, victim.Id);
    }

    private sealed record SeedResult(Guid TeamId, Guid RepositoryId, Guid VictimUserId);
}
