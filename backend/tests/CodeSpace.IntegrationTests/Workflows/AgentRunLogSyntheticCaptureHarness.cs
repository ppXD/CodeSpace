using System.Diagnostics;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Drives a log of ANY declared size through the whole real capture path and asserts what PostgreSQL then holds.
/// Everything in the path is production code — the real <see cref="AgentRunLogCaptureBridge"/>, the real
/// <see cref="AgentRunLogService"/>, the real CAS runtime coordinator and driver broker, real PostgreSQL — and the
/// only synthetic parts are the two ends that would otherwise have to MATERIALIZE the payload: the durable source
/// mints bytes per read from <see cref="SyntheticPayload"/>, and the destination keeps a
/// <c>(key, length, sha, span)</c> ledger and regenerates from the same formula when the CAS reads back.
///
/// <para>One harness rather than one test, because the size is the only thing that differs between the tier that runs
/// on every push and the tier that proves the 4 GiB case. Duplicating the doubles per tier is how those two would
/// drift into asserting different things about the same path.</para>
///
/// <para>What the assertions are FOR. The offsets and lengths on these rows are already <c>long</c> and the range
/// reads are already clamped below <c>int</c>, but nothing anywhere had ever driven a total past
/// <see cref="int.MaxValue"/>, so each of those was a claim about source text rather than about behaviour. An
/// <c>int</c> truncation of any total or offset lands here as a total mismatch or a broken ordinal chain. The memory
/// assertions are the other half: a path that quietly accumulated the payload passes every byte-correctness
/// assertion and is caught only by measuring what it was holding when it finished.</para>
/// </summary>
internal sealed class SyntheticCaptureHarness(PostgresFixture fixture)
{
    /// <summary>4 GiB — four times <see cref="int.MaxValue"/>, so no 32-bit total can represent it.</summary>
    public const long FourGibibytes = 4L << 30;

    /// <summary>
    /// The same path at a size that still exercises multi-page manifest verification but costs a few seconds.
    ///
    /// <para>The odd remainder is deliberate and is the only reason this tier exercises the segment boundary at all.
    /// A total that is an exact multiple of <see cref="AgentRunLogCaptureBridge.MaximumSegmentBytes"/> makes the
    /// source's own <c>min(available, MaximumBytes)</c> serve an exactly-maximal window on every read, so every
    /// segment is exactly the ceiling and the short final segment every real log ends with never occurs anywhere in
    /// this suite. With the remainder the last read is 12345 bytes, the last segment is short, and the segment-count
    /// prediction has to ceiling-divide rather than divide.</para>
    /// </summary>
    public const long SmallCaptureBytes = 32L * 1024 * 1024 + 12345;

    /// <summary>
    /// Live managed heap the capture may still be holding when it finishes, INDEPENDENT of the captured size — the
    /// absolute arm of <see cref="LiveHeapCeiling"/>, and the only arm that can bind at 4 GiB. A streaming path's
    /// residue is segment-sized and does not grow with the total: 11 MiB for 32 MiB, and for 4 GiB — 128 times the
    /// payload — 6 MiB on the CI runner and 16 MiB on a dev Mac. Nothing here ASSERTS that flatness: each tier only
    /// checks its own fence, and comparing across tiers is a human reading two <see cref="Report"/> lines. The gap
    /// between those two 4 GiB figures is why the fence is an order of magnitude, not a budget — what a GC leaves
    /// reachable at the moment of measurement is a property of the host, not of the write path.
    /// </summary>
    public const long LiveHeapCeilingBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Cumulative managed allocation the whole capture-and-finalize may churn, as a multiple of the captured size.
    /// A streaming copy pipeline is inherently linear here — one copy per stage is one copy per byte — so this can
    /// only ever bound the NUMBER of copies and can never prove the absence of buffering; that is
    /// <see cref="LiveHeapCeilingBytes"/>'s job. Today's path costs six (three in the no-secrets redaction transform,
    /// one in the append's <c>Bytes.ToArray()</c>, the rest framing and EF), and ten is the fence.
    /// </summary>
    public const int AllocationCopiesCeiling = 10;

    private const long WorkerFenceEpoch = 7;

