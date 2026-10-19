using System.Net;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity — the two recurring sweeps that act on a credential they have judged dead, run against a credential whose
/// token somebody is writing: the repair of stranded bindings (<see cref="RepairDisconnectedCredentialBindingsCommand"/>) and
/// the retirement of a departed member's tokens (<see cref="RetireDepartedMembersCredentialsCommand"/>), each sent through the host's own
/// mediator — the Hangfire tick that would send them is not what is under test. The re-authorization is the real HTTP one (the
/// GitLab callback, through the real ASP.NET pipeline and the real GitLab OAuth client), against real Postgres and a
/// <see cref="GitLabDoorkeeperStub"/> on a loopback port. The one stand-in is the writer's lock: another pod's hold on the
/// credential's advisory lock is taken by the test on a connection of its own, as a second host would take it.
///
/// <para>What this pins end to end: a person's reconnect that is queued behind a write is not undone by a repair pass that
/// runs meanwhile — the pass skips the credential, the reconnect then lands, and the next pass finds a healthy credential and
/// leaves its repository on it; and a departed member's token that is being written is kept by the pass that finds it busy and
/// retired by the one after, the skipped passes counting as nothing against it. The interleavings inside one request (a
/// verdict recorded between a sweep's read and its move) are pinned by the integration tier, where the gap can be made.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "ProviderE2E")]
[Trait("Surface", "Http")]
public sealed class CredentialSweepLockE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string BindingCookie = "codespace_oauth_nonce";
    private const string CallbackPath = "/api/credentials/oauth/callback";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TaskLaunchApiFactory _factory;
    private readonly TenantHarness _tenants;

    public CredentialSweepLockE2ETests(TaskLaunchApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Fact]
    public async Task A_repair_pass_that_runs_while_a_reconnect_is_waiting_to_write_moves_nothing_and_the_next_one_leaves_the_reconnected_credential_its_repository()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedStrandedAsync(gitlab).ConfigureAwait(false);
        var init = await InitAsync(world).ConfigureAwait(false);
        var state = await StateOfAsync(init).ConfigureAwait(false);
        Task<HttpResponseMessage> reconnect;

        await using (await HoldLockLikeAnotherPodAsync(world.DeadId).ConfigureAwait(false))
        {
            reconnect = Task.Run(() => CallbackAsync(state, "authz:nacho", CookieValue(BindingCookieOf(init)!)));
            await WaitForAsync(AnAdvisoryLockIsWaitedForAsync, $"the reconnect, having exchanged its code, queued for credential {world.DeadId}'s write lock — `SELECT * FROM pg_locks WHERE locktype = 'advisory' AND NOT granted` shows whether it did").ConfigureAwait(false);
            reconnect.IsCompleted.ShouldBeFalse("fixture check: the reconnect is waiting for the write lock another pod holds");

            await RepairWithinPatienceAsync("the repair pass finished without waiting for the lock").ConfigureAwait(false);

            (await ReadRepositoryAsync(world.RepositoryId).ConfigureAwait(false)).CredentialId.ShouldBe(world.DeadId, "the credential is being written: the pass cannot know the reconnect is about to revive it, so it moved nothing");
            (await ReadCredentialAsync(world.DeadId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Error, "fixture check: the reconnect has not landed yet");
        }

        var callback = await reconnect.WaitAsync(Patience).ConfigureAwait(false);
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await TenantHarness.DescribeAsync(callback).ConfigureAwait(false));
        (await ReadCredentialAsync(world.DeadId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active, "the reconnect landed once the lock was free");

        await RepairWithinPatienceAsync("the repair pass after the reconnect finished").ConfigureAwait(false);

        (await ReadRepositoryAsync(world.RepositoryId).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(world.DeadId, "healthy again, so no longer stranded: its repository stays on it"), r => r.Status.ShouldBe(RepositoryStatus.Active));
        (await _tenants.DbAsync(db => db.Repository.AsNoTracking().CountAsync(r => r.CredentialId == world.SuccessorId)).ConfigureAwait(false)).ShouldBe(0, "nothing was carried onto the owner's other credential");
        await AssertLockFreeAsync(world.DeadId).ConfigureAwait(false);
    }

    [Fact]
    public async Task A_repair_pass_that_finds_nothing_held_moves_a_stranded_credentials_repository_to_the_owners_live_one_and_gives_the_lock_back()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedStrandedAsync(gitlab).ConfigureAwait(false);

        await RepairWithinPatienceAsync("the repair pass finished").ConfigureAwait(false);

        (await ReadRepositoryAsync(world.RepositoryId).ConfigureAwait(false)).CredentialId.ShouldBe(world.SuccessorId, "the normal case, as it always was: a credential nobody is writing and whose owner connected another is repaired");
        await AssertLockFreeAsync(world.DeadId).ConfigureAwait(false);
    }

    [Fact]
    public async Task A_departed_members_token_that_is_being_written_is_kept_by_every_pass_that_finds_it_busy_and_retired_by_the_first_that_does_not()
    {
        var world = await SeedDepartedMembersTokenAsync().ConfigureAwait(false);

        await using (await HoldLockLikeAnotherPodAsync(world.CredentialId).ConfigureAwait(false))
        {
            for (var pass = 0; pass <= SweepFailureBackoff.FailuresBeforeBackoff; pass++)
                await RetireWithinPatienceAsync("the retirement pass finished without waiting for the lock").ConfigureAwait(false);

            (await ReadCredentialAsync(world.CredentialId).ConfigureAwait(false)).ShouldSatisfyAllConditions(c => c.EncryptedPayload.ShouldNotBeEmpty("somebody is writing the token: a retirement under that write would blank what it lands"), c => c.DeletedDate.ShouldBeNull());
            (await ReadRepositoryAsync(world.RepositoryId).ConfigureAwait(false)).Status.ShouldBe(RepositoryStatus.Active);
        }

        await RetireWithinPatienceAsync("the retirement pass after the write finished").ConfigureAwait(false);

        (await ReadCredentialAsync(world.CredentialId).ConfigureAwait(false)).ShouldSatisfyAllConditions(c => c.EncryptedPayload.ShouldBeEmpty("a busy token is not a failing one: the passes that skipped it did not set it aside"), c => c.DeletedDate.ShouldNotBeNull(), c => c.Status.ShouldBe(CredentialStatus.Revoked));
        (await ReadRepositoryAsync(world.RepositoryId).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.Status.ShouldBe(RepositoryStatus.Error), r => r.LastError.ShouldBe(CredentialRevocationService.DisconnectedRepositoryError));
        await AssertLockFreeAsync(world.CredentialId).ConfigureAwait(false);
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private sealed record StrandedWorld(Guid OwnerId, Guid TeamId, Guid InstanceId, Guid DeadId, Guid SuccessorId, Guid RepositoryId);

    private sealed record DepartedWorld(Guid CredentialId, Guid RepositoryId);

    /// <summary>
    /// What the repair is for: nacho's credential refused by GitLab, with a repository bound to it, and a live credential of his
    /// connected after it to follow. The dead one has an id that sorts ahead of any random one, so a pass reads it in its first page
    /// whatever else the shared database holds.
    /// </summary>
    private async Task<StrandedWorld> SeedStrandedAsync(GitLabDoorkeeperStub gitlab)
    {
        var owner = await _tenants.AccountAsync().ConfigureAwait(false);
        var team = await _tenants.TeamAsync((owner, TeamRole.Admin)).ConfigureAwait(false);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var serializer = scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>();

        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team, Provider = ProviderKind.GitLab, DisplayName = $"gitlab-{Guid.NewGuid():N}"[..14], BaseUrl = gitlab.BaseUrl, ApiUrl = gitlab.BaseUrl, OauthClientId = GitLabDoorkeeperStub.DefaultClientId, OauthClientSecretEnc = encryptor.Encrypt(GitLabDoorkeeperStub.DefaultClientSecret), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var dead = NachosCredential(team, instance.Id, owner, encryptor, serializer, "at-0", "rt-0", FrontOfTheQueue(), CredentialStatus.Error, "401 Unauthorized");
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, CredentialId = dead.Id, ExternalId = Random.Shared.Next(100_000, 999_999).ToString(), NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"{gitlab.BaseUrl}/acme/api", CloneUrlHttps = $"{gitlab.BaseUrl}/acme/api.git", Status = RepositoryStatus.Active };
        var identity = new UserProviderIdentity { Id = Guid.NewGuid(), UserId = owner, ProviderInstanceId = instance.Id, CredentialId = dead.Id, ProviderUserId = gitlab.UserIdOf("nacho").ToString(), ProviderUsername = "nacho" };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(dead);
        db.Repository.Add(repository);
        db.UserProviderIdentity.Add(identity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var successor = NachosCredential(team, instance.Id, owner, encryptor, serializer, "at-1", "rt-1", Guid.NewGuid(), CredentialStatus.Active, lastError: null);

        db.Credential.Add(successor);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new StrandedWorld(owner, team, instance.Id, dead.Id, successor.Id, repository.Id);
    }

    private static Credential NachosCredential(Guid team, Guid instanceId, Guid owner, IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer, string accessToken, string refreshToken, Guid id, CredentialStatus status, string? lastError)
    {
        var payload = new OAuthPayload { AccessToken = accessToken, RefreshToken = refreshToken, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(status == CredentialStatus.Error ? -1 : 120) };

        return new Credential { Id = id, TeamId = team, ProviderInstanceId = instanceId, OwnerUserId = owner, Ownership = CredentialOwnership.Personal, AuthType = payload.Type, DisplayName = "GitLab · nacho", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)), ExpiresDate = payload.ExpiresAt, Status = status, LastError = lastError, CreatedBy = owner, LastModifiedBy = owner };
    }

    /// <summary>What the retirement is for: a live personal token of someone who never joined the team, with a repository bound through it.</summary>
    private async Task<DepartedWorld> SeedDepartedMembersTokenAsync()
    {
        var admin = await _tenants.AccountAsync().ConfigureAwait(false);
        var departed = await _tenants.AccountAsync().ConfigureAwait(false);
        var team = await _tenants.TeamAsync((admin, TeamRole.Admin)).ConfigureAwait(false);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var serializer = scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>();

        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team, Provider = ProviderKind.GitLab, DisplayName = $"gitlab-{Guid.NewGuid():N}"[..14], BaseUrl = "http://127.0.0.1:1", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var payload = new PatPayload { Token = $"glpat-departed-{Guid.NewGuid():N}" };
        var credential = new Credential { Id = FrontOfTheQueue(), TeamId = team, ProviderInstanceId = instance.Id, OwnerUserId = departed, Ownership = CredentialOwnership.Personal, AuthType = payload.Type, DisplayName = "GitLab · departed", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)), Status = CredentialStatus.Active, CreatedBy = departed, LastModifiedBy = departed };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = Random.Shared.Next(100_000, 999_999).ToString(), NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "http://127.0.0.1:1/acme/api", CloneUrlHttps = "http://127.0.0.1:1/acme/api.git", Status = RepositoryStatus.Active };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new DepartedWorld(credential.Id, repository.Id);
    }

    /// <summary>An id that sorts ahead of any random one: a sweep reads its candidates in id order, so a candidate here is in the first page whatever else the shared database holds.</summary>
    private static Guid FrontOfTheQueue() => Guid.Parse($"00000000-0000-0000-0000-{Guid.NewGuid().ToString("N")[..12]}");

    private async Task<HttpResponseMessage> InitAsync(StrandedWorld world) =>
        await _tenants.Client().SendAsync(_tenants.Request(HttpMethod.Post, "/api/credentials/oauth/init", world.OwnerId, world.TeamId, new { providerInstanceId = world.InstanceId, displayName = "GitLab · nacho", returnUrl = "/settings/credentials", replaceCredentialId = world.DeadId })).ConfigureAwait(false);

    private async Task<HttpResponseMessage> CallbackAsync(string state, string code, string bindingCookie)
    {
        var request = _tenants.Request(HttpMethod.Get, $"{CallbackPath}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", userId: null, teamId: null);

        request.Headers.Add("Cookie", $"{BindingCookie}={bindingCookie}");

        return await _tenants.Client().SendAsync(request).ConfigureAwait(false);
    }

    /// <summary>What another pod holds while it writes the credential's token: the advisory lock the write takes, on a connection that is not this host's.</summary>
    private async Task<IAsyncDisposable> HoldLockLikeAnotherPodAsync(Guid credentialId)
    {
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ICrossProcessLock>().AcquireAsync(CredentialRefreshLock.KeyOf(credentialId), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The lock was given back: both tiers are taken at once. A lock a sweep leaked would hang every later write of the credential, so this waits only as long as a free lock takes.</summary>
    private async Task AssertLockFreeAsync(Guid credentialId)
    {
        using var scope = _factory.Services.CreateScope();

        await using var held = await CredentialRefreshLock.AcquireAsync(scope.ServiceProvider.GetRequiredService<ICrossProcessLock>(), credentialId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private async Task RepairWithinPatienceAsync(string signal) =>
        await SweepWithinPatienceAsync(new RepairDisconnectedCredentialBindingsCommand { BatchSize = 500 }, signal).ConfigureAwait(false);

    private async Task RetireWithinPatienceAsync(string signal) =>
        await SweepWithinPatienceAsync(new RetireDepartedMembersCredentialsCommand { BatchSize = 500 }, signal).ConfigureAwait(false);

    private async Task SweepWithinPatienceAsync(ICommand<int> sweep, string signal)
    {
        using var scope = _factory.Services.CreateScope();

        try
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(sweep).WaitAsync(Patience).ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            throw new TimeoutException($"{signal} within {Patience.TotalSeconds}s — a sweep waits for no lock, so look for a transaction holding a row it needs: `SELECT pid, pg_blocking_pids(pid), state, query FROM pg_stat_activity WHERE datname = current_database()`.", timeout);
        }
    }

    /// <summary>True while some session in this database is queued for an advisory lock it has not been granted: the reconnect waiting for the write lock another pod holds.</summary>
    private async Task<bool> AnAdvisoryLockIsWaitedForAsync() =>
        await _tenants.DbAsync(async db => await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())").SingleAsync().ConfigureAwait(false) > 0).ConfigureAwait(false);

    private static async Task WaitForAsync(Func<Task<bool>> condition, string signal)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (!await condition().ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"{signal} — not seen within {Patience.TotalSeconds}s");

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private async Task<Credential> ReadCredentialAsync(Guid credentialId) =>
        await _tenants.DbAsync(db => db.Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId)).ConfigureAwait(false);

    private async Task<Repository> ReadRepositoryAsync(Guid repositoryId) =>
        await _tenants.DbAsync(db => db.Repository.AsNoTracking().SingleAsync(r => r.Id == repositoryId)).ConfigureAwait(false);

    private static string? BindingCookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(c => c.StartsWith($"{BindingCookie}=", StringComparison.Ordinal)) : null;

    private static string CookieValue(string setCookie) => setCookie.Split(';')[0][(BindingCookie.Length + 1)..];

    private static async Task<string> StateOfAsync(HttpResponseMessage init) =>
        JsonDocument.Parse(await init.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.GetProperty("state").GetString()!;
}
