using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Commands.Storage;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The routed sharing guard as the PREVIOUS build asks it, over revisions THIS build admits — the mixed fleet a rolling
/// deploy or a rollback runs. That build decided "one object" by comparing the namespace fingerprints revisions STORE,
/// so the bytes it keeps depend on what this build writes into that column.
///
/// <para><b>Fidelity: medium-mirror.</b> The previous build's question is reproduced below verbatim from its source
/// (583efb0df <c>ArtifactRetentionReaper.RoutedPurgePathAsync</c>) and run against the real database; admission and
/// this build's reaper are the production classes. The mirrored source is a released build that can no longer change,
/// so there is nothing for a drift detector to compare it with: what can drift is this build's writes, which is what
/// these tests pin.</para>
/// </summary>
public sealed partial class ArtifactRetentionReaperFlowTests
{
    [Fact]
    public async Task A_root_admitted_now_and_typed_as_the_previous_build_admitted_it_is_one_object_to_both_reapers()
    {
        // A victim's destination admitted by the previous build stored the hash of 'R/' as typed. A second team types
        // the same 'R/' and THIS build admits it. If this build stored the physical identity, the previous build's
        // reaper — on a worker not yet replaced, or back after a rollback — would see two namespaces and collect the
        // second team's object, deleting the victim's bytes with it.
        var victim = await SeedWorldAsync();
        var collector = await SeedWorldAsync();
        var root = NamespaceRoot();
        var typed = RootConfig(root + "/");
        var bytes = RoutedBytes("one payload under one root typed alike by two builds");
        var file = PlaceObject(root, bytes);

        var victimPlacement = await SeedPlacementAsync(victim, bytes, LocalRwxArtifactStorageDriverFactory.TypeKey, typed);
        var collectorPlacement = await SeedPlacementUnderAsync(collector, bytes, await AdmitAsync(collector, typed));
        await DeclarePlacementAsync(collector, collectorPlacement.ArtifactId);

        (await StoredFingerprintAsync(collectorPlacement)).ShouldNotBe(PhysicalFingerprint(typed),
            "the precondition that makes this a regression test: the physical identity of 'R/' differs from the hash the previous build stored for it");
        (await PreviousBuildFindsSharedAsync(collectorPlacement.ArtifactId)).ShouldBeTrue(
            "the previous build's reaper compares stored fingerprints: a revision admitted now must store what that build stored for the same text, or that reaper collects the victim's object");

        await SweepToSettlementAsync(collectorPlacement.ArtifactId);

        var declaration = (await DeclarationAsync(collectorPlacement.ArtifactId)).ShouldNotBeNull("this build's reaper collected an object two teams reach");
        declaration.State.ShouldBe(ArtifactRetentionState.Indeterminate);
        declaration.LastErrorCode.ShouldBe("artifact-routed-object-shared", "this build's reaper still derives the object at sweep time and still keeps it");
        File.Exists(file).ShouldBeTrue($"the victim's object at {file} is gone; check `ls {Path.GetDirectoryName(file)}`");
        (await LocationStateAsync(victimPlacement.LocationId)).ShouldBe(ArtifactLocationState.Available);
    }

    [Fact]
    public async Task The_previous_builds_question_still_tells_physically_distinct_roots_apart()
    {
        // The positive control for the mirror: content addressing gives both teams the same key, so a question that
        // answered "shared" for distinct directories would make the test above pass for the wrong reason.
        var victim = await SeedWorldAsync();
        var collector = await SeedWorldAsync();
        var bytes = RoutedBytes("one payload in two directories, both admitted now");

        await SeedPlacementUnderAsync(victim, bytes, await AdmitAsync(victim, RootConfig(NamespaceRoot())));
        var collectorPlacement = await SeedPlacementUnderAsync(collector, bytes, await AdmitAsync(collector, RootConfig(NamespaceRoot() + "/")));

        (await PreviousBuildFindsSharedAsync(collectorPlacement.ArtifactId)).ShouldBeFalse();
    }

    // ─── Previous-build helpers ──────────────────────────────────────────────

    /// <summary>A destination admitted by THIS build, through the production admission path a Settings save takes.</summary>
    private async Task<Guid> AdmitAsync(World world, string config)
    {
        using var scope = _fixture.BeginScope();
        using var document = JsonDocument.Parse(config);

        var admitted = await scope.Resolve<IStorageProfileService>().CreateAsync(world.TeamId, world.ActorId, new CreateStorageProfileCommand
        {
            StableName = $"admitted-now-{Guid.NewGuid():N}", ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfig = document.RootElement,
        }, CancellationToken.None);

        return admitted.Revisions.ShouldHaveSingleItem().Id;
    }

    private static string PhysicalFingerprint(string config)
    {
        using var document = JsonDocument.Parse(config);
        return StorageProfileRules.NamespaceFingerprint(new LocalRwxStorageProviderModule(), document.RootElement);
    }

    /// <summary>
    /// The previous build's routed sharing question, verbatim: does another object's placement sit at one of this
    /// object's keys under a revision with the same STORED namespace fingerprint? True is that build's
    /// <c>RoutedObjectShared</c> (keep), false its <c>Routed</c> (collect).
    /// </summary>
    private async Task<bool> PreviousBuildFindsSharedAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var objectId = await db.WorkflowArtifact.AsNoTracking().Where(artifact => artifact.Id == artifactId).Select(artifact => artifact.CasArtifactObjectId!.Value).SingleAsync();

        var mine = await db.ArtifactLocation.AsNoTracking()
            .Where(location => location.ArtifactObjectId == objectId)
            .Join(db.StorageProfileRevision.AsNoTracking(), location => location.StorageProfileRevisionId, revision => revision.Id,
                (location, revision) => new PreviousBuildPlacedAt(location.ObjectKey, revision.NamespaceFingerprint))
            .Distinct()
            .ToListAsync();

        var keys = mine.Select(placed => placed.ObjectKey).Distinct().ToList();

        var collisions = await db.ArtifactLocation.AsNoTracking()
            .Where(location => location.ArtifactObjectId != objectId && keys.Contains(location.ObjectKey))
            .Join(db.StorageProfileRevision.AsNoTracking(), location => location.StorageProfileRevisionId, revision => revision.Id,
                (location, revision) => new PreviousBuildPlacedAt(location.ObjectKey, revision.NamespaceFingerprint))
            .Distinct()
            .ToListAsync();

        return collisions.Any(mine.Contains);
    }

    private sealed record PreviousBuildPlacedAt(string ObjectKey, string NamespaceFingerprint);
}
