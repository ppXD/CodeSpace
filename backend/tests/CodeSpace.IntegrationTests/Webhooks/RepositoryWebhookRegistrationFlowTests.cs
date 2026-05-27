using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.IntegrationTests.Binding;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
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
/// Lifecycle proof for the outbox-demolition refactor. Covers the seven invariants the
/// design relies on:
/// <list type="number">
///   <item>Happy-path bind drives a RepositoryWebhook row through Pending → Enqueued →
///         Registering → Registered with the remote provider id written atomically.</item>
///   <item>Dispatcher CAS gives no-double-enqueue under concurrency (count remote
///         registrations: must be exactly one).</item>
///   <item>Registrar's idempotency check (find-by-callback-URL) reuses an existing remote
///         hook instead of creating a duplicate.</item>
///   <item>Unbind during in-flight registration cancels the row (terminal Cancelled),
///         doesn't leak as Registered.</item>
///   <item>Reconciler revives due-Failed rows.</item>
///   <item>Reconciler reverts stuck Registering rows back to Pending.</item>
///   <item>Registrar dead-letters after MaxAttempts — preventing infinite retry on a
///         permanently-broken provider.</item>
/// </list>
///
/// <para><b>Test client semantics:</b> <see cref="InMemoryBackgroundJobClient.AutoExecute"/>
/// defaults to <c>true</c>, so <c>Bind</c> drives the whole dispatcher → registrar chain
/// synchronously. Tests that need to observe intermediate state (e.g. "row is Enqueued
/// after dispatch but before worker pickup") set the flag to <c>false</c> for the
/// duration of the scenario.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RepositoryWebhookRegistrationFlowTests
{
    private readonly PostgresFixture _fixture;

    public RepositoryWebhookRegistrationFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        // TestRemoteHookStore is a fixture-scoped singleton; reset between tests within the
        // same fixture so the per-test "register called exactly N times" assertions are
        // deterministic.
        using var scope = _fixture.BeginScope();
        scope.Resolve<TestRemoteHookStore>().Reset();
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

        // Outer transaction committed; drain queued registrar to drive Pending → Registered.
        using (var drainScope = _fixture.BeginScope())
        {
            await drainScope.Resolve<InMemoryBackgroundJobClient>().WaitForPendingAsync().ConfigureAwait(false);
        }

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

        // Provider was called exactly once.
        verify.Resolve<TestRemoteHookStore>().RegisterCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Dispatcher_CAS_only_one_caller_succeeds_under_race()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);

        // Stage a Pending webhook row WITHOUT going through BindAsync — we want to control
        // the starting state and not have the binding service auto-dispatch.
        var webhookId = await StagePendingWebhookAsync(teamId, providerInstanceId, credentialId).ConfigureAwait(false);

        // Two concurrent dispatch attempts. Both run their Pending → Enqueued CAS in
        // parallel; exactly one row update succeeds (returns true), the others see
        // rows-affected = 0 and return false. The WINNER's Enqueue call then synchronously
        // runs the registrar (AutoExecute=true) — so the end state is Registered, not
        // Enqueued. We verify "exactly one winner" by counting actual remote registrations.
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
                // A CAS loser may surface as an exception under SERIALIZABLE-style isolation.
                return false;
            }
        }

        var results = await Task.WhenAll(RaceAsync(), RaceAsync(), RaceAsync()).ConfigureAwait(false);

        results.Count(r => r).ShouldBe(1,
            customMessage: "Exactly one dispatcher should win the Pending → Enqueued CAS; the others lose.");

        // Drain queued registrar(s). Only the winner enqueued — there should be at most one
        // job in the queue, and after drain the row is terminal Registered.
        using (var drainScope = _fixture.BeginScope())
        {
            await drainScope.Resolve<InMemoryBackgroundJobClient>().WaitForPendingAsync().ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered,
            customMessage: "Winner's queued job ran post-commit → terminal Registered.");

        // Definitive no-double-registration proof: the remote provider was called exactly once.
        verify.Resolve<TestRemoteHookStore>().RegisterCallCount.ShouldBe(1,
            customMessage: "Three concurrent dispatchers must not produce three remote registrations.");
    }

    [Fact]
    public async Task Registrar_idempotency_reuses_existing_remote_hook()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);
        var webhookId = await StagePendingWebhookAsync(teamId, providerInstanceId, credentialId).ConfigureAwait(false);

        // Pre-populate the remote hook store with the same callback URL the registrar will
        // construct — simulates a prior attempt that succeeded at the remote but crashed
        // before writing external_id locally.
        string preExistingExternalId;
        using (var preScope = _fixture.BeginScope())
        {
            var preDb = preScope.Resolve<CodeSpaceDbContext>();
            var hookStore = preScope.Resolve<TestRemoteHookStore>();
            var callbackUrl = await preDb.RepositoryWebhook.AsNoTracking().Where(w => w.Id == webhookId).Select(w => w.CallbackUrl).SingleAsync().ConfigureAwait(false);

            var preExisting = hookStore.Register(new Messages.Dtos.Providers.WebhookRegistration
            {
                CallbackUrl = callbackUrl,
                Secret = "preexisting",
                SubscribedEvents = new List<string> { "push" }
            });
            preExistingExternalId = preExisting.ExternalId;
        }

        // Disable auto-execute so we can drive Enqueue → registrar.RunAsync deterministically
        // in two distinct steps (otherwise the dispatcher's Enqueue would synchronously run
        // the registrar, but we want to assert that the registrar's find-by-URL hits the
        // pre-existing hook without creating a new one — easier to read in two steps).
        using var jobClientScope = _fixture.BeginScope();
        var client = jobClientScope.Resolve<InMemoryBackgroundJobClient>();
        client.AutoExecute = false;
        try
        {
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
        }
        finally
        {
            client.AutoExecute = true;
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);

        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered);
        hook.ExternalId.ShouldBe(preExistingExternalId,
            customMessage: "Registrar must reuse the pre-existing hook's external id instead of creating a duplicate.");

        // Critical assertion: registrar saw the pre-existing hook + did NOT register a fresh one.
        // The pre-populate seeded RegisterCallCount = 1; if the registrar (incorrectly) registered
        // again, count would be 2.
        verify.Resolve<TestRemoteHookStore>().RegisterCallCount.ShouldBe(1,
            customMessage: "Find-by-URL idempotency: registrar must not call RegisterWebhookAsync when a hook already exists.");
    }

    [Fact]
    public async Task Unbind_during_inflight_cancels_pending_webhook()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);

        // Disable auto-execute so Bind leaves the webhook in Enqueued (not Registered), then
        // unbind hits the in-flight CAS path.
        using var jobClientScope = _fixture.BeginScope();
        var client = jobClientScope.Resolve<InMemoryBackgroundJobClient>();
        client.AutoExecute = false;

        try
        {
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
        finally
        {
            client.AutoExecute = true;
        }
    }

    [Fact]
    public async Task Reconciler_revives_due_Failed_rows()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);
        var webhookId = await StagePendingWebhookAsync(teamId, providerInstanceId, credentialId).ConfigureAwait(false);

        // Manually drive the row into Failed with next_attempt_at in the past — simulates
        // a registrar that hit MaxAttempts-1, set backoff, and the backoff window has now
        // elapsed.
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

        // Disable auto-execute so reconciler's redispatch path stops at Enqueued instead of
        // running the registrar synchronously — keeps the assertion focused on "Failed → Pending → Enqueued".
        using var jobClientScope = _fixture.BeginScope();
        var client = jobClientScope.Resolve<InMemoryBackgroundJobClient>();
        client.AutoExecute = false;

        try
        {
            ReconcileStuckWebhookRegistrationsResponse summary;
            using (var scope = _fixture.BeginScope())
            {
                var mediator = scope.Resolve<IMediator>();
                summary = await mediator.Send(new ReconcileStuckWebhookRegistrationsCommand()).ConfigureAwait(false);
            }

            summary.RevivedFromFailed.ShouldBeGreaterThanOrEqualTo(1,
                customMessage: "Reconciler must revive at least the one Failed row we staged.");

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();
            var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);
            // Reviver flipped Failed→Pending, then the Pending sweep dispatcher may have flipped Pending→Enqueued.
            hook.RegistrationStatus.ShouldBeOneOf(RepositoryWebhookRegistrationStatus.Pending, RepositoryWebhookRegistrationStatus.Enqueued);
        }
        finally
        {
            client.AutoExecute = true;
        }
    }

    [Fact]
    public async Task Reconciler_reverts_stuck_Registering_to_Pending()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);
        var webhookId = await StagePendingWebhookAsync(teamId, providerInstanceId, credentialId).ConfigureAwait(false);

        // Force the row into Registering with a registering_at older than threshold (5min).
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

        using var jobClientScope = _fixture.BeginScope();
        var client = jobClientScope.Resolve<InMemoryBackgroundJobClient>();
        client.AutoExecute = false;

        try
        {
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
            hook.RegistrationStatus.ShouldBeOneOf(RepositoryWebhookRegistrationStatus.Pending, RepositoryWebhookRegistrationStatus.Enqueued);
            hook.RegisteringAt.ShouldBeNull(
                customMessage: "Reverting from Registering must clear the timestamp so a fresh attempt is not seen as stuck.");
        }
        finally
        {
            client.AutoExecute = true;
        }
    }

    [Fact]
    public async Task Registrar_dead_letters_after_max_attempts()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedAsync().ConfigureAwait(false);
        var webhookId = await StagePendingWebhookAsync(teamId, providerInstanceId, credentialId).ConfigureAwait(false);

        // Stage the row in Enqueued with Attempts = MaxAttempts - 1 — one more failure and
        // the registrar must flip to DeadLettered (terminal). To force a deterministic
        // failure, soft-delete the parent repository — LoadRepositoryAsync filters by
        // DeletedDate == null, so it returns null, the registrar throws, RecordFailureAsync
        // runs, attempts ticks to MaxAttempts → DeadLettered. Same code path as a permanently
        // broken provider, achieved without standing up a throwing provider double.
        using (var stageScope = _fixture.BeginScope())
        {
            var stageDb = stageScope.Resolve<CodeSpaceDbContext>();
            var webhookRepoId = await stageDb.RepositoryWebhook.AsNoTracking()
                .Where(w => w.Id == webhookId)
                .Select(w => w.RepositoryId)
                .SingleAsync()
                .ConfigureAwait(false);

            await stageDb.RepositoryWebhook
                .Where(w => w.Id == webhookId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Enqueued)
                    .SetProperty(w => w.Attempts, RepositoryWebhookRegistrar.MaxAttempts - 1)
                    .SetProperty(w => w.EnqueuedAt, (DateTimeOffset?)DateTimeOffset.UtcNow))
                .ConfigureAwait(false);

            await stageDb.Repository
                .Where(r => r.Id == webhookRepoId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedDate, (DateTimeOffset?)DateTimeOffset.UtcNow))
                .ConfigureAwait(false);
        }

        using (var runScope = _fixture.BeginScope())
        {
            var registrar = runScope.Resolve<IRepositoryWebhookRegistrar>();
            await registrar.RunAsync(webhookId, CancellationToken.None).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);

        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.DeadLettered,
            customMessage: "Registrar must dead-letter after Attempts reaches MaxAttempts.");
        hook.Attempts.ShouldBe(RepositoryWebhookRegistrar.MaxAttempts);
        hook.LastError.ShouldNotBeNullOrEmpty(
            customMessage: "DeadLettered row must carry the last error message for operator triage.");
    }

    /// <summary>
    /// Insert a Pending RepositoryWebhook + its parent Repository directly via the DbContext,
    /// without going through the binding service. Lets tests control the starting state
    /// without the binding flow's automatic dispatch step.
    /// </summary>
    private async Task<Guid> StagePendingWebhookAsync(Guid teamId, Guid providerInstanceId, Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var projectId = await db.Project.AsNoTracking().Where(p => p.TeamId == teamId).Select(p => p.Id).SingleAsync().ConfigureAwait(false);

        var repoId = Guid.NewGuid();
        var webhookId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);

        var repo = new Repository
        {
            Id = repoId,
            TeamId = teamId,
            ProjectId = projectId,
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
        var project = TestProjectSeed.BuildDefaultProject(team.Id, owner.Id);
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
        db.Project.Add(project);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (team.Id, instance.Id, credential.Id);
    }
}
