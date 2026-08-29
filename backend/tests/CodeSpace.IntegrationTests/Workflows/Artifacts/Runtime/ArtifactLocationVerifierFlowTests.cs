using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Runtime;

/// <summary>
/// The verifier that makes Missing and Corrupt real states rather than schema documentation.
///
/// <para>Demotion is the dangerous direction — a location moved off Available stops being readable — so most of what
/// these tests pin is what the verifier REFUSES to do.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactLocationVerifierFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public ArtifactLocationVerifierFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_object_that_is_still_there_is_confirmed_and_its_verified_at_moves_forward()
    {
        // verified_at was frozen at the write instant with an ORDER BY as its only consumer. Moving it is what makes
        // "when was this last known good" a real answer instead of "when was it written".
        var world = await SeedStoredArtifactAsync();
        var before = (await LocationAsync(world)).VerifiedAt.ShouldNotBeNull();

        var summary = await VerifyAsync();

        summary.Checked.ShouldBeGreaterThanOrEqualTo(1);
        var location = await LocationAsync(world);
        location.State.ShouldBe(ArtifactLocationState.Available);
        location.VerifiedAt.ShouldNotBeNull().ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task An_object_the_destination_no_longer_holds_is_marked_missing_and_stops_being_served()
    {
        // Before this, those bytes stayed Available forever and the loss surfaced only when a person opened the
        // artifact. The read must now fail the same way — but knowably, ahead of the person.
        var world = await SeedStoredArtifactAsync();
        DeleteStoredObject(world);

        await VerifyAsync();

        var location = await LocationAsync(world);
        location.State.ShouldBe(ArtifactLocationState.Missing);
        location.LastErrorCode.ShouldBe("location-object-missing");
        location.LastErrorMessage.ShouldNotBeNullOrWhiteSpace("a demoted location must say what the destination answered");

        using var scope = _fixture.BeginScope();
        var failure = await Should.ThrowAsync<ArtifactContentUnavailableException>(
            () => scope.Resolve<IArtifactStore>().GetBytesAsync(world.TeamId, world.ArtifactId, CancellationToken.None));
        failure.Kind.ShouldBe(ArtifactContentUnavailableKind.PhysicalObjectMissing, "a Missing location must leave the read path, not silently keep serving");
    }

    [Fact]
    public async Task An_object_replaced_by_something_else_is_marked_corrupt_rather_than_served()
    {
        // The key still resolves, so a reachability check would call this healthy. Whatever is there is not the
        // artifact, and serving it would hand a caller bytes that do not match what was recorded.
        var world = await SeedStoredArtifactAsync();
        OverwriteStoredObject(world, "a different object entirely");

        await VerifyAsync();

        var location = await LocationAsync(world);
        location.State.ShouldBe(ArtifactLocationState.Corrupt);
        location.LastErrorCode.ShouldBe("location-object-mismatch");
    }

    [Fact]
    public async Task A_destination_that_cannot_answer_leaves_the_location_exactly_as_it_was()
    {
        // The property that makes this safe to run unattended. An outage, a throttle or a revoked key says something
        // about the REQUEST, not about the object — demoting on any of them would turn a transient blip into readable
        // bytes becoming unreadable, which is worse than the silence this replaces.
        var world = await SeedStoredArtifactAsync();
        var before = await LocationAsync(world);

        BreakDestination(world);

        await VerifyAsync();

        var after = await LocationAsync(world);
        after.State.ShouldBe(ArtifactLocationState.Available, "an unreachable destination is not evidence the object is gone");
        after.VerifiedAt.ShouldBe(before.VerifiedAt, "and its verified_at must NOT move, so the row stays visibly stale rather than looking freshly checked");
        after.Revision.ShouldBe(before.Revision);
    }

    [Fact]
    public async Task A_location_wrongly_demoted_by_a_destination_fault_comes_back_when_the_destination_answers_again()
    {
        // The safety valve that makes automated demotion acceptable at all. A verifier that only ever moves rows OUT of
        // Available is a one-way door: one bad pass over a flapping destination would permanently mark bytes that are
        // sitting right there as unreadable, with nothing that could ever re-examine them.
        var world = await SeedStoredArtifactAsync();
        var path = ObjectPath(world);
        var content = await File.ReadAllBytesAsync(path);
        var stashed = path + ".stashed";

        // Moved rather than rewritten, so the object comes back byte-identical AND metadata-identical — the shape of a
        // destination that was briefly serving an incomplete view of itself, not of someone editing the bytes.
        File.Move(path, stashed);
        await VerifyAsync();
        (await LocationAsync(world)).State.ShouldBe(ArtifactLocationState.Missing);

        File.Move(stashed, path);

        var location = await VerifyUntilAsync(world, ArtifactLocationState.Available);

        location.State.ShouldBe(ArtifactLocationState.Available, "the recorded object is present and matches, which is the same evidence its placement was accepted on");
        location.LastErrorCode.ShouldBeNull("a restored location must not keep advertising the error it no longer has");

        using var scope = _fixture.BeginScope();
        var bytes = await scope.Resolve<IArtifactStore>().GetBytesAsync(world.TeamId, world.ArtifactId, CancellationToken.None);
        bytes.Bytes.ShouldBe(content, "and it must be readable again, not merely marked healthy");
    }

    [Fact]
    public async Task A_corrupt_location_is_not_swept_back_into_circulation()
    {
        // Corrupt takes a positive disagreement to reach — an outage cannot fabricate a wrong-sized object — so unlike
        // Missing it carries no risk of being a false demotion, and re-reading it would only invite flapping.
        var world = await SeedStoredArtifactAsync();
        OverwriteStoredObject(world, "a different object entirely");
        await VerifyAsync();
        (await LocationAsync(world)).State.ShouldBe(ArtifactLocationState.Corrupt);
        var demoted = await LocationAsync(world);

        await VerifyAsync();

        var after = await LocationAsync(world);
        after.State.ShouldBe(ArtifactLocationState.Corrupt);
        after.Revision.ShouldBe(demoted.Revision, "a Corrupt row must not even be re-examined, let alone rewritten");
    }

    [Fact]
    public async Task A_destination_whose_whole_root_has_vanished_demotes_nothing()
    {
        // The scenario the liveness corroboration exists for, and the one it did not actually cover: an unmounted
        // volume, a detached disk, a deleted directory. Every object under it reads as absent, so without a truthful
        // answer about the DESTINATION every placement a team owns is demoted in a single pass — and a demotion of
        // bytes that are merely unreachable is far worse than the silence it replaced.
        //
        // The existing sibling test replaces the root with a FILE, which throws and is therefore easy to answer
        // honestly. A root that is simply GONE is the case a probe can silently paper over by creating it.
        var world = await SeedStoredArtifactAsync();
        var before = await LocationAsync(world);

        Directory.Delete(world.Root, recursive: true);

        await VerifyAsync();

        var after = await LocationAsync(world);
        after.State.ShouldBe(ArtifactLocationState.Available, "a destination that is not there cannot testify that an object was deleted");
        after.VerifiedAt.ShouldBe(before.VerifiedAt, "and the row must stay visibly unchecked rather than looking freshly confirmed");
        Directory.Exists(world.Root).ShouldBeFalse("checking a destination must never be what creates it");
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Sweeps until THIS test's location reaches <paramref name="state"/>.
    ///
    /// <para>The sweep is deployment-wide and bounded, and the recovery half of its batch is deliberately the smaller
    /// half, so a Missing row left behind by any earlier test competes for the same slots. Asserting after one pass
    /// would be asserting that this row won a race it has no reason to win — and would get harder to satisfy as the
    /// suite grows.</para>
    /// </summary>
    private async Task<ArtifactLocation> VerifyUntilAsync(StoredArtifact world, ArtifactLocationState state, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        ArtifactLocation? seen = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await VerifyAsync();
            seen = await LocationAsync(world);

            if (seen.State == state) return seen;
        }

        throw new Xunit.Sdk.XunitException(
            $"The location for artifact {world.ArtifactId} never reached {state} (last seen {seen?.State.ToString() ?? "absent"}, "
            + $"last error {seen?.LastErrorCode ?? "none"}). The sweep is deployment-wide and its recovery share is bounded, "
            + "so check whether earlier tests left more Missing rows than that share holds.");
    }

    private async Task<ArtifactLocationVerificationSummary> VerifyAsync()
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(100, CancellationToken.None);
    }

    private async Task<ArtifactLocation> LocationAsync(StoredArtifact world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .SingleAsync(location => location.TeamId == world.TeamId);
    }

    private static string ObjectPath(StoredArtifact world) => Directory.GetFiles(world.Root, "*", SearchOption.AllDirectories).Single();

    private static void DeleteStoredObject(StoredArtifact world) => File.Delete(ObjectPath(world));

    private static void OverwriteStoredObject(StoredArtifact world, string content) => File.WriteAllText(ObjectPath(world), content);

    /// <summary>Replaces the destination root with a FILE, so the driver cannot open it at all — a transport fault, not an answer about the object.</summary>
    private static void BreakDestination(StoredArtifact world)
    {
        Directory.Delete(world.Root, recursive: true);
        File.WriteAllText(world.Root, "not a directory");
    }

    private async Task<StoredArtifact> SeedStoredArtifactAsync()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var destination = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);
        var artifactId = await RoutedArtifactSeed.WriteRoutedAsync(_fixture, teamId, "verifiable bytes", "application/octet-stream");

        _roots.Add(destination.Root);
        destination.ObjectCount.ShouldBe(1, "the object must physically exist for a verification test to mean anything");

        return new StoredArtifact(teamId, artifactId, destination.Root);
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                else if (File.Exists(root)) File.Delete(root);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record StoredArtifact(Guid TeamId, Guid ArtifactId, string Root);
}
