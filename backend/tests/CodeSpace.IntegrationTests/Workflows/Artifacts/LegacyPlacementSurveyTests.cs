using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Backends;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local.Legacy;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// The pre-CAS artifact tier, made countable without being made writable.
///
/// <para>These rows carry a <c>storage_url</c> and no <c>artifact_location</c>, so every monitoring component in the
/// plane is blind to them. The pass under test resolves and asks; phase two is what would mint rows, and it is gated
/// on these numbers — which is why the fixture stages BOTH ways a pass can come back empty. A destination that lost
/// its bytes and a layout that cannot name them look identical in a confirmation count and mean opposite things.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LegacyPlacementSurveyTests : IDisposable
{
    private const int LegacyBlobs = 100;

    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public LegacyPlacementSurveyTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_pass_tells_a_blob_the_destination_lost_from_the_ninety_nine_it_still_holds()
    {
        var world = await SeedLegacyWorldAsync();
        var removed = RemoveOneBlob(world);
        var before = await RowCountsAsync(world);

        var survey = await SurveyAsync(world, world.ProfileId);

        survey.Refusal.ShouldBe(LegacyPlacementSurveyRefusalValue.None);
        survey.Found.ShouldBe(LegacyBlobs, "the whole pre-CAS population, whatever one pass had budget to examine");
        survey.Surveyed.ShouldBe(LegacyBlobs);
        survey.Resolved.ShouldBe(LegacyBlobs, "every row's own recorded locator is reproduced by this layout — the key mapping is right");
        survey.Confirmed.ShouldBe(LegacyBlobs - 1);
        survey.Unconfirmed.ShouldBe(1, "one blob is gone from the disk, and that is a statement about bytes rather than about keys");
        survey.ConfirmedSizeBytes.ShouldBe(world.TotalSizeBytes - removed, "bytes as the destination reports them, never as the rows claim them");
        survey.AdoptionAdmissible.ShouldBeTrue();

        (await RowCountsAsync(world)).ShouldBe(before, "a report-only pass mints no placement and relinks no artifact row");
    }

    [Fact]
    public async Task A_provider_with_no_pre_cas_layout_resolves_nothing_and_names_that_as_the_reason()
    {
        // The same rows, asked of a destination whose layout is a different tier entirely. It must refuse to guess:
        // local-rwx interposes an "objects" segment of its own, so every key it derived would miss.
        var world = await SeedLegacyWorldAsync();

        var survey = await SurveyAsync(world, world.RwxProfileId);

        survey.Refusal.ShouldBe(LegacyPlacementSurveyRefusalValue.ProviderHasNoLegacyLayout);
        survey.Found.ShouldBe(LegacyBlobs, "the population is a property of the team, not of the profile it was asked about");
        survey.Surveyed.ShouldBe(0, "not one row is asked about through a layout that cannot name any of them");
        survey.Resolved.ShouldBe(0);
        survey.AdoptionAdmissible.ShouldBeFalse();
    }

    [Fact]
    public async Task A_root_the_deployment_no_longer_mounts_resolves_every_key_and_is_still_refused()
    {
        // The other way a pass comes back with nothing, and the one a resolution-only gate would wave through. Every
        // key resolves — the layout is right — and the destination answers for not one of them, which is exactly what
        // an unmounted or emptied root looks like from here. Admitting it would have phase two mint a Missing
        // placement for every legacy artifact in the deployment against a destination that is merely not mounted.
        var world = await SeedLegacyWorldAsync();
        Directory.Delete(world.RootPath, recursive: true);

        var survey = await SurveyAsync(world, world.ProfileId);

        survey.Refusal.ShouldBe(LegacyPlacementSurveyRefusalValue.None, "the destination opened; it is the bytes it holds that are the question");
        survey.Resolved.ShouldBe(LegacyBlobs, "resolution is path arithmetic against each row's own locator, so it survives a root that is not there");
        survey.Confirmed.ShouldBe(0);
        survey.Unconfirmed.ShouldBe(LegacyBlobs);
        survey.ConfirmedSizeBytes.ShouldBe(0);
        survey.AdoptionAdmissible.ShouldBeFalse("resolution proves the key mapping and confirmation proves the destination; one without the other is not evidence a minting pass may act on");
    }

    [Fact]
    public async Task A_layout_rooted_somewhere_else_resolves_nothing_and_reports_no_lost_bytes()
    {
        // The key-mapping bug — the failure phase two is actually gated on. Every row is examined, none resolves,
        // and crucially NONE is reported unconfirmed: an unresolved row is never HEADed, so a misconfigured root can
        // never be mistaken for a destination that lost a hundred blobs.
        var world = await SeedLegacyWorldAsync();

        var survey = await SurveyAsync(world, world.DisplacedProfileId);

        survey.Refusal.ShouldBe(LegacyPlacementSurveyRefusalValue.None, "the provider does have a pre-CAS layout; it is the root that is wrong");
        survey.Surveyed.ShouldBe(LegacyBlobs);
        survey.Resolved.ShouldBe(0);
        survey.Confirmed.ShouldBe(0);
        survey.Unconfirmed.ShouldBe(0, "a key this profile cannot name is never asked about, so it can never read as missing bytes");
        survey.ConfirmedSizeBytes.ShouldBe(0);
        survey.AdoptionAdmissible.ShouldBeFalse("a destination that resolves nothing is a key-mapping bug far more often than one that lost everything");
    }

    // ─── World ───────────────────────────────────────────────────────────────

    private async Task<LegacyPlacementSurvey> SurveyAsync(LegacyWorld world, Guid profileId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<ILegacyPlacementSurveyor>()
            .SurveyAsync(world.TeamId, profileId, LegacyPlacementSurveyLimits.MaxRowsPerPass, CancellationToken.None);
    }

    /// <summary>Scoped to this world's team: sibling classes write their own rows to both tables in the same database.</summary>
    private async Task<(int Locations, int Artifacts)> RowCountsAsync(LegacyWorld world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        return (await db.ArtifactLocation.AsNoTracking().CountAsync(row => row.TeamId == world.TeamId),
            await db.WorkflowArtifact.AsNoTracking().CountAsync(row => row.TeamId == world.TeamId));
    }

    /// <summary>Takes one blob off the disk without touching its row — a destination that lost an object, which is what the pass must be able to see.</summary>
    private static long RemoveOneBlob(LegacyWorld world)
    {
        var path = new Uri(world.StorageUrls[0]).LocalPath;
        var size = new FileInfo(path).Length;
        File.Delete(path);

        return size;
    }

    /// <summary>
    /// A team whose artifacts really are on disk in the pre-CAS layout, plus the three profiles the pass has to tell
    /// apart: one that names them, one whose provider cannot, and one whose root is somewhere else entirely.
    /// </summary>
    private async Task<LegacyWorld> SeedLegacyWorldAsync()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot("legacy");
        var elsewhere = NewRoot("elsewhere");
        var backend = new LocalFileArtifactBlobBackend(root);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var storageUrls = new List<string>(LegacyBlobs);
        var totalSizeBytes = 0L;

        foreach (var index in Enumerable.Range(0, LegacyBlobs))
        {
            var payload = Encoding.UTF8.GetBytes($"pre-CAS artifact {index} for team {teamId:N}");
            var sha = ArtifactStore.ComputeSha256Hex(payload);
            storageUrls.Add(await backend.WriteAsync(sha, payload, CancellationToken.None));
            totalSizeBytes += payload.Length;

            db.WorkflowArtifact.Add(new WorkflowArtifact
            {
                Id = Guid.NewGuid(), TeamId = teamId, Sha256 = sha, ContentType = "text/plain",
                SizeBytes = payload.Length, StorageUrl = storageUrls[index], CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();

        var profileId = await AddProfileAsync(db, teamId, actorId, LocalLegacyArtifactStorageDriverFactory.TypeKey, Path.GetFullPath(root));
        // Its own root, never the pre-CAS one: this profile is refused before a driver is ever opened, so the path it
        // names is irrelevant to what the test proves — and a read-write tier pointed at the legacy blobs is the one
        // construct this work may not build even inertly.
        var rwxProfileId = await AddProfileAsync(db, teamId, actorId, LocalRwxArtifactStorageDriverFactory.TypeKey, Path.GetFullPath(NewRoot("rwx")));
        var displacedProfileId = await AddProfileAsync(db, teamId, actorId, LocalLegacyArtifactStorageDriverFactory.TypeKey, Path.GetFullPath(elsewhere));

        return new LegacyWorld(teamId, profileId, rwxProfileId, displacedProfileId, Path.GetFullPath(root), storageUrls, totalSizeBytes);
    }

    private static async Task<Guid> AddProfileAsync(CodeSpaceDbContext db, Guid teamId, Guid actorId, string providerTypeKey, string rootPath)
    {
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var config = JsonSerializer.Serialize(new { rootPath });
        using var document = JsonDocument.Parse(config);

        db.StorageProfile.Add(new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"legacy-{profileId:N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
            Revisions =
            {
                new StorageProfileRevision
                {
                    Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
                    ProviderTypeKey = providerTypeKey, NonSecretConfigJson = config, CredentialRef = null,
                    NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(providerTypeKey, document.RootElement),
                    CreatedDate = now, CreatedBy = actorId,
                },
            },
        });
        await db.SaveChangesAsync();

        return profileId;
    }

    private string NewRoot(string label)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);

        return root;
    }

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed record LegacyWorld(Guid TeamId, Guid ProfileId, Guid RwxProfileId, Guid DisplacedProfileId, string RootPath, IReadOnlyList<string> StorageUrls, long TotalSizeBytes);
}
