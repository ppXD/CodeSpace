using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Storage;

/// <summary>
/// Two teams, one physical directory, two spellings of it — and the retention sweep of one team must not delete the
/// other team's live bytes.
///
/// <para>The shape an audit proved destructive: a victim team's admin points <c>workflow-artifact/v1</c> at a local root
/// <c>R</c> and stores an artifact; a member of that team who owns a Personal team points their own destination at
/// <c>R/</c> (or <c>R/.</c>, or any other spelling of <c>R</c>) and stores the same bytes, unreferenced. Object keys are
/// content-addressed with no team segment, so both teams' locations name ONE file. The sharing guard compared a hash of
/// the configuration as typed, saw two namespaces, and collected the second team's object — deleting the file the
/// victim's download then answered 502 for.</para>
///
/// <para>A spelling need not name the victim's root at all. The driver opens <c>&lt;root&gt;/objects/&lt;key&gt;</c>
/// and the operating system follows a link anywhere along it, so a collector root that is a directory of its own, with
/// <c>objects</c> (or a directory below it) linked into the victim's, reaches the same file — and so does a link whose
/// target climbs out through another link (<c>L -&gt; a/..</c>), which the operating system resolves before it
/// climbs.</para>
///
/// <para><b>Fidelity: high.</b> Every destination is created over real HTTP through admission, where the namespace
/// fingerprint is derived; bytes land through the production routed write path into the real local-rwx driver on real
/// directories; the real reaper sweeps the real Postgres database; and the victim reads back over real HTTP. The only
/// non-production steps are the two that have no HTTP surface or would take a week: bytes are put through the in-process
/// <see cref="IArtifactStore"/> and <see cref="IArtifactRetentionWriter"/> seams (no endpoint uploads an artifact; runs
/// do, through these same classes), and the declaration is aged in SQL past its age floor and quarantine window.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class StorageNamespaceSharingE2ETests : IClassFixture<TaskLaunchApiFactory>, IDisposable
{
    /// <summary>Two sweeps decide any declaration here; the slack absorbs a sweep another class's rows crowd, and a loop that stops progressing fails instead of hanging.</summary>
    private const int MaxSweeps = 6;

    private readonly TaskLaunchApiFactory _factory;
    private readonly List<string> _paths = [];

    public StorageNamespaceSharingE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

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
    public async Task A_second_team_reaching_the_victims_root_through_any_spelling_cannot_collect_the_victims_bytes(string spelling)
    {
        if (spelling.Contains("link") && OperatingSystem.IsWindows()) return;

        var world = await SeedTwoTeamsAsync();
        var root = Root();
        var payload = Payload($"victim deliverable {spelling}");

        await CreateDestinationAsync(world.Victim, root);
        var victimArtifactId = await PutRoutedAsync(world.Victim.TeamId, payload);
        var spelled = Spell(root, spelling);
        await CreateDestinationAsync(world.Collector, spelled);
        var collectorArtifactId = await PutDeclaredAsync(world.Collector.TeamId, payload);
        var file = ObjectPath(root, payload);

        File.Exists(file).ShouldBeTrue($"the precondition: both teams' writes landed on ONE file at {file}");
        File.Exists(ObjectPath(spelled, payload)).ShouldBeTrue($"the precondition: the operating system opens that file through '{spelled}'");

        await SweepUntilSettledAsync(collectorArtifactId);

        var declaration = (await DeclarationAsync(collectorArtifactId)).ShouldNotBeNull(
            $"the collector's artifact was COLLECTED through '{spelled}', which reaches '{root}' — so the victim's bytes went with it");
        declaration.State.ShouldBe(ArtifactRetentionState.Indeterminate);
        declaration.LastErrorCode.ShouldBe("artifact-routed-object-shared", "the kept row must say WHY, not merely survive");
        File.Exists(file).ShouldBeTrue($"the victim's object is gone from disk; `ls {Path.GetDirectoryName(file)}` shows what the sweep left");

        var download = await DownloadAsync(world.Victim, victimArtifactId);
        download.StatusCode.ShouldBe(HttpStatusCode.OK, $"the victim's own artifact must still download after the other team's sweep: {await download.Content.ReadAsStringAsync()}");
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(payload);
    }

    [Fact]
    public async Task A_victim_destination_admitted_before_namespaces_were_canonical_is_still_protected()
    {
        // Every destination already in a database was admitted when the fingerprint hashed the configuration as typed,
        // and revisions are immutable, so those stored values never change. A victim whose root was typed 'R/' carries
        // that text's hash; a collector admitted now at 'R' carries the directory's. Only a sweep that re-derives both
        // namespaces from their configurations sees that they are one directory.
        var world = await SeedTwoTeamsAsync();
        var root = Root();
        var payload = Payload("victim deliverable from before canonical admission");

        await SeedDestinationAdmittedBeforeCanonicalAsync(world.Victim, root + "/");
        var victimArtifactId = await PutRoutedAsync(world.Victim.TeamId, payload);
        await CreateDestinationAsync(world.Collector, root);
        var collectorArtifactId = await PutDeclaredAsync(world.Collector.TeamId, payload);

        (await StoredFingerprintsAsync(world)).Distinct().Count().ShouldBe(2, "the precondition: the stored fingerprints disagree, so they cannot be what keeps these bytes");

        await SweepUntilSettledAsync(collectorArtifactId);

        var declaration = (await DeclarationAsync(collectorArtifactId)).ShouldNotBeNull("the sweep trusted the victim's stored fingerprint and collected the shared object");
        declaration.LastErrorCode.ShouldBe("artifact-routed-object-shared");

        var download = await DownloadAsync(world.Victim, victimArtifactId);
        download.StatusCode.ShouldBe(HttpStatusCode.OK, await download.Content.ReadAsStringAsync());
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(payload);
    }

    [Fact]
    public async Task A_second_team_in_a_root_of_its_own_still_has_its_unreferenced_bytes_collected()
    {
        // The positive control: content addressing gives both teams the same key, so if a different directory also
        // counted as shared, deduplicated content would never be collected and every theory case above would pass
        // for the wrong reason.
        var world = await SeedTwoTeamsAsync();
        var victimRoot = Root();
        var collectorRoot = Root();
        var payload = Payload("one payload in two separate directories");

        await CreateDestinationAsync(world.Victim, victimRoot);
        var victimArtifactId = await PutRoutedAsync(world.Victim.TeamId, payload);
        await CreateDestinationAsync(world.Collector, collectorRoot + "/");
        var collectorArtifactId = await PutDeclaredAsync(world.Collector.TeamId, payload);

        await SweepUntilSettledAsync(collectorArtifactId);

        (await DeclarationAsync(collectorArtifactId)).ShouldBeNull("the collector's directory is its own, so nothing else reaches its bytes and they are collected");
        File.Exists(ObjectPath(collectorRoot, payload)).ShouldBeFalse("collection removes the bytes the collected row named");
        File.Exists(ObjectPath(victimRoot, payload)).ShouldBeTrue("a different directory holding the same key is a different object");

        var download = await DownloadAsync(world.Victim, victimArtifactId);
        download.StatusCode.ShouldBe(HttpStatusCode.OK, await download.Content.ReadAsStringAsync());
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(payload);
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    private sealed record Member(Guid UserId, Guid TeamId);

    private sealed record World(Member Victim, Member Collector);

    /// <summary>
    /// The victim: Owner of a workspace team. The collector: a Viewer of that team — so it can read the team's bytes
    /// but has no storage rights there — and Owner of its own Personal team, where it has every storage right.
    /// </summary>
    private async Task<World> SeedTwoTeamsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var victim = new Member(Guid.NewGuid(), Guid.NewGuid());
        var collector = new Member(Guid.NewGuid(), Guid.NewGuid());

        db.User.AddRange(
            new User { Id = victim.UserId, SecurityStamp = TestToken.SeedStamp, Email = $"namespace-victim-{suffix}@test.local", Name = "Victim", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId },
            new User { Id = collector.UserId, SecurityStamp = TestToken.SeedStamp, Email = $"namespace-collector-{suffix}@test.local", Name = "Collector", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.AddRange(
            new Team { Id = victim.TeamId, Slug = $"namespace-victim-{suffix}", Name = "Victim", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId },
            new Team { Id = collector.TeamId, Slug = $"namespace-personal-{suffix}", Name = "Personal", Kind = TeamKind.Personal, PersonalForUserId = collector.UserId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.AddRange(
            new TeamMembership { Id = Guid.NewGuid(), TeamId = victim.TeamId, UserId = victim.UserId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId },
            new TeamMembership { Id = Guid.NewGuid(), TeamId = victim.TeamId, UserId = collector.UserId, Role = TeamRole.Viewer, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId },
            new TeamMembership { Id = Guid.NewGuid(), TeamId = collector.TeamId, UserId = collector.UserId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();

        return new World(victim, collector);
    }

    /// <summary>The Settings "add destination" call: admits the profile, activates it and routes <c>workflow-artifact/v1</c> at it in one step.</summary>
    private async Task CreateDestinationAsync(Member member, string rootPath)
    {
        var response = await SendAsync(member, HttpMethod.Post, "/api/storage/destinations", new
        {
            name = $"namespace-{Guid.NewGuid():N}"[..24],
            providerTypeKey = "local-rwx/v1",
            nonSecretConfig = new { rootPath },
            dataClassTypeKeys = new[] { "workflow-artifact/v1" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"creating a destination at '{rootPath}': {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// A destination exactly as admission recorded it before namespaces were canonical: its fingerprint is the hash of
    /// the configuration as typed. Seeded rather than created, because no endpoint can produce that row any more.
    /// </summary>
    private async Task SeedDestinationAdmittedBeforeCanonicalAsync(Member member, string rootPath)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var config = JsonSerializer.Serialize(new { rootPath });
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        using var typed = JsonDocument.Parse(config);

        db.StorageProfile.Add(new StorageProfile
        {
            Id = profileId, TeamId = member.TeamId, StableName = $"pre-canonical-{profileId:N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = member.UserId, LastModifiedDate = now, LastModifiedBy = member.UserId,
            Revisions =
            {
                new StorageProfileRevision
                {
                    Id = Guid.NewGuid(), TeamId = member.TeamId, StorageProfileId = profileId, Revision = 1, ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
                    NonSecretConfigJson = config, CredentialRef = null, NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, typed.RootElement),
                    CreatedDate = now, CreatedBy = member.UserId,
                },
            },
        });
        db.StorageRoute.Add(new StorageRoute
        {
            Id = routeId, TeamId = member.TeamId, DataClassTypeKey = "workflow-artifact/v1", CurrentRevision = 1,
            State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = member.UserId, LastModifiedDate = now, LastModifiedBy = member.UserId,
            Revisions =
            {
                new StorageRouteRevision
                {
                    Id = Guid.NewGuid(), TeamId = member.TeamId, StorageRouteId = routeId, Revision = 1, StorageProfileId = profileId,
                    ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite, PinnedProfileRevision = null, CreatedDate = now, CreatedBy = member.UserId,
                },
            },
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE storage_route SET state = 'Active' WHERE id = {routeId}");
    }

    private async Task<List<string>> StoredFingerprintsAsync(World world)
    {
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().StorageProfileRevision.AsNoTracking()
            .Where(revision => revision.TeamId == world.Victim.TeamId || revision.TeamId == world.Collector.TeamId)
            .Select(revision => revision.NamespaceFingerprint).ToListAsync();
    }

    private async Task<Guid> PutRoutedAsync(Guid teamId, byte[] payload)
    {
        using var scope = _factory.Services.CreateScope();
        var artifactId = await scope.ServiceProvider.GetRequiredService<IArtifactStore>().PutAsync(teamId, payload, "application/octet-stream", CancellationToken.None);

        await EnsureRoutedAsync(artifactId);
        return artifactId;
    }

    /// <summary>The declaring write <c>ArtifactManifestStore</c> makes, before its manifest row exists — the unreferenced shape retention collects.</summary>
    private async Task<Guid> PutDeclaredAsync(Guid teamId, byte[] payload)
    {
        using var scope = _factory.Services.CreateScope();
        var write = await scope.ServiceProvider.GetRequiredService<IArtifactRetentionWriter>().PutDeclaredAsync(
            new ArtifactRetentionWriteRequest(teamId, payload, "application/octet-stream", ArtifactRetentionClass.ArtifactManifestContent, "artifact_manifest", Guid.NewGuid()), CancellationToken.None);

        write.Declared.ShouldBeTrue("a first routed write must mint the declaration this test sweeps");
        await EnsureRoutedAsync(write.ArtifactId);
        return write.ArtifactId;
    }

    private async Task EnsureRoutedAsync(Guid artifactId)
    {
        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(value => value.Id == artifactId);

        row.CasArtifactObjectId.ShouldNotBeNull("a write that stayed inline or fell back to local disk proves nothing about a routed namespace");
    }

    /// <summary>
    /// Ages the declaration past its class's age floor, then sweeps until it settles — collected, or kept terminally —
    /// re-ageing an opened quarantine between sweeps.
    /// </summary>
    private async Task SweepUntilSettledAsync(Guid artifactId)
    {
        await AgeAsync(artifactId);

        for (var sweep = 0; sweep < MaxSweeps; sweep++)
        {
            using (var scope = _factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None);

            var declaration = await DeclarationAsync(artifactId);
            if (declaration == null || declaration.State == ArtifactRetentionState.Indeterminate) return;

            await AgeQuarantineAsync(artifactId);
        }

        var stuck = await DeclarationAsync(artifactId);
        throw new ShouldAssertException($"declaration {artifactId} did not settle within {MaxSweeps} sweeps (state {stuck?.State}, last error '{stuck?.LastErrorCode}'); "
            + $"inspect it with `SELECT * FROM workflow_artifact_retention WHERE artifact_id = '{artifactId}'`");
    }

    private async Task AgeAsync(Guid artifactId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        // workflow_artifact rejects UPDATE (migration 0016). The trigger is disabled inside ONE transaction, which is
        // what scopes it: DISABLE TRIGGER is a catalog change every session sees until it is re-enabled.
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact DISABLE TRIGGER workflow_artifact_enforce_immutability");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_artifact SET created_at = clock_timestamp() - interval '30 days' WHERE id = {artifactId}");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact ENABLE TRIGGER workflow_artifact_enforce_immutability");
            await transaction.CommitAsync();
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workflow_artifact_retention
            SET declared_at = clock_timestamp() - interval '30 days', next_sweep_at = clock_timestamp() - interval '30 days', last_modified_at = clock_timestamp()
            WHERE artifact_id = {artifactId}
            """);
    }

    private async Task AgeQuarantineAsync(Guid artifactId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workflow_artifact_retention
            SET quarantined_at = clock_timestamp() - interval '2 days', next_sweep_at = clock_timestamp() - interval '2 days', last_modified_at = clock_timestamp()
            WHERE artifact_id = {artifactId} AND state = 'Quarantined'
            """);
    }

    private async Task<WorkflowArtifactRetention?> DeclarationAsync(Guid artifactId)
    {
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking().SingleOrDefaultAsync(row => row.ArtifactId == artifactId);
    }

    private async Task<HttpResponseMessage> DownloadAsync(Member member, Guid artifactId) => await SendAsync(member, HttpMethod.Get, $"/api/artifacts/{artifactId}");

    private async Task<HttpResponseMessage> SendAsync(Member member, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(member.UserId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", member.TeamId.ToString());
        if (body != null) request.Content = JsonContent.Create(body);

        return await _factory.CreateClient().SendAsync(request);
    }

    /// <summary>A real directory of this test's own, GUID-named so repeated and concurrent runs never meet.</summary>
    private string Root()
    {
        var root = Path.Combine(Path.GetTempPath(), "codespace-namespace-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _paths.Add(root);
        return root;
    }

    /// <summary>The spelling under test; the local-rwx driver opens every one of them as <paramref name="root"/> itself.</summary>
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
        var holder = Root();
        Directory.CreateSymbolicLink(Path.Combine(holder, "a"), Path.Combine(root, "objects"));
        Directory.CreateSymbolicLink(Path.Combine(holder, "L"), Path.Combine("a", ".."));
        return Path.Combine(holder, "L");
    }

    /// <summary>A root of the collector's own whose <paramref name="beneath"/> is a link into <paramref name="root"/>: the root itself names nothing shared.</summary>
    private string LinkBeneath(string root, string beneath)
    {
        var collectorRoot = Root();
        var link = Path.Combine(collectorRoot, beneath);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, Path.Combine(root, beneath));
        return collectorRoot;
    }

    private string LinkTo(string root)
    {
        var link = Path.Combine(Path.GetTempPath(), "codespace-namespace-e2e", $"link-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(link, root);
        _paths.Add(link);
        return link;
    }

    /// <summary>Where the local-rwx driver keeps these bytes: <c>&lt;root&gt;/objects/workflow-artifacts/{aa}/{bb}/{sha256}</c>, the key with no team in it.</summary>
    private static string ObjectPath(string root, byte[] payload)
    {
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        return Path.Combine(root, "objects", "workflow-artifacts", sha[..2], sha.Substring(2, 2), sha);
    }

    /// <summary>32 KiB, past the inline threshold, so the bytes must leave the row for the destination.</summary>
    private static byte[] Payload(string content)
    {
        var head = System.Text.Encoding.UTF8.GetBytes($"{content} {Guid.NewGuid():N}\n");
        var bytes = new byte[32 * 1024];
        head.CopyTo(bytes, 0);
        for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)('a' + (i % 26));

        return bytes;
    }

    public void Dispose()
    {
        foreach (var path in Enumerable.Reverse(_paths))
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
