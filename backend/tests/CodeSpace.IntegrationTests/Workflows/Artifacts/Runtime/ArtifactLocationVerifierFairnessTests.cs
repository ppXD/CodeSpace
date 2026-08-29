using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Runtime;

/// <summary>
/// Whether the sweep keeps looking at healthy placements once a destination has been abandoned.
///
/// <para><c>verified_at</c> is both the honest record of when a placement was last observed AND the cursor the sweep
/// orders by. A conclusive answer that failed to move it would pin that row at the front of the ordering permanently —
/// and demoted rows are the oldest rows in the table by construction, because being oldest is why they were picked.
/// Enough of them and every pass re-asks the same batch and no healthy location is ever verified again, deployment
/// wide. That failure is silent: the sweep keeps reporting work done.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactLocationVerifierFairnessTests
{
    private readonly PostgresFixture _fixture;

    public ArtifactLocationVerifierFairnessTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_abandoned_destination_does_not_stop_healthy_placements_from_ever_being_checked_again()
    {
        var teamId = await SeedTeamAsync();
        await PlaceManyAsync(teamId, ArtifactLocationState.Missing, count: 8, age: TimeSpan.FromDays(60));
        var healthy = await PlaceAsync(teamId, ArtifactLocationState.Available, age: TimeSpan.FromDays(30));
        var beforeSweep = healthy.VerifiedAt.ShouldNotBeNull();

        // A batch smaller than the abandoned population is the whole point: a single ordering would spend all of it on
        // rows already known to be gone, and the healthy one — younger, so ordered behind every one of them — would
        // never be reached.
        await VerifyAsync(batchSize: 4);

        var seen = await LocationAsync(healthy);
        seen.VerifiedAt.ShouldNotBeNull().ShouldBeGreaterThan(beforeSweep, "the healthy placement must be examined even while an abandoned destination holds more rows than the batch does");
    }

    [Fact]
    public async Task Re_confirming_a_loss_moves_its_cursor_so_the_next_pass_looks_elsewhere()
    {
        // Without this the row is re-asked every hour forever, at the cost of one provider round trip each time, and
        // it never yields its slot.
        var teamId = await SeedTeamAsync();
        var stuck = await PlaceAsync(teamId, ArtifactLocationState.Missing, age: TimeSpan.FromDays(60));
        var before = (await LocationAsync(stuck)).VerifiedAt.ShouldNotBeNull();

        await VerifyAsync(batchSize: 50);

        var after = await LocationAsync(stuck);
        after.VerifiedAt.ShouldNotBeNull().ShouldBeGreaterThan(before, "the destination answered, and an answer is what verified_at records");
        after.State.ShouldBe(ArtifactLocationState.Missing, "moving the cursor must not be mistaken for the object coming back");
        after.Revision.ShouldBeGreaterThan(stuck.Revision, "the schema requires every observation of a location to be an entry in its ledger, so the row advances even though its state did not");
    }

    // ─── World ───────────────────────────────────────────────────────────────

    private async Task VerifyAsync(int batchSize)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(batchSize, CancellationToken.None);
    }

    private async Task<ArtifactLocation> LocationAsync(ArtifactLocation seeded)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking().SingleAsync(location => location.Id == seeded.Id);
    }

    private async Task<Guid> SeedTeamAsync()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);

        return teamId;
    }

    private async Task PlaceManyAsync(Guid teamId, ArtifactLocationState state, int count, TimeSpan age)
    {
        foreach (var index in Enumerable.Range(0, count)) await PlaceAsync(teamId, state, age + TimeSpan.FromMinutes(index));
    }

    private async Task<ArtifactLocation> PlaceAsync(Guid teamId, ArtifactLocationState state, TimeSpan age)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var revisionId = await db.StorageProfileRevision.AsNoTracking().Where(revision => revision.TeamId == teamId)
            .OrderByDescending(revision => revision.Revision).Select(revision => revision.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;
        var observed = now - age;
        var objectId = Guid.NewGuid();
        var checksum = System.Security.Cryptography.SHA256.HashData(objectId.ToByteArray());

        db.ArtifactObject.Add(new ArtifactObject { Id = objectId, TeamId = teamId, Digest = checksum, SizeBytes = 12, CreatedDate = observed });

        var location = new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = teamId, ArtifactObjectId = objectId, StorageProfileRevisionId = revisionId,
            Locator = "local://fairness", ObjectKey = $"objects/{objectId:N}", State = state, VerifiedAt = observed,
            Revision = 1, CreatedDate = observed, LastModifiedDate = observed,
            ObservedSizeBytes = 12, ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = checksum,
        };
        db.ArtifactLocation.Add(location);
        db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = teamId, ArtifactLocationId = location.Id, Revision = location.Revision,
            EventType = ArtifactLocationEventType.Verified, State = location.State, ObservedAt = observed,
            ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
            ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt, DetailsJson = "{}",
        });

        await db.SaveChangesAsync();

        return location;
    }
}
