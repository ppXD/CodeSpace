using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Messages.Artifacts;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The routed sharing guard against REAL namespaces: two teams, real directories, a real file at the shared key, and
/// the real local-rwx driver the purge would delete it through.
///
/// <para>Every revision here carries the fingerprint pre-canonical admission stored — the hash of the configuration
/// exactly as typed — because that is what every revision already in a database carries, and revisions are
/// immutable. So a guard that read the stored column would see two spellings of one root as two namespaces, and these
/// tests fail; only a guard that re-derives the physical namespace from the configuration at sweep time passes.</para>
///
/// <para>"Spelling" includes links the collector plants beneath a root it owns outright: the driver opens
/// <c>&lt;root&gt;/objects/&lt;key&gt;</c> and the operating system follows a link anywhere along that path, so only a
/// guard that resolves the object a key reaches — not merely the root a configuration names — sees those as shared.</para>
/// </summary>
public sealed partial class ArtifactRetentionReaperFlowTests : IDisposable
{
    private readonly List<string> _namespaceRoots = [];

    [Theory]
    [InlineData("as-written")]
    [InlineData("trailing-slash")]
    [InlineData("dot-segment")]
    [InlineData("doubled-slash")]
    [InlineData("relative")]
    [InlineData("symlink")]
    [InlineData("dotdot-through-link")]
    [InlineData("objects-link")]
    [InlineData("nested-link")]
    public async Task A_routed_object_another_team_reaches_through_another_spelling_of_one_root_is_kept(string spelling)
    {
        if (spelling.Contains("link") && OperatingSystem.IsWindows()) return;

        var victim = await SeedWorldAsync();
        var collector = await SeedWorldAsync();
        var root = NamespaceRoot();
        var bytes = RoutedBytes("one payload two teams both stored");
        var file = PlaceObject(root, bytes);
        var spelled = Spell(root, spelling);

        File.Exists(Path.Combine([spelled, "objects", .. ObjectKeyOf(bytes).Split('/')])).ShouldBeTrue($"the precondition: the operating system opens {file} through '{spelled}'");

        var victimPlacement = await SeedPlacementAsync(victim, bytes, LocalRwxArtifactStorageDriverFactory.TypeKey, RootConfig(root));
        var collectorPlacement = await SeedPlacementAsync(collector, bytes, LocalRwxArtifactStorageDriverFactory.TypeKey, RootConfig(spelled));
        await DeclarePlacementAsync(collector, collectorPlacement.ArtifactId);

        (await StoredFingerprintAsync(victimPlacement) == await StoredFingerprintAsync(collectorPlacement)).ShouldBe(spelling == "as-written",
            "the precondition that makes this a regression test: the stored fingerprints disagree for every spelling but the literal one");

        await SweepToSettlementAsync(collectorPlacement.ArtifactId);

        var declaration = (await DeclarationAsync(collectorPlacement.ArtifactId)).ShouldNotBeNull(
            $"the artifact was COLLECTED: '{spelled}' reaches the file '{root}' holds, so its delete removed the victim's bytes");
        declaration.State.ShouldBe(ArtifactRetentionState.Indeterminate);
        declaration.LastErrorCode.ShouldBe("artifact-routed-object-shared", "the kept row must say WHY, not merely survive");
        File.Exists(file).ShouldBeTrue($"the victim's object at {file} is the collector's object too; check `ls {Path.GetDirectoryName(file)}`");
        (await File.ReadAllBytesAsync(file)).ShouldBe(bytes);
        (await LocationStateAsync(victimPlacement.LocationId)).ShouldBe(ArtifactLocationState.Available);
    }

    [Fact]
    public async Task A_routed_object_whose_key_another_team_holds_in_a_physically_distinct_root_is_still_collected()
    {
        // The positive control for the theory above: content addressing gives every team storing these bytes the same
        // key, so if distinct directories also counted as shared, deduplicated content would never be collected.
        var victim = await SeedWorldAsync();
        var collector = await SeedWorldAsync();
        var victimRoot = NamespaceRoot();
        var collectorRoot = NamespaceRoot();
        var bytes = RoutedBytes("one payload in two separate directories");
        var victimFile = PlaceObject(victimRoot, bytes);
        var collectorFile = PlaceObject(collectorRoot, bytes);

        var victimPlacement = await SeedPlacementAsync(victim, bytes, LocalRwxArtifactStorageDriverFactory.TypeKey, RootConfig(victimRoot));
        var collectorPlacement = await SeedPlacementAsync(collector, bytes, LocalRwxArtifactStorageDriverFactory.TypeKey, RootConfig(collectorRoot + "/"));
        await DeclarePlacementAsync(collector, collectorPlacement.ArtifactId);

        await SweepToSettlementAsync(collectorPlacement.ArtifactId);

        (await ArtifactExistsAsync(collectorPlacement.ArtifactId)).ShouldBeFalse("the collector's directory is its own, so nothing else reaches these bytes");
        File.Exists(collectorFile).ShouldBeFalse("collection removes the bytes the collected row named");
        File.Exists(victimFile).ShouldBeTrue("a different directory holding the same key is a different object");
        (await LocationStateAsync(victimPlacement.LocationId)).ShouldBe(ArtifactLocationState.Available);
    }

