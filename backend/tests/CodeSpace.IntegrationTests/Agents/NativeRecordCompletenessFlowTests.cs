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
using CodeSpace.Messages.Dtos.Agents;
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
            .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Facet == WorkflowRunDataOwnerKinds.NativeRecord);

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

    [Fact]
    public async Task A_batch_with_semantic_projections_declares_exact_run_owned_coverage_in_the_same_two_round_trips()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;
        var handle = await OpenAsync(plane, run);
        var first = Frame(handle, 0);
        var second = Frame(handle, 1);

        await plane.WriteAsync(Batch(handle, first, second) with
        {
            Events = [Event(handle, first, SemanticProjectionQuality.Exact), Event(handle, second, SemanticProjectionQuality.RedactedExact)],
        }, CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var statement = await db.WorkflowRunDataManifest.AsNoTracking()
            .SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Facet == WorkflowRunDataOwnerKinds.SemanticEvent);
        statement.ExpectedRecordCount.ShouldBe(2);
        statement.PresentRecordCount.ShouldBe(2);
        statement.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.RedactedExact,
            "one redacted-exact projection latches the facet's honest byte-fidelity arm");

        var view = (await scope.Resolve<IRunDataCompletenessReader>().ReadAsync(run.WorkflowRunId, run.TeamId, CancellationToken.None)).ShouldNotBeNull();
        view.RequiredFacets.ShouldContain(WorkflowRunDataOwnerKinds.SemanticEvent,
            customMessage: "a run that actually invoked the conditional producer must owe its statement in the fold");
    }

    [Fact]
    public async Task A_semantic_backfill_accounts_the_prior_declaration_without_declaring_it_twice()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;
        var handle = await OpenAsync(plane, run);
        var zero = Frame(handle, 0);
        var one = Frame(handle, 1);

        await plane.WriteAsync(Batch(handle, zero, one) with
        {
            Events = [Event(handle, zero), Event(handle, one)],
        }, CancellationToken.None);

        var two = Frame(handle, 2);
        await Should.ThrowAsync<DbUpdateException>(() => plane.WriteAsync(Batch(handle, Frame(handle, 1), two) with
        {
            Events = [Event(handle, one), Event(handle, two)],
        }, CancellationToken.None));

        var refused = await StatementAsync(run, WorkflowRunDataOwnerKinds.SemanticEvent);
        refused.ExpectedRecordCount.ShouldBe(4, "the refused projection batch declared before its shared transaction failed");
        refused.PresentRecordCount.ShouldBe(2);

        var three = Frame(handle, 3);
        await plane.WriteAsync(Backfill(handle, two, three) with
        {
            Events = [Event(handle, two), Event(handle, three)],
        }, CancellationToken.None);

        var recovered = await StatementAsync(run, WorkflowRunDataOwnerKinds.SemanticEvent);
        recovered.ExpectedRecordCount.ShouldBe(4,
            "a reattached batch represents the obligation already declared by the refused write; declaring it again permanently inflates the run's debt");
        recovered.PresentRecordCount.ShouldBe(4,
            "every semantic projection that became durable in the original and repair batches is accounted once");
        recovered.Verdict.IsStrictlyReadable().ShouldBeFalse(
            customMessage: "the refusal's open gap remains authoritative until a cited recovery closes it");
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
        gap.AgentRunId.ShouldBe(handle.AgentRunId);
        gap.HarnessExecutionId.ShouldBe(handle.ExecutionId);
        gap.HarnessProcessAttemptId.ShouldBe(handle.AttemptId);
        gap.AttemptWorkerFenceEpoch.ShouldBe(run.FenceEpoch,
            customMessage: "the gap cites the immutable launch fence of the exact process attempt; it must never be inferred later from a missing native row or from the Agent Run's current fence");
        gap.RangeStart.ShouldBe(1, customMessage: "the first frame of the refused batch is where this stream's records stop");
        gap.RangeEnd.ShouldBeNull(customMessage: "capture stops for the round on a refusal, so 'from here on, and I do not know how much' is the honest extent");
        gap.Resolution.ShouldBe(CaptureGapResolution.Open);
        gap.ReasonDetail.ShouldNotBeNullOrWhiteSpace(customMessage: "a gap with no reason detail is a hole with a label on it");

        var statement = await db.WorkflowRunDataManifest.AsNoTracking().SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Facet == WorkflowRunDataOwnerKinds.NativeRecord);
        statement.Verdict.IsStrictlyReadable().ShouldBeFalse(
            customMessage: "a known-missing span un-completes the statement; a verdict that read complete beside it would be the false assurance this plane exists to refuse");
        statement.KnownMissingCount.ShouldBe(1);
        statement.PresentRecordCount.ShouldBe(2, customMessage: "the refused frames never landed, so they were never present");
        statement.ExpectedRecordCount.ShouldBe(4, customMessage: "the plane undertook four frames and two of them are not here — the shortfall must be visible in the counts too, not only in the gap");

        var observed = (await scope.Resolve<IAgentRunService>().GetSummaryForTeamAsync(run.AgentRunId, run.TeamId, CancellationToken.None))!.CaptureGaps;
        observed.Availability.ShouldBe(AgentRunCaptureGapReadAvailability.Available);
        observed.Truncated.ShouldBeFalse();
        var item = observed.Items.ShouldHaveSingleItem();
        item.Id.ShouldBe(gap.Id);
        item.HarnessExecutionId.ShouldBe(handle.ExecutionId);
        item.HarnessProcessAttemptId.ShouldBe(handle.AttemptId);
        item.AttemptWorkerFenceEpoch.ShouldBe(handle.WorkerFenceEpoch);
        item.SubjectKind.ShouldBe(WorkflowRunDataOwnerKinds.NativeRecord);
        item.Reason.ShouldBe(nameof(CaptureGapReason.WriteRefused));
        item.RangeKind.ShouldBe(nameof(CaptureGapRangeKind.Ordinal));
        item.RangeStart.ShouldBe(1);
    }

    [Fact]
    public async Task An_attributed_gap_is_refused_when_any_tenant_run_execution_attempt_or_frozen_fence_coordinate_disagrees()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;
        var handle = await OpenAsync(plane, run);

        await RejectsAttributedGapAsync(handle, gap => gap.AgentRunId = null, "all-or-none");

        // The first two are caught by the OWNER derivation, which runs first because everything after it keys off the
        // workflow run that derivation settles. Neither is admitted, and each is now told which coordinate disagreed:
        // a gap whose team does not own the Agent Run it names is a tenancy claim, not an attempt-attribution one.
        await RejectsAttributedGapAsync(handle, gap => gap.TeamId = Guid.NewGuid(), "requires its tenant-bound AgentRun");
        await RejectsAttributedGapAsync(handle, gap => gap.AgentRunId = Guid.NewGuid(), "requires its tenant-bound AgentRun");

        // A parent that DISAGREES is refused rather than corrected: an omitted one is a producer that did not carry a
        // value it never had, while a wrong one is a producer that believes this gap belongs to another run, and
        // quietly rewriting that would hide the disagreement instead of reporting it.
        await RejectsAttributedGapAsync(handle, gap => gap.WorkflowRunId = Guid.NewGuid(), "must name its AgentRun's workflow run exactly");

        await RejectsAttributedGapAsync(handle, gap => gap.HarnessExecutionId = Guid.NewGuid(), "does not match one frozen harness process attempt");
        await RejectsAttributedGapAsync(handle, gap => gap.HarnessProcessAttemptId = Guid.NewGuid(), "does not match one frozen harness process attempt");
        await RejectsAttributedGapAsync(handle, gap => gap.AttemptWorkerFenceEpoch++, "does not match one frozen harness process attempt");
    }

    [Fact]
    public async Task A_reattached_gap_keeps_the_process_attempts_frozen_launch_fence_not_the_agent_runs_new_fence()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;
        var launched = await OpenAsync(plane, run);

        long reattachFence;
        using (var scope = _fixture.BeginScope())
        {
            var runs = scope.Resolve<IAgentRunService>();
            (await runs.ReclaimForReattachAsync(run.AgentRunId, CancellationToken.None)).ShouldBeTrue();
            reattachFence = (await runs.GetAsync(run.AgentRunId, CancellationToken.None)).FenceEpoch;
        }
        reattachFence.ShouldBeGreaterThan(launched.WorkerFenceEpoch);

        var resumed = await ((INativeRecordExecutionPlane)plane).ReopenAsync(new NativeRecordCaptureRequest
        {
            TeamId = run.TeamId, AgentRunId = run.AgentRunId, HarnessTypeKey = "unused-on-resume/v1",
            RunnerKind = "unused", RunnerLocatorJson = "{}", WorkerFenceEpoch = reattachFence,
            Channel = NativeRecordChannel.Stdout, Resume = true, ResumeSourceOffset = 0,
        }, CancellationToken.None);
        var handle = resumed.ShouldNotBeNull().Handle;
        handle.AttemptId.ShouldBe(launched.AttemptId);
        handle.WorkerFenceEpoch.ShouldBe(launched.WorkerFenceEpoch,
            customMessage: "reattach observes the process launched by the older fence; replacing it with the current Agent Run fence would manufacture a different process identity");

        await Should.ThrowAsync<DbUpdateException>(() =>
            plane.WriteAsync(Batch(handle, Frame(handle, 0), Frame(handle, 0)), CancellationToken.None));

        using var read = _fixture.BeginScope();
        var gap = await read.Resolve<CodeSpaceDbContext>().WorkflowRunCaptureGap.AsNoTracking()
            .SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId);
        gap.HarnessProcessAttemptId.ShouldBe(launched.AttemptId);
        gap.AttemptWorkerFenceEpoch.ShouldBe(launched.WorkerFenceEpoch);
        gap.AttemptWorkerFenceEpoch.ShouldNotBe(reattachFence);
    }

    [Fact]
    public async Task Capture_gap_observation_is_newest_first_team_scoped_and_bounded()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;
        var handle = await OpenAsync(plane, run);
        var start = DateTimeOffset.UtcNow.AddMinutes(-2);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunCaptureGap.AddRange(Enumerable.Range(0, 51).Select(index => AttributedGap(handle, start.AddSeconds(index), index)));
            var legacy = AttributedGap(handle, start.AddHours(1), 999);
            legacy.AgentRunId = null;
            legacy.HarnessExecutionId = null;
            legacy.HarnessProcessAttemptId = null;
            legacy.AttemptWorkerFenceEpoch = null;
            db.WorkflowRunCaptureGap.Add(legacy);
            await db.SaveChangesAsync();
        }

        using var read = _fixture.BeginScope();
        var runs = read.Resolve<IAgentRunService>();
        var observed = (await runs.GetSummaryForTeamAsync(run.AgentRunId, run.TeamId, CancellationToken.None))!.CaptureGaps;

        observed.Availability.ShouldBe(AgentRunCaptureGapReadAvailability.Available);
        observed.Items.Count.ShouldBe(50);
        observed.Truncated.ShouldBeTrue();
        observed.Items.Select(item => item.RangeStart).ShouldBe(Enumerable.Range(1, 50).Reverse().Select(index => (long?)index),
            customMessage: "the bounded page keeps the newest 50 observations and leaves the oldest outside the window");
        observed.Items.ShouldNotContain(item => item.RangeStart == 999,
            customMessage: "a newer workflow-only legacy gap has no provable Agent Run coordinate and must not be inferred into this summary");
        (await runs.GetSummaryForTeamAsync(run.AgentRunId, Guid.NewGuid(), CancellationToken.None)).ShouldBeNull(
            customMessage: "the owning Agent Run gate runs before the gap query, so a foreign team learns neither the run nor its gaps");
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
                    .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Facet == WorkflowRunDataOwnerKinds.NativeRecord))
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

        var first = Frame(handle, 0);
        await plane.WriteAsync(Batch(handle, first) with { Events = [Event(handle, first)] }, CancellationToken.None);

        // No CloseAsync: this is exactly a worker torn down mid-round, leaving its process attempt Running.
        await ((INativeRecordExecutionPlane)plane).TerminalizeAsync(run.TeamId, run.AgentRunId, run.FenceEpoch, CancellationToken.None);

        var died = await StatementAsync(run);
        died.ExpectedRecordCount.ShouldBeNull(
            customMessage: "an observer that died mid-window read an unknown number of frames it never made durable, so the expectation is unstated rather than rounded down to what did land");
        died.PresentRecordCount.ShouldBe(1, customMessage: "what did land is still a fact, and stays stated");
        died.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);
        died.Verdict.IsStrictlyReadable().ShouldBeFalse();
        var semanticDied = await StatementAsync(run, WorkflowRunDataOwnerKinds.SemanticEvent);
        semanticDied.ExpectedRecordCount.ShouldBeNull(
            customMessage: "semantic projections share the dead observer's unknown source window; leaving this facet Exact would make the run contradict its native source");
        semanticDied.PresentRecordCount.ShouldBe(1);
        semanticDied.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);

        var second = Frame(handle, 1);
        await plane.WriteAsync(Batch(handle, second) with { Events = [Event(handle, second)] }, CancellationToken.None);

        var after = await StatementAsync(run);
        after.ExpectedRecordCount.ShouldBeNull(
            customMessage: "a later batch cannot restore a total nobody ever knew — counting back up to complete here would convert the unknown into the assurance 0146 refuses");
        after.PresentRecordCount.ShouldBe(2);
        after.Verdict.IsStrictlyReadable().ShouldBeFalse();
        var semanticAfter = await StatementAsync(run, WorkflowRunDataOwnerKinds.SemanticEvent);
        semanticAfter.ExpectedRecordCount.ShouldBeNull();
        semanticAfter.PresentRecordCount.ShouldBe(2);
        semanticAfter.Verdict.IsStrictlyReadable().ShouldBeFalse();
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
                .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Facet == WorkflowRunDataOwnerKinds.NativeRecord))
            .ShouldNotBeNull(customMessage: "the expectation is declared BEFORE the frames, so losing the accounting that follows them must still leave a statement — a facet with no row at all is only not-complete by a convention nothing enforces yet");

        statement.ExpectedRecordCount.ShouldBe(2, customMessage: "the declaration stated what the batch undertook, and nothing lowered it");
        statement.PresentRecordCount.ShouldBe(0, customMessage: "the presence advance is the one that was lost");
        statement.Verdict.IsStrictlyReadable().ShouldBeFalse(
            customMessage: "a shortfall must not read complete. If this fails, the two counts are advancing together again and a lost accounting reads Exact over frames nobody counted.");
    }

    /// <summary>
    /// A STANDALONE Agent Run belongs to no workflow run, and the MANIFEST is keyed to one. Its frames are still
    /// recorded and NO statement is invented for them — the same named keying gap 0137/0141 already carry, and a row
    /// against an invented parent would be worse than none. A reader must therefore treat an absent statement as
    /// indeterminate, which is what the manifest entity already says a later fold has to do. The gap plane is keyed to
    /// the run that OWNS the record instead, so nothing here rests on a run's losses being unrecordable — this run
    /// simply lost nothing.
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
            customMessage: "the manifest is keyed to a workflow run, so a standalone run states nothing rather than stating it against an invented parent");
        (await db.WorkflowRunCaptureGap.CountAsync(candidate => candidate.TeamId == run.TeamId)).ShouldBe(0);
    }

    /// <summary>
    /// THE RUN THIS PRODUCER USED TO BE SILENT ABOUT. A standalone Agent Run has no workflow run, and while the gap
    /// plane demanded one its producer's only legal answer to a refused batch was to record nothing — so the run whose
    /// facet no manifest can carry was also the run whose losses left no trace at all. A gap has to be recordable
    /// wherever a producer can notice one, or "complete because nothing said otherwise" comes back through the one door
    /// the plane never closed.
    ///
    /// <para>The manifest stays absent on purpose: it is keyed to a workflow run and this run has none, and an absent
    /// statement is the indeterminate answer. The gap is not the same case — it is keyed to the run that OWNS the
    /// record.</para>
    /// </summary>
    [Fact]
    public async Task A_standalone_run_whose_batch_is_refused_records_a_gap_against_the_agent_run_that_owns_it()
    {
        var run = await SeedStandaloneRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var handle = await OpenAsync(plane, run);
        handle.WorkflowRunId.ShouldBeNull(customMessage: "the premise: the opening reads its scope off the Agent Run, and this run belongs to no workflow run");

        await plane.WriteAsync(Batch(handle, Frame(handle, 0), Frame(handle, 1)), CancellationToken.None);

        // Ordinal 1 is already recorded on this stream, and ux_workflow_run_native_record_ordinal refuses the second
        // copy — a real refusal of a real durable write, not a mocked one.
        await Should.ThrowAsync<DbUpdateException>(() =>
            plane.WriteAsync(Batch(handle, Frame(handle, 1), Frame(handle, 2)), CancellationToken.None));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var gap = await db.WorkflowRunCaptureGap.AsNoTracking().SingleAsync(candidate => candidate.TeamId == run.TeamId);
        gap.WorkflowRunId.ShouldBeNull(customMessage: "there is no workflow run, and inventing one would key the span to a parent that does not exist");
        gap.AgentRunId.ShouldBe(run.AgentRunId, customMessage: "the run that owns the record is the run the gap names");
        gap.HarnessProcessAttemptId.ShouldBe(handle.AttemptId,
            customMessage: "the exact process attribution is admitted for a standalone run too — the guard's execution join has to be NULL-safe, or every attributed gap of exactly these runs is refused");
        gap.AttemptWorkerFenceEpoch.ShouldBe(handle.WorkerFenceEpoch);
        gap.SubjectKind.ShouldBe(WorkflowRunDataOwnerKinds.NativeRecord);
        gap.Reason.ShouldBe(CaptureGapReason.WriteRefused);
        gap.RangeStart.ShouldBe(1);
        gap.Resolution.ShouldBe(CaptureGapResolution.Open);

        (await db.WorkflowRunDataManifest.CountAsync(candidate => candidate.TeamId == run.TeamId)).ShouldBe(0,
            customMessage: "the manifest is still keyed to a workflow run; a gap that can be recorded does not make a statement that cannot");

        var observed = (await scope.Resolve<IAgentRunService>().GetSummaryForTeamAsync(run.AgentRunId, run.TeamId, CancellationToken.None))!.CaptureGaps;
        observed.Availability.ShouldBe(AgentRunCaptureGapReadAvailability.Available);
        observed.Items.ShouldHaveSingleItem().Id.ShouldBe(gap.Id,
            customMessage: "the only production reader of this plane keys on the Agent Run, so a standalone run's gap reaches an operator through exactly the query that was already there");
    }

    /// <summary>
    /// A gap that names NO run is a hole nobody can locate — no worse than the gap a NOT NULL workflow run stopped a
    /// standalone run from recording, but no better either. Nullability had to be bought with the CHECK: the doc-comment
    /// that says "every gap names a run" enforces nothing, and the database is where this plane's other invariants all
    /// live.
    /// </summary>
    [Fact]
    public async Task A_gap_that_names_neither_run_is_refused()
    {
        var run = await SeedStandaloneRunAsync();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunCaptureGap.Add(new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = run.TeamId, WorkflowRunId = null, AgentRunId = null,
            SubjectKind = WorkflowRunDataOwnerKinds.NativeRecord, RangeKind = CaptureGapRangeKind.Unbounded,
            Reason = CaptureGapReason.BoundExceeded, ReasonDetail = "a span belonging to nobody",
            CaptureSource = "test-harness/v1", NoticedAt = now, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        });

        var refused = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        refused.InnerException?.Message.ShouldContain("ck_workflow_run_capture_gap_owner",
            customMessage: "the at-least-one rule has to be the database's, or a producer that forgot both keys writes an unattributable hole and nothing notices");
    }

    /// <summary>
    /// THE OTHER WAY A NULLABLE KEY GOES WRONG, and the one that reads as good news. Every consequence a gap has —
    /// 0146's downgrade, its open-gap floor, the complete verdict it refuses — is reached through the WORKFLOW run. So
    /// a gap that named only its Agent Run while that run HAS a parent would sit in the table looking recorded while
    /// its run went on reading complete: the exact false-complete this plane exists to prevent, arrived at through the
    /// door the standalone shape opened.
    ///
    /// <para>A writer that knows the Agent Run already knows its parent, so this may not rest on producers remembering:
    /// the database derives the parent from the Agent Run the gap names, the same value 0137 already makes the harness
    /// execution mirror. The gap below names ONLY its Agent Run, and its run still stops reading complete.</para>
    /// </summary>
    [Fact]
    public async Task A_workflow_bound_runs_gap_that_names_only_its_agent_run_still_downgrades_its_run()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var handle = await OpenAsync(plane, run);
        await plane.WriteAsync(Batch(handle, Frame(handle, 0)), CancellationToken.None);

        (await StatementAsync(run)).Verdict.IsStrictlyReadable().ShouldBeTrue(
            customMessage: "the premise: the run reads complete before the gap arrives, or there is no downgrade below to observe");

        await NoticeOwnerOnlyGapAsync(run);

        using var scope = _fixture.BeginScope();
        var stored = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunCaptureGap.AsNoTracking()
            .SingleAsync(candidate => candidate.TeamId == run.TeamId);

        stored.WorkflowRunId.ShouldBe(run.WorkflowRunId,
            customMessage: "the parent is recorded because the row's own Agent Run has one — left to a convention, a producer that omitted it would file this loss where none of the run's readers look");

        (await StatementAsync(run)).Verdict.IsStrictlyReadable().ShouldBeFalse(
            customMessage: "the run has a known-missing span and must stop reading complete. If this passes with the gap stored against no workflow run, the plane admitted the loss and let the run claim a whole record anyway.");
    }

    /// <summary>Records a gap naming ONLY its Agent Run — the shape a refused attempt insert leaves behind, since its subject is the very row the attempt columns would reference.</summary>
    private async Task NoticeOwnerOnlyGapAsync(SeededRun run)
    {
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunCaptureGap.Add(new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = run.TeamId, WorkflowRunId = null, AgentRunId = run.AgentRunId,
            SubjectKind = WorkflowRunDataOwnerKinds.HarnessProcessAttempt, SubjectId = Guid.NewGuid().ToString(),
            RangeKind = CaptureGapRangeKind.Unbounded, Reason = CaptureGapReason.WriteRefused,
            ReasonDetail = "a span whose producer named the run that owns it and nothing else",
            CaptureSource = "test-harness/v1", NoticedAt = now, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// THE OTHER DIRECTION A LOST CLAIM CAN GO, and the one that turns into a false assurance rather than a visible
    /// shortfall. When it is the DECLARATION that is lost rather than the accounting, the plane must not go on to state
    /// presence: 0148's insert reads a present delta over an expected delta of zero as Exact, and its update states the
    /// batch's presence against an expectation that never counted it. Either way the facet reads complete over frames
    /// whose obligation nobody established — and unlike a shortfall, nothing downstream can ever detect it.
    ///
    /// <para>The two arms are the two states the facet can be in when that happens: with nothing stated before,
    /// un-stating invents no row and the absent statement IS the indeterminate answer; with an earlier batch's
    /// statement already there, the expectation it carries is un-stated in place, which the database itself refuses
    /// every complete verdict over.</para>
    /// </summary>
    [Theory]
    [InlineData(0, "absent")]
    [InlineData(2, "LegacyUnknown over expected=null present=2")]
    public async Task A_lost_batch_declaration_leaves_the_facet_indeterminate_instead_of_counting_a_present_only_delta(int accountedFrames, string indeterminate)
    {
        var run = await SeedWorkflowBoundRunAsync();

        using var planeScope = _fixture.BeginScope(builder => builder.Register<IRunDataCompletenessWriter>(context =>
                new DeclarationLosingWriter(new RunDataCompletenessWriter(context.Resolve<IServiceScopeFactory>(), NullLogger<RunDataCompletenessWriter>.Instance)))
            .InstancePerLifetimeScope());

        var losing = planeScope.Resolve<INativeRecordPlane>();
        var handle = await OpenAsync(losing, run);

        if (accountedFrames > 0)
        {
            var plane = Plane(out var accountedScope);
            using var opened = accountedScope;
            await plane.WriteAsync(Batch(handle, Frame(handle, 0), Frame(handle, 1)), CancellationToken.None);
        }

        await losing.WriteAsync(Batch(handle, Frame(handle, accountedFrames), Frame(handle, accountedFrames + 1)), CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunNativeRecord.CountAsync(candidate => candidate.StreamId == handle.StreamId)).ShouldBe(accountedFrames + 2,
            customMessage: "the premise: the frames are durable and only the declaration about them was lost, or this test asserts nothing about the fail direction");

        Describe(await StatementOrNullAsync(run)).ShouldBe(indeterminate,
            customMessage: "the plane must not follow a lost expectation with a present-only delta. With nothing stated before, that delta writes expected=0 beside present=2 and 0148 reads it as Exact over frames nobody counted; with an earlier statement standing, it states this batch's presence against an expectation that never undertook it.");
    }

    /// <summary>
    /// THE MASKED OBSERVATION IS STICKY, which is what makes the redacted arm mean anything across a run rather than
    /// across one batch. A masked frame reached storage, so the run's record is not verbatim and never becomes verbatim
    /// again — no volume of later unmasked frames can turn it back.
    ///
    /// <para>It has to be sticky in a COLUMN rather than in the verdict, because the verdict is overwritten on the way
    /// past: the next batch's declaration reads present below expected and legitimately writes Partial, which erases
    /// the only record that anything was ever masked, and the accounting that follows reaches parity and reads
    /// <see cref="WorkflowRunCaptureCompleteness.Exact"/>. That claims the stored bytes are verbatim when the run holds
    /// masked ones — the single-batch pin above cannot see it, because it never advances the facet a second time.</para>
    /// </summary>
    [Fact]
    public async Task A_masked_frame_keeps_the_redacted_arm_across_every_later_unmasked_batch()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var handle = await OpenAsync(plane, run);

        await plane.WriteAsync(Batch(handle, Frame(handle, 0, NativeRecordRedaction.Masked), Frame(handle, 1, NativeRecordRedaction.Masked)), CancellationToken.None);

        (await StatementAsync(run)).Verdict.ShouldBe(WorkflowRunCaptureCompleteness.RedactedExact,
            customMessage: "the premise: one masked batch reaches the redacted arm, or this test is asserting stickiness over something that was never sticky");

        await plane.WriteAsync(Batch(handle, Frame(handle, 2), Frame(handle, 3)), CancellationToken.None);
        await plane.WriteAsync(Batch(handle, Frame(handle, 4), Frame(handle, 5)), CancellationToken.None);

        Describe(await StatementOrNullAsync(run)).ShouldBe("RedactedExact over expected=6 present=6",
            customMessage: "a run holding masked frames may never read back as verbatim. If this says Exact, the masked observation lived only in the verdict column and the next batch's honest Partial erased it — and the record now claims bytes it does not have.");
    }

    /// <summary>
    /// THE WHOLE CYCLE, driven through the real plane rather than a refusing double: a batch is declared, the database
    /// refuses it, the span becomes an open gap, and a later backfill re-delivers the frames the refused batch carried.
    /// The verdict must still be not-complete afterwards — the backfill repairs THIS plane's records, and admitting the
    /// recovered range into the historical expectation is a separate digest/ordinal-aware transition that does not
    /// exist. A producer that silently healed here would close the one gap that was honestly observed.
    /// </summary>
    [Fact]
    public async Task A_backfill_of_refused_frames_does_not_silently_heal_the_gap_the_refusal_opened()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var handle = await OpenAsync(plane, run);

        await plane.WriteAsync(Batch(handle, Frame(handle, 0), Frame(handle, 1)), CancellationToken.None);

        // Ordinal 1 is already recorded on this stream, so ux_workflow_run_native_record_ordinal refuses the batch —
        // a real refusal of a real durable write, which is what opens the gap the backfill must not close.
        await Should.ThrowAsync<DbUpdateException>(() =>
            plane.WriteAsync(Batch(handle, Frame(handle, 1), Frame(handle, 2)), CancellationToken.None));

        var refused = await StatementAsync(run);
        refused.ExpectedRecordCount.ShouldBe(4, customMessage: "the premise: the refused batch declared before it tried, so its shortfall is in the counts");
        refused.KnownMissingCount.ShouldBe(1);

        await plane.WriteAsync(Backfill(handle, Frame(handle, 2), Frame(handle, 3)), CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunNativeRecord.CountAsync(candidate => candidate.StreamId == handle.StreamId)).ShouldBe(4,
            customMessage: "the premise: the backfill's frames are durable, or the test asserts nothing about what a delivered repair does to the verdict");
        (await db.WorkflowRunCaptureGap.CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Resolution == CaptureGapResolution.Open)).ShouldBe(1,
            customMessage: "this producer does not close the gap its own refusal opened; recovery has to CITE what now covers the span, which a backfill cannot");

        var healed = await StatementAsync(run);
        healed.ExpectedRecordCount.ShouldBe(4, customMessage: "a backfill re-delivers frames an earlier batch already declared, so it must not declare them a second time and inflate what the run owes");
        healed.PresentRecordCount.ShouldBe(4);
        healed.Verdict.IsStrictlyReadable().ShouldBeFalse(
            customMessage: "the counts reached parity but a known-missing span of this run is still open, so the verdict must stay not-complete. A run that healed itself back to complete here would have erased the only honest observation of what it lost.");
    }

    /// <summary>The facet's whole answer as one line, so a red run prints what was actually written rather than which of four assertions tripped first.</summary>
    private static string Describe(WorkflowRunDataManifest? statement) =>
        statement is null
            ? "absent"
            : $"{statement.Verdict} over expected={statement.ExpectedRecordCount?.ToString() ?? "null"} present={statement.PresentRecordCount}";

    private async Task<WorkflowRunDataManifest> StatementAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunDataManifest.AsNoTracking()
            .SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Facet == WorkflowRunDataOwnerKinds.NativeRecord);
    }

    private async Task<WorkflowRunDataManifest> StatementAsync(SeededRun run, string facet)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunDataManifest.AsNoTracking()
            .SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Facet == facet);
    }

    /// <summary>The facet's statement, or null where the facet has none — because "no row" is itself one of the two indeterminate answers this producer can leave behind.</summary>
    private async Task<WorkflowRunDataManifest?> StatementOrNullAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunDataManifest.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Facet == WorkflowRunDataOwnerKinds.NativeRecord);
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

    /// <summary>A batch re-delivering frames an earlier batch already declared, which is what a replacement observer's repair is — so it must state presence without declaring the expectation a second time.</summary>
    private static NativeRecordBatch Backfill(NativeRecordCaptureHandle handle, params NativeRecordV1[] frames) =>
        Batch(handle, frames) with { BackfillsDeclaredFrames = true };

    private async Task RejectsAttributedGapAsync(NativeRecordCaptureHandle handle, Action<WorkflowRunCaptureGap> forge, string expected)
    {
        var gap = AttributedGap(handle, DateTimeOffset.UtcNow, 0);
        forge(gap);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunCaptureGap.Add(gap);
        var rejected = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        rejected.InnerException?.Message.ShouldContain(expected);
    }

    private static WorkflowRunCaptureGap AttributedGap(NativeRecordCaptureHandle handle, DateTimeOffset noticedAt, long rangeStart) => new()
    {
        Id = Guid.NewGuid(), TeamId = handle.TeamId, WorkflowRunId = handle.WorkflowRunId!.Value,
        AgentRunId = handle.AgentRunId, HarnessExecutionId = handle.ExecutionId,
        HarnessProcessAttemptId = handle.AttemptId, AttemptWorkerFenceEpoch = handle.WorkerFenceEpoch,
        SubjectKind = WorkflowRunDataOwnerKinds.NativeRecord, StreamId = handle.StreamId, Channel = handle.Channel,
        RangeKind = CaptureGapRangeKind.Ordinal, RangeStart = rangeStart, Reason = CaptureGapReason.WriteRefused,
        CaptureSource = NativeRecordPlane.CompletenessCaptureSource, NoticedAt = noticedAt,
        Resolution = CaptureGapResolution.Open, SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = noticedAt,
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

    private static AgentSemanticEventV1 Event(NativeRecordCaptureHandle handle, NativeRecordV1 source, SemanticProjectionQuality quality = SemanticProjectionQuality.Exact) => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        EventId = Guid.NewGuid(),
        EventType = "urn:codespace:test:semantic-event",
        EventSchemaVersion = 1,
        SourceNativeRecordIds = [source.RecordId],
        ExecutionId = handle.ExecutionId,
        Necessity = SemanticEventNecessity.Ignorable,
        ProjectionQuality = quality,
    };

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
    /// <summary>
    /// The real writer with the OTHER one of the plane's two advances dropped: the declaration. Same survivable failure
    /// the containment already produces in production — a lost claim is reported as false, never thrown — and scoped to
    /// this facet so the opening's other statements are untouched.
    /// </summary>
    private sealed class DeclarationLosingWriter : IRunDataCompletenessWriter
    {
        private readonly IRunDataCompletenessWriter _real;

        public DeclarationLosingWriter(IRunDataCompletenessWriter real) => _real = real;

        public Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken) =>
            _real.InitializeAsync(initialization, cancellationToken);

        public Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) =>
            advance.Facet == WorkflowRunDataOwnerKinds.NativeRecord && advance.Expected > 0
                ? Task.FromResult(false)
                : _real.AdvanceAsync(advance, cancellationToken);

        public Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) => _real.NoticeAsync(gap, cancellationToken);

        public Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) =>
            _real.UnstateExpectationAsync(teamId, workflowRunId, facet, cancellationToken);
    }

    private sealed class PresenceLosingWriter : IRunDataCompletenessWriter
    {
        private readonly IRunDataCompletenessWriter _real;

        public PresenceLosingWriter(IRunDataCompletenessWriter real) => _real = real;

        public Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken) =>
            _real.InitializeAsync(initialization, cancellationToken);

        public Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) =>
            advance.Present > 0 ? Task.FromResult(false) : _real.AdvanceAsync(advance, cancellationToken);

        public Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) => _real.NoticeAsync(gap, cancellationToken);

        public Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) =>
            _real.UnstateExpectationAsync(teamId, workflowRunId, facet, cancellationToken);
    }
}
