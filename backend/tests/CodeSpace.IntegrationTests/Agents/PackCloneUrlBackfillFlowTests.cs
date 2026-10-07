using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// HIGH fidelity (Rule 12): the clone-URL backfill through the REAL mediator (<see cref="BackfillPackCloneUrlsCommand"/>
/// → its handler → <see cref="PackCloneUrlBackfillService"/>) over real Postgres, against pack rows seeded by raw SQL in
/// exactly the shape the code before the seal wrote — the pasted URL verbatim in <c>url</c>, no new column set. A sealed
/// row must hold no token anywhere at rest, decrypt through the production <see cref="IPackCloneUrlProtector"/> to the
/// URL it was imported with, and keep syncing against a remote that demands that token (<see cref="PrivatePackSource"/>).
/// Legacy forks of one repository must settle into one holder plus duplicates that each keep syncing; a re-import that
/// arrives before the backfill must land in the unsealed row (the clean pack, when one exists) rather than fork it; several
/// workers at once must seal each row once and converge.
///
/// <para>The sweep is deployment-wide and these tests share a database, so every assertion is on rows the test owns,
/// never on a pass's tally; passes are looped with a bounded count and an actionable failure. Seeded rows are dated
/// decades back so they sort first into every batch.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PackCloneUrlBackfillFlowTests
{
    private static readonly DateTimeOffset LongAgo = DateTimeOffset.UtcNow.AddYears(-30);

    private readonly PostgresFixture _fixture;

    public PackCloneUrlBackfillFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_legacy_tokened_row_syncs_before_and_after_it_is_sealed()
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync();
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();
        var packId = await harness.SeedLegacyPackAsync(team, source.TokenedUrl, LongAgo);

        (await harness.SendAsync(team.OwnerId, team.TeamId, new SyncPackCommand { PackId = packId })).NewArtifacts.Agents.ShouldContain(a => a.SourcePath == PrivatePackSource.Agent, "an unsealed legacy row still clones from the token in its URL");

        await harness.BackfillUntilSealedAsync(packId);

        var columns = await harness.ColumnsAsync(packId);
        columns.Url.ShouldBe(source.CleanUrl);
        columns.DuplicateOfPackId.ShouldBeNull("nothing else holds this source");
        (await harness.CloneUrlOfAsync(packId)).ShouldBe(source.TokenedUrl, "the production protector decrypts the seal to the URL the pack was imported with");
        PrivatePackSource.ShouldHoldNoToken(await harness.RawRowAsync(packId), "the sealed row");

        (await harness.SendAsync(team.OwnerId, team.TeamId, new SyncPackCommand { PackId = packId })).NewArtifacts.Agents.ShouldContain(a => a.SourcePath == PrivatePackSource.Agent, "the sealed row clones with its sealed token");
    }

    [Fact]
    public async Task A_second_pass_leaves_a_sealed_row_exactly_as_the_first_left_it()
    {
        var harness = new PackCredentialHarness(_fixture);
        var team = await harness.SeedTeamAsync();
        var packId = await harness.SeedLegacyPackAsync(team, $"https://x-access-token:{PrivatePackSource.Token}@git.example.test/acme/agents", LongAgo);

        await harness.BackfillUntilSealedAsync(packId);
        var first = await harness.RawRowAsync(packId);

        await harness.BackfillPassAsync();

        (await harness.RawRowAsync(packId)).ShouldBe(first, "a sealed row is no candidate — no column, the ciphertext included, is rewritten");
    }

    [Fact]
    public async Task A_soft_deleted_tokened_row_is_sealed_too()
    {
        var harness = new PackCredentialHarness(_fixture);
        var team = await harness.SeedTeamAsync();
        var packId = await harness.SeedLegacyPackAsync(team, $"https://x-access-token:{PrivatePackSource.Token}@git.example.test/acme/agents", LongAgo, deleted: true);

        await harness.BackfillUntilSealedAsync(packId);

        var columns = await harness.ColumnsAsync(packId);
        columns.DeletedDate.ShouldNotBeNull("fixture check: the row stays soft-deleted");
        columns.Url.ShouldBe("https://git.example.test/acme/agents");
        columns.EncryptedCloneUrl.ShouldNotBeNull();
        PrivatePackSource.ShouldHoldNoToken(await harness.RawRowAsync(packId), "a soft-deleted row — plaintext at rest either way");
    }

    [Theory]
    [InlineData(false)]   // the repository was imported once without a token (a clean holder exists) and once with one
    [InlineData(true)]    // the repository was imported twice, with two tokens
    public async Task A_legacy_fork_settles_into_one_holder_and_a_duplicate_that_both_keep_syncing(bool twoTokens)
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync(authenticateReads: twoTokens);
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();

        // The tokened row is the OLDER one in the clean-holder case, so the clean row holding proves "clean wins", not "oldest wins".
        var older = await harness.SeedLegacyPackAsync(team, source.TokenedUrl, LongAgo);
        var newer = await harness.SeedLegacyPackAsync(team, twoTokens ? source.UrlWith("x-access-token:fake%2Dpublish-token-0123456789") : source.CleanUrl, LongAgo.AddMinutes(1));

        await harness.BackfillUntilSealedAsync(older, newer);

        var (holder, duplicate) = twoTokens ? (older, newer) : (newer, older);
        (await harness.ColumnsAsync(holder)).DuplicateOfPackId.ShouldBeNull(twoTokens ? "with no clean row, the oldest sealed row holds the source" : "the clean row holds the source");
        (await harness.ColumnsAsync(duplicate)).DuplicateOfPackId.ShouldBe(holder);
        (await harness.ColumnsAsync(duplicate)).Url.ShouldBe(source.CleanUrl, "both rows now show the same credential-free URL");

        foreach (var packId in new[] { holder, duplicate })
            (await harness.SendAsync(team.OwnerId, team.TeamId, new SyncPackCommand { PackId = packId })).NewArtifacts.Agents.ShouldContain(a => a.SourcePath == PrivatePackSource.Agent, $"pack {packId} keeps syncing from its own source");

        (await harness.ImportAsync(team, source.TokenedUrl, PrivatePackSource.Agent)).ShouldBe(holder, "a new import resolves to the holder");

        var added = await harness.SendAsync(team.OwnerId, team.TeamId, new ImportPackArtifactsCommand { PackId = duplicate, SourcePaths = new[] { PrivatePackSource.Skill } });
        added.PackId.ShouldBe(duplicate, "an import from the duplicate lands on the duplicate, not on the holder");
        added.Items.ShouldHaveSingleItem().Outcome.ShouldBe(PackImportOutcome.Imported);
    }

    [Theory]
    [InlineData(null)]                                                  // the same tokened URL pasted again: the code before the seal updated this row in place
    [InlineData("x-access-token:fake%2Dpublish-token-0123456789")]     // a rotated token (a new spelling the remote still accepts), as the rotation advice prompts
    public async Task A_re_import_before_the_backfill_lands_in_the_unsealed_legacy_pack_and_seals_it(string? rotatedUserInfo)
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync();
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();
        var legacy = await harness.ImportAsync(team, source.TokenedUrl, PrivatePackSource.Agent);
        await harness.MakeLegacyAsync(legacy, source.TokenedUrl);

        var secondUrl = rotatedUserInfo is null ? source.TokenedUrl : source.UrlWith(rotatedUserInfo);
        var again = await harness.ImportAsync(team, secondUrl, PrivatePackSource.Agent, PrivatePackSource.Skill);

        again.ShouldBe(legacy, "the unsealed row holds this repository's history; a second pack would be marked its holder once the backfill seals it");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.Pack.AsNoTracking().CountAsync(p => p.TeamId == team.TeamId && p.DeletedDate == null)).ShouldBe(1, "no second pack is created beside the legacy one");
            (await db.AgentDefinition.AsNoTracking().CountAsync(a => a.TeamId == team.TeamId && a.Scope == DefinitionScope.Store && a.DeletedDate == null)).ShouldBe(1, "the re-selected agent is refreshed in place, not copied");
        }

        var columns = await harness.ColumnsAsync(legacy);
        columns.Url.ShouldBe(source.CleanUrl, "the import seals the row it lands in, as the backfill would");
        columns.DuplicateOfPackId.ShouldBeNull();
        (await harness.CloneUrlOfAsync(legacy)).ShouldBe(secondUrl, "the sealed source is the URL this import cloned");
        PrivatePackSource.ShouldHoldNoToken(await harness.RawRowAsync(legacy), "the legacy row after the re-import");

        (await harness.SendAsync(team.OwnerId, team.TeamId, new SyncPackCommand { PackId = legacy })).UpToDate.ShouldBe(2, "the pack keeps syncing from the source it now records");
    }

    [Fact]
    public async Task A_re_import_before_the_backfill_prefers_the_clean_pack_over_an_unsealed_fork()
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync();
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();

        // The tokened fork is the OLDER one, so landing on the clean pack proves "clean wins", not "oldest wins".
        var fork = await harness.SeedLegacyPackAsync(team, source.TokenedUrl, LongAgo);
        var clean = await harness.SeedLegacyPackAsync(team, source.CleanUrl, LongAgo.AddMinutes(1));

        (await harness.ImportAsync(team, source.TokenedUrl, PrivatePackSource.Agent)).ShouldBe(clean, "the clean pack holds the source; sealing the fork in place would collide with it");

        (await harness.ColumnsAsync(fork)).Url.ShouldBe(source.TokenedUrl, "the fork is left to the backfill");

        await harness.BackfillUntilSealedAsync(fork);

        (await harness.ColumnsAsync(fork)).DuplicateOfPackId.ShouldBe(clean, "the backfill settles the fork as the clean pack's duplicate");
    }

    [Fact]
    public async Task Concurrent_passes_seal_each_row_once_and_converge_on_one_holder()
    {
        var harness = new PackCredentialHarness(_fixture);
        var team = await harness.SeedTeamAsync();
        const string host = "git.example.test/acme/agents";

        var forkA = await harness.SeedLegacyPackAsync(team, $"https://x-access-token:{PrivatePackSource.Token}@{host}", LongAgo);
        var forkB = await harness.SeedLegacyPackAsync(team, $"https://oauth2:{PrivatePackSource.Token}@{host}", LongAgo);   // same instant: the holder race has no tie-break to lean on
        var alone = await harness.SeedLegacyPackAsync(team, $"https://x-access-token:{PrivatePackSource.Token}@git.example.test/acme/other", LongAgo);
        var owned = new[] { forkA, forkB, alone };
        var originals = new Dictionary<Guid, string>
        {
            [forkA] = $"https://x-access-token:{PrivatePackSource.Token}@{host}",
            [forkB] = $"https://oauth2:{PrivatePackSource.Token}@{host}",
            [alone] = $"https://x-access-token:{PrivatePackSource.Token}@git.example.test/acme/other",
        };

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(harness.BackfillPassAsync)));

        await harness.BackfillUntilSealedAsync(owned);

        var settled = new Dictionary<Guid, string>();
        foreach (var packId in owned)
        {
            (await harness.CloneUrlOfAsync(packId)).ShouldBe(originals[packId], $"pack {packId}'s seal decrypts to its own original — no worker overwrote it with another row's");
            settled[packId] = await harness.RawRowAsync(packId);
        }

        var forks = await Task.WhenAll(new[] { forkA, forkB }.Select(async id => (Id: id, Columns: await harness.ColumnsAsync(id))));
        forks.Count(f => f.Columns.DuplicateOfPackId is null).ShouldBe(1, "exactly one fork holds the source");
        forks.Single(f => f.Columns.DuplicateOfPackId is not null).Columns.DuplicateOfPackId.ShouldBe(forks.Single(f => f.Columns.DuplicateOfPackId is null).Id);
        (await harness.ColumnsAsync(alone)).DuplicateOfPackId.ShouldBeNull();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(harness.BackfillPassAsync)));

        foreach (var packId in owned)
            (await harness.RawRowAsync(packId)).ShouldBe(settled[packId], $"pack {packId} was sealed once: later passes, concurrent ones included, rewrite nothing");
    }

    [Fact]
    public async Task A_sync_that_loaded_the_row_before_its_seal_loses_on_the_concurrency_token_and_a_retry_succeeds()
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync();
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();
        var packId = await harness.SeedLegacyPackAsync(team, source.TokenedUrl, LongAgo);

        // The seal lands between the sync's read of the row and its write: the fetch runs after the load.
        var sealDuringFetch = new SealingFetcher(source.Fetcher, () => harness.BackfillUntilSealedAsync(packId));

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => SyncThroughAsync(sealDuringFetch, team, packId), "the sync's write is fenced by xmin, so it never writes back over the seal");

        PrivatePackSource.ShouldHoldNoToken(await harness.RawRowAsync(packId), "the row after the lost sync");

        (await harness.SendAsync(team.OwnerId, team.TeamId, new SyncPackCommand { PackId = packId })).NewArtifacts.Agents.ShouldContain(a => a.SourcePath == PrivatePackSource.Agent, "the retry clones with the sealed token");
    }

    [Fact]
    public async Task The_source_index_admits_a_duplicate_beside_its_holder_and_nothing_else()
    {
        var harness = new PackCredentialHarness(_fixture);
        var team = await harness.SeedTeamAsync();
        const string url = "https://git.example.test/acme/indexed";

        var holder = await harness.SeedLegacyPackAsync(team, url, LongAgo);
        var duplicate = await harness.SeedLegacyPackAsync(team, url + "-dup", LongAgo);
        (await harness.ExecuteAsync("UPDATE pack SET url = @url, duplicate_of_pack_id = @holder WHERE id = @id", ("url", url), ("holder", holder), ("id", duplicate))).ShouldBe(1, "a duplicate coexists with its holder");

        var clash = await Should.ThrowAsync<PostgresException>(() => harness.SeedLegacyPackAsync(team, url, LongAgo));
        clash.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation, "two packs holding one source is still refused");
    }

    private async Task SyncThroughAsync(IPackSourceFetcher fetcher, PackCredentialTeam team, Guid packId)
    {
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(team.OwnerId, "pack-credential")).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(team.TeamId)).As<CodeSpace.Core.Services.Identity.ICurrentTeam>().SingleInstance();
            b.RegisterInstance(fetcher).As<IPackSourceFetcher>().SingleInstance();
        });

        await scope.Resolve<IMediator>().Send(new SyncPackCommand { PackId = packId });
    }

    /// <summary>The production fetcher, with <paramref name="beforeClone"/> run first — after the sync has loaded its row.</summary>
    private sealed class SealingFetcher(IPackSourceFetcher inner, Func<Task> beforeClone) : IPackSourceFetcher
    {
        public async Task<PackCheckout> FetchAsync(string url, string? reference, CancellationToken cancellationToken)
        {
            await beforeClone();
            return await inner.FetchAsync(url, reference, cancellationToken);
        }
    }
}
