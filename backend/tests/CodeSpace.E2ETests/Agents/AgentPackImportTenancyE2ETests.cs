using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>
/// Agent-pack preview and import read a bound repository through the repository's own credential, so the repository id
/// in the body decides whose token is spent. Both requests used to carry that id under team membership alone, and the
/// loader filtered on id only: a member of team B posting team A's repository id read A's private files through A's
/// credential and imported them as B's agents. This pins the outsider refusal and the member path over real HTTP.
///
/// <para>Tier: 🟡 Medium-mock (Rule 12) — the real ASP.NET pipeline (routing, JWT auth, <c>X-Team-Id</c>, model
/// binding, the controller, the exception filter, the mediator and every authorization behavior), the real
/// <c>AgentPackImportService</c> / <c>RepositorySourceService</c> / parser / persona writer, and real Postgres. Only
/// the provider wire is a recording stub registered for the generic Git kind (which ships no source capability): the
/// external host cannot be called here, and what matters is WHETHER it is called and with whose credential, which the
/// stub records. Each test seeds GUID-suffixed teams and repository paths, so the shared stub's log is keyed per test.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class AgentPackImportTenancyE2ETests : IClassFixture<AgentPackImportTenancyE2ETests.RecordingSourceApiFactory>
{
    private const string AgentPath = "agents/reviewer.md";
    private const string PrivateBody = "TEAM-A-PRIVATE-PROMPT";

    private readonly RecordingSourceApiFactory _factory;

    public AgentPackImportTenancyE2ETests(RecordingSourceApiFactory factory) { _factory = factory; }

    [Theory]
    [InlineData("/api/agents/import-preview")]
    [InlineData("/api/agents/import")]
    public async Task An_outsider_naming_another_teams_repository_is_refused_before_that_teams_credential_is_spent(string endpoint)
    {
        var teamA = await SeedTeamAsync(TeamRole.Owner);
        var repo = await SeedBoundRepositoryAsync(teamA.TeamId);
        var outsider = await SeedTeamAsync(TeamRole.Owner);   // Owner of an unrelated team: the highest standing there is, anywhere but A

        using var response = await SendAsync(outsider.UserId, outsider.TeamId, endpoint, Body(repo.RepositoryId));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, customMessage: $"{endpoint}: another team's repository id must be refused, got {(int)response.StatusCode} {body}");
        body.ShouldContain(FailureCodes.Forbidden);
        body.ShouldNotContain(PrivateBody, customMessage: "no byte of team A's repository reaches the outsider");

        _factory.Wire.CallsFor(repo.FullPath).ShouldBeEmpty($"{endpoint}: the provider must never be called — that call would spend team A's credential for team B");
        (await AgentSlugsAsync(outsider.TeamId)).ShouldBeEmpty("nothing is imported into the outsider's team");
    }

    [Fact]
    public async Task An_unknown_repository_id_is_the_same_refusal_as_a_foreign_one()
    {
        // Before the marker an unknown id answered 400 "Repository <id> not found" while a foreign id answered 200 —
        // an existence oracle. Both now land on the same opaque 403.
        var outsider = await SeedTeamAsync(TeamRole.Owner);

        using var response = await SendAsync(outsider.UserId, outsider.TeamId, "/api/agents/import-preview", Body(Guid.NewGuid()));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, customMessage: await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_member_previews_and_imports_their_own_teams_repository_through_its_credential()
    {
        var teamA = await SeedTeamAsync(TeamRole.Owner);
        var repo = await SeedBoundRepositoryAsync(teamA.TeamId);
        var memberId = await AddMemberAsync(teamA.TeamId, TeamRole.Member);

        using var preview = await SendAsync(memberId, teamA.TeamId, "/api/agents/import-preview", Body(repo.RepositoryId));
        var previewBody = await preview.Content.ReadAsStringAsync();

        preview.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: previewBody);
        var item = JsonDocument.Parse(previewBody).RootElement.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("sourcePath").GetString().ShouldBe(AgentPath);
        item.GetProperty("systemPrompt").GetString()!.ShouldContain(PrivateBody, customMessage: "the member reads their own team's pack");

        using var import = await SendAsync(memberId, teamA.TeamId, "/api/agents/import", Body(repo.RepositoryId));
        var importBody = await import.Content.ReadAsStringAsync();

        import.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: importBody);
        JsonDocument.Parse(importBody).RootElement.EnumerateArray().ShouldHaveSingleItem().GetProperty("outcome").GetString().ShouldBe("Imported", customMessage: importBody);
        (await AgentSlugsAsync(teamA.TeamId)).ShouldBe(new[] { "pack-reviewer" });

        _factory.Wire.CallsFor(repo.FullPath).ShouldAllBe(call => call.CredentialId == repo.CredentialId, "every provider call used the repository's own team credential");
        _factory.Wire.CallsFor(repo.FullPath).ShouldNotBeEmpty("fixture check: the member path really reaches the provider wire");
    }

    // ─── Seeding / plumbing ─────────────────────────────────────────────────

    private sealed record SeededTeam(Guid UserId, Guid TeamId);

    private sealed record SeededRepository(Guid RepositoryId, Guid CredentialId, string FullPath);

    private static object Body(Guid repositoryId) => new { repositoryId, selectedSourcePaths = new[] { AgentPath } };

    private async Task<HttpResponseMessage> SendAsync(Guid userId, Guid teamId, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<SeededTeam> SeedTeamAsync(TeamRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"pack-{suffix}@test.local", Name = "Pack " + suffix, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"pack-{suffix}", Name = "Pack " + suffix, Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return new SeededTeam(userId, teamId);
    }

    private async Task<Guid> AddMemberAsync(Guid teamId, TeamRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"pack-member-{userId:N}@test.local", Name = "Pack member", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return userId;
    }

    private async Task<SeededRepository> SeedBoundRepositoryAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var serializer = scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "Git", BaseUrl = $"https://git-{suffix}.test.local", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.Pat, DisplayName = "team token", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "fake-pack-token" })), Status = CredentialStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = $"private-{suffix}", FullPath = $"acme/private-{suffix}", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"https://git-{suffix}.test.local/acme/private-{suffix}", Status = RepositoryStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);

        await db.SaveChangesAsync();
        return new SeededRepository(repository.Id, credential.Id, repository.FullPath);
    }

    private async Task<IReadOnlyList<string>> AgentSlugsAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        return await db.AgentDefinition.AsNoTracking().Where(d => d.TeamId == teamId).Select(d => d.Slug).ToListAsync();
    }

    /// <summary>The real host, plus a recording source wire for the generic Git kind.</summary>
    public sealed class RecordingSourceApiFactory : TaskLaunchApiFactory
    {
        public RecordingSourceWire Wire { get; } = new();

        protected override void ConfigureContractTestServices(ContainerBuilder builder) =>
            builder.RegisterInstance(Wire).As<IProviderCapability>().SingleInstance();
    }

    /// <summary>Answers one agent file under <c>agents/</c> and records every call with the credential it was handed.</summary>
    public sealed class RecordingSourceWire : IRepositorySourceCapability
    {
        private readonly ConcurrentQueue<SourceCall> _calls = new();

        public ProviderKind Kind => ProviderKind.Git;

        public IReadOnlyList<SourceCall> CallsFor(string repositoryFullPath) => _calls.Where(call => call.RepositoryFullPath == repositoryFullPath).ToList();

        public Task<IReadOnlyList<RemoteBranch>> ListBranchesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken)
        {
            Record(context, repository);
            return Task.FromResult<IReadOnlyList<RemoteBranch>>([]);
        }

        public Task<IReadOnlyList<RemoteTreeEntry>> ListTreeAsync(ProviderContext context, RemoteRepository repository, string? path, string? reference, CancellationToken cancellationToken)
        {
            Record(context, repository);
            return Task.FromResult<IReadOnlyList<RemoteTreeEntry>>([new RemoteTreeEntry { Name = "reviewer.md", Path = AgentPath, Type = RemoteTreeEntryType.File }]);
        }

        public Task<RemoteFileContent> GetFileAsync(ProviderContext context, RemoteRepository repository, string path, string? reference, CancellationToken cancellationToken)
        {
            Record(context, repository);
            return Task.FromResult(new RemoteFileContent { Path = path, Name = "reviewer.md", Text = $"---\nname: pack-reviewer\n---\n{PrivateBody}\n" });
        }

        private void Record(ProviderContext context, RemoteRepository repository) => _calls.Enqueue(new SourceCall(repository.FullPath, context.Credential.Id));
    }

    public sealed record SourceCall(string RepositoryFullPath, Guid CredentialId);
}
