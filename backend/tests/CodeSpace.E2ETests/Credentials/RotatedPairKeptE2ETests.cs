using System.Net;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity: a GitLab OAuth credential whose access token has aged out is used through the real HTTP surface
/// (<c>GET /api/credentials/{id}/accessible-repositories</c>: the real mediator pipeline, the real GitLab provider over
/// NGitLab, the real refresher with its Postgres advisory lock, the real <see cref="GitLabOAuthClient"/>), against real
/// Postgres and a <see cref="GitLabDoorkeeperStub"/> on a loopback port whose refresh tokens are single use. The one
/// production class replaced is the payload writer, wrapped on this test host only so the test can fail the store of the
/// rotated pair — the failure that, unhandled, leaves the row holding a token GitLab has consumed, and the credential
/// refused with <c>invalid_grant</c> by every request after it while it still reads as connected.
///
/// <para>The credential never goes dead: the request after a failed store is served, GitLab is never shown a refresh token
/// it has already consumed, and what the row holds is a refresh token GitLab still honours.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "ProviderE2E")]
[Trait("Surface", "Http")]
public sealed class RotatedPairKeptE2ETests : IClassFixture<PayloadStoreFaultApiFactory>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly PayloadStoreFaultApiFactory _factory;
    private readonly TenantHarness _tenants;
    private readonly ITestOutputHelper _output;

    public RotatedPairKeptE2ETests(PayloadStoreFaultApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
        _output = output;
    }

    [Theory]
    [InlineData(int.MaxValue, true)]   // every store fails: the request that rotated the pair fails with it
    [InlineData(1, false)]             // one store fails: the refresher's retry lands it and the request never notices
    public async Task A_rotated_pair_whose_store_fails_is_stored_by_the_credentials_next_use_and_the_credential_never_goes_dead(int failedStores, bool firstRequestFails)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        _factory.StoreFault.FailNext(failedStores);

        var first = await ListAccessibleRepositoriesAsync(world).ConfigureAwait(false);

        _factory.StoreFault.FailNext(0);
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, $"fixture check: GitLab rotated the pair during the first request, whatever became of it — {await TenantHarness.DescribeAsync(first).ConfigureAwait(false)}");
        (first.IsSuccessStatusCode == !firstRequestFails).ShouldBeTrue($"the first request {(firstRequestFails ? "carries the failed store" : "retries it")} — {await TenantHarness.DescribeAsync(first).ConfigureAwait(false)}");

        var second = await ListAccessibleRepositoriesAsync(world).ConfigureAwait(false);

        Dump(gitlab);
        second.StatusCode.ShouldBe(HttpStatusCode.OK, $"the credential's next use should be served — {await TenantHarness.DescribeAsync(second).ConfigureAwait(false)}");
        gitlab.Count(StubEvent.Refused).ShouldBe(0, $"a request presented a refresh token GitLab had already consumed: {string.Join(", ", gitlab.RefusedRefreshTokens())}");

        var row = await ReadCredentialAsync(world.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Active);
        row.LastError.ShouldBeNull();
        gitlab.IsRefreshTokenLive(row.StoredPayload.RefreshToken!).ShouldBeTrue("the row must hold a refresh token GitLab still honours, or the next use is refused");
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> ListAccessibleRepositoriesAsync(World world)
    {
        try
        {
            return await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{world.CredentialId}/accessible-repositories", world.UserId, world.TeamId).WaitAsync(Patience).ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            throw new TimeoutException($"GET accessible-repositories for credential {world.CredentialId} did not answer within {Patience.TotalSeconds}s — most likely stuck on the credential's refresh lock (the in-process semaphore or the Postgres advisory lock) or on a token request the stub is holding. While the test hangs, inspect `SELECT pid, granted, objid FROM pg_locks WHERE locktype = 'advisory'` on the test database.", timeout);
        }
    }

    private void Dump(GitLabDoorkeeperStub gitlab)
    {
        _output.WriteLine($"GitLab saw: rotated={gitlab.Count(StubEvent.Rotated)} refused={gitlab.Count(StubEvent.Refused)} api-200={gitlab.Count(StubEvent.Api200)} api-401={gitlab.Count(StubEvent.Api401)}; the host's store was tried {_factory.StoreFault.Stores} times");

        foreach (var entry in gitlab.Log) _output.WriteLine($"  {entry.At:HH:mm:ss.fff} {entry.Kind}: {entry.Detail}");
    }

    private sealed record World(Guid UserId, Guid TeamId, Guid CredentialId);

    private sealed record StoredCredential(CredentialStatus Status, string? LastError, OAuthPayload StoredPayload);

    /// <summary>One user in one team, an OAuth credential of their own on a GitLab instance whose OAuth app is the stub's, and the pair it holds — expired, and known to GitLab.</summary>
    private async Task<World> SeedAsync(GitLabDoorkeeperStub gitlab)
    {
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");

        var user = await _tenants.AccountAsync().ConfigureAwait(false);
        var team = await _tenants.TeamAsync((user, TeamRole.Member)).ConfigureAwait(false);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var serializer = scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>();
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team, Provider = ProviderKind.GitLab, DisplayName = $"gitlab-{Guid.NewGuid():N}"[..14], BaseUrl = gitlab.BaseUrl, OauthClientId = GitLabDoorkeeperStub.DefaultClientId, OauthClientSecretEnc = encryptor.Encrypt(GitLabDoorkeeperStub.DefaultClientSecret), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var expired = new OAuthPayload { AccessToken = "at-0", RefreshToken = "rt-0", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, OwnerUserId = user, Ownership = CredentialOwnership.Personal, AuthType = AuthType.OAuth, DisplayName = "GitLab · NACHO.L", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(expired)), ExpiresDate = expired.ExpiresAt, Status = CredentialStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new World(user, team, credential.Id);
    }

    private async Task<StoredCredential> ReadCredentialAsync(Guid credentialId)
    {
        using var scope = _factory.Services.CreateScope();
        var credential = await _tenants.DbAsync(db => db.Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId)).ConfigureAwait(false);
        var payload = (OAuthPayload)scope.ServiceProvider.GetRequiredService<ICredentialResolver>().Resolve(credential);

        return new StoredCredential(credential.Status, credential.LastError, payload);
    }
}