    [Fact]
    public async Task A_routed_object_whose_key_is_a_link_to_another_teams_file_is_collected_and_only_the_link_goes()
    {
        // The other edge of following links: a delete unlinks the NAME it is given, so a key that is itself a link
        // removes the link and never the file it points at. Counting it as shared would only stall the sweep; proving
        // the unlink here is what makes leaving the last component unresolved safe.
        if (OperatingSystem.IsWindows()) return;

        var victim = await SeedWorldAsync();
        var collector = await SeedWorldAsync();
        var victimRoot = NamespaceRoot();
        var collectorRoot = NamespaceRoot();
        var bytes = RoutedBytes("one payload one team links to");
        var victimFile = PlaceObject(victimRoot, bytes);
        var link = Path.Combine([collectorRoot, "objects", .. ObjectKeyOf(bytes).Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, victimFile);

        var victimPlacement = await SeedPlacementAsync(victim, bytes, LocalRwxArtifactStorageDriverFactory.TypeKey, RootConfig(victimRoot));
        var collectorPlacement = await SeedPlacementAsync(collector, bytes, LocalRwxArtifactStorageDriverFactory.TypeKey, RootConfig(collectorRoot));
        await DeclarePlacementAsync(collector, collectorPlacement.ArtifactId);

        await SweepToSettlementAsync(collectorPlacement.ArtifactId);

        (await ArtifactExistsAsync(collectorPlacement.ArtifactId)).ShouldBeFalse("the collector's key is a name of its own, so its object is collected");
        new FileInfo(link).LinkTarget.ShouldBeNull("collection removes the link the collected row named");
        File.Exists(victimFile).ShouldBeTrue($"the unlink must not reach through to {victimFile}");
        (await File.ReadAllBytesAsync(victimFile)).ShouldBe(bytes);
        (await LocationStateAsync(victimPlacement.LocationId)).ShouldBe(ArtifactLocationState.Available);
    }

    [Theory]
    [InlineData("retired-provider/v1", """{"rootPath":"<root>"}""")]
    [InlineData("local-rwx/v1", """{"unexpected":"<root>"}""")]
    [InlineData("local-rwx/v1", """{"rootPath":42}""")]
    public async Task A_key_collision_with_a_revision_whose_namespace_cannot_be_derived_is_kept(string providerTypeKey, string neighbourConfig)
    {
        // A provider this build no longer registers, or a configuration it can no longer read, names SOME namespace —
        // this build just cannot say which. Reading that as "a different one" would delete on a guess.
        var neighbour = await SeedWorldAsync();
        var collector = await SeedWorldAsync();
        var root = NamespaceRoot();
        var bytes = RoutedBytes($"one payload beside an unreadable revision {providerTypeKey} {neighbourConfig}");
        var file = PlaceObject(root, bytes);

        await SeedPlacementAsync(neighbour, bytes, providerTypeKey, neighbourConfig.Replace("<root>", JsonEncodedText.Encode(root).ToString()));
        var collectorPlacement = await SeedPlacementAsync(collector, bytes, LocalRwxArtifactStorageDriverFactory.TypeKey, RootConfig(root));
        await DeclarePlacementAsync(collector, collectorPlacement.ArtifactId);

        await SweepToSettlementAsync(collectorPlacement.ArtifactId);

        var declaration = (await DeclarationAsync(collectorPlacement.ArtifactId)).ShouldNotBeNull("an unknown namespace was read as a different one and the object was collected");
        declaration.State.ShouldBe(ArtifactRetentionState.Indeterminate);
        declaration.LastErrorCode.ShouldBe("artifact-routed-object-shared");
        File.Exists(file).ShouldBeTrue();
    }

    // ─── Namespace helpers ───────────────────────────────────────────────────

    private sealed record SeededPlacement(Guid ArtifactId, Guid LocationId, Guid RevisionId);

    /// <summary>Ages the declaration past both waits and sweeps twice: the first sweep quarantines, the second decides.</summary>
    private async Task SweepToSettlementAsync(Guid artifactId)
    {
        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));
        await SweepAsync();
        await AgeQuarantineAsync(artifactId, TimeSpan.FromDays(2));
        await SweepAsync();
    }

    /// <summary>A real directory of this test's own, removed on dispose.</summary>
    private string NamespaceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codespace-namespace-reaper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _namespaceRoots.Add(root);
        return root;
    }

    /// <summary>The spelling under test, each of which the local-rwx driver opens as <paramref name="root"/> itself.</summary>
    private string Spell(string root, string spelling) => spelling switch
    {
        "as-written" => root,
        "trailing-slash" => root + "/",
        "dot-segment" => root + "/.",
        "doubled-slash" => root + "//",
        "relative" => Path.GetRelativePath(Environment.CurrentDirectory, root),
        "symlink" => LinkTo(root),
        "dotdot-through-link" => DotDotThroughLink(root),
        "objects-link" => LinkBeneath(root, "objects"),
        "nested-link" => LinkBeneath(root, Path.Combine("objects", "workflow-artifacts")),
        _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, null),
    };

    /// <summary>
    /// <c>holder/L -&gt; a/..</c> with <c>holder/a -&gt; root/objects</c>: the operating system follows <c>a</c> before
    /// it climbs, so <c>holder/L</c> is <c>root</c> — while collapsing the target as text would call it <c>holder</c>.
    /// </summary>
    private string DotDotThroughLink(string root)
    {
        var holder = NamespaceRoot();
        Directory.CreateSymbolicLink(Path.Combine(holder, "a"), Path.Combine(root, "objects"));
        Directory.CreateSymbolicLink(Path.Combine(holder, "L"), Path.Combine("a", ".."));
        return Path.Combine(holder, "L");
    }

    /// <summary>A root of the collector's own whose <paramref name="beneath"/> is a link into <paramref name="root"/>: the root itself names nothing shared.</summary>
    private string LinkBeneath(string root, string beneath)
    {
        var collectorRoot = NamespaceRoot();
        var link = Path.Combine(collectorRoot, beneath);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, Path.Combine(root, beneath));
        return collectorRoot;
    }

    private string LinkTo(string root)
    {
        var link = Path.Combine(Path.GetTempPath(), "codespace-namespace-reaper", $"link-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(link, root);
        _namespaceRoots.Add(link);
        return link;
    }

    /// <summary>Content unique to the call, past nothing in particular: a routed row's placement is what is under test, not its size.</summary>
    private static byte[] RoutedBytes(string content) => System.Text.Encoding.UTF8.GetBytes($"{content} {Guid.NewGuid():N}");

    /// <summary>The key production gives these bytes (<c>ArtifactStore.ObjectKeyFor</c>): content-addressed, with no team segment.</summary>
    private static string ObjectKeyOf(byte[] bytes)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return $"workflow-artifacts/{sha[..2]}/{sha.Substring(2, 2)}/{sha}";
    }

    /// <summary>Writes the object where the local-rwx driver keeps it: <c>&lt;root&gt;/objects/&lt;key&gt;</c>.</summary>
    private static string PlaceObject(string root, byte[] bytes)
    {
        var path = Path.Combine([root, "objects", .. ObjectKeyOf(bytes).Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string RootConfig(string rootPath) => JsonSerializer.Serialize(new { rootPath });

    /// <summary>
    /// What admission stored before namespaces were canonical: the hash of the configuration as typed. Every revision
    /// already in a database carries this, so it is the fixture the sweep has to be right about.
    /// </summary>
    private static string StoredBeforeCanonicalAdmission(string providerTypeKey, string config)
    {
        using var document = JsonDocument.Parse(config);
        return StorageProfileRules.NamespaceFingerprint(providerTypeKey, document.RootElement);
    }

    /// <summary>
    /// One team's routed artifact: an Active profile revision, the team's own <c>artifact_object</c> for the digest, the
    /// pointing row, and an Available location at the production key. Each team owns its own object row for the same
    /// bytes, exactly as two independent routed writes produce.
    /// </summary>
    private async Task<SeededPlacement> SeedPlacementAsync(World world, byte[] bytes, string providerTypeKey, string config) =>
        await SeedPlacementUnderAsync(world, bytes, await SeedRevisionAdmittedBeforeCanonicalAsync(world, providerTypeKey, config));

    /// <summary>An Active profile whose one revision carries what admission stored before namespaces were canonical.</summary>
    private async Task<Guid> SeedRevisionAdmittedBeforeCanonicalAsync(World world, string providerTypeKey, string config)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();

        db.StorageProfile.Add(new StorageProfile
        {
            Id = profileId, TeamId = world.TeamId, StableName = $"namespace-probe-{profileId:N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
            Revisions =
            {
                new StorageProfileRevision
                {
                    Id = revisionId, TeamId = world.TeamId, StorageProfileId = profileId, Revision = 1, ProviderTypeKey = providerTypeKey,
                    NonSecretConfigJson = config, CredentialRef = null, NamespaceFingerprint = StoredBeforeCanonicalAdmission(providerTypeKey, config),
                    CreatedDate = now, CreatedBy = world.ActorId,
                },
            },
        });
        await db.SaveChangesAsync();

        return revisionId;
    }

    /// <summary>
    /// One team's routed artifact under an existing revision of its own: the team's own <c>artifact_object</c> for the
    /// digest, the pointing row, and an Available location at the production key.
    /// </summary>
    private async Task<SeededPlacement> SeedPlacementUnderAsync(World world, byte[] bytes, Guid revisionId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var digest = SHA256.HashData(bytes);
        var objectId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var key = ObjectKeyOf(bytes);

        db.ArtifactObject.Add(new ArtifactObject
        {
            Id = objectId, TeamId = world.TeamId, DigestAlgorithm = ArtifactDigestAlgorithm.Sha256, Digest = digest,
            SizeBytes = bytes.Length, CreatedDate = now, CreatedBy = world.ActorId,
        });
        db.WorkflowArtifact.Add(new WorkflowArtifact
        {
            Id = artifactId, TeamId = world.TeamId, Sha256 = Convert.ToHexStringLower(digest), ContentType = "application/octet-stream",
            SizeBytes = bytes.Length, InlineBytes = null, StorageUrl = null, CasArtifactObjectId = objectId, CreatedAt = now,
        });
        await db.SaveChangesAsync();

        // The location and its event go in ONE SaveChanges: every location revision needs its byte-identical event.
        db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactLocationId = locationId, Revision = 1,
            EventType = ArtifactLocationEventType.Created, State = ArtifactLocationState.Available, ObservedAt = now,
            ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = digest, ObservedSizeBytes = bytes.Length, VerifiedAt = now, CreatedBy = world.ActorId,
        });
        db.ArtifactLocation.Add(new ArtifactLocation
        {
            Id = locationId, TeamId = world.TeamId, ArtifactObjectId = objectId, StorageProfileRevisionId = revisionId,
            Locator = key, ObjectKey = key, State = ArtifactLocationState.Available, Revision = 1,
            ObservedSizeBytes = bytes.Length, ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = digest, VerifiedAt = now,
            CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        });
        await db.SaveChangesAsync();

        return new SeededPlacement(artifactId, locationId, revisionId);
    }

    /// <summary>The declaration <c>IArtifactRetentionWriter</c> mints on a first routed write, planted beside a seeded row.</summary>
    private async Task DeclarePlacementAsync(World world, Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.WorkflowArtifactRetention.Add(new WorkflowArtifactRetention
        {
            ArtifactId = artifactId, TeamId = world.TeamId, RetentionClass = nameof(ArtifactRetentionClass.ArtifactManifestContent),
            HolderKind = "artifact_manifest", HolderId = world.AgentRunId, State = ArtifactRetentionState.Declared,
            DeclaredAt = now, NextSweepAt = now, Revision = 1, LastModifiedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> StoredFingerprintAsync(SeededPlacement placement)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().StorageProfileRevision.AsNoTracking()
            .Where(revision => revision.Id == placement.RevisionId).Select(revision => revision.NamespaceFingerprint).SingleAsync();
    }

    private async Task<ArtifactLocationState> LocationStateAsync(Guid locationId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .Where(location => location.Id == locationId).Select(location => location.State).SingleAsync();
    }

    public void Dispose()
    {
        foreach (var path in Enumerable.Reverse(_namespaceRoots))
        {
            try
            {
                if (new FileInfo(path).LinkTarget != null) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
