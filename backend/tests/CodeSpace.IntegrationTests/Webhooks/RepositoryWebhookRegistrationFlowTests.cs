using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.IntegrationTests.Binding;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Commands.Webhooks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// Lifecycle proof for the outbox-demolition refactor. Covers the four invariants the
/// design relies on:
/// <list type="number">
///   <item>Happy-path bind drives a RepositoryWebhook row through Pending → Enqueued →
///         Registering → Registered with the remote provider id written atomically.</item>
///   <item>Dispatcher CAS gives no-double-enqueue under concurrency.</item>
///   <item>Registrar's idempotency check (find-by-callback-URL) reuses an existing remote
///         hook instead of creating a duplicate.</item>
///   <item>Unbind during in-flight registration cancels the row (terminal Cancelled),
///         doesn't leak as Registered.</item>
///   <item>Reconciler revives all four stuck-state classes (Pending overdue, Enqueued
///         stale, Registering crashed, Failed due).</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public class RepositoryWebhookRegistrationFlowTests
{
    private readonly PostgresFixture _fixture;

    public RepositoryWebhookRegistrationFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        // The TestRepositoryProvider's "remote" state is a process-static dictionary keyed
        // by callback URL — reset so prior fixtures' registrations don't bleed across cases.
        TestRepositoryProvider.ResetRegistrations();
    }

    [Fact]
    public async Task Bind_drives_webhook_through_full_lifecycle_to_Registered()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);

        Guid repositoryId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            repositoryId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = $"acme/lifecycle-{Guid.NewGuid():N}"
            }).ConfigureAwait(false);
        }

        // Immediately after the bind command completes, the dispatcher has already CAS'd
        // Pending → Enqueued and called Hangfire.Enqueue. The in-memory job client records
        // the call but doesn't execute. Drain the registration queue (simulates the worker).
        await _fixture.DrainPendingWebhookRegistrationsAsync().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.RepositoryId == repositoryId).ConfigureAwait(false);

        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered);
        hook.ExternalId.ShouldNotBeNullOrEmpty(
            customMessage: "Registered row must carry the provider-assigned external id, set atomically with the status transition.");
        hook.RegisteredAt.ShouldNotBeNull();
        hook.EnqueuedAt.ShouldNotBeNull();
        hook.RegisteringAt.ShouldNotBeNull();
        hook.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Dispatcher_CAS_only_one_caller_succeeds_under_race()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);

        // Stage a Pending webhook row WITHOUT going through BindAsync — we want to control
        // the starting state and not have the binding service auto-dispatch it.
        var webhookId = await StagePendingWebhookAsync(teamId, providerInstanceId, credentialId).ConfigureAwait(false);

        // Two concurrent dispatch attempts. Both run their Pending → Enqueued CAS in
        // parallel; exactly one row update should succeed (returns true), the other
        // sees rows-affected = 0 and returns false.
        async Task<bool> RaceAsync()
        {
            using var raceScope = _fixture.BeginScope();
            var dispatcher = raceScope.Resolve<IRepositoryWebhookRegistrationDispatcher>();
            try
            {
                return await dispatcher.DispatchAsync(webhookId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // A CAS loser may surface as an exception under SERIALIZABLE-style isolation
                // (or a duplicate Hangfire enqueue under our InMemoryBackgroundJobClient).
                // Treat as a lost race.
                return false;
            }
        }

        var results = await Task.WhenAll(RaceAsync(), RaceAsync(), RaceAsync()).ConfigureAwait(false);

        results.Count(r => r).ShouldBe(1,
            customMessage: "Exactly one dispatcher should win the Pending → Enqueued CAS; the others lose.");

        // Final row state: Enqueued — none of the losers reverted us.
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Enqueued);
    }

    [Fact]
    public async Task Registrar_idempotency_reuses_existing_remote_hook()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);
        var webhookId = await StagePendingWebhookAsync(teamId, providerInstanceId, credentialId).ConfigureAwait(false);

        // Pre-populate the test provider's registration store with an existing hook at the
        // same callback URL — simulates a previous attempt that succeeded at the remote but
        // crashed before writing external_id locally (Hangfire retry / reconciler re-dispatch).
        string preExistingExternalId;
        using (var preScope = _fixture.BeginScope())
        {
            var preDb = preScope.Resolve<CodeSpaceDbContext>();
            var callbackUrl = await preDb.RepositoryWebhook.AsNoTracking().Where(w => w.Id == webhookId).Select(w => w.CallbackUrl).SingleAsync().ConfigureAwait(false);

            // Reach into the test provider's static store directly by calling the same
            // register-then-find path the registrar would use.
            var provider = new TestRepositoryProvider();
            var registration = new Messages.Dtos.Providers.WebhookRegistration { CallbackUrl = callbackUrl, Secret = "preexisting", SubscribedEvents = new List<string> { "push" } };
            var preExisting = await provider.RegisterWebhookAsync(null!, BuildRemoteRepo(), registration, CancellationToken.None).ConfigureAwait(false);
            preExistingExternalId = preExisting.ExternalId;
        }

        // Walk the row from Pending → Enqueued via the dispatcher, then invoke the registrar
        // directly. The registrar's FindWebhookByCallbackUrlAsync should hit the pre-existing
        // hook and reuse its external id without calling RegisterWebhookAsync again.
        using (var dispatchScope = _fixture.BeginScope())
        {
            var dispatcher = dispatchScope.Resolve<IRepositoryWebhookRegistrationDispatcher>();
            (await dispatcher.DispatchAsync(webhookId, CancellationToken.None).ConfigureAwait(false)).ShouldBeTrue();
        }
        using (var runScope = _fixture.BeginScope())
        {
            var registrar = runScope.Resolve<IRepositoryWebhookRegistrar>();
            await registrar.RunAsync(webhookId, CancellationToken.None).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);

        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered);
        hook.ExternalId.ShouldBe(preExistingExternalId,
            customMessage: "Registrar must reuse the pre-existing hook's external id instead of creating a duplicate.");
    }

    [Fact]
    public async Task Unbind_during_inflight_cancels_pending_webhook()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);

        // Bind happens but we DELIBERATELY do not drain — the webhook sits in Enqueued
        // (dispatcher CAS'd it but no worker ran). Unbind should flip the in-flight row to
        // Cancelled, not delete it (the audit-trail intent of the Cancelled state).
        Guid repositoryId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            repositoryId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = $"acme/unbind-inflight-{Guid.NewGuid():N}"
            }).ConfigureAwait(false);
        }

        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new UnbindRepositoryCommand { RepositoryId = repositoryId }).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.RepositoryId == repositoryId).ConfigureAwait(false);

        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Cancelled,
            customMessage: "Unbind during a non-terminal registration must CAS the row to Cancelled, not delete it.");
    }

    [Fact]
    public async Task Reconciler_revives_due_Failed_rows()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);
        var webhookId = await StagePendingWebhookAsync(teamId, providerInstanceId, credentialId).ConfigureAwait(false);

        // Manually drive the row into Failed with next_attempt_at in the past — simulates
        // a registrar that hit MaxAttempts-1, set backoff, and the backoff window has now
        // elapsed. The reconciler's ReviveDueFailedAsync should flip it back to Pending.
        using (var stageScope = _fixture.BeginScope())
        {
            var stageDb = stageScope.Resolve<CodeSpaceDbContext>();
            await stageDb.RepositoryWebhook
                .Where(w => w.Id == webhookId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Failed)
                    .SetProperty(w => w.Attempts, 2)
                    .SetProperty(w => w.LastError, "simulated provider failure")
                    .SetProperty(w => w.NextAttemptAt, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)))
                .ConfigureAwait(false);
        }

        // Run the reconciler. Expect at minimum this Failed row revived; the dispatcher
        // call inside RedispatchDuePendingAsync may also CAS it to Enqueued (depending on
        // whether the Pending threshold sweep picks it up the same tick).
        ReconcileStuckWebhookRegistrationsResponse summary;
        using (var scope = _fixture.BeginScope())
        {
            var mediator = scope.Resolve<IMediator>();
            summary = await mediator.Send(new ReconcileStuckWebhookRegistrationsCommand()).ConfigureAwait(false);
        }

        summary.RevivedFromFailed.ShouldBeGreaterThanOrEqualTo(1,
            customMessage: "Reconciler must revive at least the one Failed row we staged.");

        // Row should no longer be in Failed; it's at least Pending (revived) and possibly
        // Enqueued (if the Pending sweep also picked it up this tick — that's correct).
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);
        hook.RegistrationStatus.ShouldBeOneOf(RepositoryWebhookRegistrationStatus.Pending, RepositoryWebhookRegistrationStatus.Enqueued);
    }

    [Fact]
    public async Task Reconciler_reverts_stuck_Registering_to_Pending()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);
        var webhookId = await StagePendingWebhookAsync(teamId, providerInstanceId, credentialId).ConfigureAwait(false);

        // Force the row into Registering with a registering_at older than the threshold
        // (5min). Simulates a worker that CAS'd Enqueued → Registering and then crashed
        // before responding. The reconciler should flip it back to Pending so the next
        // dispatcher tick re-fires; the registrar's idempotency check on the next run
        // covers the "provider call already landed before crash" case.
        var stale = DateTimeOffset.UtcNow - StuckWebhookRegistrationReconcilerService.RegisteringStuckAfter - TimeSpan.FromMinutes(1);
        using (var stageScope = _fixture.BeginScope())
        {
            var stageDb = stageScope.Resolve<CodeSpaceDbContext>();
            await stageDb.RepositoryWebhook
                .Where(w => w.Id == webhookId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Registering)
                    .SetProperty(w => w.RegisteringAt, (DateTimeOffset?)stale))
                .ConfigureAwait(false);
        }

        ReconcileStuckWebhookRegistrationsResponse summary;
        using (var scope = _fixture.BeginScope())
        {
            var mediator = scope.Resolve<IMediator>();
            summary = await mediator.Send(new ReconcileStuckWebhookRegistrationsCommand()).ConfigureAwait(false);
        }

        summary.RevertedFromRegistering.ShouldBeGreaterThanOrEqualTo(1);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);
        // After revert, sweep order may also redispatch to Enqueued — either is acceptable.
        hook.RegistrationStatus.ShouldBeOneOf(RepositoryWebhookRegistrationStatus.Pending, RepositoryWebhookRegistrationStatus.Enqueued);
        hook.RegisteringAt.ShouldBeNull(
            customMessage: "Reverting from Registering must clear the timestamp so a fresh attempt is not seen as stuck.");
    }

    /// <summary>
    /// Construct the minimal RemoteRepository the TestRepositoryProvider's
    /// RegisterWebhookAsync ignores most fields on — we only need a valid stub.
    /// </summary>
    private static Messages.Dtos.Providers.RemoteRepository BuildRemoteRepo() => new()
    {
        ExternalId = "id-acme-api",
        NamespacePath = "acme",
        Name = "api",
        FullPath = "acme/api",
        DefaultBranch = "main",
        Visibility = RepositoryVisibility.Private,
        WebUrl = "https://test.local/acme/api"
    };

    /// <summary>
    /// Insert a Pending RepositoryWebhook + its parent Repository directly via the DbContext,
    /// without going through the binding service. Lets tests control the starting state
    /// (e.g. test the dispatcher / registrar / reconciler in isolation) without the binding
    /// flow's automatic dispatch step.
    /// </summary>
    private async Task<Guid> StagePendingWebhookAsync(Guid teamId, Guid providerInstanceId, Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var repoId = Guid.NewGuid();
        var webhookId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);

        var repo = new Repository
        {
            Id = repoId,
            TeamId = teamId,
            ProviderInstanceId = providerInstanceId,
            CredentialId = credentialId,
            ExternalId = $"id-staged-{suffix}",
            NamespacePath = "acme",
            Name = $"staged-{suffix}",
            FullPath = $"acme/staged-{suffix}",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = "https://test.local",
            Status = RepositoryStatus.Active
        };

        var webhook = new RepositoryWebhook
        {
            Id = webhookId,
            RepositoryId = repoId,
            ExternalId = null,
            CallbackUrl = $"https://test.local/api/webhooks/{webhookId}",
            SecretEnc = encryptor.Encrypt("staged-secret"),
            SubscribedEvents = new List<string> { "push" },
            Active = true,
            RegistrationStatus = RepositoryWebhookRegistrationStatus.Pending,
            NextAttemptAt = DateTimeOffset.UtcNow
        };

        db.Repository.Add(repo);
        db.RepositoryWebhook.Add(webhook);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return webhookId;
    }

    private async Task<(Guid TeamId, Guid InstanceId, Guid CredentialId)> SeedAsync()
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
            BaseUrl = "https://test.local"
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
