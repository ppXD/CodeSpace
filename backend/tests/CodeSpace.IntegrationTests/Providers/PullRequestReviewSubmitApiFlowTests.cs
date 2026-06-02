using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// Full-pipeline coverage for the synchronous "submit PR review as me" endpoint
/// (<see cref="SubmitPullRequestReviewCommand"/>), driven through MediatR so the handler's
/// actor-from-<c>ICurrentUser</c> wiring, the repository-access authorization behaviour, the real
/// <c>IProviderRegistry</c>, and <c>PullRequestService</c> all run together on real Postgres. The
/// test-only <c>ProviderKind.Git</c> double echoes the acting credential's id as the review's
/// <c>ExternalId</c>, so the linked case can assert WHICH credential authenticated.
///
/// This is the synchronous trigger that lets an unlinked caller's request reach the SPA as
/// <c>actor_identity_required</c>: here we assert the handler throws
/// <see cref="ActorIdentityRequiredException"/>; the exception → HTTP 428 mapping is pinned
/// separately by <c>GlobalExceptionFilterTests</c> (the MVC filter isn't on the mediator path).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PullRequestReviewSubmitApiFlowTests
{
    private readonly PostgresFixture _fixture;

    public PullRequestReviewSubmitApiFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Linked_caller_submits_the_review_as_their_own_identity()
    {
        var seed = await SeedAsync(linkActor: true);

        RemotePullRequestReview review;
        using (var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId, Roles.Admin))
            review = await scope.Resolve<IMediator>().Send(new SubmitPullRequestReviewCommand
            {
                RepositoryId = seed.RepositoryId, Number = 5, Verdict = PullRequestReviewVerdict.Comment, Body = "looks good"
            });

        review.ExternalId.ShouldBe(seed.ActorCredentialId.ToString(),
            customMessage: "the endpoint must act AS the caller's own linked credential, not the repo connection credential");
        review.Verdict.ShouldBe(PullRequestReviewVerdict.Comment);
    }

    [Fact]
    public async Task Unlinked_caller_triggers_actor_identity_required_naming_the_provider()
    {
        var seed = await SeedAsync(linkActor: false);

        ActorIdentityRequiredException ex;
        using (var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId, Roles.Admin))
            ex = await Should.ThrowAsync<ActorIdentityRequiredException>(() => scope.Resolve<IMediator>().Send(new SubmitPullRequestReviewCommand
            {
                RepositoryId = seed.RepositoryId, Number = 5, Verdict = PullRequestReviewVerdict.Comment, Body = "looks good"
            }));

        ex.ProviderKind.ShouldBe(ProviderKind.Git);
        ex.ProviderInstanceId.ShouldBe(seed.ProviderInstanceId,
            customMessage: "the 428 body must name the provider instance so the SPA can open the link modal for the right one");
    }

    // ── Seed ───────────────────────────────────────────────────────────────────

    private async Task<SeedResult> SeedAsync(bool linkActor)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        string Pat(string token) => encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = token }));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
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
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(connection);
        db.Repository.Add(repo);

        var actorCredentialId = Guid.Empty;
        if (linkActor)
        {
            var actorCred = new Credential
            {
                Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, OwnerUserId = user.Id,
                Ownership = CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = "actor", EncryptedPayload = Pat("actor"), Status = CredentialStatus.Active
            };
            db.Credential.Add(actorCred);
            db.UserProviderIdentity.Add(new UserProviderIdentity
            {
                Id = Guid.NewGuid(), UserId = user.Id, ProviderInstanceId = instance.Id, CredentialId = actorCred.Id,
                ProviderUserId = "42", ProviderUsername = "tester"
            });
            actorCredentialId = actorCred.Id;
        }

        await db.SaveChangesAsync();

        return new SeedResult(user.Id, team.Id, instance.Id, repo.Id, actorCredentialId);
    }

    private sealed record SeedResult(Guid UserId, Guid TeamId, Guid ProviderInstanceId, Guid RepositoryId, Guid ActorCredentialId);
}
