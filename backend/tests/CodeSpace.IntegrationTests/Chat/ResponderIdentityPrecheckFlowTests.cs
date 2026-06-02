using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Chat;

/// <summary>
/// The chat-driven act-as-user path's identity gate, end-to-end on real Postgres: a workflow posts a
/// review card that declares "responding acts AS you on this repo" (chat.post_message's
/// requireResponderIdentityForRepositoryId → WorkflowWaitTarget) and parks (flow.wait_action). When the
/// responder clicks, MessageInteractionService pre-checks their linked identity BEFORE resolving the wait:
///   • unlinked → ActorIdentityRequiredException (→ 428 on the synchronous respond request, so the client
///     prompts a link), and the wait stays OPEN — the run does NOT fail in the background;
///   • linked → the wait resolves and the run resumes.
/// This is the workflow-path complement to the synchronous PR-detail flow (which the GlobalExceptionFilter
/// 428 mapping is pinned for in GlobalExceptionFilterTests).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ResponderIdentityPrecheckFlowTests
{
    private readonly PostgresFixture _fixture;

    public ResponderIdentityPrecheckFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Unlinked_responder_is_prompted_to_link_and_the_wait_stays_open()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);
        var (repoId, providerInstanceId) = await SeedRepoAsync(teamId, ownerId, linkIdentity: false);

        var workflowId = await CreateWorkflowAsync(teamId, ownerId, ReviewDefinition(channelId, ownerId, repoId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);   // posts the card (declaring the repo requirement) + parks on flow.wait_action
        var messageId = await ReadPostedMessageIdAsync(runId);

        var ex = await Should.ThrowAsync<ActorIdentityRequiredException>(() => RespondAsync(teamId, messageId, ownerId));
        ex.ProviderKind.ShouldBe(ProviderKind.Git);
        ex.ProviderInstanceId.ShouldBe(providerInstanceId,
            customMessage: "the 428 must name the provider instance the responder has to link, so the client opens the modal for the right one");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, "the wait must NOT resolve — the run stays parked so a retry-after-link can succeed, instead of failing in the background");
    }

    [Fact]
    public async Task Linked_responder_resolves_the_wait_and_the_run_resumes()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);
        var (repoId, _) = await SeedRepoAsync(teamId, ownerId, linkIdentity: true);

        var workflowId = await CreateWorkflowAsync(teamId, ownerId, ReviewDefinition(channelId, ownerId, repoId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        var messageId = await ReadPostedMessageIdAsync(runId);

        await RespondAsync(teamId, messageId, ownerId);   // identity present → pre-check passes → wait resolves
        await RunEngineAsync(runId);                       // the run resumes from the wait

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "with a linked identity the responder's click resolves the wait and the run completes");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task RespondAsync(Guid teamId, Guid messageId, Guid actorUserId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMessageInteractionService>().RespondAsync(teamId, messageId, "comment", actorUserId, "looks good", null, default);
    }

    private async Task<Guid> ReadPostedMessageIdAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
        var post = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "post");
        return Guid.Parse(JsonDocument.Parse(post.OutputsJson).RootElement.GetProperty("messageId").GetString()!);
    }

    private async Task<Guid> SeedChannelAsync(Guid teamId, Guid ownerId)
    {
        using var scope = _fixture.BeginScope();
        var slug = "ident-" + Guid.NewGuid().ToString("N")[..8];
        return await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, ownerId, default);
    }

    /// <summary>Seed a Git provider instance + connection-credentialled repo under the existing team; optionally link the owner's own identity on that instance.</summary>
    private async Task<(Guid RepositoryId, Guid ProviderInstanceId)> SeedRepoAsync(Guid teamId, Guid ownerId, bool linkIdentity)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        string Pat(string token) => encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = token }));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "instance",
            BaseUrl = $"https://git-{suffix}.local", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret")
        };
        var connection = new Credential
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService,
            AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = Pat("conn"), Status = CredentialStatus.Active
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, CredentialId = connection.Id,
            ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://git.local/acme/api", Status = RepositoryStatus.Active
        };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(connection);
        db.Repository.Add(repo);

        if (linkIdentity)
        {
            var actorCred = new Credential
            {
                Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, OwnerUserId = ownerId,
                Ownership = CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = "actor", EncryptedPayload = Pat("actor"), Status = CredentialStatus.Active
            };
            db.Credential.Add(actorCred);
            db.UserProviderIdentity.Add(new UserProviderIdentity
            {
                Id = Guid.NewGuid(), UserId = ownerId, ProviderInstanceId = instance.Id, CredentialId = actorCred.Id,
                ProviderUserId = "42", ProviderUsername = "tester"
            });
        }

        await db.SaveChangesAsync();

        return (repo.Id, instance.Id);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "ident-precheck-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    // manual → post (declares the repo requirement) → wait → end. The card acts as the responder on `repoId`,
    // so the respond pre-check requires their linked identity before the wait can resolve.
    private static WorkflowDefinition ReviewDefinition(Guid channelId, Guid reviewerId, Guid repoId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new()
            {
                Id = "post",
                TypeKey = "chat.post_message",
                Config = WorkflowsTestSeed.EmptyJson(),
                Inputs = WorkflowsTestSeed.Json(JsonSerializer.Serialize(new
                {
                    conversationId = channelId.ToString(),
                    body = "Review PR #80?",
                    actions = new[] { new { key = "comment", label = "Comment" } },
                    allowedResponderUserIds = new[] { reviewerId.ToString() },
                    requireResponderIdentityForRepositoryId = repoId.ToString(),
                })),
            },
            new() { Id = "wait", TypeKey = "flow.wait_action", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "token": "{{nodes.post.outputs.token}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "post" },
            new() { From = "post", To = "wait" },
            new() { From = "wait", To = "end" },
        },
    };
}
