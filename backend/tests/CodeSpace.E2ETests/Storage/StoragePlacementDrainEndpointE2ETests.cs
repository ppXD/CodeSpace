using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Storage;

/// <summary>
/// The whole way out of a destination that is gone, over the surfaces a Settings screen has.
///
/// <para>Retirement was refused by a guard that named a population the operator could not see, could not drain, and
/// could only reach by opening a database. Every step here is an HTTP call the dialog makes — count what is held,
/// look at it, drain it, retire — so a green run is the claim that no terminal is needed, and a red one is that
/// claim failing.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class StoragePlacementDrainEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>, IDisposable
{
    /// <summary>Enough passes to finish this fixture's placements many times over, and small enough that a drain that stops making progress fails rather than hangs.</summary>
    private const int MaxPasses = 10;

    private readonly TaskLaunchApiFactory _factory;
    private readonly List<string> _roots = [];

    public StoragePlacementDrainEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task An_operator_refused_a_retirement_drains_the_profile_and_retires_it_without_leaving_the_screen()
    {
        var world = await SeedRoutedTeamAsync();
        foreach (var index in Enumerable.Range(0, 3)) await PutAsync(world.TeamId, Payload($"held {index}"));
        await QuiesceRouteAsync(world);

        var refused = await RetireAsync(world);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await DescribeAsync(refused));

        var totals = await JsonAsync(await SendAsync(world, HttpMethod.Get, $"/api/storage/profiles/{world.ProfileId}/placements/totals"));
        HeldCount(totals).ShouldBe(3, "the refusal has to have a population behind it, or there is nothing to act on");

        var listed = await JsonAsync(await SendAsync(world, HttpMethod.Get, $"/api/storage/profiles/{world.ProfileId}/placements?limit=50"));
        listed.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("objectKey").GetString()).Distinct().Count().ShouldBe(3,
            "a count with no keys behind it names nothing an operator can go and look at");

        EmptyDestination(world.Root);
        var final = await DrainAsync(world);

        final.GetProperty("remaining").GetInt32().ShouldBe(0, "draining to zero must mean draining to what actually unblocks retirement");
        var retired = await RetireAsync(world);
        retired.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(retired));
        (await JsonAsync(retired)).GetProperty("state").GetString().ShouldBe("Retired");
    }

    [Fact]
    public async Task A_pass_over_a_destination_that_still_serves_its_objects_closes_nothing_and_says_which()
    {
        // The refusal that makes the control safe to put in front of an operator. Nothing is closed on their say-so,
        // and the summary names the objects that kept their records so they can go and check one.
        var world = await SeedRoutedTeamAsync();
        await PutAsync(world.TeamId, Payload("still there"));
        await QuiesceRouteAsync(world);

        var pass = await JsonAsync(await AbandonAsync(world));

        pass.GetProperty("abandoned").GetInt32().ShouldBe(0);
        pass.GetProperty("stillServed").GetInt32().ShouldBe(1);
        pass.GetProperty("remaining").GetInt32().ShouldBe(1);
        var outcome = pass.GetProperty("outcomes").EnumerateArray().Single();
        outcome.GetProperty("outcome").GetString().ShouldBe("StillServed");
        outcome.GetProperty("objectKey").GetString().ShouldNotBeNullOrWhiteSpace("a still-served count nobody can resolve to an object is unactionable");
        (await RetireAsync(world)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "a served object must keep blocking the irreversible step");
    }

    [Fact]
    public async Task A_pass_the_destination_stopped_is_repeatable_and_the_next_one_drains_what_was_behind_it()
    {
        // The dead end the batch ordering exists to prevent, over the wire an operator actually uses. Five placements
        // sit on a volume that is gone; they refuse uniformly enough to stop any pass that meets them first, and they
        // are what a pass meets first. Read as "the destination has answered, stop offering", the operator never
        // reaches the fifteen ordered behind them — which are the records that would close, and the only thing
        // between this profile and retirement.
        var world = await SeedRoutedTeamAsync(strandedOnAVanishedVolume: 5);
        foreach (var index in Enumerable.Range(0, 15)) await PutAsync(world.TeamId, Payload($"held {index}"));
        await QuiesceRouteAsync(world);
        EmptyDestination(world.Root);

        var first = await JsonAsync(await AbandonAsync(world));

        first.GetProperty("stoppedBy").GetString().ShouldNotBeNullOrWhiteSpace("a pass that stopped early has to name why, or nothing downstream can tell this shape from a small profile");
        first.GetProperty("abandoned").GetInt32().ShouldBe(0, "a first pass that already closed records would prove nothing about what the second one reaches");
        Remaining(first).ShouldBeGreaterThan(first.GetProperty("examined").GetInt32(),
            "the stopped pass left placements it never reached — the fact that makes pressing again worth anything, and the one a dialog must not throw away");

        var second = await JsonAsync(await AbandonAsync(world));

        second.GetProperty("abandoned").GetInt32().ShouldBe(15, "every placement ordered behind the refusers has to be reachable by the pass after the one that stopped");
        Remaining(second).ShouldBe(5, "only the placements on the volume that is gone may still be held");
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Repeats bounded passes the way the dialog does, and fails loudly rather than looping when one stops reducing
    /// the population.
    ///
    /// <para>A pass the circuit breaker stopped is NOT a reason to give up, which is why nothing here reads
    /// <c>stoppedBy</c>: the batch is ordered least-recently-touched first precisely so placements that refuse sort
    /// behind the ones a pass never reached, and the pass after a stop looks at different rows. What ends this loop
    /// is a pass that closed nothing, because that is the only shape repeating cannot get past.</para>
    /// </summary>
    private async Task<JsonElement> DrainAsync(RoutedTeam world)
    {
        var pass = await JsonAsync(await AbandonAsync(world));

        for (var attempt = 1; attempt < MaxPasses && Remaining(pass) > 0; attempt++)
        {
            var next = await JsonAsync(await AbandonAsync(world));

            Remaining(next).ShouldBeLessThan(Remaining(pass), $"pass {attempt} reduced nothing, so repeating it is a loop rather than a drain: {next}");
            pass = next;
        }

        return pass;
    }

    private static int Remaining(JsonElement pass) => pass.GetProperty("remaining").GetInt32();

    private async Task<HttpResponseMessage> AbandonAsync(RoutedTeam world) =>
        await SendAsync(world, HttpMethod.Post, $"/api/storage/profiles/{world.ProfileId}/placements/abandon", new { batchSize = 50 });

    private async Task<HttpResponseMessage> RetireAsync(RoutedTeam world)
    {
        var profile = await JsonAsync(await SendAsync(world, HttpMethod.Get, $"/api/storage/profiles/{world.ProfileId}"));

        return await SendAsync(world, HttpMethod.Put, $"/api/storage/profiles/{world.ProfileId}/state", new
        {
            expectedXmin = profile.GetProperty("xmin").GetUInt32(),
            expectedCurrentRevision = profile.GetProperty("currentRevision").GetInt32(),
            state = "Retired",
        });
    }

    /// <summary>Stops the route that targets this profile, so the only thing left blocking retirement is what is stored.</summary>
    private async Task QuiesceRouteAsync(RoutedTeam world)
    {
        var route = await JsonAsync(await SendAsync(world, HttpMethod.Get, $"/api/storage/routes/{world.RouteId}"));
        var disabled = await SendAsync(world, HttpMethod.Put, $"/api/storage/routes/{world.RouteId}/state", new
        {
            expectedXmin = route.GetProperty("xmin").GetUInt32(),
            expectedCurrentRevision = route.GetProperty("currentRevision").GetInt32(),
            state = "Disabled",
        });

        disabled.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(disabled));
    }

    /// <summary>The population the retirement guard counts — everything the ledger has not settled.</summary>
    private static int HeldCount(JsonElement totals) =>
        totals.EnumerateArray().Where(total => total.GetProperty("state").GetString() is not ("Purged" or "Deleted"))
            .Sum(total => total.GetProperty("count").GetInt32());

    /// <summary>Takes the objects and leaves the destination, which is what an emptied bucket looks like to the drain.</summary>
    private static void EmptyDestination(string root)
    {
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
    }

    private async Task<HttpResponseMessage> SendAsync(RoutedTeam world, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(world.UserId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", world.TeamId.ToString());
        if (body != null) request.Content = JsonContent.Create(body);

        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task PutAsync(Guid teamId, byte[] payload)
    {
        using var scope = _factory.Services.CreateScope();
        var artifactId = await scope.ServiceProvider.GetRequiredService<IArtifactStore>()
            .PutAsync(teamId, payload, "application/octet-stream", CancellationToken.None);

        var row = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking()
            .SingleAsync(value => value.Id == artifactId);
        row.CasArtifactObjectId.ShouldNotBeNull("a fixture that stayed inline records no placement, so it would drain nothing");
    }

    /// <summary>Past the inline threshold, so the bytes must leave the row and a placement is actually recorded.</summary>
    private static byte[] Payload(string content)
    {
        var head = System.Text.Encoding.UTF8.GetBytes(content + "\n");
        var bytes = new byte[32 * 1024];
        head.CopyTo(bytes, 0);
        for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)('a' + (i % 26));

        return bytes;
    }

    /// <summary>
    /// A team whose route places into one live destination, and optionally placements left behind on a volume that
    /// is gone.
    ///
    /// <para>The stranded ones sit under an EARLIER revision of the same profile, which is exactly where re-pointing
    /// a profile leaves the rows it already placed. That revision names a root nothing ever creates, so the
    /// destination cannot answer for itself and every placement under it refuses for the same reason — the uniform
    /// answer a pass stops on.</para>
    /// </summary>
    private async Task<RoutedTeam> SeedRoutedTeamAsync(int strandedOnAVanishedVolume = 0)
    {
        var root = Path.Combine(Path.GetTempPath(), "codespace-drain-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var seed = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8], DateTimeOffset.UtcNow);
        var strandedRevisionId = Guid.NewGuid();

        db.User.Add(new User { Id = seed.UserId, SecurityStamp = TestToken.SeedStamp, Email = $"drain-{seed.Suffix}@test.local", Name = "Drain", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = seed.TeamId, Slug = $"drain-{seed.Suffix}", Name = "Drain", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = seed.TeamId, UserId = seed.UserId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        var profile = new StorageProfile
        {
            Id = seed.ProfileId, TeamId = seed.TeamId, StableName = $"drain-{seed.Suffix}", CurrentRevision = strandedOnAVanishedVolume > 0 ? 2 : 1,
            State = StorageProfileState.Active, CreatedDate = seed.Now, CreatedBy = seed.UserId, LastModifiedDate = seed.Now, LastModifiedBy = seed.UserId,
        };

        if (strandedOnAVanishedVolume > 0) profile.Revisions.Add(Revision(seed, strandedRevisionId, revision: 1, root + "-vanished"));

        profile.Revisions.Add(Revision(seed, Guid.NewGuid(), profile.CurrentRevision, root));
        db.StorageProfile.Add(profile);
        db.StorageRoute.Add(Route(seed));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE storage_route SET state = 'Active' WHERE id = {seed.RouteId}");

        foreach (var index in Enumerable.Range(0, strandedOnAVanishedVolume)) Strand(db, seed, strandedRevisionId, index);
        await db.SaveChangesAsync();

        return new RoutedTeam(seed.UserId, seed.TeamId, seed.ProfileId, seed.RouteId, root);
    }

    /// <summary>One revision of the profile, pointed at a root. A revision IS its destination, so two of them are how one profile holds placements in two places at once.</summary>
    private static StorageProfileRevision Revision(Seed seed, Guid revisionId, int revision, string root) => new()
    {
        Id = revisionId, TeamId = seed.TeamId, StorageProfileId = seed.ProfileId, Revision = revision,
        ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
        NonSecretConfigJson = JsonSerializer.Serialize(new { rootPath = root }), CredentialRef = null,
        NamespaceFingerprint = $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root))).ToLowerInvariant()}",
        CreatedDate = seed.Now, CreatedBy = seed.UserId,
    };

    private static StorageRoute Route(Seed seed) => new()
    {
        Id = seed.RouteId, TeamId = seed.TeamId, DataClassTypeKey = "workflow-artifact/v1", CurrentRevision = 1,
        State = StorageRouteState.Draft, CreatedDate = seed.Now, CreatedBy = seed.UserId, LastModifiedDate = seed.Now, LastModifiedBy = seed.UserId,
        Revisions =
        {
            new StorageRouteRevision
            {
                Id = Guid.NewGuid(), TeamId = seed.TeamId, StorageRouteId = seed.RouteId, Revision = 1, StorageProfileId = seed.ProfileId,
                ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite, PinnedProfileRevision = null,
                CreatedDate = seed.Now, CreatedBy = seed.UserId,
            },
        },
    };

    /// <summary>
    /// One placement on the vanished volume: an object row, a location row, and no bytes anywhere, because the
    /// volume that held them is not there to write to.
    ///
    /// <para>Stamped an hour into the past so it sorts ahead of everything the route goes on to place. That is what
    /// puts these at the head of the first batch, which is the position that makes a stopped pass stop before it has
    /// reached anything else.</para>
    /// </summary>
    private static void Strand(CodeSpaceDbContext db, Seed seed, Guid revisionId, int index)
    {
        var objectId = Guid.NewGuid();
        var stranded = seed.Now.AddHours(-1).AddSeconds(index);
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"stranded {objectId:N}"));
        var objectKey = $"artifacts/{objectId:N}";

        db.ArtifactObject.Add(new ArtifactObject { Id = objectId, TeamId = seed.TeamId, Digest = digest, SizeBytes = 1024, CreatedDate = stranded });

        var location = new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = seed.TeamId, ArtifactObjectId = objectId, StorageProfileRevisionId = revisionId,
            Locator = objectKey, ObjectKey = objectKey, State = ArtifactLocationState.Available, Revision = 1, VerifiedAt = stranded,
            ObservedSizeBytes = 1024, ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = digest,
            CreatedDate = stranded, CreatedBy = seed.UserId, LastModifiedDate = stranded, LastModifiedBy = seed.UserId,
        };
        db.ArtifactLocation.Add(location);
        db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = seed.TeamId, ArtifactLocationId = location.Id, Revision = 1,
            EventType = ArtifactLocationEventType.Created, State = ArtifactLocationState.Available, ObservedAt = stranded,
            ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = digest, ObservedSizeBytes = 1024,
            VerifiedAt = stranded, DetailsJson = "{}", CreatedBy = seed.UserId,
        });
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private static async Task<string> DescribeAsync(HttpResponseMessage response) => $"got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record RoutedTeam(Guid UserId, Guid TeamId, Guid ProfileId, Guid RouteId, string Root);

    /// <summary>The identities one seeded world is built from, so the rows that make it up are constructed from one source rather than seven parameters each.</summary>
    private sealed record Seed(Guid UserId, Guid TeamId, Guid ProfileId, Guid RouteId, string Suffix, DateTimeOffset Now);
}
