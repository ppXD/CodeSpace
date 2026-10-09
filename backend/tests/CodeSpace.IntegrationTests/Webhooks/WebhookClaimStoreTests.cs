using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Webhooks;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Webhooks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// The claim the replay check and the PR debounce both stand on, against real Postgres: one holder per key per window,
/// the same holder re-claiming freely (a provider redelivery), an expired claim taken over in place, two concurrent
/// claimants settled by the primary key rather than by a read both could pass, and a rolled-back delivery releasing its
/// claim so the provider's retry is not refused. Expired rows are cleared by the recurring sweep, outside any delivery's
/// transaction — a delivery that cleared them itself held their row locks until it committed, and two of them could
/// each wait on a key the other had just deleted.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WebhookClaimStoreTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly PostgresFixture _fixture;

    public WebhookClaimStoreTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_key_is_held_by_its_first_holder_and_free_to_that_holder_again()
    {
        var key = NewKey();

        (await ClaimAsync(key, "delivery-1")).ShouldBeTrue("a free key");
        (await ClaimAsync(key, "delivery-2")).ShouldBeFalse("held by another holder inside its window");
        (await ClaimAsync(key, "delivery-1")).ShouldBeTrue("the same holder — a provider redelivering the same delivery — is not a second event");
    }

    [Fact]
    public async Task An_expired_claim_is_taken_over_by_the_next_holder()
    {
        var key = NewKey();
        await ClaimAsync(key, "delivery-1");
        await ExpireAsync(key);

        (await ClaimAsync(key, "delivery-2")).ShouldBeTrue();
        (await LoadHolderAsync(key)).ShouldBe("delivery-2");
    }

    [Fact]
    public async Task Concurrent_claimants_are_settled_by_the_key_and_exactly_one_wins()
    {
        var key = NewKey();
        using var firstScope = _fixture.BeginScope();
        using var secondScope = _fixture.BeginScope();
        var firstDb = firstScope.Resolve<CodeSpaceDbContext>();
        var secondDb = secondScope.Resolve<CodeSpaceDbContext>();

        await using var first = await firstDb.Database.BeginTransactionAsync();
        await using var second = await secondDb.Database.BeginTransactionAsync();

        (await firstScope.Resolve<IWebhookClaimStore>().TryClaimAsync(key, "delivery-1", Window, CancellationToken.None)).ShouldBeTrue();

        var contender = secondScope.Resolve<IWebhookClaimStore>().TryClaimAsync(key, "delivery-2", Window, CancellationToken.None);
        await Task.Delay(200);
        contender.IsCompleted.ShouldBeFalse("the second claimant must wait on the first's uncommitted row, not read past it");

        await first.CommitAsync();

        (await contender.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeFalse(customMessage: "once the first commits, the second sees its claim — check the ON CONFLICT ... WHERE in WebhookClaimStore.UpsertClaimAsync");
        await second.CommitAsync();
    }

    [Fact]
    public async Task A_rolled_back_delivery_releases_its_claim()
    {
        var key = NewKey();

        using (var scope = _fixture.BeginScope())
        {
            await using var transaction = await scope.Resolve<CodeSpaceDbContext>().Database.BeginTransactionAsync();
            (await scope.Resolve<IWebhookClaimStore>().TryClaimAsync(key, "delivery-1", Window, CancellationToken.None)).ShouldBeTrue();
            await transaction.RollbackAsync();
        }

        (await ClaimAsync(key, "delivery-2")).ShouldBeTrue("a delivery that failed and rolled back must not refuse the provider's retry of the same event");
    }

    [Fact]
    public async Task Two_deliveries_claiming_among_many_expired_keys_do_not_deadlock()
    {
        // The interleaving that deadlocked when each claim purged a batch of expired rows inside its own delivery's
        // transaction: each delivery's purge locked (deleted) a different batch, then each upserted a key sitting in the
        // OTHER's batch, and each waited on the other's commit.
        var prefix = $"test:{Guid.NewGuid():N}:";
        await SeedExpiredAsync(prefix, count: 150);

        using var firstScope = _fixture.BeginScope();
        using var secondScope = _fixture.BeginScope();
        await using var first = await firstScope.Resolve<CodeSpaceDbContext>().Database.BeginTransactionAsync();
        await using var second = await secondScope.Resolve<CodeSpaceDbContext>().Database.BeginTransactionAsync();
        var firstStore = firstScope.Resolve<IWebhookClaimStore>();
        var secondStore = secondScope.Resolve<IWebhookClaimStore>();

        (await firstStore.TryClaimAsync(NewKey(), "delivery-a", Window, CancellationToken.None)).ShouldBeTrue();
        (await secondStore.TryClaimAsync(NewKey(), "delivery-b", Window, CancellationToken.None)).ShouldBeTrue();

        var firstTakesOver = firstStore.TryClaimAsync(prefix + 120, "delivery-a", Window, CancellationToken.None);
        var secondTakesOver = secondStore.TryClaimAsync(prefix + 50, "delivery-b", Window, CancellationToken.None);

        (await firstTakesOver.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue(customMessage: "an expired key is taken over in place without waiting on anyone — a wait here means a claim is locking rows it did not ask for");
        (await secondTakesOver.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();

        await first.CommitAsync();
        await second.CommitAsync();
    }

    [Fact]
    public async Task The_sweep_deletes_expired_claims_and_keeps_live_ones()
    {
        var stale = NewKey();
        var live = NewKey();
        await ClaimAsync(stale, "old");
        await ClaimAsync(live, "new");
        await ExpireAsync(stale, at: new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IMediator>().Send(new PurgeExpiredWebhookClaimsCommand())).Deleted.ShouldBeGreaterThanOrEqualTo(1);

        (await LoadHolderAsync(stale)).ShouldBeNull("the sweep is what keeps the table to about one window of deliveries");
        (await LoadHolderAsync(live)).ShouldBe("new", "a claim inside its window is still holding something off");
    }

    [Fact]
    public async Task The_sweep_steps_around_a_claim_an_open_delivery_holds_instead_of_waiting_on_it()
    {
        var held = NewKey();
        await ClaimAsync(held, "old");
        await ExpireAsync(held);

        using var deliveryScope = _fixture.BeginScope();
        await using var delivery = await deliveryScope.Resolve<CodeSpaceDbContext>().Database.BeginTransactionAsync();
        (await deliveryScope.Resolve<IWebhookClaimStore>().TryClaimAsync(held, "delivery-1", Window, CancellationToken.None)).ShouldBeTrue("the delivery takes the expired key over and holds its row until it commits");

        using (var sweepScope = _fixture.BeginScope())
            await sweepScope.Resolve<IMediator>().Send(new PurgeExpiredWebhookClaimsCommand()).WaitAsync(TimeSpan.FromSeconds(10));

        await delivery.CommitAsync();

        (await LoadHolderAsync(held)).ShouldBe("delivery-1", "the sweep must skip a row a delivery has locked — waiting on it is how a sweep joins a deadlock, and deleting it would drop a live claim");
    }

    private static string NewKey() => $"test:{Guid.NewGuid():N}";

    /// <summary><paramref name="count"/> claims under <paramref name="prefix"/>, expired in key order — the oldest first, as a purge batch would pick them.</summary>
    private async Task SeedExpiredAsync(string prefix, int count)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"INSERT INTO webhook_claim SELECT {prefix} || g, 'old-' || g, now() - interval '1 hour', now() - make_interval(secs => 300 - g) FROM generate_series(0, {count - 1}) g");
    }

    private async Task<bool> ClaimAsync(string key, string holder)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWebhookClaimStore>().TryClaimAsync(key, holder, Window, CancellationToken.None);
    }

    private async Task ExpireAsync(string key, DateTimeOffset? at = null)
    {
        using var scope = _fixture.BeginScope();
        var expiry = at ?? DateTimeOffset.UtcNow.AddMinutes(-1);
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE webhook_claim SET claimed_at = {expiry}, expires_at = {expiry} WHERE claim_key = {key}");
    }

    private async Task<string?> LoadHolderAsync(string key)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().Database.SqlQuery<string>($"SELECT holder AS \"Value\" FROM webhook_claim WHERE claim_key = {key}").SingleOrDefaultAsync();
    }
}
