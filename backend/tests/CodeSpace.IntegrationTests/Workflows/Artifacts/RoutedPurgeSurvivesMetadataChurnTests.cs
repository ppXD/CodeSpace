using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// The other half of trusting a volatile ETag: the same recorded value gates deletion, so an object that churned its
/// metadata could be neither read nor removed — retention would keep failing on it forever, and an operator asked to
/// delete a team's data could not honestly say it was gone.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RoutedPurgeSurvivesMetadataChurnTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public RoutedPurgeSurvivesMetadataChurnTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_artifact_whose_bytes_were_rewritten_in_place_can_still_be_purged()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var destination = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);
        _roots.Add(destination.Root);

        await RoutedArtifactSeed.WriteRoutedAsync(_fixture, teamId, "work that must be deletable", "text/plain");
        var path = Directory.GetFiles(destination.Root, "*", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(path);

        File.Delete(path);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(30));

        await PurgeAsync(teamId, actorId);

        File.Exists(path).ShouldBeFalse("the bytes must actually leave the destination, not merely be marked purged in the database");
    }

    private async Task PurgeAsync(Guid teamId, Guid actorId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(value => value.TeamId == teamId);

        var result = await scope.Resolve<IArtifactCasPurgeCoordinator>().PurgeAsync(new ArtifactCasPurgeRequest
        {
            TeamId = teamId, ArtifactObjectId = location.ArtifactObjectId, ActorId = actorId,
        }, CancellationToken.None);

        result.ShouldBeOfType<ArtifactCasPurgeResult.Purged>();
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
