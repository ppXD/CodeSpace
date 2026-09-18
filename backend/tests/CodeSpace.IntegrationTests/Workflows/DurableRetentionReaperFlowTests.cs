using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Core.Services.Workflows.Retention;
using CodeSpace.Core.Services.Workflows.Retention.Cursors;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The durable-retention plane against a real database and a real local-rwx destination, where the parts that cannot
/// be unit-tested actually live: the guard trigger that decides which retention statements PostgreSQL admits, the pin
/// rows a seal writes in its own transaction, and the order in which bytes and their tombstone commit.
///
/// <para>The positive control at the top is what proves the counter-examples below it are not passing vacuously: each
/// of those builds a stream that is ONE property away from collectable and asserts its bytes survive.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DurableRetentionReaperFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public DurableRetentionReaperFlowTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The positive control, and the ordering claim with it: the bytes leave the destination FIRST and the head row's
    /// tombstone commits after, so a crash between them leaves bytes gone and a row the next sweep finishes — never a
    /// row that says purged about bytes still being paid for.
    /// </summary>
    [Fact]
    public async Task A_terminal_unpinned_log_stream_past_its_rule_is_purged_blobs_first_then_rows_and_the_Room_reads_purged()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "the archived transcript of a run nobody opened again");
        await AgeTerminalAsync(stream, TimeSpan.FromDays(31));

        var first = await SweepAsync();

        first.Quarantined.ShouldBeGreaterThanOrEqualTo(1, "the first uncited observation only starts the quarantine clock");
        (await StreamAsync(stream)).PurgedAt.ShouldBeNull("the sweep that first noticed the stream must not touch its bytes");
        (await StreamAsync(stream)).RetainUntil.ShouldNotBeNull("the quarantine deadline has to be durable — an in-memory one is no wait at all");
        (await BytesReadableAsync(world, stream)).ShouldBeTrue();

        await ElapseQuarantineAsync(stream);
        var second = await SweepAsync();

        second.Collected.ShouldBeGreaterThanOrEqualTo(1);
        var purged = await StreamAsync(stream);
        purged.PurgedAt.ShouldNotBeNull("the head row is the tombstone: it outlives its bytes so a reader is told they were reclaimed");
        purged.State.ShouldBe(AgentRunLogStreamState.Completed, "a purge is not a capture verdict — the state it settled in is untouched");
        purged.TotalBytes.ShouldBeGreaterThan(0, "the byte head still records what was captured; only the bytes themselves are gone");
        (await UnpurgedLocationsAsync(world, stream)).ShouldBe(0, "every location behind the stream's segments must be drained before the tombstone commits");
        (await BytesReadableAsync(world, stream)).ShouldBeFalse("the destination no longer holds the object");
        RoomProjector.SummarizeLogs([Row(purged)]).Status.ShouldBe(RoomAgentLogStatus.Purged,
            "the Room must say purged rather than claim an integrity proof over bytes that are gone");
    }

    /// <summary>
    /// The pin-table-first property, from the other side: a stream a sealed qualification result cites is never
    /// collected, however long both windows have been open. Mutation: drop the pin probe in
    /// <c>LogStreamRetentionCursor.ClassifyAsync</c> and this test reds with the stream purged.
    /// </summary>
    [Fact]
    public async Task A_pinned_log_stream_past_its_rule_survives()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "evidence a capability claim was computed from");
        await SealResultAsync(world);
        await AgeTerminalAsync(stream, TimeSpan.FromDays(400));

        await SweepAsync();

        (await StreamAsync(stream)).RetainUntil.ShouldBeNull("a cited stream is never even quarantined — the verdict is keep, not a wait");

        // The strongest form of the claim: a quarantine deadline that has ALREADY elapsed, so the only thing left
        // between the reaper and the bytes is the pin.
        await ElapseQuarantineAsync(stream);
        await SweepAsync();

        var kept = await StreamAsync(stream);
        kept.PurgedAt.ShouldBeNull("a sealed result still cites this stream; no elapsed window outranks that");
        (await BytesReadableAsync(world, stream)).ShouldBeTrue();
        (await UnpurgedLocationsAsync(world, stream)).ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The atomicity claim, measured rather than asserted: <c>xmin</c> is the id of the transaction that inserted a
    /// row, so a result and its pins sharing one <c>xmin</c> IS "written in the same transaction". Mutation: move the
    /// pin write to its own <c>SaveChanges</c> (or its own scope) and the two ids differ — which is the crash window
    /// in which a sealed result exists whose evidence the reaper is free to reclaim.
    /// </summary>
    [Fact]
    public async Task A_sealed_result_pins_every_stream_and_artifact_it_cites_in_one_transaction()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "one archived stream");
        var (receiptId, artifactId) = await SeedCitedRecordsAsync(world);

        var groupId = await SealResultAsync(world);

        var pins = await PinsAsync(groupId);
        pins.Where(pin => pin.Kind == DurablePinKind.AgentRun).Select(pin => pin.Target).ShouldBe([world.AgentRunId]);
        pins.Where(pin => pin.Kind == DurablePinKind.LogStream).Select(pin => pin.Target).ShouldBe([stream]);
        pins.Where(pin => pin.Kind == DurablePinKind.CleanupReceipt).Select(pin => pin.Target).ShouldBe([receiptId]);
        pins.Where(pin => pin.Kind == DurablePinKind.Artifact).Select(pin => pin.Target).ShouldBe([artifactId]);
        pins.ShouldAllBe(pin => (pin.Kind == DurablePinKind.Artifact) == (pin.PinnedArtifactId != null),
            "an artifact pin has to sit in the column the artifact oracle probes by name, and nothing else may");

        var transactions = await InsertingTransactionsAsync(groupId);
        transactions.Count.ShouldBe(1, $"the result and its {pins.Count} pins must commit together; they were inserted by {transactions.Count} transactions");

        ArtifactReferenceOracle.ReferenceSites.ShouldContain(("paired_qualification_result_pin", "pinned_artifact_id"),
            "an artifact a sealed result pins has to be a probed reference site, or the ARTIFACT reaper collects what this plane is protecting");
    }

    /// <summary>
    /// Bytes before rows, proven by the failure: with the destination gone the purge is refused, and the sweep must
    /// leave the head row exactly as it found it. Mutation: stamp the tombstone before (or without) the byte removal
    /// and this reds — the Room would then read "purged" about an archive still sitting at the destination, and no
    /// later sweep would ever reclaim it, because a purged row is never claimed again.
    /// </summary>
    [Fact]
    public async Task A_refused_byte_purge_leaves_the_tombstone_unwritten()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "bytes at a destination that stops answering");
        await AgeTerminalAsync(stream, TimeSpan.FromDays(31));
        await SweepAsync();
        await ElapseQuarantineAsync(stream);

        var sweep = await SweepAgainstARefusingDestinationAsync();

        sweep.Collected.ShouldBe(0, "nothing may be reported collected while the destination refuses to give the bytes up");
        var kept = await StreamAsync(stream);
        kept.PurgedAt.ShouldBeNull("the tombstone must never run ahead of the bytes");
        (await UnpurgedLocationsAsync(world, stream)).ShouldBeGreaterThan(0, "a refused delete leaves the location exactly where it was");
        kept.RetainUntil.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow,
            "the stream is deferred rather than retried every tick, so one unreachable destination cannot own the batch");
    }

    /// <summary>
    /// Migration 0236's arm, at the only place that can pin it: the database. Everything the guard refuses here is a
    /// statement that would let a retention write masquerade as something else — or let a purge claim a wait it never
    /// served.
    /// </summary>
    [Theory]
    [InlineData("purged_at = now()", "cannot be purged without the retain_until")]
    [InlineData("retain_until = now(), state = 'Corrupt'", "cannot rewrite anything but its own retention columns")]
    [InlineData("retain_until = now(), total_bytes = 0", "cannot rewrite anything but its own retention columns")]
    [InlineData("retain_until = now(), completed_at = now()", "cannot rewrite anything but its own retention columns")]
    public async Task The_guard_refuses_a_retention_statement_that_says_more_than_retention(string smuggled, string refusal)
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "a settled stream");
        using var scope = _fixture.BeginScope();
        var sql = $"UPDATE agent_run_log_stream SET {smuggled}, revision = revision + 1, last_modified_at = now() WHERE team_id = {{0}} AND id = {{1}}";

        var raised = await Should.ThrowAsync<Exception>(async () =>
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(sql, [world.TeamId, stream]));

        PostgresErrorOf(raised).MessageText.ShouldContain(refusal);
        var row = await StreamAsync(stream);
        row.RetainUntil.ShouldBeNull("a refused statement leaves the row exactly as it was");
        row.PurgedAt.ShouldBeNull();
    }

    /// <summary>A live capture is never a retention candidate, and the guard is what makes that true rather than a query predicate somebody can forget.</summary>
    [Fact]
    public async Task The_guard_refuses_a_retention_statement_on_an_open_stream()
    {
        var world = await SeedWorldAsync();
        using var scope = _fixture.BeginScope();
        var logs = Logs(scope);
        var session = Guid.NewGuid();
        var opened = (await logs.OpenAsync(Open(world, session), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();

        var raised = await Should.ThrowAsync<Exception>(async () => await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE agent_run_log_stream SET retain_until = now(), revision = revision + 1, last_modified_at = now() WHERE team_id = {0} AND id = {1}",
            [world.TeamId, opened.Metadata.StreamId]));

        PostgresErrorOf(raised).MessageText.ShouldContain("rejected on a live stream");
    }

    /// <summary>A purge is final: nothing may put a stream back into a state where it claims to hold bytes it gave up.</summary>
    [Fact]
    public async Task The_guard_refuses_to_un_purge_a_purged_stream()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "a stream that will be reclaimed");
        await AgeTerminalAsync(stream, TimeSpan.FromDays(31));
        await SweepAsync();
        await ElapseQuarantineAsync(stream);
        await SweepAsync();
        (await StreamAsync(stream)).PurgedAt.ShouldNotBeNull();

        using var scope = _fixture.BeginScope();
        var raised = await Should.ThrowAsync<Exception>(async () => await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE agent_run_log_stream SET purged_at = NULL, revision = revision + 1, last_modified_at = now() WHERE team_id = {0} AND id = {1}",
            [world.TeamId, stream]));

        PostgresErrorOf(raised).MessageText.ShouldContain("purge is final");
    }

    // ── The world, and the durable stream inside it ────────────────────────────────────────────────────────────────

    /// <summary>One real capture through the real service: an open, one segment at a real local-rwx destination, a finalized source and a v3 completion.</summary>
    private async Task<Guid> CaptureAsync(World world, string text)
    {
        using var scope = _fixture.BeginScope();
        var logs = Logs(scope);
        var session = Guid.NewGuid();
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var opened = (await logs.OpenAsync(Open(world, session), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        var appended = (await logs.AppendAsync(new AgentRunLogAppendRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId, WorkerFenceEpoch = Fence,
            CaptureSessionId = session, ExpectedSegmentOrdinal = 1, ExpectedOffsetBytes = 0, ExpectedSourceOffsetBytes = 0,
            SourceLengthBytes = bytes.Length, StorageProfileId = world.StorageProfileId, StorageProfileRevision = 1,
            ActorId = world.ActorId, Bytes = bytes,
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
        var finalized = (await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId, WorkerFenceEpoch = Fence,
            CaptureSessionId = session, ExpectedRevision = appended.Metadata.Revision, ExpectedSourceOffsetBytes = appended.Metadata.SourceOffsetBytes,
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogFinalizeSourceResult.Finalized>();
        (await logs.CompleteAsync(new AgentRunLogCompleteRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId, WorkerFenceEpoch = Fence,
            CaptureSessionId = session, ExpectedRevision = finalized.Metadata.Revision,
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();

        return opened.Metadata.StreamId;
    }

    /// <summary>
    /// Time travel for the age floor only. The trigger is suspended for this one statement because the guard admits
    /// no rewrite of <c>completed_at</c> at all — which is exactly the property the theory above pins — and the floor
    /// is measured in days that no test can wait out.
    /// </summary>
    private async Task AgeTerminalAsync(Guid streamId, TimeSpan age)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE agent_run_log_stream DISABLE TRIGGER agent_run_log_stream_enforce_invariants");
        try
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE agent_run_log_stream SET completed_at = completed_at - {0}::interval WHERE id = {1}",
                [$"{age.TotalSeconds} seconds", streamId]);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE agent_run_log_stream ENABLE TRIGGER agent_run_log_stream_enforce_invariants");
        }
    }

    /// <summary>Pulls the quarantine deadline the sweep itself wrote into the past — through the guard, since that is a statement it admits.</summary>
    private async Task ElapseQuarantineAsync(Guid streamId)
    {
        using var scope = _fixture.BeginScope();
        var updated = await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE agent_run_log_stream SET retain_until = now() - interval '1 second', revision = revision + 1, last_modified_at = now() WHERE id = {0}", [streamId]);

        updated.ShouldBe(1);
    }

    /// <summary>
    /// The real reaper over the real cursor, with only the CAS purge verdict swapped for the one a destination that
    /// will not give the bytes up produces. Everything that decides whether the tombstone is written is production
    /// code; the refusal is the single injected fact.
    /// </summary>
    private async Task<Messages.Retention.DurableRetentionSweepSummary> SweepAgainstARefusingDestinationAsync()
    {
        using var scope = _fixture.BeginScope();
        var options = scope.Resolve<DbContextOptions<CodeSpaceDbContext>>();
        var cursor = new LogStreamRetentionCursor(options, new RefusingPurgeCoordinator(scope.Resolve<IArtifactCasPurgeCoordinator>()), NullLogger<LogStreamRetentionCursor>.Instance);

        return await new DurableRetentionReaper(options, [cursor], NullLogger<DurableRetentionReaper>.Instance).SweepAsync(CancellationToken.None);
    }

    /// <summary>A destination that answers every delete with a refusal and no effect — the shape a revoked key or a read-only bucket produces.</summary>
    private sealed class RefusingPurgeCoordinator : IArtifactCasPurgeCoordinator
    {
        private readonly IArtifactCasPurgeCoordinator _inner;

        public RefusingPurgeCoordinator(IArtifactCasPurgeCoordinator inner) { _inner = inner; }

        public Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactCasPurgeResult>(new ArtifactCasPurgeResult.Rejected(new ArtifactCasProblem(ArtifactCasProblemCode.ProviderUnavailable, true)));

        public Task<ArtifactCasPurgeClaimResult> ClaimAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) => _inner.ClaimAsync(request, cancellationToken);
        public Task<ArtifactCasPurgeResult> DeleteAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) => _inner.DeleteAsync(claim, cancellationToken);
        public Task<ArtifactCasReleaseOutcome> ReleaseAsync(ArtifactCasPurgeClaim claim, ArtifactCasReleaseEvidence evidence, CancellationToken cancellationToken) => _inner.ReleaseAsync(claim, evidence, cancellationToken);
        public Task<ArtifactCasAbandonResult> AbandonAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) => _inner.AbandonAsync(claim, cancellationToken);
    }

    // ── What a sealed result cites ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A cleanup receipt and an offloaded event payload for the same run, so the seal has all four pin kinds to write.</summary>
    private async Task<(Guid ReceiptId, Guid ArtifactId)> SeedCitedRecordsAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var artifactId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        db.WorkflowArtifact.Add(new WorkflowArtifact
        {
            Id = artifactId, TeamId = world.TeamId, ContentType = "application/json", SizeBytes = 2,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2])).ToLowerInvariant(), InlineBytes = [1, 2],
        });
        db.AgentRunCleanupReceipt.Add(new AgentRunCleanupReceiptRecord
        {
            Id = receiptId, TeamId = world.TeamId, AgentRunId = world.AgentRunId, FenceEpoch = Fence,
            Kind = Messages.Agents.Recovery.RunResourceKind.Spool, Outcome = Messages.Agents.Recovery.RunResourceOutcome.Completed,
            RecordedByHost = "retention-test", RecordedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        db.AgentRunEvent.Add(new AgentRunEvent
        {
            Id = Guid.NewGuid(), AgentRunId = world.AgentRunId, Kind = Messages.Agents.AgentEventKind.Warning,
            Text = "an offloaded payload", DataArtifactId = artifactId,
        });
        await db.SaveChangesAsync();

        return (receiptId, artifactId);
    }

    /// <summary>
    /// A real seal through the real store: a hand-seeded protocol whose exact observation census is present, sealed as
    /// a replay so the runtime gate — which is a different slice's concern — stays out of this one.
    /// </summary>
    private async Task<Guid> SealResultAsync(World world)
    {
        var groupId = Guid.NewGuid();
        var manifest = new EvalSuiteManifest { Version = "sha256/retention-suite:v1", Cells = [new CorpusCellRef { TaskId = "task-a", Mode = BenchmarkMode.HarnessCli }] };
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.PairedQualificationProtocol.Add(new PairedQualificationProtocol
        {
            ObservationGroupId = groupId, TeamId = world.TeamId, SuiteDigest = "sha256:hidden", SuiteVersion = manifest.Version,
            CodeRevision = new string('c', 40), ControlModelRowId = world.ControlModelRowId, CandidateModelRowId = world.CandidateModelRowId,
            RequiresCellAdmission = false, RequiresResultDigest = false, StatisticsVersion = PairedQualificationOutcome.StatisticsVersion,
            Criterion = "Quality", SessionsPerCell = 1, MinimumIndependentClusters = 1, MinimumStrata = 1, MinimumRequiredExecutionClusters = 1,
            MinimumEvaluatorHealth = 1, MaxCostUsdPerLaunch = 3m, MinimumQualityLift = 0.05, OrderingSeed = "frozen-order",
            ProtocolDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(groupId.ToByteArray())),
        });
        foreach (var arm in new[] { "control", "candidate" })
            db.BenchmarkResultRecord.Add(Observation(world, groupId, manifest.Version, arm));
        await db.SaveChangesAsync();

        var outcome = Outcome(groupId, manifest.Version, db.PairedQualificationProtocol.Single(row => row.ObservationGroupId == groupId).ProtocolDigest);
        var store = new PairedQualificationResultStore(db, scope.Resolve<IQualificationRuntimeGate>());
        await store.SealAsync(new PairedQualificationSealRequest
        {
            ObservationGroupId = groupId, Manifest = manifest, Outcome = outcome, Source = PairedQualificationSealSource.Replay,
        }, CancellationToken.None);

        return groupId;
    }

    private BenchmarkResultRecord Observation(World world, Guid groupId, string suiteVersion, string arm) => new()
    {
        Id = Guid.NewGuid(), TeamId = world.TeamId, SuiteVersion = suiteVersion, TaskId = "task-a", Mode = BenchmarkMode.HarnessCli.ToString(),
        Harness = "claude-code", Model = arm, ModelCredentialModelId = arm == "control" ? world.ControlModelRowId : world.CandidateModelRowId,
        ObservationGroupId = groupId, ObservationArm = arm, ObservationSession = 0, AgentRunId = world.AgentRunId,
        ObservedModel = arm, OutcomeState = "Solved", Solved = true, RunStatus = AgentRunStatus.Succeeded.ToString(),
        MaxCostUsd = 3m, CostUsd = 1m, CostIndeterminate = false, GitSha = new string('c', 40),
    };

    private static PairedQualificationOutcome Outcome(Guid groupId, string suiteVersion, string protocolDigest) => new()
    {
        ObservationGroupId = groupId, ProtocolDigest = protocolDigest, CodeRevision = new string('c', 40), SuiteDigest = "sha256:hidden",
        SuiteVersion = suiteVersion, IndependentClusters = 1, PairedCells = 1, RequiredExecutionClusters = 1,
        Control = Arm(), Candidate = Arm(), QualityDifference = 0.1, QualityDifferenceLower95 = 0.06,
        RequiredExecutionComplete = true, QualifiedForCapabilityClaim = true, BlockingReasons = [], Strata = [], InfraFailures = [],
    };

    private static PairedArmQualificationSummary Arm() => new()
    {
        Solved = 1, BudgetAdmissibleSolved = 1, Total = 1, InfraUnknown = 0, CostKnownCells = 1,
        CapabilityVerdictCells = 1, ObservedModelCells = 1, EvaluatorHealth = 1, ObservedModels = ["model"],
    };

    // ── Reads ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<Messages.Retention.DurableRetentionSweepSummary> SweepAsync()
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IDurableRetentionReaper>().SweepAsync(CancellationToken.None);
    }

    private async Task<AgentRunLogStream> StreamAsync(Guid streamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRunLogStream.AsNoTracking().SingleAsync(row => row.Id == streamId);
    }

    private async Task<IReadOnlyList<PairedQualificationResultPin>> PinsAsync(Guid resultId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().PairedQualificationResultPin.AsNoTracking()
            .Where(pin => pin.ResultId == resultId).OrderBy(pin => pin.Kind).ToListAsync();
    }

    /// <summary>The ids of the transactions that inserted the result and its pins. One id is the whole atomicity claim.</summary>
    private async Task<IReadOnlyList<string>> InsertingTransactionsAsync(Guid resultId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Database.SqlQueryRaw<string>(
            "SELECT DISTINCT xmin::text AS \"Value\" FROM paired_qualification_result WHERE observation_group_id = {0} "
            + "UNION SELECT DISTINCT xmin::text FROM paired_qualification_result_pin WHERE result_id = {0}", resultId).ToListAsync();
    }

    private async Task<int> UnpurgedLocationsAsync(World world, Guid streamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        return await db.ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == world.TeamId && location.State != ArtifactLocationState.Purged && location.State != ArtifactLocationState.Deleted)
            .Join(db.AgentRunLogSegment.AsNoTracking().Where(segment => segment.StreamId == streamId),
                location => location.ArtifactObjectId, segment => segment.ArtifactObjectId, (location, _) => location.Id)
            .CountAsync();
    }

    /// <summary>Whether the stream's bytes still come back through the production read path — the only reading of "the bytes are there" that matters.</summary>
    private async Task<bool> BytesReadableAsync(World world, Guid streamId)
    {
        using var scope = _fixture.BeginScope();
        var stream = await StreamAsync(streamId);
        var read = await Logs(scope).ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, streamId, 0, checked((int)stream.TotalBytes)), CancellationToken.None);

        return read is AgentRunLogRangeResult.Available;
    }

    private static RoomProjector.AgentLogRow Row(AgentRunLogStream stream) =>
        new(stream.AgentRunId, stream.State, stream.SchemaVersion, stream.ManifestDigest != null, stream.RemoteStallSince != null, stream.PurgedAt != null);

    private static AgentRunLogService Logs(ILifetimeScope scope) =>
        new(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), scope.Resolve<IArtifactCasRuntimeCoordinator>(), TimeProvider.System);

    private static AgentRunLogOpenRequest Open(World world, Guid session) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, WorkerFenceEpoch = Fence, CaptureSessionId = session,
        StreamKind = AgentRunLogKinds.StandardOutput, ContentType = AgentRunLogRepresentations.PlainTextContentType,
        ContentEncoding = AgentRunLogRepresentations.Utf8ContentEncoding, CaptureSource = "retention-test/v1",
    };

    /// <summary>EF's execution strategy wraps the server error; the guard's own message stays decisive.</summary>
    private static PostgresException PostgresErrorOf(Exception error)
    {
        while (error is not PostgresException && error.InnerException is { } inner) error = inner;
        return error.ShouldBeOfType<PostgresException>();
    }

    private const long Fence = 7;

    /// <summary>A team whose log storage route is Active, whose credential resolves, and whose local-rwx root is real — so a refused purge is only ever the one the test caused.</summary>
    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var root = NewRoot();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"durable-retention-{actorId:N}@test.local", Name = "Durable Retention" });
        db.Team.Add(new Team { Id = teamId, Slug = $"durable-retention-{teamId:N}", Name = "Durable Retention", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        var credential = new StorageCredential
        {
            Id = credentialId, TeamId = teamId, StableName = $"durable-retention-{credentialId:N}",
            CurrentRevision = 1, State = StorageCredentialState.Active, CreatedDate = now, CreatedBy = actorId,
        };
        credential.Revisions.Add(new StorageCredentialRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageCredentialId = credentialId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt("{}"),
            SafeHint = "safe", EnvelopeFingerprint = $"sha256:{new string('b', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageCredential.Add(credential);
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"durable-retention-{profileId:N}", State = StorageProfileState.Active,
            CurrentRevision = 1, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = $"{{\"rootPath\":\"{root.Replace("\\", "\\\\")}\"}}",
            CredentialRef = $"db:{credentialId:D}:1", NamespaceFingerprint = $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(profileId.ToByteArray())).ToLowerInvariant()}",
            CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        var runId = Guid.NewGuid();
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Running, TaskJson = "{}",
            FenceEpoch = Fence, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();

        return new World(teamId, actorId, profileId, runId, Guid.NewGuid(), Guid.NewGuid());
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-durable-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid StorageProfileId, Guid AgentRunId, Guid ControlModelRowId, Guid CandidateModelRowId);
}
