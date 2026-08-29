using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Runtime;

/// <summary>
/// The proactive half of knowing whether stored work survived. Destination health answers "can this be reached now";
/// this answers "is what was already written still there", and only the second one notices a loss before a person
/// clicks an artifact and gets nothing.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PlacementIntegrityReaderTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly Dictionary<Guid, Guid> _revisions = [];
    private readonly List<string> _roots = [];

    public PlacementIntegrityReaderTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Known_losses_are_counted_against_the_population_they_came_from()
    {
        var teamId = await SeedTeamAsync();
        await PlaceAsync(teamId, ArtifactLocationState.Available, verifiedAt: DateTimeOffset.UtcNow.AddHours(-2));
        await PlaceAsync(teamId, ArtifactLocationState.Available, verifiedAt: DateTimeOffset.UtcNow.AddHours(-1));
        await PlaceAsync(teamId, ArtifactLocationState.Missing, verifiedAt: DateTimeOffset.UtcNow);
        await PlaceAsync(teamId, ArtifactLocationState.Corrupt, verifiedAt: DateTimeOffset.UtcNow);

        var integrity = await ReadAsync(teamId);

        integrity.Missing.ShouldBe(1);
        integrity.Corrupt.ShouldBe(1);
        integrity.Available.ShouldBe(2, "two losses out of four placements and two out of four thousand are different situations, and a bare count cannot tell them apart");
    }

    [Fact]
    public async Task How_overdue_the_checking_is_comes_from_the_placement_nobody_has_looked_at_longest()
    {
        // The operator-facing question behind the number is "how far back could a loss have gone unnoticed", which is
        // answered by the WORST-checked placement, not the average or the most recent.
        var teamId = await SeedTeamAsync();
        var longestUnchecked = DateTimeOffset.UtcNow.AddDays(-30);
        await PlaceAsync(teamId, ArtifactLocationState.Available, verifiedAt: longestUnchecked);
        await PlaceAsync(teamId, ArtifactLocationState.Available, verifiedAt: DateTimeOffset.UtcNow);

        var integrity = await ReadAsync(teamId);

        integrity.OldestVerifiedAt.ShouldNotBeNull().ShouldBe(longestUnchecked, tolerance: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task One_team_never_sees_another_teams_placements()
    {
        var teamId = await SeedTeamAsync();
        var otherTeamId = await SeedTeamAsync();
        await PlaceAsync(otherTeamId, ArtifactLocationState.Missing, verifiedAt: DateTimeOffset.UtcNow);

        var integrity = await ReadAsync(teamId);

        integrity.Missing.ShouldBe(0, "an operator reading their own storage page must never be shown another team's losses");
        integrity.Available.ShouldBe(0);
    }

    [Fact]
    public async Task A_team_that_has_stored_nothing_reports_nothing_rather_than_failing()
    {
        var integrity = await ReadAsync(await SeedTeamAsync());

        integrity.Available.ShouldBe(0);
        integrity.Missing.ShouldBe(0);
        integrity.OldestVerifiedAt.ShouldBeNull("no placement has ever been verified because none exists; that is not the same as one that is overdue");
    }

    private async Task<PlacementIntegritySummary> ReadAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IPlacementIntegrityReader>().ReadAsync(teamId, CancellationToken.None);
    }

    /// <summary>A team with a real routed destination, so its placements can reference a profile revision that exists.</summary>
    private async Task<Guid> SeedTeamAsync()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var destination = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);
        _roots.Add(destination.Root);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        _revisions[teamId] = await db.StorageProfileRevision.AsNoTracking()
            .Where(revision => revision.TeamId == teamId && revision.StorageProfileId == destination.ProfileId)
            .OrderByDescending(revision => revision.Revision).Select(revision => revision.Id).FirstAsync();

        return teamId;
    }

    private async Task PlaceAsync(Guid teamId, ArtifactLocationState state, DateTimeOffset? verifiedAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var objectId = Guid.NewGuid();

        db.ArtifactObject.Add(new ArtifactObject
        {
            Id = objectId, TeamId = teamId, Digest = System.Security.Cryptography.SHA256.HashData(objectId.ToByteArray()),
            SizeBytes = 12, CreatedDate = now.AddDays(-31),
        });
        var checksum = System.Security.Cryptography.SHA256.HashData(objectId.ToByteArray());
        var location = new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = teamId, ArtifactObjectId = objectId,
            StorageProfileRevisionId = _revisions[teamId], Locator = "local://placements", ObjectKey = $"objects/{objectId:N}",
            State = state, VerifiedAt = verifiedAt, Revision = 1, CreatedDate = now.AddDays(-31), LastModifiedDate = now,
            ObservedSizeBytes = 12, ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = checksum,
        };
        db.ArtifactLocation.Add(location);

        // The schema refuses a placement whose observation is not also in the append-only ledger, so a fixture that
        // skipped it would be seeding a state production can never reach.
        db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = teamId, ArtifactLocationId = location.Id, Revision = location.Revision,
            EventType = ArtifactLocationEventType.Verified, State = location.State, ObservedAt = now,
            ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
            ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt, DetailsJson = "{}",
        });

        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
