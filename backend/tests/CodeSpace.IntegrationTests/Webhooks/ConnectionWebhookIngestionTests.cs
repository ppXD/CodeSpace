using System.Security.Cryptography;
using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Webhooks;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Webhooks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.Push;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// The identity change. A per-repository hook's URL says which repository a delivery is about; a
/// group hook's does not, so the answer has to come out of the payload — and the ways that can go
/// wrong are the ways this feature can silently ruin a workflow: routing an event to the WRONG
/// repository, or treating the group's ordinary traffic as a fault.
///
/// <para>Both connections here carry two repositories, so "it routed" and "it routed to the right
/// one" are different assertions. Real normalizers and real signature verifiers throughout.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ConnectionWebhookIngestionTests
{
    private readonly PostgresFixture _fixture;

    public ConnectionWebhookIngestionTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task GitLab_group_delivery_routes_to_the_repository_the_payload_names()
    {
        var secret = $"gl-conn-{Guid.NewGuid():N}";
        var seed = await SeedConnectionAsync(ProviderKind.GitLab, secret).ConfigureAwait(false);

        var body = BuildGitLabPushBody(projectId: seed.SecondExternalId, path: seed.SecondFullPath);
        ClearCapturedEvents();

        await IngestAsync(seed.ConnectionWebhookId, body, GitLabHeaders(secret)).ConfigureAwait(false);

        var captured = SnapshotCapturedEvents().OfType<PushReceivedEvent>().ToList();
        captured.ShouldContain(e => e.RepositoryId == seed.SecondRepositoryId,
            customMessage: "The delivery named the second project, so the event must carry the second repository's id.");
        captured.ShouldNotContain(e => e.RepositoryId == seed.FirstRepositoryId,
            customMessage: "Routing to the connection's other repository would run the wrong workflow against the wrong code.");
    }

    [Fact]
    public async Task GitHub_organization_delivery_routes_to_the_repository_the_payload_names()
    {
        var secret = $"gh-conn-{Guid.NewGuid():N}";
        var seed = await SeedConnectionAsync(ProviderKind.GitHub, secret).ConfigureAwait(false);

        var body = BuildGitHubPushBody(repositoryId: seed.SecondExternalId, fullName: seed.SecondFullPath);
        ClearCapturedEvents();

        await IngestAsync(seed.ConnectionWebhookId, body, GitHubHeaders(body, secret)).ConfigureAwait(false);

        var captured = SnapshotCapturedEvents().OfType<PushReceivedEvent>().ToList();
        captured.ShouldContain(e => e.RepositoryId == seed.SecondRepositoryId);
        captured.ShouldNotContain(e => e.RepositoryId == seed.FirstRepositoryId);
    }

    [Fact]
    public async Task A_delivery_for_an_unbound_repository_is_dropped_and_audited()
    {
        // A group hook covers every project in the group and we asked for two of them. The rest is
        // ordinary traffic: it must not start anything, must not raise, and must leave a record
        // saying exactly what arrived — otherwise the operator's only signal is silence.
        var secret = $"gl-conn-{Guid.NewGuid():N}";
        var seed = await SeedConnectionAsync(ProviderKind.GitLab, secret).ConfigureAwait(false);
        var deliveryId = $"gl-unbound-{Guid.NewGuid():N}";

        var body = BuildGitLabPushBody(projectId: "99999999", path: "acme/someone-elses-project");
        ClearCapturedEvents();

        await IngestAsync(seed.ConnectionWebhookId, body, GitLabHeaders(secret, deliveryId)).ConfigureAwait(false);

        SnapshotCapturedEvents().OfType<PushReceivedEvent>().ShouldBeEmpty(
            customMessage: "A repository we have not bound must start nothing.");

        var audit = await LoadAuditAsync(deliveryId).ConfigureAwait(false);
        audit.Status.ShouldBe(WorkflowRunRequestStatus.Rejected);
        audit.Error.ShouldStartWith(WorkflowRunRequestRejectionReasons.RepositoryNotBound,
            customMessage: "The drop must be recorded under its own reason — reusing 'malformed' or 'not mapped' would describe a fault where there is none.");
        audit.Error.ShouldContain("acme/someone-elses-project",
            customMessage: "The operator's next question is which repository; the audit row has to answer it.");
    }

    [Fact]
    public async Task Signature_is_verified_against_the_connection_rows_secret()
    {
        // The connection hook was registered with its own secret. A delivery signed with a
        // repository hook's secret is not from our group hook, and must be refused — reaching for
        // the repository's secret here would accept anything signed by any hook on the connection.
        var connectionSecret = $"gl-conn-{Guid.NewGuid():N}";
        var repositorySecret = $"gl-repo-{Guid.NewGuid():N}";
        var seed = await SeedConnectionAsync(ProviderKind.GitLab, connectionSecret, repositorySecret).ConfigureAwait(false);

        var body = BuildGitLabPushBody(projectId: seed.FirstExternalId, path: seed.FirstFullPath);
        ClearCapturedEvents();

        await Should.ThrowAsync<UnauthorizedAccessException>(() => IngestAsync(seed.ConnectionWebhookId, body, GitLabHeaders(repositorySecret))).ConfigureAwait(false);

        SnapshotCapturedEvents().OfType<PushReceivedEvent>().ShouldBeEmpty();

        // …and the connection's own secret is accepted, so the refusal above is about WHICH secret
        // and not about the verifier rejecting everything.
        await IngestAsync(seed.ConnectionWebhookId, body, GitLabHeaders(connectionSecret)).ConfigureAwait(false);

        SnapshotCapturedEvents().OfType<PushReceivedEvent>().ShouldContain(e => e.RepositoryId == seed.FirstRepositoryId);
    }

    [Fact]
    public async Task An_unknown_id_is_never_rescued_by_a_matching_path()
    {
        // The spoofing case. A group hook receives events for every project in the group, so a body
        // can name an id we have never seen. If a MISS on the id fell through to the path, that body
        // could pick any repository it liked by writing its path — and the delivery is already
        // signature-valid, because the hook it came from is genuinely ours. The id is authoritative
        // and exclusive: present-and-unknown is the whole answer.
        var secret = $"gl-conn-{Guid.NewGuid():N}";
        var seed = await SeedConnectionAsync(ProviderKind.GitLab, secret).ConfigureAwait(false);
        var deliveryId = $"gl-spoof-{Guid.NewGuid():N}";

        var body = BuildGitLabPushBody(projectId: "77777777", path: seed.FirstFullPath);
        ClearCapturedEvents();

        await IngestAsync(seed.ConnectionWebhookId, body, GitLabHeaders(secret, deliveryId)).ConfigureAwait(false);

        SnapshotCapturedEvents().OfType<PushReceivedEvent>().ShouldNotContain(e => e.RepositoryId == seed.FirstRepositoryId,
            customMessage: "A payload naming an id we do not know must not be routed onto a repository we do just because it also wrote that repository's path.");

        var audit = await LoadAuditAsync(deliveryId).ConfigureAwait(false);
        audit.Error.ShouldStartWith(WorkflowRunRequestRejectionReasons.RepositoryNotBound);
    }

    [Fact]
    public async Task A_path_only_payload_still_routes_by_path()
    {
        // The other half of the same rule, so the fix above is a narrowing and not a removal: the
        // path is the fallback for payload shapes that carry NO id, and those still have to work.
        var secret = $"gl-conn-{Guid.NewGuid():N}";
        var seed = await SeedConnectionAsync(ProviderKind.GitLab, secret).ConfigureAwait(false);

        var body = BuildGitLabPathOnlyPushBody(seed.SecondFullPath);
        ClearCapturedEvents();

        await IngestAsync(seed.ConnectionWebhookId, body, GitLabHeaders(secret)).ConfigureAwait(false);

        SnapshotCapturedEvents().OfType<PushReceivedEvent>().ShouldContain(e => e.RepositoryId == seed.SecondRepositoryId,
            customMessage: "A payload with no id at all must still be placed by its path — that is what the path is for.");
    }

    [Fact]
    public async Task A_retired_hook_refuses_the_delivery_and_records_why()
    {
        // A scope switch retires the hooks the connection moved off. It deletes them at the provider
        // best-effort, so the ones it could not delete keep delivering — and `active` is still true,
        // because nobody switched them off. Accepting those would run workflows in a mode this
        // connection has left, which is the exact double-delivery the switch exists to prevent.
        var secret = $"gl-conn-{Guid.NewGuid():N}";
        var seed = await SeedConnectionAsync(ProviderKind.GitLab, secret).ConfigureAwait(false);

        await RetireHookAsync(seed.ConnectionWebhookId).ConfigureAwait(false);

        var body = BuildGitLabPushBody(projectId: seed.FirstExternalId, path: seed.FirstFullPath);
        ClearCapturedEvents();

        await Should.ThrowAsync<InvalidOperationException>(() => IngestAsync(seed.ConnectionWebhookId, body, GitLabHeaders(secret))).ConfigureAwait(false);

        SnapshotCapturedEvents().OfType<PushReceivedEvent>().ShouldBeEmpty(
            customMessage: "A hook the connection has moved off must start nothing.");

        // Driven at the service rather than through the command for the audit half, exactly as the
        // inactive-webhook test is: every rejection that THROWS loses its row to the transactional
        // middleware's rollback, which is a property of the pipeline and not of this gate.
        await Should.ThrowAsync<InvalidOperationException>(() => IngestDirectAsync(seed.ConnectionWebhookId, body, GitLabHeaders(secret))).ConfigureAwait(false);

        // Looked up by the hook rather than by a delivery id: this refusal happens before the body is
        // read, and it keeps the same no-external-id shape `webhook_inactive` has for that reason.
        var errors = await LoadErrorsForHookAsync(seed.ConnectionWebhookId).ConfigureAwait(false);
        errors.ShouldContain(e => e.StartsWith(WorkflowRunRequestRejectionReasons.WebhookRetired, StringComparison.Ordinal),
            customMessage: "Recorded under its own reason: 'inactive' would say an operator switched this off, and nobody did.");
    }

    [Fact]
    public async Task Repeat_deliveries_for_one_unbound_repository_leave_one_row_not_a_row_each()
    {
        // The expected case for a group hook, which is exactly why it must not accumulate like an
        // anomaly: bind three of a five-hundred-project group and the other four-hundred-and-ninety-
        // seven push all day. One row per repository per day still answers "deliveries are arriving
        // for repositories you have not bound, and here is which"; ten thousand answer it no better
        // and bury every other refusal in the list.
        var secret = $"gl-conn-{Guid.NewGuid():N}";
        var seed = await SeedConnectionAsync(ProviderKind.GitLab, secret).ConfigureAwait(false);
        var unbound = $"acme/unbound-{Guid.NewGuid():N}";

        var body = BuildGitLabPushBody(projectId: "88888881", path: unbound);

        await IngestAsync(seed.ConnectionWebhookId, body, GitLabHeaders(secret)).ConfigureAwait(false);
        await IngestAsync(seed.ConnectionWebhookId, body, GitLabHeaders(secret)).ConfigureAwait(false);
        await IngestAsync(seed.ConnectionWebhookId, body, GitLabHeaders(secret)).ConfigureAwait(false);

        (await CountNotBoundRowsAsync(unbound).ConfigureAwait(false)).ShouldBe(1,
            customMessage: "Three deliveries for the same unbound repository must leave one row — the suppression is what keeps this reason from growing without bound.");

        // …and it is per repository, not a global mute: a DIFFERENT unbound repository is news.
        var other = $"acme/unbound-{Guid.NewGuid():N}";
        await IngestAsync(seed.ConnectionWebhookId, BuildGitLabPushBody("88888882", other), GitLabHeaders(secret)).ConfigureAwait(false);

        (await CountNotBoundRowsAsync(other).ConfigureAwait(false)).ShouldBe(1,
            customMessage: "Suppressing one repository must not suppress the next one — 'which repositories' is the useful half of the fact.");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task IngestDirectAsync(Guid connectionWebhookId, string body, IReadOnlyDictionary<string, string> headers)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IConnectionWebhookIngestionService>().IngestConnectionAsync(connectionWebhookId, body, headers, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RetireHookAsync(Guid connectionWebhookId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook
            .Where(w => w.Id == connectionWebhookId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Cancelled))
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> LoadErrorsForHookAsync(Guid connectionWebhookId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking()
            .Where(r => r.Error != null && r.Error.Contains(connectionWebhookId.ToString()))
            .Select(r => r.Error!)
            .ToListAsync().ConfigureAwait(false);
    }

    private async Task<int> CountNotBoundRowsAsync(string repositoryName)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking()
            .CountAsync(r => r.Error != null && r.Error.Contains(WorkflowRunRequestRejectionReasons.RepositoryNotBound) && r.Error.Contains(repositoryName))
            .ConfigureAwait(false);
    }

    /// <summary>A GitLab body with the project OBJECT trimmed to a path and no id anywhere — the shape the path fallback exists for.</summary>
    private static string BuildGitLabPathOnlyPushBody(string path) =>
        "{\"object_kind\":\"push\",\"project\":{\"path_with_namespace\":\"" + path + "\"}" +
        ",\"ref\":\"refs/heads/main\",\"before\":\"0000\",\"after\":\"abcd\",\"user_id\":7,\"user_name\":\"Alice\"" +
        ",\"commits\":[{\"id\":\"abcd\",\"message\":\"Work\",\"author\":{\"email\":\"a@x\",\"name\":\"Alice\"}}]}";


    private async Task IngestAsync(Guid connectionWebhookId, string body, IReadOnlyDictionary<string, string> headers)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMediator>().Send(new ReceiveConnectionWebhookCommand
        {
            ConnectionWebhookId = connectionWebhookId,
            Body = body,
            Headers = headers
        }).ConfigureAwait(false);
    }

    private async Task<WorkflowRunRequest> LoadAuditAsync(string deliveryId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking()
            .SingleAsync(r => r.ExternalEventId == deliveryId).ConfigureAwait(false);
    }

    private void ClearCapturedEvents()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<CapturedNormalizedEvents>().Clear();
    }

    private IReadOnlyList<NormalizedEvent> SnapshotCapturedEvents()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<CapturedNormalizedEvents>().Snapshot();
    }

    private static Dictionary<string, string> GitLabHeaders(string secret, string? deliveryId = null) => new()
    {
        ["X-Gitlab-Event"] = "Push Hook",
        ["X-Gitlab-Event-UUID"] = deliveryId ?? $"gl-{Guid.NewGuid():N}",
        ["X-Gitlab-Token"] = secret
    };

    private static Dictionary<string, string> GitHubHeaders(string body, string secret, string? deliveryId = null) => new()
    {
        ["X-GitHub-Event"] = "push",
        ["X-GitHub-Delivery"] = deliveryId ?? $"gh-{Guid.NewGuid():N}",
        ["X-Hub-Signature-256"] = ComputeGitHubSignature(body, secret)
    };

    private static string BuildGitLabPushBody(string projectId, string path) =>
        "{\"object_kind\":\"push\",\"project_id\":" + projectId +
        ",\"project\":{\"id\":" + projectId + ",\"path_with_namespace\":\"" + path + "\"}" +
        ",\"ref\":\"refs/heads/main\",\"before\":\"0000\",\"after\":\"abcd\",\"user_id\":7,\"user_name\":\"Alice\"" +
        ",\"commits\":[{\"id\":\"abcd\",\"message\":\"Work\",\"author\":{\"email\":\"a@x\",\"name\":\"Alice\"}}]}";

    private static string BuildGitHubPushBody(string repositoryId, string fullName) =>
        "{\"ref\":\"refs/heads/main\",\"before\":\"0000\",\"after\":\"abcd\"" +
        ",\"repository\":{\"id\":" + repositoryId + ",\"full_name\":\"" + fullName + "\"}" +
        ",\"pusher\":{\"name\":\"Alice\"},\"sender\":{\"id\":7,\"login\":\"alice\"}" +
        ",\"commits\":[{\"id\":\"abcd\",\"message\":\"Work\",\"author\":{\"email\":\"a@x\",\"name\":\"Alice\"}}]}";

    private static string ComputeGitHubSignature(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>
    /// A connection-wide instance with a group hook and TWO bound repositories under the same owner,
    /// so a routing assertion can distinguish "reached a repository" from "reached the right one".
    /// The optional repository secret seeds a per-repository hook alongside, for the test that pins
    /// which secret the connection path verifies against.
    /// </summary>
    private async Task<ConnectionSeed> SeedConnectionAsync(ProviderKind provider, string connectionSecret, string? repositorySecret = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team" };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = provider,
            DisplayName = "Connection",
            BaseUrl = "https://provider.test",
            WebhookScope = ProviderWebhookScope.Connection
        };

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.Pat,
            DisplayName = "PAT",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "pat-xxx" }))
        };

        var first = BuildRepository(team.Id, instance.Id, credential.Id, $"1{DateTime.UtcNow.Ticks % 100000}", $"acme/api-{suffix}");
        var second = BuildRepository(team.Id, instance.Id, credential.Id, $"2{DateTime.UtcNow.Ticks % 100000}", $"acme/web-{suffix}");

        var connectionWebhookId = Guid.NewGuid();

        db.User.Add(owner);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner });
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.AddRange(first, second);
        db.ConnectionWebhook.Add(new ConnectionWebhook
        {
            Id = connectionWebhookId,
            ProviderInstanceId = instance.Id,
            CredentialId = credential.Id,
            OwnerPath = "acme",
            ExternalId = "remote-group-hook",
            CallbackUrl = $"https://codespace.test/api/webhooks/connection/{connectionWebhookId}",
            SecretEnc = encryptor.Encrypt(connectionSecret),
            SubscribedEvents = new List<string> { "push" },
            RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered,
            RegisteredAt = DateTimeOffset.UtcNow
        });

        if (repositorySecret != null)
        {
            var repositoryWebhookId = Guid.NewGuid();
            db.RepositoryWebhook.Add(new RepositoryWebhook
            {
                Id = repositoryWebhookId,
                RepositoryId = first.Id,
                ExternalId = "remote-project-hook",
                CallbackUrl = $"https://codespace.test/api/webhooks/{repositoryWebhookId}",
                SecretEnc = encryptor.Encrypt(repositorySecret),
                SubscribedEvents = new List<string> { "push" },
                RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered
            });
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new ConnectionSeed
        {
            ConnectionWebhookId = connectionWebhookId,
            FirstRepositoryId = first.Id,
            FirstExternalId = first.ExternalId,
            FirstFullPath = first.FullPath,
            SecondRepositoryId = second.Id,
            SecondExternalId = second.ExternalId,
            SecondFullPath = second.FullPath
        };
    }

    private static Repository BuildRepository(Guid teamId, Guid instanceId, Guid credentialId, string externalId, string fullPath) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = teamId,
        ProviderInstanceId = instanceId,
        CredentialId = credentialId,
        ExternalId = externalId,
        NamespacePath = fullPath[..fullPath.LastIndexOf('/')],
        Name = fullPath[(fullPath.LastIndexOf('/') + 1)..],
        FullPath = fullPath,
        DefaultBranch = "main",
        Visibility = RepositoryVisibility.Private,
        WebUrl = $"https://provider.test/{fullPath}",
        Status = RepositoryStatus.Active
    };

    private sealed record ConnectionSeed
    {
        public required Guid ConnectionWebhookId { get; init; }
        public required Guid FirstRepositoryId { get; init; }
        public required string FirstExternalId { get; init; }
        public required string FirstFullPath { get; init; }
        public required Guid SecondRepositoryId { get; init; }
        public required string SecondExternalId { get; init; }
        public required string SecondFullPath { get; init; }
    }
}
