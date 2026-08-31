using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Core.Settings;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// The MAIN artifact plane honouring the operator's configured storage. Drives the real <see cref="IArtifactStore"/>
/// against the real CAS runtime and the real local-rwx provider driver over a temp root, so an assertion about "the
/// bytes landed where Settings said" is an assertion about a file that is actually there.
///
/// <para>The two properties this suite exists to protect: a team with NO route for <c>workflow-artifact/v1</c> — the
/// shipped state of every existing team — is byte-identical to before, and a routed read resolves the profile
/// revision its own location was stamped with rather than whatever the route points at today.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactStoreRoutedDestinationFlowTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];
    private readonly List<Guid> _destinations = [];

    public ArtifactStoreRoutedDestinationFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_unrouted_team_keeps_the_local_backend_and_its_exact_storage_url_shape()
    {
        // The compatibility pin. Every deployed team is in this state, and this is the hot path for patches,
        // manifests and model-call bodies: a change in the storage_url shape strands every row that already has one.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('u', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);

        var artifactId = await PutAsync(teamId, content);

        var row = await RowAsync(artifactId);
        row.InlineBytes.ShouldBeNull();
        row.CasArtifactObjectId.ShouldBeNull("an unrouted team must not acquire a CAS pointer");
        row.StorageUrl.ShouldBe(LocalUrlFor(sha), "the local backend's file:// shape is what every pre-existing row records");
        File.Exists(new Uri(row.StorageUrl!).LocalPath).ShouldBeTrue();

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().ArtifactObject.CountAsync(o => o.TeamId == teamId)).ShouldBe(0,
            "no route means no CAS traffic at all — not even a transfer intent");
        (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None))!.Bytes.ShouldBe(content);
    }

    [Fact]
    public async Task An_unrouted_team_still_decides_inline_versus_offload_by_size_alone()
    {
        // Routing changes WHERE an offloaded blob goes, never WHETHER it is offloaded.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var small = Encoding.UTF8.GetBytes("small enough to stay in the row");

        var row = await RowAsync(await PutAsync(teamId, small));

        row.InlineBytes.ShouldBe(small);
        row.StorageUrl.ShouldBeNull();
        row.CasArtifactObjectId.ShouldBeNull();
    }

    [Fact]
    public async Task A_routed_team_places_the_bytes_through_the_coordinator_and_stamps_the_exact_profile_revision()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        var content = Encoding.UTF8.GetBytes(new string('r', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);

        var artifactId = await PutAsync(teamId, content);

        var row = await RowAsync(artifactId);
        row.InlineBytes.ShouldBeNull();
        row.StorageUrl.ShouldBeNull("a routed row must not also claim a local locator");
        row.CasArtifactObjectId.ShouldNotBeNull();
        File.Exists(ObjectPath(root, sha)).ShouldBeTrue(
            "the operator configured this root; the bytes have to actually be under it");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(l => l.TeamId == teamId && l.ArtifactObjectId == row.CasArtifactObjectId);
        location.State.ShouldBe(ArtifactLocationState.Available);
        var revision = await db.StorageProfileRevision.SingleAsync(r => r.Id == location.StorageProfileRevisionId);
        revision.StorageProfileId.ShouldBe(profileId);
        revision.Revision.ShouldBe(1, "the location carries the exact revision the write was authorised against");

        (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None))!.Bytes.ShouldBe(content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_large_reopenable_stream_uses_bounded_reads_and_preserves_each_destinations_exact_shape(bool routed)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var routedRoot = NewRoot();
        if (routed) await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, routedRoot));
        var content = Encoding.UTF8.GetBytes(new string(routed ? 'r' : 'l', 600_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var source = new ReopenableByteSource(content);

        var artifactId = await PutStreamAsync(teamId, source);

        source.OpenCount.ShouldBe(2, "one bounded identity pass and one placement pass are sufficient on a healthy destination");
        source.LargestReadRequest.ShouldBeLessThanOrEqualTo(128 * 1024,
            "neither routing arm may turn the stream back into one payload-sized read request");
        var row = await RowAsync(artifactId);
        if (routed)
        {
            row.CasArtifactObjectId.ShouldNotBeNull();
            row.StorageUrl.ShouldBeNull();
            File.Exists(ObjectPath(routedRoot, sha)).ShouldBeTrue();
        }
        else
        {
            row.CasArtifactObjectId.ShouldBeNull();
            row.StorageUrl.ShouldBe(LocalUrlFor(sha), "the additive contract must keep the legacy local locator byte-identical");
        }

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None))!.Bytes.ShouldBe(content);
    }

    [Fact]
    public async Task A_small_reopenable_stream_stays_inline_and_is_opened_only_for_identity_admission()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes("small stream remains in the workflow_artifact row");
        var source = new ReopenableByteSource(content);

        var row = await RowAsync(await PutStreamAsync(teamId, source));

        source.OpenCount.ShouldBe(1);
        row.InlineBytes.ShouldBe(content);
        row.StorageUrl.ShouldBeNull();
        row.CasArtifactObjectId.ShouldBeNull();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task A_reopenable_stream_with_a_false_declared_length_is_refused_before_routing_or_metadata(int lengthDelta)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('x', 300_000));
        var source = new ReopenableByteSource(content, content.LongLength + lengthDelta);

        await Should.ThrowAsync<InvalidDataException>(() => PutStreamAsync(teamId, source));

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.CountAsync(candidate => candidate.TeamId == teamId)).ShouldBe(0);
        (await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.CountAsync(candidate => candidate.TeamId == teamId)).ShouldBe(0,
            "identity admission must finish before destination resolution or a CAS effect intent");
    }

    [Fact]
    public async Task A_deferred_routed_stream_reopens_fresh_content_for_each_attempt_and_finishes_the_same_intent()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        var content = Encoding.UTF8.GetBytes(new string('w', 300_000));
        var source = new ReopenableByteSource(content);
        var intentId = await SeedLeasedIntentAsync(teamId, profileId, content, TimeSpan.FromSeconds(3));

        var artifactId = await PutStreamAsync(teamId, source);

        source.OpenCount.ShouldBeGreaterThan(2,
            "the identity pass is one open; every deferred/claimed transfer attempt must own a newly opened stream rather than seek or retain an earlier one");
        (await RowAsync(artifactId)).CasArtifactObjectId.ShouldNotBeNull();
        using var scope = _fixture.BeginScope();
        var intent = await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.AsNoTracking().SingleAsync(candidate => candidate.TeamId == teamId);
        intent.Id.ShouldBe(intentId);
        intent.State.ShouldBe(ArtifactTransferState.Committed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sealed_agent_transcript_streams_to_local_or_routed_storage_with_the_same_identity(bool routed)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var routedRoot = NewRoot();
        if (routed) await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, routedRoot));
        var line = new string(routed ? 't' : 's', 300_000);
        var expected = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        await using var transcript = new AgentTranscriptSpool(NewRoot(), 16);
        await transcript.AppendLineAsync(line, CancellationToken.None);
        await transcript.SealAsync(CancellationToken.None);

        var artifactId = await PutStreamAsync(teamId, transcript);

        transcript.OpenReadCount.ShouldBe(2, "one identity open and one healthy placement open are sufficient for the sealed spill");
        var row = await RowAsync(artifactId);
        row.Sha256.ShouldBe(ArtifactStore.ComputeSha256Hex(expected));
        row.SizeBytes.ShouldBe(expected.LongLength);
        if (routed)
        {
            row.CasArtifactObjectId.ShouldNotBeNull();
            row.StorageUrl.ShouldBeNull();
            File.Exists(ObjectPath(routedRoot, row.Sha256)).ShouldBeTrue();
        }
        else
        {
            row.CasArtifactObjectId.ShouldBeNull();
            row.StorageUrl.ShouldBe(LocalUrlFor(row.Sha256));
        }

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None))!.Bytes.ShouldBe(expected);
    }

    [Fact]
    public async Task A_deferred_routed_agent_transcript_reopens_its_sealed_spill_for_every_attempt()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        var line = new string('f', 300_000);
        var expected = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        await using var transcript = new AgentTranscriptSpool(NewRoot(), 16);
        await transcript.AppendLineAsync(line, CancellationToken.None);
        await transcript.SealAsync(CancellationToken.None);
        var intentId = await SeedLeasedIntentAsync(teamId, profileId, expected, TimeSpan.FromSeconds(3));

        var artifactId = await PutStreamAsync(teamId, transcript);

        transcript.OpenReadCount.ShouldBeGreaterThan(2,
            "the sealed file source must return a fresh stream after each Deferred result; retaining or seeking one handle is not retry-safe");
        (await RowAsync(artifactId)).CasArtifactObjectId.ShouldNotBeNull();
        using var scope = _fixture.BeginScope();
        var intent = await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.AsNoTracking().SingleAsync(candidate => candidate.TeamId == teamId);
        intent.Id.ShouldBe(intentId);
        intent.State.ShouldBe(ArtifactTransferState.Committed);
    }

    [Fact]
    public async Task A_read_resolves_the_location_it_recorded_even_after_the_route_is_repointed()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var originalRoot = NewRoot();
        var originalProfileId = await SeedProfileAsync(teamId, originalRoot);
        await SeedRouteAsync(teamId, originalProfileId);
        var content = Encoding.UTF8.GetBytes(new string('h', 30_000));
        var artifactId = await PutAsync(teamId, content);

        var replacementRoot = NewRoot();
        await RepointRouteAsync(teamId, await SeedProfileAsync(teamId, replacementRoot));

        ArtifactBytes? fetched;
        using (var scope = _fixture.BeginScope())
            fetched = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None);

        fetched!.Bytes.ShouldBe(content, "the read must follow the artifact's own recorded location, not current policy");
        Directory.Exists(Path.Combine(replacementRoot, "objects")).ShouldBeFalse(
            "if the read had consulted the CURRENT route it would have looked here — and these bytes were never written here");
    }

    [Fact]
    public async Task A_route_created_but_never_activated_leaves_every_offloaded_write_on_local_disk()
    {
        // The regression this test exists to prevent: Settings creates every route in Draft, the snapshot resolver
        // reported it as simply non-Active, the destination resolver refused it, and PlaceOffloadedAsync threw — so
        // pressing "Create data route" stopped every offloaded write for the team until someone also activated it.
        // Draft means "not cut over yet", so it has to be as inert as having no route at all.
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var profileId = await SeedProfileAsync(teamId, NewRoot());
        var route = await CreateDraftRouteAsync(teamId, actorId, profileId);
        route.State.ShouldBe(StorageRouteStateValue.Draft, "precondition: the real create path is what puts a route in Draft");
        var content = Encoding.UTF8.GetBytes(new string('n', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);

        var artifactId = await PutAsync(teamId, content);

        var row = await RowAsync(artifactId);
        row.InlineBytes.ShouldBeNull($"{content.Length} bytes is over the {ArtifactStoreConfig.InlineThresholdBytes}-byte inline threshold, so this write must be offloaded");
        row.CasArtifactObjectId.ShouldBeNull("a route nobody activated must not route bytes");
        row.StorageUrl.ShouldBe(LocalUrlFor(sha), "an un-activated route keeps the local backend's exact locator shape");
        File.Exists(new Uri(row.StorageUrl!).LocalPath).ShouldBeTrue();

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.CountAsync(i => i.TeamId == teamId)).ShouldBe(0);
        (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None))!.Bytes.ShouldBe(content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_destination_that_cannot_take_bytes_fails_the_write_closed_instead_of_using_local_disk(bool disableRoute)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        if (disableRoute) await SetRouteStateAsync(teamId, StorageRouteState.Disabled);
        else await SetProfileStateAsync(teamId, profileId, StorageProfileState.Disabled);
        var content = Encoding.UTF8.GetBytes(new string('f', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);

        var exception = await Should.ThrowAsync<ArtifactStorageDestinationUnavailableException>(() => PutAsync(teamId, content));

        exception.RoutingProblem.ShouldBe(disableRoute
            ? WorkflowArtifactDestinationProblem.RouteNotActive
            : WorkflowArtifactDestinationProblem.ProfileNotActive);
        File.Exists(new Uri(LocalUrlFor(sha)).LocalPath).ShouldBeFalse(
            "a silent fallback to local disk is exactly the dishonesty this plane exists to remove");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowArtifact.CountAsync(a => a.TeamId == teamId)).ShouldBe(0, "no row may claim bytes that were never placed");
        (await db.ArtifactTransferIntent.CountAsync(i => i.TeamId == teamId)).ShouldBe(0, "routing refuses before any provider effect is minted");
    }

    [Fact]
    public async Task Existing_local_artifacts_stay_readable_after_the_team_adopts_a_route()
    {
        // A read must never require a route to exist, and must never be re-pointed by one appearing later.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('l', 20_000));
        var artifactId = await PutAsync(teamId, content);

        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, NewRoot()));

        using var scope = _fixture.BeginScope();
        var fetched = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None);

        fetched!.Bytes.ShouldBe(content);
        (await RowAsync(artifactId)).StorageUrl.ShouldNotBeNull("the pre-route row keeps its local locator forever");
    }

    [Fact]
    public async Task Routed_dedup_returns_the_same_id_and_never_restores_the_object_onto_local_disk()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('d', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var first = await PutAsync(teamId, content);

        var second = await PutAsync(teamId, content);

        second.ShouldBe(first, "(team, sha) dedup is unchanged by the destination");
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowArtifact.CountAsync(a => a.TeamId == teamId)).ShouldBe(1);
            (await db.ArtifactObject.CountAsync(o => o.TeamId == teamId)).ShouldBe(1);
            (await db.ArtifactTransferIntent.CountAsync(i => i.TeamId == teamId)).ShouldBe(1, "the dedup hit short-circuits before any second transfer");
        }

        // The local backend's self-healing restore exists because it has no location ledger. A routed row has no
        // storage_url, so it must never trigger — healing routed bytes onto local disk would be the silent fallback.
        File.Delete(ObjectPath(root, sha));

        (await PutAsync(teamId, content)).ShouldBe(first);
        File.Exists(new Uri(LocalUrlFor(sha)).LocalPath).ShouldBeFalse();

        using var readScope = _fixture.BeginScope();
        var failure = await Should.ThrowAsync<ArtifactContentUnavailableException>(
            () => readScope.Resolve<IArtifactStore>().GetBytesAsync(teamId, first, CancellationToken.None));
        failure.Kind.ShouldBe(ArtifactContentUnavailableKind.PhysicalObjectMissing,
            "a missing routed object is reported as a typed storage fact, never as empty content");
    }

    [Fact]
    public async Task Routed_dedup_never_returns_a_row_while_its_only_location_is_claimed_for_deletion()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, NewRoot()));
        var content = Encoding.UTF8.GetBytes(new string('q', 20_000));
        var first = await PutAsync(teamId, content);
        using (var claimScope = _fixture.BeginScope())
        {
            var db = claimScope.Resolve<CodeSpaceDbContext>();
            var location = await db.ArtifactLocation.SingleAsync(value => value.TeamId == teamId);
            await RecordLocationStateAsync(db, location, ArtifactLocationState.Deleting);
        }

        var failure = await Should.ThrowAsync<ArtifactStorageDestinationUnavailableException>(() => PutAsync(teamId, content));

        failure.TransferProblem.ShouldNotBeNull("a write may retry after the purge settles, but it must never receive the id whose bytes are being removed");
        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowArtifact.CountAsync(value => value.TeamId == teamId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_post_claim_manifest_recapture_cannot_obtain_the_candidate_id_without_passing_location_admission()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, NewRoot()));
        var content = Encoding.UTF8.GetBytes(new string('g', 20_000));
        Guid artifactId;
        Guid objectId;
        using (var firstScope = _fixture.BeginScope())
        {
            var write = await firstScope.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(new ArtifactRetentionWriteRequest(
                teamId, content, "application/octet-stream", ArtifactRetentionClass.ArtifactManifestContent, "artifact_manifest", actorId), CancellationToken.None);
            write.Declared.ShouldBeTrue();
            artifactId = write.ArtifactId;
            objectId = (await firstScope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(value => value.Id == artifactId)).CasArtifactObjectId!.Value;
        }

        ArtifactCasPurgeClaim physical;
        using (var claimScope = _fixture.BeginScope())
        {
            physical = (await claimScope.Resolve<IArtifactCasPurgeCoordinator>().ClaimAsync(new ArtifactCasPurgeRequest
            {
                TeamId = teamId, ArtifactObjectId = objectId, ActorId = actorId,
            }, CancellationToken.None)).ShouldBeOfType<ArtifactCasPurgeClaimResult.Claimed>().Claim;
        }

        using (var recaptureScope = _fixture.BeginScope())
        {
            var failure = await Should.ThrowAsync<ArtifactStorageDestinationUnavailableException>(() =>
                recaptureScope.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(new ArtifactRetentionWriteRequest(
                    teamId, content, "application/octet-stream", ArtifactRetentionClass.ArtifactManifestContent, "artifact_manifest", actorId), CancellationToken.None));
            failure.TransferProblem.ShouldNotBeNull("the sole production holder writer must reacquire through Put/dedup, and Deleting is not an admissible id");
        }

        using (var verifyScope = _fixture.BeginScope())
        {
            var db = verifyScope.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowArtifact.AsNoTracking().SingleAsync(value => value.TeamId == teamId)).Id.ShouldBe(artifactId);
            (await db.WorkflowArtifactRetention.AsNoTracking().SingleAsync(value => value.ArtifactId == artifactId)).State.ShouldBe(ArtifactRetentionState.Declared);
            (await db.ArtifactLocation.AsNoTracking().SingleAsync(value => value.TeamId == teamId)).State.ShouldBe(ArtifactLocationState.Deleting);
        }
        using var releaseScope = _fixture.BeginScope();
        (await releaseScope.Resolve<IArtifactCasPurgeCoordinator>().ReleaseAsync(physical, ArtifactCasReleaseEvidence.Untouched, CancellationToken.None)).ShouldBe(ArtifactCasReleaseOutcome.Released);
    }

    [Fact]
    public async Task A_purged_routed_object_can_be_stored_again_and_read_through_its_recorded_revision()
    {
        // The end-to-end a routed purge needs to exist at all. Reclaiming the bytes without this is data loss with
        // extra steps: the object key can hold no second location row (ux_artifact_location_profile_object_key) and
        // the first write's intent is Committed forever (0131), so unless the purged row is revivable under a fresh
        // generation, that content is unstorable for this team under this profile revision for good.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('p', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var first = await PutAsync(teamId, content);

        await PurgeRoutedObjectAsync(teamId, first, root, sha);

        var again = await PutAsync(teamId, content);

        again.ShouldNotBe(first, "the purge took the pointing row with the bytes, so this write owns a new one");
        (await File.ReadAllBytesAsync(ObjectPath(root, sha))).ShouldBe(content, "the bytes are back on the operator's configured storage");
        await AssertIntentsAsync(teamId, [
            (IntentKeyFor(sha, 0), ArtifactTransferState.Committed),
            (IntentKeyFor(sha, 1), ArtifactTransferState.Committed),
        ]);

        using var scope = _fixture.BeginScope();
        var fetched = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, again, CancellationToken.None);
        fetched!.Bytes.ShouldBe(content, "the read resolves the revision the revived location records");
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.AsNoTracking().SingleAsync(l => l.TeamId == teamId);
        location.State.ShouldBe(ArtifactLocationState.Available, "the purged row was revived; a second row for this key is not allowed");
        (await db.ArtifactObject.CountAsync(o => o.TeamId == teamId)).ShouldBe(1, "artifact_object rows can never be deleted, so the same content keeps the same object");
    }

    /// <summary>
    /// The exit a placement whose destination stopped serving it never had, through the entry point a workflow
    /// actually writes with. A <c>Missing</c> row was unreadable and stayed unreadable however often a run re-emitted
    /// the identical payload, because a producer cannot vary the one thing that decides this: the intent scope is the
    /// content's own sha, so every re-presentation lands on the first write's <c>Committed</c> intent, and that intent
    /// was answered from the ledger — <c>TargetMissing</c>, before a byte of provider I/O — for every location state
    /// except <c>Available</c>.
    /// </summary>
    [Fact]
    public async Task A_missing_placement_is_put_back_by_the_producer_that_re_presents_the_same_bytes()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('m', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var first = await PutAsync(teamId, content);
        File.Delete(ObjectPath(root, sha));
        var lost = await DemoteLocationAsync(teamId, ArtifactLocationState.Missing);

        var again = await PutAsync(teamId, content);

        again.ShouldBe(first, "the placement is back in service, so dedup answers with the row that already names this content");
        (await File.ReadAllBytesAsync(ObjectPath(root, sha))).ShouldBe(content,
            "the object was gone, so only a real re-upload onto the operator's storage could have put it back");
        await AssertIntentsAsync(teamId, [(IntentKeyFor(sha, 0), ArtifactTransferState.Committed)]);

        using var scope = _fixture.BeginScope();
        var location = await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking().SingleAsync(value => value.TeamId == teamId);
        location.State.ShouldBe(ArtifactLocationState.Available);
        location.Revision.ShouldBe(lost.Revision + 1, "one observation, appended to the one durable placement identity");
        location.LastErrorCode.ShouldBeNull("a placement back in service must not keep advertising the error it no longer has");
        (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, first, CancellationToken.None))!.Bytes.ShouldBe(content);
    }

    /// <summary>
    /// The first exit <c>Corrupt</c> has ever had, staged the ONE way production reaches that state: the destination
    /// is caught holding an object that is not this artifact, and <see cref="ArtifactLocationVerifier"/> — its only
    /// writer — records the disagreement. Nothing could return such a placement to service afterwards: the sweep
    /// deliberately never re-examines a <c>Corrupt</c> row, and every re-presentation of the content was refused by
    /// the ledger before it reached the provider.
    ///
    /// <para>The demotion writes state, revision and error and nothing else, so the recorded observation a genuine
    /// <c>Corrupt</c> row carries is the CORRECT one — asserted below, because it is what makes this repair an
    /// overwrite rather than a re-record. The object is present at the key, so a revival that HEADs first and uploads
    /// only when the destination reports nothing there would skip its upload entirely, fail its digest on the foreign
    /// bytes, and refuse — leaving the artifact unreadable with somebody else's bytes still being served.</para>
    /// </summary>
    [Fact]
    public async Task A_corrupt_placement_is_put_back_by_the_producer_that_re_presents_the_same_bytes()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('x', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var first = await PutAsync(teamId, content);
        await File.WriteAllBytesAsync(ObjectPath(root, sha), Encoding.UTF8.GetBytes(new string('z', 19_000)));

        var lost = await VerifyUntilAsync(teamId, ArtifactLocationState.Corrupt);

        lost.LastErrorCode.ShouldBe("location-object-mismatch", "the sweep is the only writer of Corrupt, and only a positive disagreement about the object gets it there");
        lost.ObservedSizeBytes.ShouldBe(content.LongLength, "a demotion records the disagreement in state and error alone, so the observation it was taken against stays true");
        lost.ProviderChecksum.ShouldBe(Convert.FromHexString(sha), "which is what the revival's whole difficulty is: the record is right and the DESTINATION is wrong");
        (await ReadFailureAsync(teamId, first)).Kind.ShouldBe(ArtifactContentUnavailableKind.PhysicalObjectMissing,
            "precondition: a Corrupt placement is off the read path entirely, which is the state this test exists to get the artifact out of");

        var again = await PutAsync(teamId, content);

        again.ShouldBe(first);
        (await File.ReadAllBytesAsync(ObjectPath(root, sha))).ShouldBe(content,
            "the key was holding a foreign object, so only overwriting it could put this artifact back");
        await AssertIntentsAsync(teamId, [(IntentKeyFor(sha, 0), ArtifactTransferState.Committed)]);

        using var scope = _fixture.BeginScope();
        var location = await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking().SingleAsync(value => value.TeamId == teamId);
        location.State.ShouldBe(ArtifactLocationState.Available);
        location.Revision.ShouldBe(lost.Revision + 1, "one observation, appended to the one durable placement identity");
        location.LastErrorCode.ShouldBeNull("a placement back in service must not keep advertising the error it no longer has");
        (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, first, CancellationToken.None))!.Bytes.ShouldBe(content,
            "and the artifact must read its ORIGINAL content again, not merely be marked healthy");
    }

    /// <summary>
    /// What a LOSING reviver leaves behind, now that the <c>Corrupt</c> repair is the first CAS write that rewrites an
    /// object a COMMITTED placement already records. Two producers revive one broken placement: the winner overwrites,
    /// verifies, and commits the provider conditions its readback reported, and the loser's unconditional PUT of the
    /// SAME bytes lands after that. The loser's own commit is refused — pinned separately in
    /// <c>ArtifactCasRuntimeCoordinatorTests</c> — so its PUT is the whole of what it leaves, and this asks what still
    /// depends on the conditions it made stale.
    ///
    /// <para>Nothing does, and this destination is what makes the question real rather than theoretical: the local
    /// driver derives its ETag from the file's modification time, so the rewrite genuinely changes it and the winner's
    /// recorded value is genuinely stale for the rest of the placement's life. That is asserted before anything else,
    /// because a rewrite that happened to reproduce the ETag would make every assertion after it vacuous.</para>
    ///
    /// <para>The read survives because a recorded ETag is compared only when its provider declares <c>StableETag</c> —
    /// content-derived — which this one does not; the sweep survives because it compares content-derived evidence
    /// alone. The remaining fence, <c>provider_object_version</c>, has nothing to invalidate: no shipped module
    /// declares <c>ObjectVersioning</c> and the driver conformance kit refuses a version from one that does not, so
    /// the column is never populated.</para>
    /// </summary>
    [Fact]
    public async Task A_losing_revivers_overwrite_leaves_the_winners_placement_readable_and_verifiable()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        var content = Encoding.UTF8.GetBytes(new string('r', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var artifactId = await PutAsync(teamId, content);
        await File.WriteAllBytesAsync(ObjectPath(root, sha), Encoding.UTF8.GetBytes(new string('s', 19_000)));
        await VerifyUntilAsync(teamId, ArtifactLocationState.Corrupt);
        (await PutAsync(teamId, content)).ShouldBe(artifactId);
        var winner = await PlacementAsync(teamId);
        winner.State.ShouldBe(ArtifactLocationState.Available, "precondition: the winning reviver overwrote the foreign object and committed its own readback");
        winner.ProviderETag.ShouldNotBeNullOrWhiteSpace("and recorded the provider conditions that readback reported");

        var rewritten = await OverwriteThroughDriverAsync(teamId, profileId, ObjectKeyFor(sha), content);

        rewritten.ShouldNotBe(winner.ProviderETag, "this destination derives its ETag from the file's mtime, so a rewrite of identical bytes really does change it — without that, nothing below is being tested at all");
        var stale = await PlacementAsync(teamId);
        stale.Revision.ShouldBe(winner.Revision, "nothing re-observes a committed placement, so the row keeps the conditions the winner read BEFORE the loser's write");
        stale.ProviderETag.ShouldBe(winner.ProviderETag);

        using (var scope = _fixture.BeginScope())
        {
            (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None))!.Bytes.ShouldBe(content,
                "a stale ETag must not cost the artifact its readability: a recorded one is compared only from a provider that promises it identifies the bytes, and this one does not");
        }

        var swept = await VerifyUntilObservedAsync(teamId, winner.Revision);

        swept.State.ShouldBe(ArtifactLocationState.Available, "and the sweep has to confirm it rather than demote it — a verifier that compared ETags would mark every rewritten object Corrupt");
        swept.LastErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task Retention_reaps_routed_bytes_and_the_pointing_row_then_the_same_content_can_be_stored_again()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('z', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        Guid artifactId;
        using (var writeScope = _fixture.BeginScope())
        {
            var write = await writeScope.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(new ArtifactRetentionWriteRequest(
                teamId, content, "application/octet-stream", ArtifactRetentionClass.ArtifactManifestContent, "artifact_manifest", actorId), CancellationToken.None);
            write.Declared.ShouldBeTrue("a routed declared write has a physical purge path now and must enter the positive retention ledger");
            artifactId = write.ArtifactId;
        }
        await AgeRoutedDeclarationAsync(artifactId);

        using (var firstScope = _fixture.BeginScope())
            (await firstScope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None)).Quarantined.ShouldBeGreaterThanOrEqualTo(1);
        File.Exists(ObjectPath(root, sha)).ShouldBeTrue("the first unreferenced observation only opens quarantine");
        await AgeRoutedQuarantineAsync(artifactId);
        using (var secondScope = _fixture.BeginScope())
            (await secondScope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None)).Collected.ShouldBeGreaterThanOrEqualTo(1);

        File.Exists(ObjectPath(root, sha)).ShouldBeFalse();
        using (var verifyScope = _fixture.BeginScope())
        {
            var db = verifyScope.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowArtifact.AnyAsync(value => value.TeamId == teamId && value.Id == artifactId)).ShouldBeFalse(
                "leaving the pointing row would make dedup return an id whose bytes were purged");
            (await db.WorkflowArtifactRetention.AnyAsync(value => value.TeamId == teamId && value.ArtifactId == artifactId)).ShouldBeFalse();
            var location = await db.ArtifactLocation.AsNoTracking().SingleAsync(value => value.TeamId == teamId);
            location.State.ShouldBe(ArtifactLocationState.Purged);
            location.Revision.ShouldBe(3);
            (await db.ArtifactObject.CountAsync(value => value.TeamId == teamId)).ShouldBe(1, "the CAS object is the permanent content tombstone");
        }

        var rewrittenId = await PutAsync(teamId, content);

        rewrittenId.ShouldNotBe(artifactId);
        (await File.ReadAllBytesAsync(ObjectPath(root, sha))).ShouldBe(content);
        using var readScope = _fixture.BeginScope();
        (await readScope.Resolve<IArtifactStore>().GetBytesAsync(teamId, rewrittenId, CancellationToken.None))!.Bytes.ShouldBe(content);
        var revived = await readScope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking().SingleAsync(value => value.TeamId == teamId);
        revived.State.ShouldBe(ArtifactLocationState.Available);
        revived.Revision.ShouldBe(4, "the rewrite revives the one durable location identity rather than leaking another row");
    }

    [Fact]
    public async Task A_reference_committed_after_the_location_claim_is_seen_before_provider_delete_and_releases_the_location()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = await SeedAgentRunAsync(teamId, actorId);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('y', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        Guid artifactId;
        using (var writeScope = _fixture.BeginScope())
        {
            var write = await writeScope.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(new ArtifactRetentionWriteRequest(
                teamId, content, "application/octet-stream", ArtifactRetentionClass.ArtifactManifestContent, "artifact_manifest", agentRunId), CancellationToken.None);
            write.Declared.ShouldBeTrue();
            artifactId = write.ArtifactId;
        }
        await AgeRoutedDeclarationAsync(artifactId);
        using (var quarantineScope = _fixture.BeginScope())
            await quarantineScope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None);
        await AgeRoutedQuarantineAsync(artifactId);

        ArtifactRetentionSweepSummary summary;
        using (var sweepScope = _fixture.BeginScope())
        {
            var routed = new ReferenceAfterClaimPurgeCoordinator(sweepScope.Resolve<IArtifactCasPurgeCoordinator>(),
                token => ReferenceFromAgentRunAsync(agentRunId, artifactId, token));
            var reaper = new ArtifactRetentionReaper(sweepScope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), sweepScope.Resolve<IArtifactReferenceOracle>(),
                sweepScope.Resolve<IArtifactBlobBackend>(), routed, NullLogger<ArtifactRetentionReaper>.Instance);
            summary = await reaper.SweepAsync(CancellationToken.None);
        }

        summary.Referenced.ShouldBeGreaterThanOrEqualTo(1);
        File.Exists(ObjectPath(root, sha)).ShouldBeTrue("the post-claim reference must stop deletion before provider I/O");
        using var verifyScope = _fixture.BeginScope();
        var db = verifyScope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowArtifact.AnyAsync(value => value.Id == artifactId)).ShouldBeTrue();
        (await db.WorkflowArtifactRetention.SingleAsync(value => value.ArtifactId == artifactId)).State.ShouldBe(ArtifactRetentionState.Referenced);
        var location = await db.ArtifactLocation.AsNoTracking().SingleAsync(value => value.TeamId == teamId);
        location.State.ShouldBe(ArtifactLocationState.Available, "a stopped delete must release its physical claim");
        location.Revision.ShouldBe(3, "Available -> Deleting -> Available are two durable, event-backed transitions");
    }

    [Fact]
    public async Task A_sweep_recovers_when_routed_bytes_were_purged_before_the_pointing_row_commit()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('c', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        Guid artifactId;
        Guid objectId;
        using (var writeScope = _fixture.BeginScope())
        {
            var write = await writeScope.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(new ArtifactRetentionWriteRequest(
                teamId, content, "application/octet-stream", ArtifactRetentionClass.ArtifactManifestContent, "artifact_manifest", actorId), CancellationToken.None);
            write.Declared.ShouldBeTrue();
            artifactId = write.ArtifactId;
            objectId = (await writeScope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(value => value.Id == artifactId)).CasArtifactObjectId!.Value;
        }
        await AgeRoutedDeclarationAsync(artifactId);
        using (var quarantineScope = _fixture.BeginScope())
            await quarantineScope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None);
        await AgeRoutedQuarantineAsync(artifactId);

        using (var purgeScope = _fixture.BeginScope())
        {
            var purge = purgeScope.Resolve<IArtifactCasPurgeCoordinator>();
            var claimed = (await purge.ClaimAsync(new ArtifactCasPurgeRequest
            {
                TeamId = teamId, ArtifactObjectId = objectId, ActorId = actorId,
            }, CancellationToken.None)).ShouldBeOfType<ArtifactCasPurgeClaimResult.Claimed>();
            (await purge.DeleteAsync(claimed.Claim, CancellationToken.None)).ShouldBeOfType<ArtifactCasPurgeResult.Purged>();
        }
        File.Exists(ObjectPath(root, sha)).ShouldBeFalse("precondition: the provider effect and Purged observation committed");
        (await RowAsync(artifactId)).Id.ShouldBe(artifactId, "precondition: the metadata transaction was the crashed phase");

        using (var recoveryScope = _fixture.BeginScope())
            (await recoveryScope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None)).Collected.ShouldBeGreaterThanOrEqualTo(1);

        using var verifyScope = _fixture.BeginScope();
        var db = verifyScope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowArtifact.AnyAsync(value => value.Id == artifactId)).ShouldBeFalse();
        (await db.WorkflowArtifactRetention.AnyAsync(value => value.ArtifactId == artifactId)).ShouldBeFalse();
        (await db.ArtifactLocation.AsNoTracking().SingleAsync(value => value.TeamId == teamId)).State.ShouldBe(ArtifactLocationState.Purged);
    }

    [Fact]
    public async Task An_uncertain_provider_delete_stays_live_and_the_next_sweep_reconciles_its_deleting_claim()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('v', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        Guid artifactId;
        using (var writeScope = _fixture.BeginScope())
        {
            var write = await writeScope.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(new ArtifactRetentionWriteRequest(
                teamId, content, "application/octet-stream", ArtifactRetentionClass.ArtifactManifestContent, "artifact_manifest", actorId), CancellationToken.None);
            write.Declared.ShouldBeTrue();
            artifactId = write.ArtifactId;
        }
        await AgeRoutedDeclarationAsync(artifactId);
        using (var quarantineScope = _fixture.BeginScope())
            await quarantineScope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None);
        await AgeRoutedQuarantineAsync(artifactId);

        using (var uncertainScope = _fixture.BeginScope())
        {
            var routed = new UncertainDeletePurgeCoordinator(uncertainScope.Resolve<IArtifactCasPurgeCoordinator>());
            var reaper = new ArtifactRetentionReaper(uncertainScope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), uncertainScope.Resolve<IArtifactReferenceOracle>(),
                uncertainScope.Resolve<IArtifactBlobBackend>(), routed, NullLogger<ArtifactRetentionReaper>.Instance);
            (await reaper.SweepAsync(CancellationToken.None)).Retried.ShouldBeGreaterThanOrEqualTo(1);
            routed.ReleaseCalls.ShouldBe(0, "an effect-uncertain result must never be relabeled Available");
        }

        File.Exists(ObjectPath(root, sha)).ShouldBeTrue("the fake timeout happened before an effect, but the caller cannot assume that");
        using (var observeScope = _fixture.BeginScope())
        {
            var db = observeScope.Resolve<CodeSpaceDbContext>();
            (await db.ArtifactLocation.AsNoTracking().SingleAsync(value => value.TeamId == teamId)).State.ShouldBe(ArtifactLocationState.Deleting);
            var retention = await db.WorkflowArtifactRetention.AsNoTracking().SingleAsync(value => value.ArtifactId == artifactId);
            retention.State.ShouldBe(ArtifactRetentionState.Quarantined, "the physical recovery still needs a live queue entry");
            retention.AttemptCount.ShouldBe(0, "uncertain effects cannot exhaust into a terminal keep with Deleting stranded");
        }
        await AgeRoutedQuarantineAsync(artifactId);

        using (var recoveryScope = _fixture.BeginScope())
            (await recoveryScope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None)).Collected.ShouldBeGreaterThanOrEqualTo(1);

        File.Exists(ObjectPath(root, sha)).ShouldBeFalse();
        using var verifyScope = _fixture.BeginScope();
        (await verifyScope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AnyAsync(value => value.Id == artifactId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_placement_claimed_from_an_orphaned_marker_spends_the_budget_instead_of_waiting_for_a_race_that_never_comes()
    {
        // Releasing a claim taken from a Deleting marker can establish NOTHING — the marker IS the claim — so the
        // sweep's hand-back always fails on this placement. Reported as a race it becomes an UNBUDGETED wait, and the
        // declaration is re-claimed every retry delay for good: the placement's revision climbs on every pass and the
        // row never reaches a terminal state. The budget is what turns a permanent failure into an ending.
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('k', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        Guid artifactId;
        Guid objectId;
        using (var writeScope = _fixture.BeginScope())
        {
            var write = await writeScope.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(new ArtifactRetentionWriteRequest(
                teamId, content, "application/octet-stream", ArtifactRetentionClass.ArtifactManifestContent, "artifact_manifest", actorId), CancellationToken.None);
            write.Declared.ShouldBeTrue();
            artifactId = write.ArtifactId;
            objectId = (await writeScope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(value => value.Id == artifactId)).CasArtifactObjectId!.Value;
        }
        await AgeRoutedDeclarationAsync(artifactId);
        using (var quarantineScope = _fixture.BeginScope())
            await quarantineScope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None);
        await AgeRoutedQuarantineAsync(artifactId);

        var orphaned = await OrphanThePhysicalClaimAsync(teamId, objectId, actorId);

        using var sweepScope = _fixture.BeginScope();
        var reaper = BudgetedReaper(sweepScope, new RefusedDeletePurgeCoordinator(sweepScope.Resolve<IArtifactCasPurgeCoordinator>()), attempts: 2);

        var spent = await SweepUntilSettledAsync(reaper, artifactId, fromAttempt: 0);

        spent.AttemptCount.ShouldBe(1, "a release this path can never complete must SPEND an attempt, not wait out a race that cannot happen");
        spent.LastErrorCode.ShouldBe("artifact-routed-release-orphaned-claim");
        spent.LastErrorMessage.ShouldContain(nameof(ProfileAbandonmentService), Case.Sensitive, "the settlement has to name the only exit this placement has left");
        spent.State.ShouldBe(ArtifactRetentionState.Quarantined, "one spent attempt is not the end of the budget");

        var exhausted = await SweepUntilSettledAsync(reaper, artifactId, fromAttempt: spent.AttemptCount);

        exhausted.State.ShouldBe(ArtifactRetentionState.Indeterminate, "with the budget gone the declaration must settle as a terminal keep rather than be re-claimed forever");
        exhausted.LastErrorCode.ShouldBe("retention-sweep-exhausted");
        exhausted.TerminalAt.ShouldNotBeNull();
        var stranded = await LocationAsync(teamId);
        stranded.State.ShouldBe(ArtifactLocationState.Deleting, "nothing released it, and the settlement said so: the profile drain is its exit");
        stranded.Revision.ShouldBeGreaterThan(orphaned, "each sweep really did re-claim the same row — which is exactly why the passes have to be counted");
        File.Exists(ObjectPath(root, sha)).ShouldBeTrue("the destination refused before any effect, so the bytes are still there for the drain to find");
    }

    [Fact]
    public async Task A_bounded_range_read_works_through_the_routed_destination()
    {
        // The model-call viewer reads bodies this way; a routed team must not lose it.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, NewRoot()));
        var content = Encoding.UTF8.GetBytes(new string('a', 10_000) + "NEEDLE" + new string('b', 10_000));
        var artifactId = await PutAsync(teamId, content);

        using var scope = _fixture.BeginScope();
        var range = await scope.Resolve<IArtifactRangeReader>().ReadRangeAsync(teamId, artifactId, 10_000, 6, CancellationToken.None);

        range.State.ShouldBe(ArtifactRangeReadState.Available);
        Encoding.UTF8.GetString(range.Bytes!).ShouldBe("NEEDLE");
        range.TotalLength.ShouldBe(content.LongLength);
    }

    [Fact]
    public async Task Two_concurrent_writes_of_identical_bytes_both_return_the_same_id()
    {
        // The intent's idempotency key is the CONTENT, so overlapping writers of the same payload — the normal case
        // for a fan-out whose branches emit a shared prompt, file body or transcript prefix — contend for one lease.
        // The loser must not lose its contribution: PutAsync is idempotent and ALWAYS returns a valid id.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('c', 40_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);

        var ids = await Task.WhenAll(PutAsync(teamId, content), PutAsync(teamId, content));

        ids[1].ShouldBe(ids[0], "the writer that lost the transfer lease must still be handed the winner's artifact");
        File.Exists(ObjectPath(root, sha)).ShouldBeTrue();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowArtifact.CountAsync(a => a.TeamId == teamId)).ShouldBe(1);
        (await db.ArtifactObject.CountAsync(o => o.TeamId == teamId)).ShouldBe(1, "identical content is one object, however many writers raced for it");
        (await db.ArtifactTransferIntent.CountAsync(i => i.TeamId == teamId)).ShouldBe(1, "the content-keyed intent is shared, not duplicated");
        (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, ids[0], CancellationToken.None))!.Bytes.ShouldBe(content);
    }

    [Fact]
    public async Task A_write_whose_content_is_already_claimed_waits_for_that_transfer_and_finishes_it()
    {
        // The DETERMINISTIC half of the race above. Another worker holds a live lease on the intent for these exact
        // bytes at the moment PutAsync runs, so ClaimAsync's `worker_lease_expires_at <= clock_timestamp()` predicate
        // refuses this caller and the coordinator returns Deferred(TransferInProgress) on the FIRST attempt. Treating
        // that as a terminal failure would discard a contribution for content the platform is mid-way through storing.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        var content = Encoding.UTF8.GetBytes(new string('w', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var intentId = await SeedLeasedIntentAsync(teamId, profileId, content, TimeSpan.FromSeconds(3));

        var artifactId = await PutAsync(teamId, content);

        (await RowAsync(artifactId)).CasArtifactObjectId.ShouldNotBeNull("the deferred transfer must be completed, not abandoned");
        File.Exists(ObjectPath(root, sha)).ShouldBeTrue();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(i => i.TeamId == teamId);
        intent.Id.ShouldBe(intentId, "the waiter claimed the intent it was deferred behind rather than minting a second one");
        intent.State.ShouldBe(ArtifactTransferState.Committed);
        intent.WorkerFenceEpoch.ShouldBe(2, "the abandoned lease lapsed and this caller took the next fence");
    }

    [Fact]
    public async Task A_repaired_destination_lets_the_same_bytes_through_after_a_terminal_failure()
    {
        // The poisoned-content property. A non-retryable problem records Failed against the CONTENT, and the database
        // guard offers no route back out of Failed (0131: 'terminal rows cannot be claimed', and a plain transition
        // needs an unexpired lease a terminal row may not hold). Repairing what broke the transfer does not bump
        // storage_profile_revision either — here the repair is entirely outside the database — so unless a repaired
        // attempt mints a FRESH intent, these exact bytes are unstorable for this team forever.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        var content = Encoding.UTF8.GetBytes(new string('x', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        PlaceForeignObject(root, sha);

        await Should.ThrowAsync<ArtifactStorageDestinationUnavailableException>(() => PutAsync(teamId, content));
        await AssertIntentsAsync(teamId, [(IntentKeyFor(sha, 0), ArtifactTransferState.Failed)]);

        File.Delete(ObjectPath(root, sha));
        var artifactId = await PutAsync(teamId, content);

        (await RowAsync(artifactId)).CasArtifactObjectId.ShouldNotBeNull(
            "a repaired destination must be able to store content an earlier misconfiguration failed on");
        (await File.ReadAllBytesAsync(ObjectPath(root, sha))).ShouldBe(content);
        await AssertIntentsAsync(teamId, [
            (IntentKeyFor(sha, 0), ArtifactTransferState.Failed),
            (IntentKeyFor(sha, 1), ArtifactTransferState.Committed),
        ]);
    }

    [Fact]
    public async Task A_still_broken_destination_keeps_failing_closed_one_fresh_intent_at_a_time()
    {
        // The other half: minting a fresh intent must not turn a genuine refusal into a success, and must not replay
        // the burned key forever either. Each call steps exactly one generation and records its own terminal outcome.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        var content = Encoding.UTF8.GetBytes(new string('b', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        PlaceForeignObject(root, sha);

        var first = await Should.ThrowAsync<ArtifactStorageDestinationUnavailableException>(() => PutAsync(teamId, content));
        var second = await Should.ThrowAsync<ArtifactStorageDestinationUnavailableException>(() => PutAsync(teamId, content));

        first.RoutingProblem.ShouldBeNull("routing admitted this write; the transfer is what refused it");
        first.TransferProblem.ShouldBe(ArtifactCasProblemCode.TargetCorrupt);
        second.TransferProblem.ShouldBe(ArtifactCasProblemCode.TargetCorrupt,
            "the second call ran a REAL attempt of its own rather than replaying the first one's stored verdict");
        await AssertIntentsAsync(teamId, [
            (IntentKeyFor(sha, 0), ArtifactTransferState.Failed),
            (IntentKeyFor(sha, 1), ArtifactTransferState.Failed),
        ]);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.CountAsync(a => a.TeamId == teamId)).ShouldBe(0,
            "no row may claim bytes that were never placed");
    }

    [Fact]
    public async Task A_routed_team_never_gains_new_local_disk_bytes_when_a_pre_route_blob_is_missing()
    {
        // The dedup self-heal restores a missing local blob. For a team that has since adopted a route, that restore
        // would write NEW bytes to local disk — the same silent fallback the write path refuses — and would make the
        // outcome depend on whether the content happens to already have a row.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('m', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var artifactId = await PutAsync(teamId, content);
        var localPath = new Uri(LocalUrlFor(sha)).LocalPath;
        File.Exists(localPath).ShouldBeTrue("precondition: the pre-route write landed on local disk");

        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, NewRoot()));
        File.Delete(localPath);

        (await PutAsync(teamId, content)).ShouldBe(artifactId, "dedup still answers for content the store already knows");
        File.Exists(localPath).ShouldBeFalse(
            "a routed team must not gain new local-disk bytes; the dead reference surfaces as a typed read failure instead");
    }

    [Fact]
    public async Task A_bounded_read_serves_a_window_far_into_a_routed_object()
    {
        // The model-call viewer pages by offset. The routed range read asks the PROVIDER for the window — reading the
        // whole object and slicing it would cost O(offset) provider bytes on every page of every large transcript.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, NewRoot()));
        var content = Encoding.UTF8.GetBytes(new string('a', 400_000) + "NEEDLE" + new string('b', 400_000));
        var artifactId = await PutAsync(teamId, content);

        using var scope = _fixture.BeginScope();
        var range = await scope.Resolve<IArtifactRangeReader>().ReadRangeAsync(teamId, artifactId, 400_000, 6, CancellationToken.None);

        range.State.ShouldBe(ArtifactRangeReadState.Available);
        Encoding.UTF8.GetString(range.Bytes!).ShouldBe("NEEDLE");
        range.TotalLength.ShouldBe(content.LongLength);
    }

    [Fact]
    public async Task A_corrupt_routed_object_reports_a_bounded_read_failure_instead_of_throwing()
    {
        // IArtifactRangeReader's contract is that it never throws — it classifies. The routed path has to hold that
        // for a truncated or foreign-written object too, not only for a healthy one.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('t', 30_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var artifactId = await PutAsync(teamId, content);

        await File.WriteAllBytesAsync(ObjectPath(root, sha), content[..1_000]);

        using var scope = _fixture.BeginScope();
        var range = await scope.Resolve<IArtifactRangeReader>().ReadRangeAsync(teamId, artifactId, 0, 512, CancellationToken.None);

        range.State.ShouldBe(ArtifactRangeReadState.IntegrityFailure,
            "a truncated provider object is a typed storage fact, never an exception out of a bounded read");
    }

    private async Task<Guid> PutAsync(Guid teamId, byte[] content)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "text/plain", CancellationToken.None);
    }

    private async Task<Guid> PutStreamAsync(Guid teamId, IArtifactWriteSource source)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IArtifactStreamStore>().PutAsync(new ArtifactStreamWriteRequest(teamId, "text/plain", source), CancellationToken.None);
    }

    /// <summary>
    /// Stages "another worker is mid-transfer of these exact bytes" the ONLY way the schema allows. The BEFORE INSERT
    /// arm of <c>artifact_cas_transfer_guard</c> admits a first revision of 1 in state Intended and unclaimed, and
    /// nothing else; the claim is then a real fence advance (NULL to 1) with an expiring lease, which is exactly what
    /// <c>ClaimAsync</c> writes. Inserting a Failed or already-claimed row directly is rejected by the trigger, so a
    /// test that tries it never reaches its assertions.
    /// </summary>
    private async Task<Guid> SeedLeasedIntentAsync(Guid teamId, Guid profileId, byte[] content, TimeSpan leaseFor)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var revision = await db.StorageProfileRevision.AsNoTracking().SingleAsync(r => r.TeamId == teamId && r.StorageProfileId == profileId && r.Revision == 1);
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var objectKey = ObjectKeyFor(sha);
        var now = DateTimeOffset.UtcNow;
        var intentId = Guid.NewGuid();

        db.ArtifactTransferIntent.Add(new ArtifactTransferIntent
        {
            Id = intentId, TeamId = teamId, StorageProfileRevisionId = revision.Id,
            IdempotencyKey = IntentKeyFor(sha, 0),
            ExpectedDigestAlgorithm = ArtifactDigestAlgorithm.Sha256, ExpectedDigest = Convert.FromHexString(sha),
            ExpectedSizeBytes = content.Length, TargetLocator = objectKey, TargetObjectKey = objectKey,
            State = ArtifactTransferState.Intended, Revision = 1, RetryCount = 0,
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();

        var leaseMilliseconds = (long)leaseFor.TotalMilliseconds;
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE artifact_transfer_intent
            SET worker_fence_epoch = 1,
                worker_lease_expires_at = clock_timestamp() + ({{leaseMilliseconds}} * INTERVAL '1 millisecond'),
                revision = revision + 1,
                last_modified_date = clock_timestamp()
            WHERE team_id = {{teamId}} AND id = {{intentId}}
            """);
        claimed.ShouldBe(1, "the staged claim must be a legal fence advance; the guard rejects anything else");
        return intentId;
    }

    /// <summary>
    /// Stands in for the routed purge, which this lane does not build, doing every part of it this suite can observe:
    /// the location is CLAIMED before anything is removed, then the bytes go, then the pointing row goes, then the
    /// location records Purged with the append-only event each revision requires.
    ///
    /// <para>The row deletion is not optional. <see cref="Routed_dedup_returns_the_same_id_and_never_restores_the_object_onto_local_disk"/>
    /// pins that a routed dedup hit returns the existing row's id with no liveness check, so a purge that reclaims the
    /// bytes and leaves the row behind hands every later writer an id whose read is doomed — the write-back below
    /// would never even be reached.</para>
    /// </summary>
    private async Task PurgeRoutedObjectAsync(Guid teamId, Guid artifactId, string root, string sha)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(l => l.TeamId == teamId);
        await RecordLocationStateAsync(db, location, ArtifactLocationState.Deleting);

        File.Delete(ObjectPath(root, sha));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            // 0016's DELETE trigger rejects a purge that has not said so in its own session, exactly as the reaper does it.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL codespace.artifact_purge_allowed = on");
            await db.WorkflowArtifact.Where(a => a.TeamId == teamId && a.Id == artifactId).ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }

        await RecordLocationStateAsync(db, location, ArtifactLocationState.Purged);
    }

    /// <summary>
    /// Moves the team's one placement to a state the VERIFIER produces, carrying the error it would have recorded
    /// alongside it and leaving the observation itself untouched — which is what a demotion does: it says the
    /// destination stopped agreeing with the record, never that the record was wrong.
    /// </summary>
    private async Task<ArtifactLocation> DemoteLocationAsync(Guid teamId, ArtifactLocationState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(value => value.TeamId == teamId);
        location.LastErrorCode = "seeded-demotion";
        location.LastErrorMessage = "Seeded by a test to reach a state the verifier produces.";

        await RecordLocationStateAsync(db, location, state);

        return location;
    }

    /// <summary>Sweeps until THIS team's placement reaches <paramref name="state"/>.</summary>
    private Task<ArtifactLocation> VerifyUntilAsync(Guid teamId, ArtifactLocationState state) =>
        SweepUntilAsync(teamId, location => location.State == state, $"reach {state}");

    /// <summary>
    /// Sweeps until THIS team's placement has been re-examined since <paramref name="revision"/> — a confirmation
    /// advances the row, so a moved revision is the only proof the sweep actually looked at it rather than at the
    /// hundreds of other rows this collection leaves behind.
    /// </summary>
    private Task<ArtifactLocation> VerifyUntilObservedAsync(Guid teamId, long revision) =>
        SweepUntilAsync(teamId, location => location.Revision > revision, $"be re-examined past revision {revision}");

    /// <summary>
    /// Sweeps until this team's placement satisfies <paramref name="settled"/>, and says what to look at when it never
    /// does.
    ///
    /// <para>A pass is deployment-wide and bounded, over a table every test in this collection writes to, and it
    /// orders by <c>verified_at</c> — so a freshly written row is the LAST one a batch would reach. Asserting after a
    /// single pass, or on that pass's tally, would be asserting that this row won a race it has no reason to win, and
    /// would get harder to satisfy as the suite grows.</para>
    /// </summary>
    private async Task<ArtifactLocation> SweepUntilAsync(Guid teamId, Func<ArtifactLocation, bool> settled, string expectation)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        ArtifactLocation? seen = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = _fixture.BeginScope();
            await scope.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(200, CancellationToken.None);
            seen = await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking().SingleAsync(value => value.TeamId == teamId);

            if (settled(seen)) return seen;
        }

        throw new Xunit.Sdk.XunitException(
            $"The placement for team {teamId} never came to {expectation} within 20s (last seen {seen?.State.ToString() ?? "absent"} at revision {seen?.Revision}). "
            + "The sweep is deployment-wide and bounded, so check whether earlier tests left more stale locations behind than one batch holds, "
            + "and whether this team's destination root still exists — an unreachable destination is deliberately never demoted.");
    }

    /// <summary>This team's one placement row, read fresh and untracked.</summary>
    private async Task<ArtifactLocation> PlacementAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking().SingleAsync(value => value.TeamId == teamId);
    }

    /// <summary>
    /// The write a losing reviver leaves at the destination: the same bytes, at the same key, under the same
    /// unconditional condition the <c>Corrupt</c> repair uses — issued through the very driver the CAS would have
    /// opened for it, so this IS that call rather than a stand-in for it. Returns the ETag the destination reports
    /// afterwards.
    /// </summary>
    private async Task<string?> OverwriteThroughDriverAsync(Guid teamId, Guid profileId, string objectKey, byte[] content)
    {
        using var scope = _fixture.BeginScope();
        var resolution = await scope.Resolve<IStorageRuntimeDriverBroker>().OpenAsync(new StorageRuntimeDriverRequest(teamId, profileId, 1, StorageProfileEligibility.Write), CancellationToken.None);
        await using var lease = resolution.ShouldBeOfType<StorageRuntimeDriverResolution.Ready>().Lease;
        await using var input = new MemoryStream(content, writable: false);

        var put = await lease.Driver.PutAsync(new ArtifactStoragePutRequest(objectKey, input)
        {
            ContentLength = content.LongLength,
            ExpectedSha256 = ArtifactStore.ComputeSha256Hex(content),
            ContentType = "text/plain",
            Condition = ArtifactStorageWriteCondition.None,
        }, CancellationToken.None);

        put.IsSuccess.ShouldBeTrue(put.Error?.Message);

        return put.Metadata!.ETag;
    }

    /// <summary>The typed verdict a whole-object read gives when it cannot serve the row — asserted rather than let escape, so a test that expected a failure cannot pass on the wrong one.</summary>
    private async Task<ArtifactContentUnavailableException> ReadFailureAsync(Guid teamId, Guid artifactId)
    {
        using var scope = _fixture.BeginScope();

        return await Should.ThrowAsync<ArtifactContentUnavailableException>(
            () => scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None));
    }

    /// <summary>One location observation, as the schema demands it: revision advances exactly once and carries a matching append-only event.</summary>
    private static async Task RecordLocationStateAsync(CodeSpaceDbContext db, ArtifactLocation location, ArtifactLocationState state)
    {
        location.State = state;
        location.Revision++;
        location.LastModifiedDate = DateTimeOffset.UtcNow;
        db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
            EventType = ArtifactLocationEventType.StateChanged, State = state, ObservedAt = location.LastModifiedDate,
            ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
            ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
            ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
            ContentEncoding = location.ContentEncoding, EncryptionKeyVersion = location.EncryptionKeyVersion,
            ErrorCode = location.LastErrorCode, ErrorMessage = location.LastErrorMessage, DetailsJson = "{}", CreatedBy = location.LastModifiedBy,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Puts a foreign object of the wrong length at the target key, which is the cheapest reachable NON-retryable
    /// transfer failure for the local-rwx provider: <c>HeadCanMatch</c> compares the head's length against the
    /// expected size, so the coordinator raises <c>TargetCorrupt</c> — <c>Problem(code)</c> defaults to
    /// non-retryable — and <c>HandleProblemAsync</c> drives the intent to Failed. The credential-shaped problems the
    /// blocker names (CredentialInvalid, CredentialUnavailable) reach the SAME classification, but this driver has no
    /// credentials to break. Deleting this file is a repair entirely outside the database, which is the point: it
    /// leaves storage_profile_revision untouched.
    /// </summary>
    private static void PlaceForeignObject(string root, string sha)
    {
        var path = ObjectPath(root, sha);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not the bytes this key promises"));
    }

    /// <summary>Every intent this team owns, in key order, as (idempotency key, state) — so a test names the exact generations it expects and nothing else.</summary>
    private async Task AssertIntentsAsync(Guid teamId, (string Key, ArtifactTransferState State)[] expected)
    {
        using var scope = _fixture.BeginScope();
        var intents = await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.AsNoTracking()
            .Where(i => i.TeamId == teamId).OrderBy(i => i.IdempotencyKey)
            .Select(i => new { i.IdempotencyKey, i.State }).ToListAsync();

        intents.Select(i => (i.IdempotencyKey, i.State)).ShouldBe(expected);
    }

    /// <summary>The intent key one generation of one payload claims, composed exactly the way the write path composes it: the store owns the scope, the CAS runtime owns the generation.</summary>
    private static string IntentKeyFor(string sha, int generation) => ArtifactCasRuntimeCoordinator.IdempotencyKeyFor(ArtifactStore.IdempotencyScopeFor(sha), generation);

    /// <summary>The object key the routed write targets. Content-addressed and generation-INVARIANT, so every attempt at the same bytes dedups on the provider.</summary>
    private static string ObjectKeyFor(string sha) => $"workflow-artifacts/{sha[..2]}/{sha.Substring(2, 2)}/{sha}";

    /// <summary>
    /// Where the local-rwx driver actually puts an object: it namespaces every key under an <c>objects/</c> directory
    /// beneath the configured root. Computed once here so a change to that layout fails in one place.
    /// </summary>
    private static string ObjectPath(string root, string sha) =>
        Path.Combine(root, "objects", Path.Combine(ObjectKeyFor(sha).Split('/')));

    private async Task AgeRoutedDeclarationAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact DISABLE TRIGGER workflow_artifact_enforce_immutability");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_artifact SET created_at = clock_timestamp() - INTERVAL '30 days' WHERE id = {artifactId}");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact ENABLE TRIGGER workflow_artifact_enforce_immutability");
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE workflow_artifact_retention
            SET declared_at = clock_timestamp() - INTERVAL '30 days', next_sweep_at = clock_timestamp() - INTERVAL '30 days', last_modified_at = clock_timestamp()
            WHERE artifact_id = {{artifactId}}
            """);
        await transaction.CommitAsync();
    }

    private async Task AgeRoutedQuarantineAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE workflow_artifact_retention
            SET quarantined_at = clock_timestamp() - INTERVAL '2 days', next_sweep_at = clock_timestamp() - INTERVAL '2 days', last_modified_at = clock_timestamp()
            WHERE artifact_id = {{artifactId}} AND state = 'Quarantined'
            """);
    }

    /// <summary>A worker that took the physical claim and died before it could delete or release it. Returns the revision the marker was left at.</summary>
    private async Task<long> OrphanThePhysicalClaimAsync(Guid teamId, Guid objectId, Guid actorId)
    {
        using var scope = _fixture.BeginScope();
        var claimed = (await scope.Resolve<IArtifactCasPurgeCoordinator>().ClaimAsync(new ArtifactCasPurgeRequest
        {
            TeamId = teamId, ArtifactObjectId = objectId, ActorId = actorId,
        }, CancellationToken.None)).ShouldBeOfType<ArtifactCasPurgeClaimResult.Claimed>();

        (await LocationAsync(teamId)).State.ShouldBe(ArtifactLocationState.Deleting, "a fixture that did not actually start from the marker would prove nothing");

        return claimed.Claim.LocationRevision;
    }

    /// <summary>The shipped reaper over a substituted purge coordinator, with a short attempt budget so exhausting it is two sweeps rather than eight.</summary>
    private static ArtifactRetentionReaper BudgetedReaper(ILifetimeScope scope, IArtifactCasPurgeCoordinator routed, int attempts) =>
        new(new ArtifactRetentionReaperServices(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), scope.Resolve<IArtifactReferenceOracle>(),
                scope.Resolve<IArtifactBlobBackend>(), routed, NullLogger<ArtifactRetentionReaper>.Instance),
            new ArtifactRetentionReaperOptions(BatchSize: 200, ClaimSize: 25, MaxAttempts: attempts, LeaseDuration: TimeSpan.FromSeconds(60),
                OperationTimeout: TimeSpan.FromSeconds(15), RetryDelay: TimeSpan.FromMinutes(30)));

    /// <summary>
    /// Sweeps until the declaration this test owns has been settled once more, bringing its scheduled wait forward in
    /// between. The sweep is a bounded global batch shared with every other team in the database, so "one sweep moved
    /// my row" is not something a test may assume — but "my row eventually moved" is exactly what is under test.
    /// </summary>
    private async Task<WorkflowArtifactRetention> SweepUntilSettledAsync(ArtifactRetentionReaper reaper, Guid artifactId, int fromAttempt)
    {
        foreach (var _ in Enumerable.Range(0, 8))
        {
            await reaper.SweepAsync(CancellationToken.None);
            var row = await RetentionAsync(artifactId);
            if (row.AttemptCount > fromAttempt || row.TerminalAt != null) return row;

            await AgeRoutedQuarantineAsync(artifactId);
        }

        var stuck = await RetentionAsync(artifactId);
        throw new Xunit.Sdk.XunitException(
            $"Declaration {artifactId} never moved past attempt {fromAttempt} in eight sweeps "
            + $"(state {stuck.State}, attempts {stuck.AttemptCount}, last code '{stuck.LastErrorCode}'). "
            + "An unbudgeted wait is the expected cause: check whether the routed release is reporting a race for a claim it can never release.");
    }

    private async Task<WorkflowArtifactRetention> RetentionAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking().SingleAsync(value => value.ArtifactId == artifactId);
    }

    private async Task<ArtifactLocation> LocationAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking().SingleAsync(value => value.TeamId == teamId);
    }

    private async Task<Guid> SeedAgentRunAsync(Guid teamId, Guid actorId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var agentRunId = Guid.NewGuid();
        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, Harness = "retention-reference-test", Status = AgentRunStatus.Succeeded,
            TaskJson = "{}", FenceEpoch = 1, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();
        return agentRunId;
    }

    private async Task ReferenceFromAgentRunAsync(Guid agentRunId, Guid artifactId, CancellationToken cancellationToken)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO agent_run_event (id, agent_run_id, kind, text, data_artifact_id)
            VALUES ({{Guid.NewGuid()}}, {{agentRunId}}, 'Info', 'committed after routed purge claim', {{artifactId}})
            """, cancellationToken);
    }

    private async Task<WorkflowArtifact> RowAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(a => a.Id == artifactId);
    }

    /// <summary>Computed through the production resolver so a change to where local artifacts live fails here, not silently.</summary>
    private static string LocalUrlFor(string sha) =>
        new Uri(Path.Combine(DurableRoots.ArtifactStore(RuntimeSettings.Current.ArtifactStoreDirectory), sha[..2], sha.Substring(2, 2), sha)).AbsoluteUri;

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-artifact-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private async Task<Guid> SeedProfileAsync(Guid teamId, string rootPath)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath }));
        var canonical = StorageProfileRules.CanonicalJson(document.RootElement);
        using var canonicalDocument = JsonDocument.Parse(canonical);

        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"routed-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profile.Id, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = canonical,
            NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, canonicalDocument.RootElement),
            CreatedDate = now, CreatedBy = SystemUsers.SeederId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        _destinations.Add(profile.Revisions.Single().Id);
        return profile.Id;
    }

    private async Task SeedRouteAsync(Guid teamId, Guid profileId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        // The table's triggers require a route to be born Draft at revision 1 and reach Active by update.
        var route = new StorageRoute
        {
            Id = Guid.NewGuid(), TeamId = teamId, DataClassTypeKey = WorkflowArtifactDestinationResolver.DataClassTypeKey,
            CurrentRevision = 1, State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        };
        route.Revisions.Add(new StorageRouteRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = route.Id, Revision = 1, StorageProfileId = profileId,
            ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite, CreatedDate = now, CreatedBy = SystemUsers.SeederId,
        });
        db.StorageRoute.Add(route);
        await db.SaveChangesAsync();

        route.State = StorageRouteState.Active;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Creates the route the way an operator's "Create data route" request does — through the real service, which is
    /// the only place that decides a new route's starting state. Hand-writing the row would step around exactly the
    /// decision under test.
    /// </summary>
    private async Task<StorageRouteDetail> CreateDraftRouteAsync(Guid teamId, Guid actorId, Guid profileId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IStorageRouteService>().CreateAsync(teamId, actorId, new CreateStorageRouteCommand
        {
            DataClassTypeKey = WorkflowArtifactDestinationResolver.DataClassTypeKey, StorageProfileId = profileId,
        }, CancellationToken.None);
    }

    private async Task RepointRouteAsync(Guid teamId, Guid profileId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var route = await db.StorageRoute.SingleAsync(r => r.TeamId == teamId && r.DataClassTypeKey == WorkflowArtifactDestinationResolver.DataClassTypeKey);

        db.StorageRouteRevision.Add(new StorageRouteRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = route.Id, Revision = route.CurrentRevision + 1,
            StorageProfileId = profileId, ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite,
            CreatedDate = DateTimeOffset.UtcNow, CreatedBy = SystemUsers.SeederId,
        });
        route.CurrentRevision += 1;
        route.LastModifiedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task SetRouteStateAsync(Guid teamId, StorageRouteState state)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().StorageRoute
            .Where(r => r.TeamId == teamId && r.DataClassTypeKey == WorkflowArtifactDestinationResolver.DataClassTypeKey)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, state));
    }

    private async Task SetProfileStateAsync(Guid teamId, Guid profileId, StorageProfileState state)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().StorageProfile
            .Where(p => p.TeamId == teamId && p.Id == profileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.State, state));
    }

    private sealed class ReferenceAfterClaimPurgeCoordinator : IArtifactCasPurgeCoordinator
    {
        private readonly IArtifactCasPurgeCoordinator _inner;
        private readonly Func<CancellationToken, Task> _reference;
        private int _inserted;

        public ReferenceAfterClaimPurgeCoordinator(IArtifactCasPurgeCoordinator inner, Func<CancellationToken, Task> reference)
        {
            _inner = inner;
            _reference = reference;
        }

        public async Task<ArtifactCasPurgeClaimResult> ClaimAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken)
        {
            var result = await _inner.ClaimAsync(request, cancellationToken);
            if (result is ArtifactCasPurgeClaimResult.Claimed && Interlocked.Exchange(ref _inserted, 1) == 0)
                await _reference(cancellationToken);
            return result;
        }

        public Task<ArtifactCasPurgeResult> DeleteAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) => _inner.DeleteAsync(claim, cancellationToken);
        public Task<ArtifactCasReleaseOutcome> ReleaseAsync(ArtifactCasPurgeClaim claim, ArtifactCasReleaseEvidence evidence, CancellationToken cancellationToken) => _inner.ReleaseAsync(claim, evidence, cancellationToken);

        public Task<ArtifactCasAbandonResult> AbandonAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) => _inner.AbandonAsync(claim, cancellationToken);
        public Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) => _inner.PurgeAsync(request, cancellationToken);
    }

    private sealed class UncertainDeletePurgeCoordinator : IArtifactCasPurgeCoordinator
    {
        private readonly IArtifactCasPurgeCoordinator _inner;

        public UncertainDeletePurgeCoordinator(IArtifactCasPurgeCoordinator inner) => _inner = inner;

        public int ReleaseCalls { get; private set; }

        public Task<ArtifactCasPurgeClaimResult> ClaimAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) => _inner.ClaimAsync(request, cancellationToken);

        public Task<ArtifactCasPurgeResult> DeleteAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactCasPurgeResult>(new ArtifactCasPurgeResult.Rejected(new ArtifactCasProblem(ArtifactCasProblemCode.ProviderTimeout, true), EffectMayHaveOccurred: true));

        public Task<ArtifactCasReleaseOutcome> ReleaseAsync(ArtifactCasPurgeClaim claim, ArtifactCasReleaseEvidence evidence, CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            return _inner.ReleaseAsync(claim, evidence, cancellationToken);
        }

        public Task<ArtifactCasAbandonResult> AbandonAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) => _inner.AbandonAsync(claim, cancellationToken);

        public Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) => _inner.PurgeAsync(request, cancellationToken);
    }

    /// <summary>A destination that refuses deletion outright and answers BEFORE touching anything — a revoked delete grant, not a bad moment.</summary>
    private sealed class RefusedDeletePurgeCoordinator : IArtifactCasPurgeCoordinator
    {
        private readonly IArtifactCasPurgeCoordinator _inner;

        public RefusedDeletePurgeCoordinator(IArtifactCasPurgeCoordinator inner) => _inner = inner;

        public Task<ArtifactCasPurgeClaimResult> ClaimAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) => _inner.ClaimAsync(request, cancellationToken);

        public Task<ArtifactCasPurgeResult> DeleteAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactCasPurgeResult>(new ArtifactCasPurgeResult.Rejected(new ArtifactCasProblem(ArtifactCasProblemCode.Forbidden, false)));

        public Task<ArtifactCasReleaseOutcome> ReleaseAsync(ArtifactCasPurgeClaim claim, ArtifactCasReleaseEvidence evidence, CancellationToken cancellationToken) =>
            _inner.ReleaseAsync(claim, evidence, cancellationToken);

        public Task<ArtifactCasAbandonResult> AbandonAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) => _inner.AbandonAsync(claim, cancellationToken);
        public Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) => _inner.PurgeAsync(request, cancellationToken);
    }

    private sealed class ReopenableByteSource : IArtifactWriteSource
    {
        private readonly byte[] _bytes;

        public ReopenableByteSource(byte[] bytes, long? declaredLength = null)
        {
            _bytes = bytes;
            LengthBytes = declaredLength ?? bytes.LongLength;
        }

        public long LengthBytes { get; }
        public int OpenCount { get; private set; }
        public int LargestReadRequest { get; private set; }

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return ValueTask.FromResult<Stream>(new ObservedReadStream(_bytes, requested => LargestReadRequest = Math.Max(LargestReadRequest, requested)));
        }
    }

    private sealed class ObservedReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly Action<int> _observe;

        public ObservedReadStream(byte[] bytes, Action<int> observe)
        {
            _inner = new MemoryStream(bytes, writable: false);
            _observe = observe;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            _observe(buffer.Length);
            return _inner.Read(buffer);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _observe(buffer.Length);
            return _inner.ReadAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Takes every placement this class made permanently out of the verifier's sweep, and removes the destination
    /// roots behind them.
    ///
    /// <para>A pass is deployment-wide and takes ONE TURN PER DESTINATION under a fixed bound, so every destination
    /// left behind here is a turn some neighbouring class's placement no longer gets — and a placement that never
    /// gets a turn is a placement whose <c>verified_at</c> never moves, which is a silent failure in a test that has
    /// nothing to do with this one. <c>Available</c> and <c>Missing</c> are the only two states a pass selects, and
    /// <c>Deleted</c> is terminal, so one observation per row is the whole of it. Best-effort, and on the failure
    /// path too: a failing test that leaks these breaks its neighbours rather than itself.</para>
    /// </summary>
    public async Task DisposeAsync()
    {
        // The roots are removed in the finally, because the placement release reaches the database and the root
        // removal does not: letting a fault there escape would both redden a test that had already passed and leak
        // the very roots this cleanup replaced. Best-effort means best-effort on both halves.
        try
        {
            foreach (var locationId in await SweepablePlacementsAsync())
            {
                try
                {
                    await ReleasePlacementAsync(locationId);
                }
                catch (DbUpdateException) { }
            }
        }
        catch (Exception) { /* best-effort: a destination this class cannot release is a neighbour's problem, not this test's verdict */ }
        finally
        {
            foreach (var root in _roots)
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>The placements under this class's destinations that a verifier pass would still select.</summary>
    private async Task<List<Guid>> SweepablePlacementsAsync()
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .Where(location => _destinations.Contains(location.StorageProfileRevisionId))
            .Where(location => location.State == ArtifactLocationState.Available || location.State == ArtifactLocationState.Missing)
            .Select(location => location.Id).ToListAsync();
    }

    /// <summary>Retires one placement, as the single revision-advancing observation the schema demands.</summary>
    private async Task ReleasePlacementAsync(Guid locationId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(value => value.Id == locationId);

        await RecordLocationStateAsync(db, location, ArtifactLocationState.Deleted);
    }
}
