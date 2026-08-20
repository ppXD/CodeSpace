using System.Security.Cryptography;
using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.RunData;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The native-record capture plane as a PRODUCER of the run data manifest, against the real plane and real Postgres
/// (Rule 12 high fidelity). Every invariant this slice leans on is a database one — 0146's fail-closed CHECK, its
/// per-run advisory rendezvous, and the statement-level trigger that downgrades a complete verdict when a gap arrives —
/// so a unit tier could only assert this producer's constants back at itself.
///
/// <para><b>What only this tier can execute</b>, named so its absence from a local unit run is not mistaken for
/// coverage: that the rows this producer writes are ones 0146 ACCEPTS (a complete verdict is refused over an
/// indeterminate expectation, a shortfall, or an open gap, and this producer must never propose one); that a refused
/// batch's gap is committed on its OWN, so no claim about the record can take the bad news down with it; and that a
/// gap and a completeness statement racing each other cannot leave the run complete beside an open gap, which is a
/// claim about two triggers and a lock and about nothing in C#.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NativeRecordCompletenessFlowTests
{
    private const string PricedModel = "claude-sonnet-4-6";

    private readonly PostgresFixture _fixture;

    public NativeRecordCompletenessFlowTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The headline, and the whole point of giving the manifest a producer: a run whose frames all landed leaves a
    /// STATEMENT saying so, in the facet's own name, with no gap beside it. A masked frame reaches the other strictly
    /// readable verdict rather than the same one — a redacted record is still a whole one, and calling it Exact would
    /// claim the stored bytes are verbatim when they are not.
    /// </summary>
    [Theory]
    [InlineData(NativeRecordRedaction.None, WorkflowRunCaptureCompleteness.Exact)]
    [InlineData(NativeRecordRedaction.Masked, WorkflowRunCaptureCompleteness.RedactedExact)]
    public async Task A_capture_that_lands_every_frame_states_a_complete_manifest(NativeRecordRedaction redaction, WorkflowRunCaptureCompleteness verdict)
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var handle = await OpenAsync(plane, run);

        await plane.WriteAsync(Batch(handle, Frame(handle, 0, redaction), Frame(handle, 1, redaction)), CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var statement = await db.WorkflowRunDataManifest.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId);

        statement.ShouldNotBeNull(
            customMessage: "a run that captured frames must leave a completeness statement — a manifest nothing produces is a table whose only writers are its tests");
        statement.Facet.ShouldBe(WorkflowRunDataOwnerKinds.NativeRecord);
        statement.ExpectedRecordCount.ShouldBe(2, customMessage: "the plane undertook two frames, and a determinate expectation is what a complete verdict is allowed to rest on");
        statement.PresentRecordCount.ShouldBe(2);
        statement.KnownMissingCount.ShouldBe(0);
        statement.Verdict.ShouldBe(verdict);
        statement.Verdict.IsStrictlyReadable().ShouldBeTrue();

        (await db.WorkflowRunCaptureGap.CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId)).ShouldBe(0,
            customMessage: "nothing was missing, so nothing may be recorded as missing");
    }

    /// <summary>
    /// The shortfall the plane can actually have: a durable write the database refuses. Today that is a log warning and
    /// a round that quietly stops capturing — the silence this plane exists to break. The refused frames must become a
    /// gap a human can locate, and the statement must stop reading complete.
    /// </summary>
    [Fact]
    public async Task A_refused_batch_becomes_a_locatable_gap_and_un_completes_the_statement()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var handle = await OpenAsync(plane, run);

        await plane.WriteAsync(Batch(handle, Frame(handle, 0), Frame(handle, 1)), CancellationToken.None);

        // Ordinal 1 is already recorded on this stream, and ux_workflow_run_native_record_ordinal refuses the second
        // copy — a real refusal of a real durable write, not a mocked one.
        await Should.ThrowAsync<DbUpdateException>(() =>
            plane.WriteAsync(Batch(handle, Frame(handle, 1), Frame(handle, 2)), CancellationToken.None));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var gap = await db.WorkflowRunCaptureGap.AsNoTracking().SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId);
        gap.SubjectKind.ShouldBe(WorkflowRunDataOwnerKinds.NativeRecord);
        gap.Reason.ShouldBe(CaptureGapReason.WriteRefused);
        gap.RangeKind.ShouldBe(CaptureGapRangeKind.Ordinal);
        gap.StreamId.ShouldBe(handle.StreamId, customMessage: "an ordinal with no stream to be an ordinal IN is a coordinate nobody can locate");
        gap.Channel.ShouldBe(handle.Channel);
        gap.RangeStart.ShouldBe(1, customMessage: "the first frame of the refused batch is where this stream's records stop");
        gap.RangeEnd.ShouldBeNull(customMessage: "capture stops for the round on a refusal, so 'from here on, and I do not know how much' is the honest extent");
        gap.Resolution.ShouldBe(CaptureGapResolution.Open);
        gap.ReasonDetail.ShouldNotBeNullOrWhiteSpace(customMessage: "a gap with no reason detail is a hole with a label on it");

        var statement = await db.WorkflowRunDataManifest.AsNoTracking().SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId);
        statement.Verdict.IsStrictlyReadable().ShouldBeFalse(
            customMessage: "a known-missing span un-completes the statement; a verdict that read complete beside it would be the false assurance this plane exists to refuse");
        statement.KnownMissingCount.ShouldBe(1);
        statement.PresentRecordCount.ShouldBe(2, customMessage: "the refused frames never landed, so they were never present");
        statement.ExpectedRecordCount.ShouldBe(4, customMessage: "the plane undertook four frames and two of them are not here — the shortfall must be visible in the counts too, not only in the gap");
    }

    /// <summary>
    /// ARRIVAL ORDER may not decide the outcome. A completeness statement and a gap for the same run are written by two
    /// connections at once, many times: whichever wins, the run must never end up with a strictly readable statement
    /// beside an open gap, AND the statement must still be there.
    ///
    /// <para>Both halves have teeth, and the second is the one that is easy to miss. The plane holds no lock and opens
    /// no transaction of its own here: the rendezvous is the FIRST statement of
    /// <c>workflow_run_data_manifest_advance</c> (0148), which is what makes the gap probe and the write it feeds see
    /// one set even though 0146's guard re-probes them under the same lock inside the trigger. Move the probe back
    /// outside that function and this test fails with no statement at all — the guard refuses the claim, the
    /// containment swallows it, and the counts it carried are a DELTA, so a lost one understates the run's expectation
    /// for good. <c>RunDataCompletenessRendezvousTests</c> pins the same property directly on the function.</para>
    /// </summary>
    [Fact]
    public async Task A_statement_and_a_gap_racing_each_other_never_leave_a_complete_manifest_beside_an_open_gap()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var run = await SeedWorkflowBoundRunAsync();
            var plane = Plane(out var planeScope);
            using var scope = planeScope;

            var handle = await OpenAsync(plane, run);

            await Task.WhenAll(
                plane.WriteAsync(Batch(handle, Frame(handle, 0)), CancellationToken.None),
                SeedGapAsync(run));

            using var reader = _fixture.BeginScope();
            var db = reader.Resolve<CodeSpaceDbContext>();

            var open = await db.WorkflowRunCaptureGap.CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Resolution == CaptureGapResolution.Open);
            var statement = (await db.WorkflowRunDataManifest.AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId))
                .ShouldNotBeNull(customMessage: $"attempt {attempt}: the race cost the run its statement entirely — the guard refused a claim computed outside the rendezvous, and the delta it carried is gone");

            open.ShouldBe(1, customMessage: "the gap must be admitted whatever the statement claims — refusing the honest observation to protect the claim is the exact inversion this plane exists to prevent");
            statement.Verdict.IsStrictlyReadable().ShouldBeFalse(
                customMessage: $"attempt {attempt}: the run ended up complete beside an open gap, so the two writers committed blind to each other");
        }
    }

    /// <summary>
    /// The INDETERMINATE arm, and the only run this producer cannot state an expectation for: one whose observer died
    /// inside the capture window. Its process attempt is still Running when the execution is terminalized, so nobody
    /// knows how many frames it had read and not yet made durable — and an expectation nobody could establish must not
    /// read as complete. It must also STAY unknowable: a later batch that lands cannot restore a total nobody ever knew,
    /// so the indeterminate absorbs rather than being counted back up to complete.
    /// </summary>
    [Fact]
    public async Task An_observer_that_died_inside_the_capture_window_leaves_a_permanently_indeterminate_statement()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var handle = await OpenAsync(plane, run);

        await plane.WriteAsync(Batch(handle, Frame(handle, 0)), CancellationToken.None);

        // No CloseAsync: this is exactly a worker torn down mid-round, leaving its process attempt Running.
        await ((INativeRecordExecutionPlane)plane).TerminalizeAsync(run.TeamId, run.AgentRunId, run.FenceEpoch, CancellationToken.None);

        var died = await StatementAsync(run);
        died.ExpectedRecordCount.ShouldBeNull(
            customMessage: "an observer that died mid-window read an unknown number of frames it never made durable, so the expectation is unstated rather than rounded down to what did land");
        died.PresentRecordCount.ShouldBe(1, customMessage: "what did land is still a fact, and stays stated");
        died.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);
        died.Verdict.IsStrictlyReadable().ShouldBeFalse();

        await plane.WriteAsync(Batch(handle, Frame(handle, 1)), CancellationToken.None);

        var after = await StatementAsync(run);
        after.ExpectedRecordCount.ShouldBeNull(
            customMessage: "a later batch cannot restore a total nobody ever knew — counting back up to complete here would convert the unknown into the assurance 0146 refuses");
        after.PresentRecordCount.ShouldBe(2);
        after.Verdict.IsStrictlyReadable().ShouldBeFalse();
    }

    /// <summary>
    /// WHICH DIRECTION A LOST ACCOUNTING ERRS IN, which is the whole reason the expectation is declared before the
    /// frames rather than counted with them. Both counts are DELTAS and neither is retryable — re-applying one whose
    /// COMMIT was acknowledged nowhere would double it, and a double-counted expectation reads present &lt; expected for
    /// good, turning a healthy run permanently not-complete. So the residue is not removed, it is POINTED: the frames
    /// land, the accounting that follows them is lost, and what remains is a visible shortfall.
    ///
    /// <para>0146 assumed exactly this shape — "a producer that wrote the record but not the counter leaves present
    /// below expected" — and a single advance carrying both counts does not have it: losing that one leaves the two
    /// equally short and the facet reads Exact over frames nobody counted. This test is what makes the assumption true
    /// of this producer, by dropping precisely the second of its two advances.</para>
    ///
    /// <para>An ABSENT row would also be not-complete by the convention the manifest entity states, and that is not
    /// good enough: no reader implements the convention yet, while <c>ck_workflow_run_data_manifest_completeness</c>
    /// refuses a complete verdict over a shortfall in the database itself. A row that shows the shortfall is enforced;
    /// an absent one is merely intended.</para>
    /// </summary>
    [Fact]
    public async Task Frames_that_landed_with_their_presence_unaccounted_leave_a_visible_shortfall()
    {
        var run = await SeedWorkflowBoundRunAsync();

        using var planeScope = _fixture.BeginScope(builder => builder.Register<IRunDataCompletenessWriter>(context =>
                new PresenceLosingWriter(new RunDataCompletenessWriter(context.Resolve<IServiceScopeFactory>(), NullLogger<RunDataCompletenessWriter>.Instance)))
            .InstancePerLifetimeScope());

        var plane = planeScope.Resolve<INativeRecordPlane>();
        var handle = await OpenAsync(plane, run);

        await plane.WriteAsync(Batch(handle, Frame(handle, 0), Frame(handle, 1)), CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunNativeRecord.CountAsync(candidate => candidate.StreamId == handle.StreamId)).ShouldBe(2,
            customMessage: "the premise: the frames are durable and only the claim about them was lost, or this test is asserting nothing about the fail direction");

        var statement = (await db.WorkflowRunDataManifest.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId))
            .ShouldNotBeNull(customMessage: "the expectation is declared BEFORE the frames, so losing the accounting that follows them must still leave a statement — a facet with no row at all is only not-complete by a convention nothing enforces yet");

        statement.ExpectedRecordCount.ShouldBe(2, customMessage: "the declaration stated what the batch undertook, and nothing lowered it");
        statement.PresentRecordCount.ShouldBe(0, customMessage: "the presence advance is the one that was lost");
        statement.Verdict.IsStrictlyReadable().ShouldBeFalse(
            customMessage: "a shortfall must not read complete. If this fails, the two counts are advancing together again and a lost accounting reads Exact over frames nobody counted.");
    }

    /// <summary>
    /// A STANDALONE Agent Run belongs to no workflow run, and both completeness tables are keyed to one. Its frames are
    /// still recorded and NO statement is invented for them — the same named keying gap 0137/0141 already carry, and a
    /// row against an invented parent would be worse than none. A reader must therefore treat an absent statement as
    /// indeterminate, which is what the manifest entity already says a later fold has to do.
    /// </summary>
    [Fact]
    public async Task A_run_that_belongs_to_no_workflow_run_records_its_frames_and_states_no_manifest()
    {
        var run = await SeedStandaloneRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var handle = await OpenAsync(plane, run);
        handle.WorkflowRunId.ShouldBeNull(customMessage: "the premise: the opening reads its scope off the Agent Run, and this run belongs to no workflow run");

        await plane.WriteAsync(Batch(handle, Frame(handle, 0)), CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunNativeRecord.CountAsync(candidate => candidate.AgentRunId == run.AgentRunId)).ShouldBe(1,
            customMessage: "the capture floor is untouched: the frame is recorded whether or not a workflow run exists to key a statement to");
        (await db.WorkflowRunDataManifest.CountAsync(candidate => candidate.TeamId == run.TeamId)).ShouldBe(0,
            customMessage: "the plane is keyed to a workflow run, so a standalone run states nothing rather than stating it against an invented parent");
        (await db.WorkflowRunCaptureGap.CountAsync(candidate => candidate.TeamId == run.TeamId)).ShouldBe(0);
    }

    private async Task<WorkflowRunDataManifest> StatementAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunDataManifest.AsNoTracking()
            .SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId);
    }

    private async Task SeedGapAsync(SeededRun run)
    {
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunCaptureGap.Add(new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = run.TeamId, WorkflowRunId = run.WorkflowRunId,
            SubjectKind = WorkflowRunDataOwnerKinds.LogSegment, RangeKind = CaptureGapRangeKind.Unbounded,
            Reason = CaptureGapReason.BoundExceeded, ReasonDetail = "a racing producer of another facet",
            CaptureSource = "test-harness/v1", NoticedAt = now, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        });

        await db.SaveChangesAsync();
    }

    private INativeRecordPlane Plane(out ILifetimeScope scope)
    {
        scope = _fixture.BeginScope();

        return scope.Resolve<INativeRecordPlane>();
    }

    private static async Task<NativeRecordCaptureHandle> OpenAsync(INativeRecordPlane plane, SeededRun run)
    {
        var opened = await plane.OpenAsync(new NativeRecordCaptureRequest
        {
            TeamId = run.TeamId,
            AgentRunId = run.AgentRunId,
            HarnessTypeKey = "claude-code/v2",
            RunnerKind = "local",
            RunnerLocatorJson = "{\"spoolKey\":\"round-0\"}",
            WorkerFenceEpoch = run.FenceEpoch,
            Channel = NativeRecordChannel.Stdout,
        }, CancellationToken.None);

        return opened.ShouldNotBeNull(customMessage: "the plane must open against the seeded run, or the test is asserting nothing");
    }

    private static NativeRecordBatch Batch(NativeRecordCaptureHandle handle, params NativeRecordV1[] frames) => new()
    {
        Handle = handle,
        Records = frames.Select(frame => new NativeRecordCapture { Frame = frame, Normalization = NativeRecordNormalization.Projected }).ToList(),
        Events = Array.Empty<AgentSemanticEventV1>(),
    };

    private static NativeRecordV1 Frame(NativeRecordCaptureHandle handle, long ordinal, NativeRecordRedaction redaction = NativeRecordRedaction.None)
    {
        var payload = $"{{\"type\":\"assistant\",\"ordinal\":{ordinal}}}";

        return new NativeRecordV1
        {
            ContractVersion = WorkflowRunDataContract.CurrentVersion, RecordId = Guid.NewGuid(), StreamId = handle.StreamId,
            Ordinal = ordinal, Channel = handle.Channel, NativeType = "assistant", IngestedAt = DateTimeOffset.UtcNow,
            ByteOffset = ordinal * 512, ByteLength = Encoding.UTF8.GetByteCount(payload), InlinePayload = payload,
            DigestAlgorithm = WorkflowRunDataContract.Sha256Algorithm,
            Digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
            SizeBytes = Encoding.UTF8.GetByteCount(payload), Encoding = NativeRecordPayloadEncoding.Utf8,
            Redaction = redaction, IsFinal = true,
        };
    }

    private async Task<SeededRun> SeedWorkflowBoundRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "native-record-completeness-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return await CreateAgentRunAsync(teamId, await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId));
    }

    private async Task<SeededRun> SeedStandaloneRunAsync()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        return await CreateAgentRunAsync(teamId, workflowRunId: null);
    }

    private async Task<SeededRun> CreateAgentRunAsync(Guid teamId, Guid? workflowRunId)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var created = await runs.CreateAsync(
            new AgentTask { Goal = "state what it captured", Harness = ClaudeCodeHarness.HarnessKind, Model = PricedModel, TimeoutSeconds = 1800 },
            teamId, workflowRunId, workflowRunId is null ? null : "implement", workflowRunId is null ? "" : "implement#1", CancellationToken.None);

        // The run must be CLAIMED before capture may open against it: the opening carries the claim epoch, and 0137
        // refuses epoch 0 outright.
        return new SeededRun(teamId, created.Id, workflowRunId ?? Guid.Empty, await runs.MarkRunningAsync(created.Id, CancellationToken.None));
    }

    private sealed record SeededRun(Guid TeamId, Guid AgentRunId, Guid WorkflowRunId, long FenceEpoch);

    /// <summary>
    /// The real writer with exactly ONE of the producer's two advances dropped: the one that states presence. That is
    /// the survivable failure the containment already produces in production — a lost claim is reported, not thrown —
    /// so this substitutes the failure rather than a different code path.
    /// </summary>
    private sealed class PresenceLosingWriter : IRunDataCompletenessWriter
    {
        private readonly IRunDataCompletenessWriter _real;

        public PresenceLosingWriter(IRunDataCompletenessWriter real) => _real = real;

        public Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) =>
            advance.Present > 0 ? Task.FromResult(false) : _real.AdvanceAsync(advance, cancellationToken);

        public Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) => _real.NoticeAsync(gap, cancellationToken);

        public Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) =>
            _real.UnstateExpectationAsync(teamId, workflowRunId, facet, cancellationToken);
    }
}
