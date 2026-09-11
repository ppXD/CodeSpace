using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// What a log capture stream does while its remote storage is TRANSIENTLY refusing segments.
///
/// <para>The property under test is the one the bridge used to get wrong: a 503 that would have cleared in seconds
/// cost the operator the tail of the transcript, because the only failure mode was "spend the append budget, then
/// terminalize and drop the queued bytes". Every assertion here is a mutation detector for that — the drained SHA-256
/// must equal the source's, which a dropped pending segment cannot satisfy — and for the other half, that a wait which
/// outgrows its ceilings NAMES what it lost instead of parking silently.</para>
///
/// <para>Time is virtual (<see cref="FakeTimeProvider"/>): the ceilings are 30 minutes and 16 MiB in production, and a
/// suite that had to spend either would not be a unit suite. The poll cadence stays real, so the loop keeps ticking
/// while the test moves the clock across a backoff or a park window.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunLogCaptureBackpressureTests
{
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid ProfileId = Guid.NewGuid();
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_storage_outage_freezes_every_offset_and_drops_no_byte_before_draining_in_order()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var logs = new FakeLogService { CurrentFence = 1, RemoteUnavailable = true };
        var stalls = new FakeStallWriter();
        var secret = "sk-outage-secret";
        var redactor = new SecretRedactor([secret]);
        var stdout = Payload(secret, 2 * 1024 * 1024 + 511);
        var source = new FakeLogSource();
        source.Set("stdout", stdout);
        source.Set("stderr", []);
        var bridge = Bridge(logs, clock, stalls);
        var expected = Result();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var capture = await bridge.OpenAsync(Request(source, redactor), CancellationToken.None);
        var observing = capture.ObserveAsync(async (_, _) => { await release.Task; return expected; }, CancellationToken.None);

        await WaitAsync(() => stalls.Held.Count > 0, "the bridge never recorded a remote stall — check FlushBacklogAsync's transient arm");
        var held = logs.Head(AgentRunLogKinds.StandardOutput);
        held.Metadata.SegmentCount.ShouldBe(0, "a transient refusal may not advance the segment head");
        held.Metadata.TotalBytes.ShouldBe(0, "a transient refusal may not advance the byte head");
        held.Metadata.SourceOffsetBytes.ShouldBe(0, "a transient refusal may not advance the source cursor");
        held.Metadata.State.ShouldBe(AgentRunLogStreamState.Open, "an outage the provider calls retryable is not a terminal capture verdict");
        stalls.Held.ShouldAllBe(row => row.StalledSince == DateTimeOffset.UnixEpoch && row.StallCode == "capture-backend-unavailable");

        logs.RemoteUnavailable = false;
        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitAsync(() => logs.Bytes(AgentRunLogKinds.StandardOutput).Length > 0, "the held segments never drained after the provider recovered");
        release.TrySetResult();
        var observed = await observing;
        await bridge.CompleteRunAsync(TeamId, RunId, 1, CancellationToken.None);

        observed.ShouldBeSameAs(expected, "shadow capture never reinterprets the harness result, outage or not");
        Sha256(logs.Bytes(AgentRunLogKinds.StandardOutput)).ShouldBe(Sha256(redactor.CreateUtf8Stream().Transform(stdout, final: true).Bytes.ToArray()),
            "not one byte may be lost to a transient outage — a mismatch means the held segment was dropped instead of retried");
        logs.Ordinals(AgentRunLogKinds.StandardOutput).ShouldBe(Enumerable.Range(1, logs.Ordinals(AgentRunLogKinds.StandardOutput).Count).Select(value => (long)value),
            "the queue drains head-first, so the committed ordinals stay contiguous and in source order");
        logs.Head(AgentRunLogKinds.StandardOutput).Metadata.State.ShouldBe(AgentRunLogStreamState.Completed);
        stalls.Cleared.ShouldNotBeEmpty("a recovered stream must stop reading as stalled, or the Room keeps reporting an outage that ended");
        stalls.Cleared.Count.ShouldBeLessThanOrEqualTo(stalls.Held.Count, "the marker is written on transitions, not once per retry");
    }

    /// <summary>The held span at the moment both ceilings are evaluated: the whole 2 MiB source, read out of the spool while the remote was refusing it.</summary>
    private const long HeldBytes = 2L * 1024 * 1024;

    [Theory]
    [InlineData(30, 64L * 1024 * 1024, 30)]          // the wait reached the park window EXACTLY — the ceiling is inclusive
    [InlineData(240, HeldBytes, 2)]                  // the held span reached the backlog window exactly — likewise inclusive
    public async Task An_outage_past_a_ceiling_parks_with_a_named_gap_instead_of_a_silent_loss(int parkAfterMinutes, long maxBacklogBytes, int advanceMinutes)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var logs = new FakeLogService { CurrentFence = 1, RemoteUnavailable = true };
        var gaps = new FakeCompletenessWriter();
        var stalls = new FakeStallWriter();
        var source = new FakeLogSource();
        source.Set("stdout", Payload(null, (int)HeldBytes));
        source.Set("stderr", []);
        var backpressure = new CaptureBackpressureOptions { ParkAfter = TimeSpan.FromMinutes(parkAfterMinutes), MaxLocalBacklogBytes = maxBacklogBytes };
        var bridge = Bridge(logs, clock, stalls, gaps, backpressure);
        var expected = Result();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var capture = await bridge.OpenAsync(Request(source), CancellationToken.None);
        var observing = capture.ObserveAsync(async (_, _) => { await release.Task; return expected; }, CancellationToken.None);
        // The marker, not the append attempt: the stall's Since is stamped after the refusal, so a clock advanced on
        // the attempt alone can land BEFORE it is stamped and reset the window this row is measuring to the minute.
        await WaitAsync(() => stalls.Held.Count > 0, "the bridge never recorded the outage it is supposed to hold through");
        clock.Advance(TimeSpan.FromMinutes(advanceMinutes));
        await WaitAsync(() => logs.Head(AgentRunLogKinds.StandardOutput).Metadata.State != AgentRunLogStreamState.Open, "the stream never parked past its ceiling");
        release.TrySetResult();
        (await observing).ShouldBeSameAs(expected);

        var stdout = logs.Head(AgentRunLogKinds.StandardOutput).Metadata;
        stdout.State.ShouldBe(AgentRunLogStreamState.CaptureFailed);
        stdout.ErrorCode.ShouldBe(CaptureBackpressureOptions.RemoteOutageExhaustedCode, "an operator greps this exact code to tell a storage incident from a broken agent");
        stdout.TotalBytes.ShouldBe(0, "parking never invents progress it did not make");

        var gap = gaps.Gaps.ShouldHaveSingleItem("a parked stream without a gap row leaves the completeness plane reporting data the run does not have");
        gap.Reason.ShouldBe(CaptureGapReason.RemoteUnavailable);
        gap.SubjectKind.ShouldBe(WorkflowRunDataOwnerKinds.LogStream);
        gap.StreamId.ShouldBe(stdout.StreamId);
        gap.AgentRunId.ShouldBe(RunId);
        gap.RangeKind.ShouldBe(CaptureGapRangeKind.ByteOffset);
        gap.RangeStart.ShouldBe(0, "the first missing source byte is the durable head, because nothing past it committed");
        gap.RangeEnd.ShouldBeNull("\"from here on, and I do not know how much\" is the honest shape of an outage that ended the capture");
        gap.ReasonDetail.ShouldNotBeNullOrWhiteSpace();
        gap.CreatedAt.ShouldBeGreaterThanOrEqualTo(gap.NoticedAt);
    }

    [Theory]
    [InlineData(30, 64L * 1024 * 1024, 29)]          // one minute inside the park window
    [InlineData(240, HeldBytes + 1, 2)]              // one byte under the backlog window
    public async Task An_outage_just_inside_a_ceiling_keeps_holding_and_still_commits_every_byte(int parkAfterMinutes, long maxBacklogBytes, int advanceMinutes)
    {
        // The held side of both boundaries. Without it the park theory above proves only that SOME ceiling fires: `>`
        // and `>=` are indistinguishable when every row is past the line, and a ceiling that parks one attempt early
        // throws away bytes the remote was about to take.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var logs = new FakeLogService { CurrentFence = 1, RemoteUnavailable = true };
        var gaps = new FakeCompletenessWriter();
        var stalls = new FakeStallWriter();
        var source = new FakeLogSource();
        source.Set("stdout", Payload(null, (int)HeldBytes));
        source.Set("stderr", []);
        var backpressure = new CaptureBackpressureOptions { ParkAfter = TimeSpan.FromMinutes(parkAfterMinutes), MaxLocalBacklogBytes = maxBacklogBytes };
        var bridge = Bridge(logs, clock, stalls, gaps, backpressure);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var capture = await bridge.OpenAsync(Request(source), CancellationToken.None);
        var observing = capture.ObserveAsync(async (_, _) => { await release.Task; return Result(); }, CancellationToken.None);
        await WaitAsync(() => stalls.Held.Count > 0, "the bridge never recorded the outage it is supposed to hold through");
        clock.Advance(TimeSpan.FromMinutes(advanceMinutes));
        await WaitAsync(() => logs.AppendAttempts > 1, "the bridge never retried after the clock crossed the backoff, so no ceiling was evaluated at all");

        var stdout = logs.Head(AgentRunLogKinds.StandardOutput).Metadata;
        stdout.State.ShouldBe(AgentRunLogStreamState.Open, "one unit under a ceiling is still a wait, not a loss");
        stdout.ErrorCode.ShouldBeNull();
        gaps.Gaps.ShouldBeEmpty("a gap row is the record of a span this run will never have; nothing is lost yet");

        // And the wait was worth making: the provider comes back inside the window and every held byte commits.
        logs.RemoteUnavailable = false;
        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitAsync(() => logs.Head(AgentRunLogKinds.StandardOutput).Metadata.SourceOffsetBytes == HeldBytes, "the span held at the boundary never drained after the provider recovered");
        release.TrySetResult();
        await observing;

        logs.Bytes(AgentRunLogKinds.StandardOutput).Length.ShouldBe((int)HeldBytes);
    }

    [Fact]
    public async Task An_outage_that_begins_during_the_final_drain_still_says_so_without_parking()
    {
        // The final drain is bounded by the finalization budget instead of the park ceiling, which is why it never
        // parks — but it was also the one path that wrote no marker, so an incident starting here left the Room
        // reading "Finalizing" for the whole budget with no health signal at all. The payload is under one minimum
        // segment, so nothing is appended until the drain: the outage below can only be seen on the final path.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var logs = new FakeLogService { CurrentFence = 1 };
        var stalls = new FakeStallWriter();
        var gaps = new FakeCompletenessWriter();
        var source = new FakeLogSource();
        source.Set("stdout", Payload(null, 4096));
        source.Set("stderr", []);
        var bridge = Bridge(logs, clock, stalls, gaps, finalizationBudget: TimeSpan.FromSeconds(2));

        var capture = await bridge.OpenAsync(Request(source), CancellationToken.None);
        await capture.ObserveAsync((_, _) => { logs.RemoteUnavailable = true; return Task.FromResult(Result()); }, CancellationToken.None);

        stalls.Held.ShouldNotBeEmpty("an outage that starts on the final drain is the same outage; the Room cannot read \"Finalizing\" through it");
        stalls.Held[0].StallCode.ShouldBe("capture-backend-unavailable");
        var stdout = logs.Head(AgentRunLogKinds.StandardOutput).Metadata;
        stdout.State.ShouldBe(AgentRunLogStreamState.Open, "the final drain never parks: a stream its budget cancels stays Open and reconcilable");
        stdout.ErrorCode.ShouldBeNull();
        gaps.Gaps.ShouldBeEmpty("a marker is not a park, and only a park may declare a span lost");
    }

    [Fact]
    public async Task A_permanent_refusal_still_terminalizes_immediately_instead_of_waiting_out_a_park_window()
    {
        // The ceiling may never be reached for a fault that would fail identically in 30 minutes: waiting one out
        // would hold a run's whole log hostage to a credential nobody is going to repair inside the window.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var logs = new FakeLogService { CurrentFence = 1, RejectAppendWith = new AgentRunLogProblem(AgentRunLogProblemCode.AccessDenied) };
        var stalls = new FakeStallWriter();
        var gaps = new FakeCompletenessWriter();
        var source = new FakeLogSource();
        source.Set("stdout", Payload(null, 300 * 1024));
        source.Set("stderr", []);
        var bridge = Bridge(logs, clock, stalls, gaps);
        var watch = Stopwatch.StartNew();

        var observed = await (await bridge.OpenAsync(Request(source), CancellationToken.None)).ObserveAsync((_, _) => Task.FromResult(Result()), CancellationToken.None);

        observed.Status.ShouldBe(SandboxStatus.Success);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        logs.Head(AgentRunLogKinds.StandardOutput).Metadata.ErrorCode.ShouldBe("capture-access-denied", "the durable cause names the real fault, never the backpressure ceiling");
        stalls.Held.ShouldBeEmpty("a permanent verdict is not a stall, and a stall marker over one would tell the Room to expect a recovery");
        gaps.Gaps.ShouldBeEmpty("the existing terminal path already names this loss on the stream; a second reason vocabulary would double-count it");
    }

    [Fact]
    public void The_committed_backpressure_ceilings_are_pinned_and_the_backoff_stays_inside_them()
    {
        // Rule 8: these are the values an operator reasons about during an incident. A rename or a quiet retune has to
        // be a visible decision, so they are pinned here rather than only exercised through a flow.
        CaptureBackpressureOptions.DefaultRetryBase.ShouldBe(TimeSpan.FromSeconds(5));
        CaptureBackpressureOptions.DefaultRetryCeiling.ShouldBe(TimeSpan.FromMinutes(2));
        CaptureBackpressureOptions.DefaultParkAfter.ShouldBe(TimeSpan.FromMinutes(30));
        CaptureBackpressureOptions.DefaultMaxLocalBacklogBytes.ShouldBe(16L * 1024 * 1024);
        CaptureBackpressureOptions.RemoteOutageExhaustedCode.ShouldBe("capture.remote-outage-exhausted");

        var options = CaptureBackpressureOptions.Default;
        var jitter = new Random(20260911);
        var delays = Enumerable.Range(1, 20).Select(attempt => options.RetryDelay(attempt, jitter)).ToList();

        delays.ShouldAllBe(delay => delay > TimeSpan.Zero && delay <= options.RetryCeiling, "jitter may only ever SHORTEN a wait, or the ceiling stops being a ceiling");
        delays[0].ShouldBeGreaterThan(options.RetryBase * 0.7, "the first wait is the base, not a fraction of it");
        delays[0].ShouldBeLessThanOrEqualTo(options.RetryBase);
        delays[^1].ShouldBeGreaterThan(options.RetryCeiling * 0.7, "a long outage settles at the ceiling so a recovery is still noticed within it");
    }

    private static AgentRunLogCaptureBridge Bridge(FakeLogService logs, FakeTimeProvider clock, FakeStallWriter stalls, FakeCompletenessWriter? gaps = null, CaptureBackpressureOptions? backpressure = null, TimeSpan? finalizationBudget = null) =>
        new(logs, new ReadyStorageResolver(), new FakeRecoveryService(), NullLogger<AgentRunLogCaptureBridge>.Instance,
            new AgentRunLogCaptureBridgeOptions(TimeSpan.FromMilliseconds(200), finalizationBudget ?? TimeSpan.FromSeconds(5)) { Backpressure = backpressure ?? CaptureBackpressureOptions.Default },
            stalls, gaps ?? new FakeCompletenessWriter(), clock);

    private static AgentRunLogCaptureOpenRequest Request(FakeLogSource source, SecretRedactor? redactor = null) => new()
    {
        TeamId = TeamId, AgentRunId = RunId, ActorId = ActorId, WorkerFenceEpoch = 1,
        Handle = new SandboxHandle { Kind = "fake", ProcessId = 1, SpoolDirectory = "/opaque", Deadline = DateTimeOffset.MaxValue, AgentRunLogCaptureSessionId = Guid.NewGuid() },
        Source = source, Redactor = redactor ?? SecretRedactor.None,
    };

    private static SandboxResult Result() => new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "legacy", Stderr = "legacy-error" };
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Deterministic bytes with an optional secret straddling a segment boundary, so redaction is exercised across the queue rather than only inside one segment.</summary>
    private static byte[] Payload(string? secret, int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++) bytes[index] = (byte)('a' + index % 23);
        if (secret != null) System.Text.Encoding.UTF8.GetBytes(secret).CopyTo(bytes, 1024 * 1024 - 4);
        return bytes;
    }

    /// <summary>Explicit timeout with the watched signal named, so a failure says what never happened rather than only that time ran out (Rule 12.10).</summary>
    private static async Task WaitAsync(Func<bool> condition, string signal)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Patience)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException($"{signal} (waited {Patience.TotalSeconds:F0}s)");
    }

    private sealed class ReadyStorageResolver : IAgentRunLogStorageResolver
    {
        public Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentRunLogStorageResolution>(new AgentRunLogStorageResolution.Ready(ProfileId, 1));
    }

    private sealed class FakeRecoveryService : IAgentRunLogCaptureRecoveryService
    {
        public Task<AgentRunLogCaptureDeclarationResult> DeclareAsync(AgentRunLogCaptureDeclarationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<AgentRunLogCaptureDeclarationResult>(new AgentRunLogCaptureDeclarationResult.Declared(request.Streams.Count, 0));
        public Task<AgentRunLogCaptureRecoverySummary> ReconcileAsync(CancellationToken cancellationToken) => Task.FromResult(new AgentRunLogCaptureRecoverySummary(0, 0, 0, 0, 0, 0));
    }

    private sealed class FakeStallWriter : IAgentRunLogRemoteStallWriter
    {
        private readonly object _gate = new();
        private readonly List<AgentRunLogRemoteStallRequest> _written = [];

        public IReadOnlyList<AgentRunLogRemoteStallRequest> Held { get { lock (_gate) return _written.Where(row => row.StalledSince != null).ToArray(); } }
        public IReadOnlyList<AgentRunLogRemoteStallRequest> Cleared { get { lock (_gate) return _written.Where(row => row.StalledSince == null).ToArray(); } }

        /// <summary>Null: the production writer hands back a head only when it actually moved one, and these tests must not depend on a refreshed revision to pass.</summary>
        public Task<AgentRunLogMetadata?> RecordRemoteStallAsync(AgentRunLogRemoteStallRequest request, CancellationToken cancellationToken)
        {
            lock (_gate) _written.Add(request);
            return Task.FromResult<AgentRunLogMetadata?>(null);
        }
    }

    private sealed class FakeCompletenessWriter : IRunDataCompletenessWriter
    {
        private readonly object _gate = new();
        private readonly List<WorkflowRunCaptureGap> _gaps = [];

        public IReadOnlyList<WorkflowRunCaptureGap> Gaps { get { lock (_gate) return _gaps.ToArray(); } }

        public Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken)
        {
            lock (_gate) _gaps.Add(gap);
            return Task.FromResult(true);
        }

        public Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeLogSource : ISandboxDurableLogSource
    {
        private readonly ConcurrentDictionary<string, byte[]> _sources = new(StringComparer.Ordinal);

        public IReadOnlyList<SandboxDurableLogDescriptor> DescribeLogs(SandboxHandle handle) =>
        [
            new("stdout", AgentRunLogKinds.StandardOutput, AgentRunLogRepresentations.PlainTextContentType, AgentRunLogRepresentations.Utf8ContentEncoding, "fake-spool/v1"),
            new("stderr", AgentRunLogKinds.StandardError, AgentRunLogRepresentations.PlainTextContentType, AgentRunLogRepresentations.Utf8ContentEncoding, "fake-spool/v1"),
        ];

        public Task<SandboxDurableLogReadResult> ReadAsync(SandboxDurableLogReadRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = _sources[request.SourceKey];
            var available = bytes.LongLength - request.OffsetBytes;
            if (available == 0 && request.FinalDrain) return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.EndOfSource());
            if (available == 0 || (!request.FinalDrain && available < request.MinimumBytes)) return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.NoData());
            var length = (int)Math.Min(available, request.MaximumBytes);
            return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.Available(bytes.AsMemory((int)request.OffsetBytes, length)));
        }

        public void Set(string key, byte[] bytes) => _sources[key] = bytes;
    }

    /// <summary>A head the bridge can actually stall against: <see cref="RemoteUnavailable"/> is the provider being out, and every contiguity guard is real so a dropped or reordered segment is refused rather than absorbed.</summary>
    private sealed class FakeLogService : IAgentRunLogService
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, StreamHead> _streams = new(StringComparer.Ordinal);

        public long CurrentFence { get; set; }
        public volatile bool RemoteUnavailable;
        public AgentRunLogProblem? RejectAppendWith { get; init; }
        public int AppendAttempts { get; private set; }

        public AgentRunLogCaptureHead Head(string kind) { lock (_gate) return _streams[kind].Head; }
        public byte[] Bytes(string kind) { lock (_gate) return _streams[kind].Bytes.ToArray(); }
        public IReadOnlyList<long> Ordinals(string kind) { lock (_gate) return _streams[kind].Ordinals.ToArray(); }

        public Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (request.WorkerFenceEpoch != CurrentFence) return Task.FromResult<AgentRunLogOpenResult>(new AgentRunLogOpenResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.StaleWorker)));
                if (!_streams.TryGetValue(request.StreamKind, out var stream))
                {
                    var now = DateTimeOffset.UtcNow;
                    var metadata = new AgentRunLogMetadata(Guid.NewGuid(), RunId, request.StreamKind, request.ContentType, request.ContentEncoding, request.CaptureSource, request.Retention, AgentRunLogStreamState.Open, 1, 0, 0, 0, null, now, now, null, null);
                    stream = new StreamHead(new AgentRunLogCaptureHead(metadata, request.WorkerFenceEpoch, request.CaptureSessionId, 0, null));
                    _streams.Add(request.StreamKind, stream);
                }
                return Task.FromResult<AgentRunLogOpenResult>(new AgentRunLogOpenResult.Opened(stream.Head.Metadata, false, false) { CaptureSourceBaseOffsetBytes = 0, CaptureFinalizedAt = stream.Head.CaptureFinalizedAt });
            }
        }

        public Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                AppendAttempts++;
                if (RejectAppendWith is { } permanent) return Task.FromResult<AgentRunLogAppendResult>(new AgentRunLogAppendResult.Rejected(permanent));
                if (RemoteUnavailable) return Task.FromResult<AgentRunLogAppendResult>(new AgentRunLogAppendResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.BackendUnavailable, true)));
                var stream = Find(request.StreamId);
                if (stream.Head.Metadata.TotalBytes != request.ExpectedOffsetBytes || stream.Head.Metadata.SourceOffsetBytes != request.ExpectedSourceOffsetBytes || stream.Head.Metadata.SegmentCount + 1 != request.ExpectedSegmentOrdinal)
                    return Task.FromResult<AgentRunLogAppendResult>(new AgentRunLogAppendResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.NonContiguous)));
                var start = stream.Head.Metadata.TotalBytes;
                stream.Bytes.AddRange(request.Bytes.ToArray());
                stream.Ordinals.Add(request.ExpectedSegmentOrdinal);
                stream.Head = stream.Head with
                {
                    Metadata = stream.Head.Metadata with
                    {
                        Revision = stream.Head.Metadata.Revision + 1, SegmentCount = stream.Head.Metadata.SegmentCount + 1,
                        TotalBytes = start + request.Bytes.Length, SourceOffsetBytes = stream.Head.Metadata.SourceOffsetBytes + request.SourceLengthBytes,
                    },
                };
                var receipt = new AgentRunLogSegmentReceipt(Guid.NewGuid(), request.ExpectedSegmentOrdinal, start, request.Bytes.Length, request.ExpectedSourceOffsetBytes, request.SourceLengthBytes, Guid.NewGuid());
                return Task.FromResult<AgentRunLogAppendResult>(new AgentRunLogAppendResult.Appended(stream.Head.Metadata, receipt, false));
            }
        }

        public Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var stream = Find(request.StreamId);
                if (stream.Head.CaptureFinalizedAt != null) return Task.FromResult<AgentRunLogFinalizeSourceResult>(new AgentRunLogFinalizeSourceResult.Finalized(stream.Head.Metadata, true));
                if (stream.Head.Metadata.SourceOffsetBytes != request.ExpectedSourceOffsetBytes)
                    return Task.FromResult<AgentRunLogFinalizeSourceResult>(new AgentRunLogFinalizeSourceResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.NonContiguous)));
                stream.Head = stream.Head with { CaptureFinalizedAt = DateTimeOffset.UtcNow, Metadata = stream.Head.Metadata with { Revision = stream.Head.Metadata.Revision + 1 } };
                return Task.FromResult<AgentRunLogFinalizeSourceResult>(new AgentRunLogFinalizeSourceResult.Finalized(stream.Head.Metadata, false));
            }
        }

        public Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var stream = Find(request.StreamId);
                if (stream.Head.CaptureFinalizedAt == null) return Task.FromResult<AgentRunLogCompleteResult>(new AgentRunLogCompleteResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.SourceNotFinalized)));
                stream.Head = stream.Head with { Metadata = stream.Head.Metadata with { State = AgentRunLogStreamState.Completed, Revision = stream.Head.Metadata.Revision + 1, CompletedAt = DateTimeOffset.UtcNow } };
                return Task.FromResult<AgentRunLogCompleteResult>(new AgentRunLogCompleteResult.Completed(stream.Head.Metadata));
            }
        }

        public Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var stream = Find(request.StreamId);
                if (stream.Head.Metadata.State == request.TerminalState) return Task.FromResult<AgentRunLogFailCaptureResult>(new AgentRunLogFailCaptureResult.Failed(stream.Head.Metadata, true));
                stream.Head = stream.Head with { Metadata = stream.Head.Metadata with { State = request.TerminalState, Revision = stream.Head.Metadata.Revision + 1, ErrorCode = request.ErrorCode, CompletedAt = DateTimeOffset.UtcNow } };
                return Task.FromResult<AgentRunLogFailCaptureResult>(new AgentRunLogFailCaptureResult.Failed(stream.Head.Metadata, false));
            }
        }

        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentRunLogMetadataResult>(new AgentRunLogMetadataResult.Found(Find(streamId).Head.Metadata));
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken)
        { lock (_gate) return Task.FromResult<IReadOnlyList<AgentRunLogMetadata>>(_streams.Values.Select(value => value.Head.Metadata).ToArray()); }
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken)
        { lock (_gate) return Task.FromResult<IReadOnlyList<AgentRunLogCaptureHead>>(_streams.Values.Select(value => value.Head).ToArray()); }
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        /// <summary>The reconciler's statement about a dead worker's streams, never the bridge's — a call here would mean the bridge reached for a verb that is not its to make.</summary>
        public Task<int> RecordOwnerLossAsync(AgentRunLogOwnerLossRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();

        private StreamHead Find(Guid streamId) => _streams.Values.Single(value => value.Head.Metadata.StreamId == streamId);

        private sealed class StreamHead(AgentRunLogCaptureHead head)
        {
            public AgentRunLogCaptureHead Head { get; set; } = head;
            public List<byte> Bytes { get; } = [];
            public List<long> Ordinals { get; } = [];
        }
    }
}
