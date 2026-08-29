using Autofac;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// Whether a routed artifact stays readable after its bytes are moved around underneath it.
///
/// <para>Restoring a destination from backup, migrating it with rsync, or copying it to a bigger volume all preserve
/// the bytes and change the filesystem metadata around them. None of those is a corruption, and none of them may cost
/// a team its diffs and transcripts.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RoutedReadSurvivesMetadataChurnTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public RoutedReadSurvivesMetadataChurnTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_artifact_restored_with_its_bytes_intact_is_still_readable()
    {
        // A restore rewrites the file, so its modification time is new while every byte is the one that was written.
        // Reading it back is the whole point of having had a backup.
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var destination = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);
        _roots.Add(destination.Root);

        var artifactId = await RoutedArtifactSeed.WriteRoutedAsync(_fixture, teamId, "work worth keeping", "text/plain");
        var path = Directory.GetFiles(destination.Root, "*", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(path);

        await RestoreFromBackupAsync(path, bytes);

        using var scope = _fixture.BeginScope();
        var read = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None);
        read.Bytes.ShouldBe(bytes, "the bytes are byte-for-byte the ones that were written; nothing about restoring them makes the artifact a different artifact");
    }

    /// <summary>Writes the same bytes back at the same key, the way any restore or migration would — new metadata, identical content.</summary>
    private static async Task RestoreFromBackupAsync(string path, byte[] bytes)
    {
        File.Delete(path);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(30));
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
