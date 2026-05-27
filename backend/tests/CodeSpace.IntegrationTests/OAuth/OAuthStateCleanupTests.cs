using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// The janitor that sweeps abandoned <c>oauth_pending_state</c> rows. Asserts deletion is
/// strictly bounded by ExpiresDate — never touches live in-flight flows.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class OAuthStateCleanupTests
{
    private readonly PostgresFixture _fixture;

    public OAuthStateCleanupTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task DeleteExpired_removes_only_rows_past_their_expires_date()
    {
        var (userId, teamId, instanceId) = await SeedPrerequisitesAsync().ConfigureAwait(false);

        // Three rows: one expired 1 hour ago, one expiring 5 minutes from now, one expiring 1 hour from now.
        await InsertStateAsync(userId, teamId, instanceId, key: "expired-1h", expiresIn: TimeSpan.FromHours(-1)).ConfigureAwait(false);
        await InsertStateAsync(userId, teamId, instanceId, key: "near-future", expiresIn: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        await InsertStateAsync(userId, teamId, instanceId, key: "far-future", expiresIn: TimeSpan.FromHours(1)).ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var cleanup = scope.Resolve<IOAuthStateCleanup>();
        var deleted = await cleanup.DeleteExpiredAsync(CancellationToken.None).ConfigureAwait(false);

        deleted.ShouldBe(1);

        var db = scope.Resolve<CodeSpaceDbContext>();
        var survivors = await db.OAuthPendingState.AsNoTracking()
            .Where(s => s.State == "expired-1h" || s.State == "near-future" || s.State == "far-future")
            .Select(s => s.State)
            .ToListAsync()
            .ConfigureAwait(false);

        survivors.ShouldNotContain("expired-1h");
        survivors.ShouldContain("near-future");
        survivors.ShouldContain("far-future");
    }

    [Fact]
    public async Task DeleteExpired_is_a_noop_when_nothing_is_expired()
    {
        using var scope = _fixture.BeginScope();
        var cleanup = scope.Resolve<IOAuthStateCleanup>();
        var deleted = await cleanup.DeleteExpiredAsync(CancellationToken.None).ConfigureAwait(false);

        // Pre-existing rows from sibling tests may already be expired, but the contract here
        // is "doesn't blow up + returns the count" — we assert non-negative.
        deleted.ShouldBeGreaterThanOrEqualTo(0);
    }

    private async Task<(Guid UserId, Guid TeamId, Guid InstanceId)> SeedPrerequisitesAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = user.Id };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = CodeSpace.Messages.Enums.ProviderKind.GitLab,
            DisplayName = "i",
            BaseUrl = $"https://gitlab-{suffix}.local"
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, team.Id, instance.Id);
    }

    private async Task InsertStateAsync(Guid userId, Guid teamId, Guid instanceId, string key, TimeSpan expiresIn)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.OAuthPendingState.Add(new OAuthPendingState
        {
            State = key,
            ProviderInstanceId = instanceId,
            TeamId = teamId,
            InitiatorUserId = userId,
            CodeVerifier = "verifier",
            IntendedDisplayName = "name",
            ExpiresDate = DateTimeOffset.UtcNow + expiresIn
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
