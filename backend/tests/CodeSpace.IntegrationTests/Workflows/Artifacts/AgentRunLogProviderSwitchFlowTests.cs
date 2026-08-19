using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// 🟢 High fidelity: the real <see cref="IAgentRunLogService"/> resolved from the container — so the real
/// <c>ArtifactCasRuntimeCoordinator</c> and the real local-rwx driver — against real Postgres and two real temp roots.
/// Two profiles of the SAME provider kind with DIFFERENT roots, so nothing here depends on a second provider's
/// availability and any failure is about revision resolution alone.
///
/// <para>The sibling of <see cref="ArtifactProviderSwitchFlowTests"/> for the SECOND routed data class. That file pins
/// "a read resolves through the profile revision the object's own location ledger recorded, never through current
/// routing policy" for <c>workflow-artifact/v1</c>. <c>agent-run-log/v1</c> shipped with no equivalent: its nearest
/// test — <c>AgentRunLogRuntimeTests.Log_bytes_stay_available_to_the_read_api_after_their_storage_profile_is_disabled_and_retired</c>
/// — only disables and retires the SAME profile the route still names, so a read path that consulted current routing
/// policy would resolve that same profile and pass it. Repointing the route to a DIFFERENT profile is the only
/// arrangement in which the recorded revision and the current policy disagree, which is what this file arranges.</para>
///
/// <para>No inline-threshold caveat applies here, unlike the artifact class: <c>AgentRunLogService.AppendAsync</c> has
/// no inline arm and puts every segment through the coordinator whatever its size. The file counts pin WHICH provider
/// root the bytes physically left for, so a read that fell through to the other profile would otherwise be able to
/// return the right bytes for the wrong reason.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunLogProviderSwitchFlowTests : IDisposable
{
    private const string ProviderTypeKey = "local-rwx/v1";
    private const long WorkerFenceEpoch = 7;

    private readonly PostgresFixture _fixture;
    private readonly string _rootA = Path.Combine(Path.GetTempPath(), "codespace-log-switch-a", Guid.NewGuid().ToString("N"));
    private readonly string _rootB = Path.Combine(Path.GetTempPath(), "codespace-log-switch-b", Guid.NewGuid().ToString("N"));

    public AgentRunLogProviderSwitchFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_segment_written_before_the_switch_still_reads_from_its_own_provider_after_it()
    {
        var world = await SeedAsync();
        var session = Guid.NewGuid();
        var before = "written-under-profile-a\n"u8.ToArray();
        var streamId = await OpenStreamAsync(world, session);

        await AppendAsync(world, streamId, session, ordinal: 1, offset: 0, before);

        FileCountUnder(_rootA).ShouldBeGreaterThan(0, "the append must have gone through the routed profile");
        FileCountUnder(_rootB).ShouldBe(0, "profile B holds nothing yet — it is not even routed");

        await RepointRouteAsync(world, world.ProfileBId);

        var after = "written-under-profile-b\n"u8.ToArray();
        await AppendAsync(world, streamId, session, ordinal: 2, offset: before.Length, after);

        FileCountUnder(_rootB).ShouldBeGreaterThan(0, "a new append follows the switched route and lands physically under profile B's root");

        (await ReadRangeAsync(world, streamId, 0, before.Length))
            .ShouldBe(before, "the pre-switch segment still reads — its location ledger, not current routing, chose the profile");
    }

    [Fact]
    public async Task A_range_spanning_the_switch_stitches_both_providers_into_one_read()
    {
        // The range path opens each segment through that segment's OWN recorded location. A read that resolved one
        // profile for the whole stream would serve half of this range and fail closed on the other half.
        var world = await SeedAsync();
        var session = Guid.NewGuid();
        var before = "segment-under-a\n"u8.ToArray();
        var after = "segment-under-b\n"u8.ToArray();
        var streamId = await OpenStreamAsync(world, session);

        await AppendAsync(world, streamId, session, ordinal: 1, offset: 0, before);
        await RepointRouteAsync(world, world.ProfileBId);
        await AppendAsync(world, streamId, session, ordinal: 2, offset: before.Length, after);

        (await ReadRangeAsync(world, streamId, 0, before.Length + after.Length))
            .ShouldBe([.. before, .. after], "both segments resolve through their own recorded revisions");
    }

    [Fact]
    public async Task Switching_back_does_not_strand_what_the_other_profile_holds()
    {
        // Two switches, one segment each. Neither read may depend on which profile the route happens to name now — the
        // failure this catches is a read path that resolves the CURRENT route and gets away with it while nothing moved.
        var world = await SeedAsync();
        var session = Guid.NewGuid();
        var underA = "under-a\n"u8.ToArray();
        var underB = "under-b\n"u8.ToArray();
        var streamId = await OpenStreamAsync(world, session);

        await AppendAsync(world, streamId, session, ordinal: 1, offset: 0, underA);
        await RepointRouteAsync(world, world.ProfileBId);
        await AppendAsync(world, streamId, session, ordinal: 2, offset: underA.Length, underB);
        await RepointRouteAsync(world, world.ProfileAId);

        (await ReadRangeAsync(world, streamId, 0, underA.Length)).ShouldBe(underA, "written under A, routed to A");
        (await ReadRangeAsync(world, streamId, underA.Length, underB.Length)).ShouldBe(underB, "written under B, routed to A — the read must ignore the route entirely");
    }

    /// <summary>Open the stream through the real service, returning the stream the appends below extend.</summary>
    private async Task<Guid> OpenStreamAsync(World world, Guid session)
    {
        using var scope = _fixture.BeginScope();
        var opened = await scope.Resolve<IAgentRunLogService>().OpenAsync(new AgentRunLogOpenRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, WorkerFenceEpoch = WorkerFenceEpoch, CaptureSessionId = session,
            StreamKind = AgentRunLogKinds.StandardOutput, ContentType = "text/plain", ContentEncoding = "utf-8", CaptureSource = "switch-test/v1",
        }, CancellationToken.None);

        return opened.ShouldBeOfType<AgentRunLogOpenResult.Opened>().Metadata.StreamId;
    }

    /// <summary>Append one segment at whatever the class's own resolver says the destination is RIGHT NOW — the same two calls the capture bridge makes.</summary>
    private async Task AppendAsync(World world, Guid streamId, Guid session, long ordinal, long offset, byte[] bytes)
    {
        using var scope = _fixture.BeginScope();
        var storage = (await scope.Resolve<IAgentRunLogStorageResolver>().ResolveAsync(world.TeamId, CancellationToken.None))
            .ShouldBeOfType<AgentRunLogStorageResolution.Ready>();

        var appended = await scope.Resolve<IAgentRunLogService>().AppendAsync(new AgentRunLogAppendRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = streamId, WorkerFenceEpoch = WorkerFenceEpoch,
            CaptureSessionId = session, ExpectedSegmentOrdinal = ordinal, ExpectedOffsetBytes = offset,
            ExpectedSourceOffsetBytes = offset, SourceLengthBytes = bytes.Length,
            StorageProfileId = storage.StorageProfileId, StorageProfileRevision = storage.StorageProfileRevision,
            ActorId = world.ActorId, Bytes = bytes,
        }, CancellationToken.None);

        appended.ShouldBeOfType<AgentRunLogAppendResult.Appended>();
    }

    private async Task<byte[]> ReadRangeAsync(World world, Guid streamId, long offset, int length)
    {
        using var scope = _fixture.BeginScope();
        var read = await scope.Resolve<IAgentRunLogService>().ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, streamId, offset, length), CancellationToken.None);

        return read.ShouldBeOfType<AgentRunLogRangeResult.Available>().Bytes.ToArray();
    }

    /// <summary>Point the class's route at another profile through the SERVICE an operator's request runs — a new route revision on the live route, not a hand-written row.</summary>
    private async Task RepointRouteAsync(World world, Guid profileId)
    {
        using var scope = _fixture.BeginScope();
        var routes = scope.Resolve<IStorageRouteService>();
        var current = (await routes.GetAsync(world.TeamId, world.RouteId, null, 10, CancellationToken.None)).ShouldNotBeNull();

        var appended = await routes.AppendRevisionAsync(world.TeamId, world.ActorId, new AppendStorageRouteRevisionCommand
        {
            RouteId = world.RouteId, ExpectedXmin = current.Xmin, ExpectedCurrentRevision = current.CurrentRevision,
            StorageProfileId = profileId,
        }, CancellationToken.None);

        appended.ShouldNotBeNull().CurrentTarget.StorageProfileId.ShouldBe(profileId, "the route's live revision now names the new profile");
    }

    private static int FileCountUnder(string root) =>
        Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() : 0;

    private async Task<World> SeedAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"log-switch-{actorId:N}@test.local", Name = $"log-switch-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"log-switch-{teamId:N}", Name = "Log Provider Switch Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });

        var profileA = AddProfile(db, teamId, actorId, now, _rootA, "log-switch-a");
        var profileB = AddProfile(db, teamId, actorId, now, _rootB, "log-switch-b");

        await db.SaveChangesAsync();

        // The run is a second save: EF batches inserts by entity type, not by dependency, so agent_run and team in one
        // batch trips agent_run_team_id_fkey.
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Running, TaskJson = "{}",
            FenceEpoch = WorkerFenceEpoch, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });

        await db.SaveChangesAsync();

        // Create + activate through the service, which is where the route's lifecycle rules live (a route is born Draft
        // at revision 1 and activated as a second step). Seeding the rows by hand would step around the guards that make
        // the rest of this file meaningful.
        var routes = scope.Resolve<IStorageRouteService>();
        var created = await routes.CreateAsync(teamId, actorId, new CreateStorageRouteCommand
        {
            DataClassTypeKey = AgentRunLogStorageResolver.DataClassTypeKey, StorageProfileId = profileA,
        }, CancellationToken.None);

        var activated = await routes.SetStateAsync(teamId, actorId, new SetStorageRouteStateCommand
        {
            RouteId = created.Id, ExpectedXmin = created.Xmin, ExpectedCurrentRevision = created.CurrentRevision,
            State = StorageRouteStateValue.Active,
        }, CancellationToken.None);

        activated.ShouldNotBeNull().State.ShouldBe(StorageRouteStateValue.Active);

        return new World(teamId, actorId, profileA, profileB, created.Id, runId);
    }

    private static Guid AddProfile(CodeSpaceDbContext db, Guid teamId, Guid actorId, DateTimeOffset now, string rootPath, string namePrefix)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath = Path.GetFullPath(rootPath) }));
        var canonicalConfig = StorageProfileRules.CanonicalJson(document.RootElement);
        using var canonical = JsonDocument.Parse(canonicalConfig);
        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"{namePrefix}-{Guid.NewGuid():N}",
            State = StorageProfileState.Active, CurrentRevision = 1,
            CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profile.Id, Revision = 1,
            ProviderTypeKey = ProviderTypeKey, NonSecretConfigJson = canonicalConfig, CredentialRef = null,
            NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(ProviderTypeKey, canonical.RootElement),
            CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);

        return profile.Id;
    }

    public void Dispose()
    {
        foreach (var root in new[] { _rootA, _rootB })
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid ProfileAId, Guid ProfileBId, Guid RouteId, Guid AgentRunId);
}
