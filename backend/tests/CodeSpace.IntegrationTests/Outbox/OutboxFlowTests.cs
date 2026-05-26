using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Outbox;
using CodeSpace.Core.Services.Outbox.Payloads;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Outbox;

[Collection(PostgresCollection.Name)]
public class OutboxFlowTests
{
    private readonly PostgresFixture _fixture;

    public OutboxFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Bind_enqueues_outbox_message_and_skips_webhook_row_creation()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);
        Guid repositoryId;

        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            repositoryId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = $"acme/outbox-{Guid.NewGuid():N}"
            }).ConfigureAwait(false);
        }

        // Do NOT drain — verify the outbox-then-drain split

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var hookCount = await db.RepositoryWebhook.CountAsync(w => w.RepositoryId == repositoryId).ConfigureAwait(false);
        hookCount.ShouldBe(0, "RepositoryWebhook must NOT exist until the dispatcher has registered the remote webhook");

        var pending = await db.OutboxMessage.AsNoTracking().SingleAsync(m => m.AggregateId == repositoryId).ConfigureAwait(false);
        pending.MessageType.ShouldBe(OutboxMessageTypes.RegisterWebhook);
        pending.Status.ShouldBe(OutboxStatus.Pending);
        pending.Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task Drain_after_bind_creates_webhook_row_and_completes_outbox()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);

        Guid repositoryId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            repositoryId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = $"acme/drain-{Guid.NewGuid():N}"
            }).ConfigureAwait(false);
        }

        await _fixture.DrainOutboxAsync().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var webhook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.RepositoryId == repositoryId).ConfigureAwait(false);
        webhook.Active.ShouldBeTrue();
        webhook.ExternalId.ShouldStartWith("test-hook-");

        var outbox = await db.OutboxMessage.AsNoTracking().SingleAsync(m => m.AggregateId == repositoryId).ConfigureAwait(false);
        outbox.Status.ShouldBe(OutboxStatus.Completed);
        outbox.LastError.ShouldBeNull();
        outbox.LastAttemptedDate.ShouldNotBeNull();
    }

    [Fact]
    public async Task Drain_with_failing_handler_increments_attempts_and_schedules_next_attempt()
    {
        // Seed an outbox row pointing to a non-existent repo — handler will throw "Repository not found"
        var orphanRepoId = Guid.NewGuid();
        var outboxId = await SeedFailingOutboxMessageAsync(orphanRepoId, attempts: 0).ConfigureAwait(false);

        await _fixture.DrainOutboxAsync().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var msg = await db.OutboxMessage.AsNoTracking().SingleAsync(m => m.Id == outboxId).ConfigureAwait(false);

        msg.Status.ShouldBe(OutboxStatus.Pending);
        msg.Attempts.ShouldBe(1);
        msg.LastError.ShouldNotBeNullOrEmpty();
        msg.LastError!.ShouldContain(orphanRepoId.ToString());
        msg.NextAttemptDate.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(30));
    }

    [Fact]
    public async Task Drain_dead_letters_after_max_attempts()
    {
        var orphanRepoId = Guid.NewGuid();

        // Pre-seed at attempts = MaxAttempts - 1 so the next failure triggers dead-letter
        var outboxId = await SeedFailingOutboxMessageAsync(orphanRepoId, attempts: OutboxDispatcher.MaxAttempts - 1).ConfigureAwait(false);

        await _fixture.DrainOutboxAsync().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var msg = await db.OutboxMessage.AsNoTracking().SingleAsync(m => m.Id == outboxId).ConfigureAwait(false);

        msg.Status.ShouldBe(OutboxStatus.DeadLettered);
        msg.Attempts.ShouldBe(OutboxDispatcher.MaxAttempts);
    }

    [Fact]
    public async Task Bind_outbox_payload_carries_only_kinds_subscribed_events()
    {
        // Pins R7 contract: SubscribedEvents flow from IProviderEventSubscriptionRegistry —
        // adding a GitHub-only "check_run" subscription must not leak into the Git-kind payload.
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);

        Guid repositoryId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            repositoryId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = $"acme/sub-{Guid.NewGuid():N}"
            }).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var message = await db.OutboxMessage.AsNoTracking().SingleAsync(m => m.AggregateId == repositoryId).ConfigureAwait(false);
        var payload = JsonSerializer.Deserialize<RegisterWebhookOutboxPayload>(message.Payload).ShouldNotBeNull();

        payload.SubscribedEvents.ShouldBe(new[] { "test-event" });
    }

    [Fact]
    public async Task Drain_skips_messages_whose_next_attempt_is_in_the_future()
    {
        var orphanRepoId = Guid.NewGuid();
        var outboxId = await SeedFailingOutboxMessageAsync(orphanRepoId, attempts: 1, nextAttempt: DateTimeOffset.UtcNow.AddHours(1)).ConfigureAwait(false);

        await _fixture.DrainOutboxAsync().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var msg = await db.OutboxMessage.AsNoTracking().SingleAsync(m => m.Id == outboxId).ConfigureAwait(false);

        msg.Attempts.ShouldBe(1, "drain must not touch rows whose NextAttemptDate has not arrived");
        msg.Status.ShouldBe(OutboxStatus.Pending);
    }

    private async Task<Guid> SeedFailingOutboxMessageAsync(Guid orphanRepoId, int attempts, DateTimeOffset? nextAttempt = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var payload = new RegisterWebhookOutboxPayload
        {
            WebhookId = Guid.NewGuid(),
            RepositoryId = orphanRepoId,
            CallbackUrl = "https://test/cb",
            Secret = "secret",
            SubscribedEvents = new[] { "push" }
        };

        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            AggregateType = nameof(Repository),
            AggregateId = orphanRepoId,
            MessageType = OutboxMessageTypes.RegisterWebhook,
            Payload = JsonSerializer.Serialize(payload),
            Status = OutboxStatus.Pending,
            Attempts = attempts,
            NextAttemptDate = nextAttempt ?? DateTimeOffset.UtcNow
        };

        db.OutboxMessage.Add(message);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return message.Id;
    }

    private async Task<(Guid TeamId, Guid InstanceId, Guid CredentialId)> SeedBindablePrerequisitesAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team", OwnerUserId = owner.Id };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = ProviderKind.Git,
            DisplayName = "Test",
            BaseUrl = $"https://test-{suffix}.local"
        };
        var payload = new PatPayload { Token = "pat-xxx" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.Pat,
            DisplayName = "PAT",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload))
        };

        db.User.Add(owner);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (team.Id, instance.Id, credential.Id);
    }
}
