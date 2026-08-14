using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// Pins the one rule every webhook decision has to share: a hook is in service in every status
/// except <c>Cancelled</c>. Four places encode that decision — ingestion accepting a delivery, the
/// provisioner deciding an owner is covered, scope switching deciding what to retire, and the
/// <c>uq_connection_webhook_owner</c> index — and when the copies disagreed, the gap was silent and
/// structural rather than a visible failure.
///
/// <para><c>DeadLettered</c> is the status they disagreed about, and it is the dangerous one: it
/// names both "registration never created a remote hook" and "teardown failed to delete one that is
/// still there and still firing", with nothing on the row to tell them apart. Every test here is
/// written against the second reading, because that is the one that loses data — treating a
/// delivering hook as gone puts a second hook beside it, and every push then arrives twice and
/// starts two runs.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WebhookInServiceRuleTests
{
    private readonly PostgresFixture _fixture;

    public WebhookInServiceRuleTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>
    /// The duplicate-hook bug, at the provisioner. A DeadLettered hook may exist at the provider, so
    /// a later bind under the same owner must find it rather than stage a second row beside it.
    /// </summary>
    [Theory]
    [InlineData(RepositoryWebhookRegistrationStatus.DeadLettered)]
    [InlineData(RepositoryWebhookRegistrationStatus.Failed)]
    [InlineData(RepositoryWebhookRegistrationStatus.Registered)]
    public async Task A_hook_in_service_covers_its_owner_so_a_later_bind_stages_nothing_beside_it(RepositoryWebhookRegistrationStatus status)
    {
        var seed = await SeedConnectionHookAsync("acme", status);

        await EnsureForOwnerAsync(seed, "acme").ConfigureAwait(false);

        var rows = await CountConnectionHooksAsync(seed.ProviderInstanceId, "acme").ConfigureAwait(false);

        rows.ShouldBe(1,
            customMessage: $"A {status} hook must count as covering 'acme'. A second row means two hooks on one " +
                           "group at the provider, so every push arrives twice and starts two runs.");
    }

    /// <summary>
    /// The other half of counting DeadLettered as covering: it is the one in-service status nothing
    /// else can leave — the reconciler skips it by design and connection hooks have no per-hook retry
    /// endpoint — so if a bind did not revive it, the owner would be wedged forever.
    /// </summary>
    [Fact]
    public async Task A_bind_revives_a_dead_lettered_hook_and_resets_its_ladder()
    {
        var seed = await SeedConnectionHookAsync("acme", RepositoryWebhookRegistrationStatus.DeadLettered, attempts: 10);

        var dispatchable = await EnsureForOwnerAsync(seed, "acme").ConfigureAwait(false);

        dispatchable.ShouldNotBeNull(
            customMessage: "A bind is the operator intervention DeadLettered asks for; returning null wedges the " +
                           "owner permanently, because nothing else revives this status.");

        var hook = await LoadHookAsync(seed.HookId).ConfigureAwait(false);

        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Pending);
        hook.Attempts.ShouldBe(0,
            customMessage: "Reviving a row already at MaxAttempts without resetting buys exactly one try — the next " +
                           "transient timeout re-buries it and the operator is back where they started.");
    }

    /// <summary>
    /// A Registered hook is working; pulling it back to Pending would re-register a hook that already
    /// exists. Guards the revive branch from swallowing the healthy states.
    /// </summary>
    [Fact]
    public async Task A_registered_hook_is_left_alone_by_a_bind()
    {
        var seed = await SeedConnectionHookAsync("acme", RepositoryWebhookRegistrationStatus.Registered);

        await EnsureForOwnerAsync(seed, "acme").ConfigureAwait(false);

        var hook = await LoadHookAsync(seed.HookId).ConfigureAwait(false);

        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered);
    }

    /// <summary>
    /// A Failed hook still on its backoff ladder belongs to the reconciler. Pulling it forward early
    /// would let a bind loop bypass the backoff entirely.
    /// </summary>
    [Fact]
    public async Task A_failed_hook_that_is_not_due_yet_stays_on_its_backoff_ladder()
    {
        var seed = await SeedConnectionHookAsync("acme", RepositoryWebhookRegistrationStatus.Failed, attempts: 3, nextAttemptAt: DateTimeOffset.UtcNow.AddMinutes(30));

        await EnsureForOwnerAsync(seed, "acme").ConfigureAwait(false);

        var hook = await LoadHookAsync(seed.HookId).ConfigureAwait(false);

        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Failed);
        hook.Attempts.ShouldBe(3, customMessage: "A not-yet-due Failed row keeps its ladder position; only an exhausted row resets.");
    }

    /// <summary>
    /// The last line of defence. Two binds racing past the coverage read at the same instant both
    /// reach the insert, and only the index can still refuse the second one — which it cannot do
    /// while it excludes DeadLettered from its predicate.
    /// </summary>
    [Fact]
    public async Task The_index_refuses_a_second_row_for_an_owner_a_dead_lettered_hook_already_holds()
    {
        var seed = await SeedConnectionHookAsync("acme", RepositoryWebhookRegistrationStatus.DeadLettered);

        var duplicate = await Should.ThrowAsync<DbUpdateException>(() => InsertRawHookAsync(seed, "acme", RepositoryWebhookRegistrationStatus.Pending));

        (duplicate.InnerException as Npgsql.PostgresException)?.SqlState.ShouldBe("23505",
            customMessage: "uq_connection_webhook_owner must exclude only Cancelled. While it also excludes " +
                           "DeadLettered, a racing insert lands a second hook on a group that is still delivering.");
    }

    /// <summary>A Cancelled hook is the one status an operator deliberately moved off, so it must NOT hold the owner.</summary>
    [Fact]
    public async Task A_cancelled_hook_does_not_hold_its_owner_against_a_fresh_attempt()
    {
        var seed = await SeedConnectionHookAsync("acme", RepositoryWebhookRegistrationStatus.Cancelled);

        await EnsureForOwnerAsync(seed, "acme").ConfigureAwait(false);

        var rows = await CountConnectionHooksAsync(seed.ProviderInstanceId, "acme").ConfigureAwait(false);

        rows.ShouldBe(2, customMessage: "Cancelled is evidence of what was tried, not a claim of coverage — a fresh row must be stageable beside it.");
    }

    /// <summary>
    /// The exit half of the rule, and the one that turns the fix into a wedge if it is missed.
    /// Widening what counts as in service without widening what a retirement can CAS out leaves
    /// DeadLettered as a status nothing can retire — it holds its owner against the unique index
    /// forever, so the group can never be covered again.
    /// </summary>
    [Fact]
    public async Task Retiring_a_connection_takes_its_dead_lettered_hook_out_of_service()
    {
        var seed = await SeedConnectionHookAsync("acme", RepositoryWebhookRegistrationStatus.DeadLettered);

        await RetireAllAsync(seed).ConfigureAwait(false);

        var hook = await LoadHookAsync(seed.HookId).ConfigureAwait(false);

        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Cancelled,
            customMessage: "A DeadLettered hook counts as covering, so a retirement MUST be able to CAS it out. " +
                           "Left in service it holds uq_connection_webhook_owner forever and no later bind can cover the group.");
    }

    /// <summary>The point of the exit half, stated as the behaviour an operator sees: a switch away and back must work.</summary>
    [Fact]
    public async Task An_owner_can_be_covered_again_after_a_retirement_that_found_it_dead_lettered()
    {
        var seed = await SeedConnectionHookAsync("acme", RepositoryWebhookRegistrationStatus.DeadLettered);

        await RetireAllAsync(seed).ConfigureAwait(false);

        var dispatchable = await EnsureForOwnerAsync(seed, "acme").ConfigureAwait(false);

        dispatchable.ShouldNotBeNull(customMessage: "After a retirement the owner must be coverable again — otherwise the connection is wedged in the mode it just left.");

        var rows = await CountConnectionHooksAsync(seed.ProviderInstanceId, "acme").ConfigureAwait(false);
        rows.ShouldBe(2, customMessage: "The retired row stays as evidence and the fresh Pending row sits beside it — which only the Cancelled exclusion in the index permits.");
    }

    private async Task RetireAllAsync(SeededConnection seed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instance = await db.ProviderInstance.SingleAsync(p => p.Id == seed.ProviderInstanceId);

        await scope.Resolve<IConnectionWebhookProvisioner>().RetireAllAsync(instance, CancellationToken.None).ConfigureAwait(false);

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<Guid?> EnsureForOwnerAsync(SeededConnection seed, string ownerPath)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instance = await db.ProviderInstance.SingleAsync(p => p.Id == seed.ProviderInstanceId);

        var id = await scope.Resolve<IConnectionWebhookProvisioner>()
            .EnsureForOwnerAsync(instance, seed.CredentialId, ownerPath, CancellationToken.None).ConfigureAwait(false);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return id;
    }

    private async Task<int> CountConnectionHooksAsync(Guid providerInstanceId, string ownerPath)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking()
            .CountAsync(w => w.ProviderInstanceId == providerInstanceId && w.OwnerPath == ownerPath).ConfigureAwait(false);
    }

    private async Task<ConnectionWebhook> LoadHookAsync(Guid hookId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking()
            .SingleAsync(w => w.Id == hookId).ConfigureAwait(false);
    }

    private async Task InsertRawHookAsync(SeededConnection seed, string ownerPath, RepositoryWebhookRegistrationStatus status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();

        db.ConnectionWebhook.Add(new ConnectionWebhook
        {
            Id = id,
            ProviderInstanceId = seed.ProviderInstanceId,
            CredentialId = seed.CredentialId,
            OwnerPath = ownerPath,
            CallbackUrl = $"https://codespace.test/api/webhooks/connection/{id}",
            SecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt("conn-secret"),
            SubscribedEvents = new List<string> { "push" },
            RegistrationStatus = status
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<SeededConnection> SeedConnectionHookAsync(string ownerPath, RepositoryWebhookRegistrationStatus status, int attempts = 0, DateTimeOffset? nextAttemptAt = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "Conn", BaseUrl = $"https://svc-{suffix}.local", WebhookScope = ProviderWebhookScope.Connection };

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.Pat,
            DisplayName = "PAT",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "pat-xxx" }))
        };

        var hookId = Guid.NewGuid();

        db.User.Add(owner);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner });
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.ConnectionWebhook.Add(new ConnectionWebhook
        {
            Id = hookId,
            ProviderInstanceId = instance.Id,
            CredentialId = credential.Id,
            OwnerPath = ownerPath,
            ExternalId = "remote-group-hook",
            CallbackUrl = $"https://codespace.test/api/webhooks/connection/{hookId}",
            SecretEnc = encryptor.Encrypt("conn-secret"),
            SubscribedEvents = new List<string> { "push" },
            RegistrationStatus = status,
            Attempts = attempts,
            NextAttemptAt = nextAttemptAt ?? DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeededConnection(team.Id, instance.Id, credential.Id, hookId);
    }

    private sealed record SeededConnection(Guid TeamId, Guid ProviderInstanceId, Guid CredentialId, Guid HookId);
}