    /// <summary>
    /// Sized for the 4 GiB case rather than for a wall-clock guess: the whole capture is one final drain, and one
    /// terminalization step verifies eight segments' worth of regenerated bytes. They bound a hang, not a duration —
    /// the caller's own deadline is what fails a genuinely stuck run.
    /// </summary>
    private static readonly AgentRunLogCaptureBridgeOptions LargeCaptureBudget = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));

    /// <summary>
    /// The live-heap fence for a capture of <paramref name="totalBytes"/>: <see cref="LiveHeapCeilingBytes"/>,
    /// tightened BELOW the payload itself whenever the payload is small enough to fit under it. The tightening is
    /// what gives the cheap tier a buffering assertion at all — a path that held all 32 MiB sits at 32 MiB live and
    /// passes a 256 MiB fence, so without this the small case guards int truncation only, whatever its doc said.
    ///
    /// <para>Three quarters rather than a half is for margin on both sides: the observed streaming residue is 11 MiB
    /// at this size, so a half-payload fence would leave only 1.45× for runner-to-runner variance in what a shared
    /// pool, a warm JIT and an EF change tracker happen to hold, while 24 MiB leaves 2.2× and still fails any path
    /// that accumulated the whole payload. It is a wrong-order-of-magnitude fence at both tiers, not a budget.</para>
    /// </summary>
    private static long LiveHeapCeiling(long totalBytes) => Math.Min(LiveHeapCeilingBytes, totalBytes * 3 / 4);

    /// <summary>One segment per read and each read capped at the bridge's ceiling, so a total that is not a multiple of it ends in one short segment and the count ceiling-divides.</summary>
    private static long ExpectedSegments(long totalBytes) => (totalBytes + AgentRunLogCaptureBridge.MaximumSegmentBytes - 1) / AgentRunLogCaptureBridge.MaximumSegmentBytes;

    public async Task RunAsync(long totalBytes, CancellationToken cancellationToken)
    {
        var expectedSegments = ExpectedSegments(totalBytes);
        var world = await SeedWorldAsync();
        var ledger = new SpanLedger();
        var source = new SyntheticLogSource(totalBytes);
        using var scope = ScopeOver(ledger);
        var bridge = BridgeOver(scope, world);
        var watch = Stopwatch.StartNew();

        var liveHeapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        await CaptureAsync(bridge, world, source, cancellationToken);
        await FinalizeAsync(bridge, world, expectedSegments, cancellationToken);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var liveHeap = GC.GetTotalMemory(forceFullCollection: true) - liveHeapBefore;
        watch.Stop();

        var stream = await StreamAsync(world, cancellationToken);
        var segments = await SegmentsAsync(world, stream.Id, cancellationToken);
        Report(totalBytes, expectedSegments, watch.Elapsed, allocated, liveHeap);

        AssertCaptured(stream, segments, source, totalBytes, expectedSegments);
        AssertLedgerAgreesWithPostgres(ledger, segments);
        Convert.ToHexStringLower(ledger.ArrivalDigest).ShouldBe(Convert.ToHexStringLower(source.Digest),
            "the destination received exactly the bytes the source produced, in order and once each — the ledger's arrival hash is only this claim because the assertions above proved no duplicate key and no reordering");
        AssertManifestSealed(stream, segments);
        AssertBounded(totalBytes, allocated, liveHeap);
    }

    /// <summary>The byte head PostgreSQL holds, and the segment chain underneath it.</summary>
    private static void AssertCaptured(AgentRunLogStream stream, IReadOnlyList<SegmentRow> segments, SyntheticLogSource source, long totalBytes, long expectedSegments)
    {
        source.Served.ShouldBe(totalBytes, "the source has to have been asked for every byte before anything below means anything");
        stream.State.ShouldBe(AgentRunLogStreamState.Completed, $"the stream never reached a terminal capture verdict (it is {stream.State}, error {stream.ErrorCode ?? "<none>"}) — inspect with: SELECT state, segment_count, total_bytes, error_code, error_message FROM agent_run_log_stream WHERE id = '{stream.Id}'");
        stream.TotalBytes.ShouldBe(totalBytes, "an int truncation of the byte head lands exactly here: 4 GiB wraps to 0 in 32 bits");
        stream.SegmentCount.ShouldBe(expectedSegments, "a log of this size is that many ceiling-capped objects, one per read, never one oversized PUT");
        segments.Count.ShouldBe((int)expectedSegments, "the stream's own segment counter and the segment rows have to agree before either can be read as evidence");

        long offset = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            segments[index].Ordinal.ShouldBe(index + 1, $"segment ordinals must be 1..N with no gap; the chain broke at index {index}");
            segments[index].StartOffset.ShouldBe(offset, $"segment {segments[index].Ordinal} does not start where the previous one ended — a truncated offset shows up here");
            offset += segments[index].Length;
        }

        offset.ShouldBe(totalBytes, "the segment lengths must tile the whole stream with no overlap and no hole");
    }

    /// <summary>
    /// PostgreSQL's account of each segment against what the destination was actually handed. Without this the whole
    /// run rests on rows the write path wrote about itself: a length or digest recorded from the REQUEST rather than
    /// from the bytes would agree with itself forever.
    /// </summary>
    private static void AssertLedgerAgreesWithPostgres(SpanLedger ledger, IReadOnlyList<SegmentRow> segments)
    {
        ledger.DuplicateKeys.ShouldBe(0, "a segment written twice would double-count in the destination's arrival hash and make that hash meaningless");
        ledger.Objects.Count.ShouldBe(segments.Count, "the destination holds exactly one object per recorded segment");
        segments.Select(value => value.ArtifactObjectId).Distinct().Count().ShouldBe(segments.Count,
            "each segment is its own CAS object; two segments sharing one would mean the payload repeated, and the object-to-segment correspondence below would prove nothing");

        foreach (var segment in segments)
        {
            var objectKey = segment.ObjectKeys.ShouldHaveSingleItem($"segment {segment.Ordinal} is placed under {segment.ObjectKeys.Length} distinct provider keys; a log segment is written once, to one key");
            var stored = ledger.Find(objectKey).ShouldNotBeNull($"segment {segment.Ordinal} names object key {objectKey}, which the destination never received");
            stored.Length.ShouldBe(segment.Length, $"segment {segment.Ordinal} records a length the destination did not store");
            stored.GlobalStart.ShouldBe(segment.StartOffset, $"segment {segment.Ordinal} claims offset {segment.StartOffset} but arrived at the destination at {stored.GlobalStart}");
            stored.ArrivalIndex.ShouldBe(segment.Ordinal - 1, $"segment {segment.Ordinal} reached the destination out of order, so arrival order is not segment order");
            Convert.ToHexStringLower(stored.Sha256).ShouldBe(Convert.ToHexStringLower(segment.Digest), $"segment {segment.Ordinal} records a digest that is not the SHA-256 of the bytes the destination received");
        }
    }

    /// <summary>The sealed v3 manifest digest, recomputed from the same canonical chain over the rows PostgreSQL holds.</summary>
    private static void AssertManifestSealed(AgentRunLogStream stream, IReadOnlyList<SegmentRow> segments)
    {
        stream.SchemaVersion.ShouldBe(3, "manifest verification only applies to the v3 stream shape");
        var manifestDigest = stream.ManifestDigest.ShouldNotBeNull("a completed v3 stream carries the sealed manifest digest; without it nothing proves which segments were verified");
        var checkpoint = new AgentRunLogVerification
        {
            TeamId = stream.TeamId, AgentRunId = stream.AgentRunId, StreamId = stream.Id, WorkerFenceEpoch = WorkerFenceEpoch,
            CaptureSessionId = stream.CaptureSessionId!.Value, StreamRevision = stream.Revision - 1,
            SegmentCount = stream.SegmentCount, TotalBytes = stream.TotalBytes, SourceOffsetBytes = stream.SourceOffsetBytes,
        };

        checkpoint.Accumulator = AgentRunLogManifestDigest.Begin(checkpoint);
        foreach (var segment in segments)
            checkpoint.Accumulator = AgentRunLogManifestDigest.Append(checkpoint.Accumulator, new AgentRunLogManifestEntry(segment.Ordinal, segment.StartOffset, segment.Length, segment.ArtifactObjectId, segment.Digest));

        Convert.ToHexStringLower(AgentRunLogManifestDigest.Seal(checkpoint)).ShouldBe(Convert.ToHexStringLower(manifestDigest),
            "the sealed digest must be the canonical chain over every segment PostgreSQL holds; a segment verified out of order, skipped, or counted twice changes it");
    }

    private static void AssertBounded(long totalBytes, long allocated, long liveHeap)
    {
        liveHeap.ShouldBeLessThan(LiveHeapCeiling(totalBytes), $"{totalBytes / 1024 / 1024} MiB were captured and {liveHeap / 1024 / 1024} MiB of managed heap are still live against a fence of {LiveHeapCeiling(totalBytes) / 1024 / 1024} MiB: something accumulated the payload instead of streaming it");
        allocated.ShouldBeLessThan(totalBytes * AllocationCopiesCeiling, $"{allocated / 1024 / 1024} MiB allocated for {totalBytes / 1024 / 1024} MiB captured is more than {AllocationCopiesCeiling} copies per byte; a stage started buffering rather than streaming");
    }

    /// <summary>
    /// Runs the capture to end-of-source. The observer returns at once on purpose: the pump's polled phase is capped
    /// at eight reads per 250 ms tick, so a 4 GiB source drained through it would be bounded by the poll schedule
    /// rather than by the write path under test. The FINAL drain has no read cap, so completing the observer
    /// immediately puts the whole source through the same real append path with nothing waiting on a timer.
    /// </summary>
    private static async Task CaptureAsync(AgentRunLogCaptureBridge bridge, World world, SyntheticLogSource source, CancellationToken cancellationToken)
    {
        var expected = new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "synthetic", Stderr = "" };
        var capture = await bridge.OpenAsync(OpenCapture(world, source), cancellationToken);

        (await capture.ObserveAsync((_, _) => Task.FromResult(expected), cancellationToken)).ShouldBeSameAs(expected, "the wrapped sandbox result stays authoritative and unchanged through capture");
    }

    /// <summary>
    /// Drives the production terminalization call until the row is sealed. v3 completion verifies eight segments per
    /// step and answers <c>Progress</c> in between, so a 4096-segment stream takes 512 of them — exactly the loop the
    /// worker plus the capture-recovery job perform, run here without the job's scheduling delay.
    /// </summary>
    private async Task FinalizeAsync(AgentRunLogCaptureBridge bridge, World world, long expectedSegments, CancellationToken cancellationToken)
    {
        var steps = expectedSegments / AgentRunLogService.VerificationSegmentsPerStep + 8;
        for (var step = 0L; step < steps; step++)
        {
            await bridge.CompleteRunAsync(world.TeamId, world.AgentRunId, WorkerFenceEpoch, cancellationToken);
            if ((await StreamAsync(world, cancellationToken)).State != AgentRunLogStreamState.Open) return;
        }

        var stalled = await StreamAsync(world, cancellationToken);
        throw new Xunit.Sdk.XunitException($"Manifest verification never sealed the stream within {steps} terminalization steps; it is still {stalled.State} at segment_count {stalled.SegmentCount}. Inspect the checkpoint with: SELECT next_segment_ordinal, verified_bytes, sealed_at FROM agent_run_log_verification WHERE stream_id = '{stalled.Id}'");
    }

    private static void Report(long totalBytes, long expectedSegments, TimeSpan elapsed, long allocated, long liveHeap) =>
        Console.WriteLine($"[large-capture] {totalBytes / 1024 / 1024} MiB over {expectedSegments} segments in {elapsed.TotalSeconds:F1}s; allocated {allocated / 1024 / 1024} MiB ({(double)allocated / totalBytes:F2} copies/byte), live heap delta {liveHeap / 1024 / 1024} MiB");

    /// <summary>The real container with one substitution: the driver factory the real broker will activate for this profile's provider key.</summary>
    private ILifetimeScope ScopeOver(SpanLedger ledger) => fixture.BeginScope(builder =>
        builder.RegisterInstance(new SpanLedgerCatalog(new SpanLedgerFactory(ledger))).As<IArtifactStorageDriverFactoryCatalog>().SingleInstance());

    private static AgentRunLogCaptureBridge BridgeOver(ILifetimeScope scope, World world)
    {
        var logs = new AgentRunLogService(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), scope.Resolve<IArtifactCasRuntimeCoordinator>(), TimeProvider.System);

        return new AgentRunLogCaptureBridge(logs, new PinnedStorageResolver(world.StorageProfileId), scope.Resolve<IAgentRunLogCaptureRecoveryService>(),
            NullLogger<AgentRunLogCaptureBridge>.Instance, LargeCaptureBudget, logs);
    }

    private static AgentRunLogCaptureOpenRequest OpenCapture(World world, SyntheticLogSource source) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, ActorId = world.ActorId, WorkerFenceEpoch = WorkerFenceEpoch,
        Handle = new SandboxHandle { Kind = "synthetic", ProcessId = 1, SpoolDirectory = "/opaque", Deadline = DateTimeOffset.MaxValue, AgentRunLogCaptureSessionId = Guid.NewGuid() },
        Source = source, Redactor = SecretRedactor.None,
    };

    private async Task<AgentRunLogStream> StreamAsync(World world, CancellationToken cancellationToken)
    {
        using var scope = fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRunLogStream.AsNoTracking()
            .SingleAsync(value => value.TeamId == world.TeamId && value.AgentRunId == world.AgentRunId && value.StreamKind == AgentRunLogKinds.StandardOutput, cancellationToken);
    }

    /// <summary>
    /// Every segment of the stream in ordinal order, each with its CAS identity and the provider key it was written
    /// under. Placements are read separately and folded rather than joined: an object may legitimately carry more than
    /// one <see cref="ArtifactLocation"/> row, and a join would then silently duplicate the segment and break the
    /// ordinal chain the caller is about to assert, for a reason that has nothing to do with capture.
    /// </summary>
    private async Task<IReadOnlyList<SegmentRow>> SegmentsAsync(World world, Guid streamId, CancellationToken cancellationToken)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var rows = await (from segment in db.AgentRunLogSegment.AsNoTracking()
                          join artifact in db.ArtifactObject.AsNoTracking() on new { segment.TeamId, Id = segment.ArtifactObjectId } equals new { artifact.TeamId, artifact.Id }
                          where segment.TeamId == world.TeamId && segment.StreamId == streamId
                          orderby segment.SegmentOrdinal
                          select new { segment.SegmentOrdinal, segment.StartOffsetBytes, segment.LengthBytes, segment.ArtifactObjectId, artifact.Digest })
            .ToListAsync(cancellationToken);
        var placements = await db.ArtifactLocation.AsNoTracking().Where(value => value.TeamId == world.TeamId)
            .Select(value => new { value.ArtifactObjectId, value.ObjectKey }).ToListAsync(cancellationToken);
        var keys = placements.GroupBy(value => value.ArtifactObjectId).ToDictionary(group => group.Key, group => group.Select(value => value.ObjectKey).Distinct(StringComparer.Ordinal).ToArray());

        return rows.Select(row => new SegmentRow(row.SegmentOrdinal, row.StartOffsetBytes, row.LengthBytes, row.ArtifactObjectId, row.Digest, keys.GetValueOrDefault(row.ArtifactObjectId, []))).ToList();
    }

    /// <summary>A team whose log storage route is Active and whose credential resolves, so the only synthetic thing in the write path is the destination's byte store.</summary>
    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"agent-log-large-{actorId:N}@test.local", Name = "Agent Log Large" });
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-log-large-{teamId:N}", Name = "Agent Log Large", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        db.StorageCredential.Add(Credential(credentialId, teamId, actorId, now, scope.Resolve<IPayloadEncryptor>()));
        db.StorageProfile.Add(Profile(profileId, teamId, credentialId, actorId, now));
        await db.SaveChangesAsync();

        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Running, TaskJson = "{}",
            FenceEpoch = WorkerFenceEpoch, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();

        return new World(teamId, actorId, profileId, runId);
    }

    private static StorageCredential Credential(Guid credentialId, Guid teamId, Guid actorId, DateTimeOffset now, IPayloadEncryptor encryptor)
    {
        var credential = new StorageCredential
        {
            Id = credentialId, TeamId = teamId, StableName = $"agent-log-large-{credentialId:N}",
            CurrentRevision = 1, State = StorageCredentialState.Active, CreatedDate = now, CreatedBy = actorId,
        };
        credential.Revisions.Add(new StorageCredentialRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageCredentialId = credentialId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, EncryptedPayload = encryptor.Encrypt("{}"),
            SafeHint = "safe", EnvelopeFingerprint = $"sha256:{new string('b', 64)}", CreatedDate = now, CreatedBy = actorId,
        });

        return credential;
    }

    private static StorageProfile Profile(Guid profileId, Guid teamId, Guid credentialId, Guid actorId, DateTimeOffset now)
    {
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"agent-log-large-{profileId:N}", State = StorageProfileState.Active,
            CurrentRevision = 1, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = "{\"rootPath\":\"/unused-by-the-ledger-driver\"}",
            CredentialRef = $"db:{credentialId:D}:1", NamespaceFingerprint = $"sha256:{new string('a', 64)}",
            CreatedDate = now, CreatedBy = actorId,
        });

        return profile;
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid StorageProfileId, Guid AgentRunId);

    /// <summary>One segment as PostgreSQL holds it, with its CAS identity and every distinct provider key its object was placed under.</summary>
    private sealed record SegmentRow(long Ordinal, long StartOffset, long Length, Guid ArtifactObjectId, byte[] Digest, string[] ObjectKeys);

    private sealed class PinnedStorageResolver(Guid profileId) : IAgentRunLogStorageResolver
    {
        public Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentRunLogStorageResolution>(new AgentRunLogStorageResolution.Ready(profileId, 1));
    }
}
