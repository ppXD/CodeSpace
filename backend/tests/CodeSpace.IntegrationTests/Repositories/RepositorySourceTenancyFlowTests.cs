using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Repositories;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Repositories;

/// <summary>
/// <see cref="IRepositorySourceService"/> loads a repository and hands its OWN credential to the provider, so the
/// repository id alone decides whose token is spent. The pipeline marker (<c>IRequireRepositoryAccess</c>) is the
/// gate, but agent-pack preview and import reached this service without it and read another team's private files —
/// so the loader itself refuses any repository outside the caller's team as well. Proven against real Postgres and the
/// real service; only the provider wire is a recording stand-in, so "the provider was never called" is observable.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RepositorySourceTenancyFlowTests
{
    private readonly PostgresFixture _fixture;

    public RepositorySourceTenancyFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    public enum Caller { OwnTeam, OtherTeam, NoTeam }

    [Theory]
    [InlineData(Caller.OwnTeam, true)]
    [InlineData(Caller.OtherTeam, false)]
    [InlineData(Caller.NoTeam, false)]
    public async Task Source_reads_reach_the_provider_only_for_a_repository_in_the_callers_team(Caller caller, bool reachesProvider)
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (repositoryId, credentialId) = await SeedBoundRepositoryAsync(teamA);

        var (userId, teamId) = caller switch
        {
            Caller.OwnTeam => (userA, (Guid?)teamA),
            Caller.OtherTeam => (userB, teamB),
            _ => (userA, (Guid?)null),
        };

        var wire = new RecordingSourceWire();
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test")).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<CodeSpace.Core.Services.Identity.ICurrentTeam>().SingleInstance();
            b.RegisterInstance(new ProviderRegistry([wire])).As<IProviderRegistry>().SingleInstance();
        });
        var service = scope.Resolve<IRepositorySourceService>();

        var reads = new Func<Task>[]
        {
            () => service.ListBranchesAsync(repositoryId, CancellationToken.None),
            () => service.ListTreeAsync(repositoryId, "agents", reference: null, CancellationToken.None),
            () => service.GetFileAsync(repositoryId, "agents/reviewer.md", reference: null, CancellationToken.None),
        };

        foreach (var read in reads)
        {
            if (reachesProvider) await read();
            else await Should.ThrowAsync<InvalidOperationException>(read, $"{caller}: a repository outside the caller's team reads as not found");
        }

        if (reachesProvider)
        {
            wire.Credentials.Count.ShouldBe(reads.Length, "every read reached the provider");
            wire.Credentials.ShouldAllBe(id => id == credentialId, "each read used the repository's own team credential");
            return;
        }

        wire.Credentials.ShouldBeEmpty($"{caller}: the provider must never be called — that would spend team A's credential for someone outside it");
    }

    private async Task<(Guid RepositoryId, Guid CredentialId)> SeedBoundRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "instance", BaseUrl = $"https://git-{suffix}.local" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.Pat, DisplayName = "connection",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "tkn" })), Status = CredentialStatus.Active,
        };
        var repository = new Repository
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "private",
            FullPath = $"acme/private-{suffix}", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://git.local/acme/private", Status = RepositoryStatus.Active,
        };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);
        await db.SaveChangesAsync();

        return (repository.Id, credential.Id);
    }

    /// <summary>Only the provider boundary is simulated; it records the credential each call was handed.</summary>
    private sealed class RecordingSourceWire : IRepositorySourceCapability
    {
        public List<Guid> Credentials { get; } = [];

        public ProviderKind Kind => ProviderKind.Git;

        public Task<IReadOnlyList<RemoteBranch>> ListBranchesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken)
        {
            Credentials.Add(context.Credential.Id);
            return Task.FromResult<IReadOnlyList<RemoteBranch>>([]);
        }

        public Task<IReadOnlyList<RemoteTreeEntry>> ListTreeAsync(ProviderContext context, RemoteRepository repository, string? path, string? reference, CancellationToken cancellationToken)
        {
            Credentials.Add(context.Credential.Id);
            return Task.FromResult<IReadOnlyList<RemoteTreeEntry>>([]);
        }

        public Task<RemoteFileContent> GetFileAsync(ProviderContext context, RemoteRepository repository, string path, string? reference, CancellationToken cancellationToken)
        {
            Credentials.Add(context.Credential.Id);
            return Task.FromResult(new RemoteFileContent { Path = path, Name = path, Text = "" });
        }
    }
}
