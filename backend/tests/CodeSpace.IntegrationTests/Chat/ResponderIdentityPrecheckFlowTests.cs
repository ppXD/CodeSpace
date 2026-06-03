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
/// The GENERIC chat-driven act-as-user identity gate, end-to-end on real Postgres. A workflow posts a
/// review card (chat.post_message), parks (flow.wait_action), and a downstream git.pr_review acts AS the
/// responder (actAsUserId wired to the wait's `by`). When the responder clicks, the resume path derives
/// — from git.pr_review's declared ActsAsUser trait, with nothing hardcoded — that resolving the wait will
/// act as them on the repo's provider, and enforces their linked identity FIRST:
///   • unlinked → ActorIdentityRequiredException (→ 428 on the synchronous respond), and the wait stays
///     OPEN — the run does NOT fail in the background;
///   • linked → the wait resolves, the run resumes, and git.pr_review submits as them.
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

        await RunEngineAsync(runId);   // posts the card + parks on flow.wait_action (git.pr_review still downstream)
        var messageId = await ReadPostedMessageIdAsync(runId);

        var ex = await Should.ThrowAsync<ActorIdentityRequiredException>(() => RespondAsync(teamId, messageId, ownerId));
        ex.ProviderKind.ShouldBe(ProviderKind.Git);
        ex.ProviderInstanceId.ShouldBe(providerInstanceId,
            customMessage: "the gate derives the requirement from the downstream git.pr_review's repo → provider instance");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, "the wait must NOT resolve — the run stays parked so a retry-after-link can succeed");
    }

    [Fact]
    public async Task Linked_responder_resolves_the_wait_and_the_run_submits_the_review_as_them()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);
        var (repoId, _) = await SeedRepoAsync(teamId, ownerId, linkIdentity: true);

        var workflowId = await CreateWorkflowAsync(teamId, ownerId, ReviewDefinition(channelId, ownerId, repoId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        var messageId = await ReadPostedMessageIdAsync(runId);

        await RespondAsync(teamId, messageId, ownerId);   // identity present → gate passes → wait resolves
        await RunEngineAsync(runId);                       // resumes through git.pr_review → end

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "with a linked identity the click resolves the wait and git.pr_review submits as the responder");
    }

    [Fact]
    public async Task Linked_responder_without_repo_permission_is_refused_and_the_wait_stays_open()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);
        // Identity IS linked (the 428 check passes) — but the repo's access probe DENIES (the "noaccess"
        // marker external id makes the test provider report no membership). The pre-flight must refuse the
        // click HERE, before the wait resolves, so the clicker learns it in chat instead of a false success.
        var (repoId, providerInstanceId) = await SeedRepoAsync(teamId, ownerId, linkIdentity: true, externalId: "noaccess-" + Guid.NewGuid().ToString("N")[..8]);

        var workflowId = await CreateWorkflowAsync(teamId, ownerId, ReviewDefinition(channelId, ownerId, repoId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        var messageId = await ReadPostedMessageIdAsync(runId);

        var ex = await Should.ThrowAsync<ActorRepoPermissionDeniedException>(() => RespondAsync(teamId, messageId, ownerId));
        ex.ProviderInstanceId.ShouldBe(providerInstanceId);
        ex.RepositoryPath.ShouldBe("acme/api");
        ex.Reason.ShouldNotBeNullOrEmpty("the client needs the reason to show on the card");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, "no repo permission → the click is refused BEFORE the wait resolves; the run stays parked — no false success, no background failure");
    }

    [Fact]
    public async Task Linked_responder_with_a_read_only_token_is_refused_by_the_capability_scope_check()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);
        // A GitLab repo + a linked identity whose token is read-only ({read_api}). git.pr_review declares it
        // acts via IPullRequestReviewCapability, which GitLab's module requires `api` for — so the gate's
        // GENERIC, capability-driven scope check refuses the click (before any membership round-trip),
        // nothing hardcoded. (Uses the real GitLab provider module's CapabilityScopeRequirements.)
        var (repoId, providerInstanceId) = await SeedRepoAsync(teamId, ownerId, linkIdentity: true, provider: ProviderKind.GitLab, actorScopes: new List<string> { "read_api", "read_user" });

        var workflowId = await CreateWorkflowAsync(teamId, ownerId, ReviewDefinition(channelId, ownerId, repoId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        var messageId = await ReadPostedMessageIdAsync(runId);

        var ex = await Should.ThrowAsync<ActorRepoPermissionDeniedException>(() => RespondAsync(teamId, messageId, ownerId));
        ex.ProviderInstanceId.ShouldBe(providerInstanceId);
        ex.Reason.ShouldContain("api", Case.Insensitive, "the reason names the scope the token is missing for this action");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, "a read-only token fails the capability scope check at click time — refused before the wait resolves");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task RespondAsync(Guid teamId, Guid messageId, Guid actorUserId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMessageInteractionService>().RespondAsync(teamId, messageId, "approve", actorUserId, null, null, default);
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
    private async Task<(Guid RepositoryId, Guid ProviderInstanceId)> SeedRepoAsync(Guid teamId, Guid ownerId, bool linkIdentity, string? externalId = null, ProviderKind provider = ProviderKind.Git, List<string>? actorScopes = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        string Pat(string token) => encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = token }));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = teamId, Provider = provider, DisplayName = "instance",
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
            ExternalId = externalId ?? $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
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
                Ownership = CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = "actor", EncryptedPayload = Pat("actor"), Status = CredentialStatus.Active, Scopes = actorScopes
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

    // manual → post → wait → git.pr_review (acts AS the responder) → end. git.pr_review's actAsUserId is
    // wired to the wait's `by`, so the engine derives — from its ActsAsUser trait — that resolving the wait
    // requires the responder's linked identity on this repo's provider.
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
                    body = "Review PR #5?",
                    actions = new[] { new { key = "approve", label = "Approve" } },
                    allowedResponderUserIds = new[] { reviewerId.ToString() },
                })),
            },
            new() { Id = "wait", TypeKey = "flow.wait_action", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "token": "{{nodes.post.outputs.token}}" }""") },
            new()
            {
                Id = "review",
                TypeKey = "git.pr_review",
                Config = WorkflowsTestSeed.EmptyJson(),
                Inputs = WorkflowsTestSeed.Json(JsonSerializer.Serialize(new
                {
                    repositoryId = repoId.ToString(),
                    number = 5,
                    verdict = "{{nodes.wait.outputs.action}}",
                    actAsUserId = "{{nodes.wait.outputs.by}}",
                })),
            },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "post" },
            new() { From = "post", To = "wait" },
            new() { From = "wait", To = "review" },
            new() { From = "review", To = "end" },
        },
    };
}
