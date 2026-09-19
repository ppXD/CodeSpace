using System.Text;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Recovery;
using CodeSpace.Core.Services.Agents.Recovery.Checkpoints;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Artifacts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The mid-run session-transcript checkpoint — what makes a run whose HOST dies continuable somewhere else.
///
/// <para>It hangs off the observer's own checkpoint tick, which fires on every poll of a running agent, so the whole
/// design rests on two gates: a tick whose file has not GROWN carries no new conversation, and a file that is growing
/// still earns at most one upload per <see cref="ArtifactSessionTranscriptCheckpointer.CheckpointInterval"/>. Delete
/// either and a busy fleet uploads whole session files continuously; delete the byte cap and one pathological
/// session reads itself into a worker's heap once a minute. Each of those is pinned below by counting the uploads a
/// FAKE store actually received — never by reading the gate's own return value, which would pass on a checkpointer
/// that never stores anything at all.</para>
///
/// <para>The clock is a <see cref="FakeTimeProvider"/>, so the cadence is decided by the test and not by how long the
/// test happened to take.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentSessionTranscriptCheckpointerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cs-checkpoint-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingRetentionWriter _store = new();
    private readonly StampingRuns _runs = new();

    public AgentSessionTranscriptCheckpointerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Checkpointer_uploads_only_when_the_file_grew()
    {
        // MUTATION this pins: drop the "length <= previous.Bytes" gate in ArtifactSessionTranscriptCheckpointer.IsDue
        // (i.e. upload unconditionally once the cadence has elapsed) and the second call stores a second copy of
        // identical bytes — the upload COUNT goes 1 → 2 and this test reds. The cadence is advanced past the interval
        // before every call, so the only thing being measured here is growth.
        var path = WriteTranscript("{\"type\":\"init\",\"session_id\":\"s-1\"}\n");
        var checkpointer = NewCheckpointer();

        var first = (await checkpointer.CheckpointAsync(Request(path), CancellationToken.None)).ShouldNotBeNull();

        var unchanged = await checkpointer.CheckpointAsync(Request(path, previousBytes: first.Bytes), CancellationToken.None);

        AppendTranscript(path, "{\"type\":\"assistant\",\"text\":\"重構結帳流程 🚀\"}\n");
        var grown = await checkpointer.CheckpointAsync(Request(path, previousBytes: first.Bytes), CancellationToken.None);

        unchanged.ShouldBeNull("a transcript that has not grown carries no new conversation, so re-uploading it buys nothing");
        grown.ShouldNotBeNull("a transcript that HAS grown is exactly what a later host needs");

        _store.Writes.Count.ShouldBe(2, $"only the first and the grown transcript may be stored; the store received {_store.Writes.Count} write(s)");
        grown.Bytes.ShouldBe(new FileInfo(path).Length, "the checkpoint reports the bytes it actually stored, which is what an operator reads to tell a captured conversation from an empty file");
        _runs.Stamps.Count.ShouldBe(2, "every stored checkpoint must reach the run row — an artifact no column names is one the reaper is entitled to collect");
        _runs.Stamps[^1].SessionId.ShouldBe("s-1", "the stamp carries the session id, because a checkpoint no CLI can be pointed at is not resumable");
    }

    [Fact]
    public void A_new_rounds_watermark_is_never_paired_with_the_old_rounds_byte_count()
    {
        // The watermark is (path, bytes) written in ONE assignment, and only when a checkpoint LANDS. Splitting it —
        // advancing the path when an attempt is dispatched, the byte count when one lands — re-creates the defect
        // the path key exists to prevent, and the sequence below is exactly how a revise round hits it: round one
        // lands 400 KiB, round two's FIRST tick on a new path declines (the CLI has not written its session file yet,
        // the ordinary first-tick outcome), and every later tick would then be measured against round one.
        // MUTATION: assign the path eagerly at Begin and the bytes only in Landed → the third Begin returns 400 KiB
        // instead of null → round two never checkpoints until it outgrows round one → red.
        var watermark = new AgentRunExecutor.SessionCheckpointWatermark();

        watermark.Begin("/round-0/session.jsonl").ShouldBeNull("nothing has landed yet");
        watermark.Landed(new SessionTranscriptCheckpoint(Guid.NewGuid(), DateTimeOffset.UtcNow, 400 * 1024));

        watermark.Begin("/round-1/session.jsonl").ShouldBeNull("a different round's transcript is a different conversation, not a shrunken one");
        watermark.Landed(null);   // declined: the new round's session file is not on disk yet

        watermark.Begin("/round-1/session.jsonl").ShouldBeNull("the decline landed nothing, so round two is still measured against nothing — never against round one's 400 KiB");

        watermark.Begin("/round-0/session.jsonl").ShouldBe(400 * 1024, "and round zero's own watermark survives, so returning to it still refuses to re-store bytes that have not changed");
    }

    [Fact]
    public void A_landed_checkpoint_is_what_the_next_attempt_must_grow_past()
    {
        // The other half of the same rule: a checkpoint that DID land must gate the next attempt, or the growth gate
        // is dead and every tick re-stores the same bytes. MUTATION: make Landed a no-op → red.
        var watermark = new AgentRunExecutor.SessionCheckpointWatermark();

        watermark.Begin("/session.jsonl");
        watermark.Landed(new SessionTranscriptCheckpoint(Guid.NewGuid(), DateTimeOffset.UtcNow, 8192));

        watermark.Begin("/session.jsonl").ShouldBe(8192);
    }

    [Theory]
    [InlineData(0, false)]    // the window was just claimed
    [InlineData(59, false)]   // still inside it
    [InlineData(60, true)]    // the interval has elapsed
    public void The_checkpoint_cadence_reopens_only_after_its_interval(int secondsSinceLastAttempt, bool due)
    {
        // The gate that decides how often a whole fleet reads and uploads transcripts. It lives on the CALLER
        // (AgentRunExecutor), in front of the locate, because locating is not free for every harness — Codex finds
        // its rollout by a recursive walk of the config home, and the drain tick fires several times a second.
        // MUTATION: return true unconditionally → the 0s and 59s arms red.
        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        AgentRunExecutor.SessionCheckpointDue(now.AddSeconds(-secondsSinceLastAttempt), now).ShouldBe(due);
        AgentRunExecutor.SessionCheckpointDue(null, now).ShouldBeTrue("the FIRST attempt must not wait — a run whose host dies in its first minute has to leave something behind");
        AgentRunExecutor.SessionCheckpointInterval.ShouldBe(TimeSpan.FromSeconds(60));
        // TWO budgets, because they bound two different things, and collapsing them costs one of the two.
        AgentRunExecutor.SessionCheckpointUploadBudget.ShouldBe(TimeSpan.FromSeconds(30),
            "one checkpoint may read and upload up to the 32 MiB capture cap, so a budget sized for a fast local store would cancel every checkpoint of a long conversation on a throttled destination");
        AgentRunExecutor.SessionCheckpointUploadBudget.ShouldBeLessThan(AgentRunExecutor.SessionCheckpointInterval,
            "an upload that outlived the cadence would overlap the next attempt");
        AgentRunExecutor.SessionCheckpointDrainBudget.ShouldBe(TimeSpan.FromSeconds(5),
            "this is how long a finished run's terminal write may be deferred by a best-effort aid — much shorter than the upload's own deadline, deliberately");
    }

    [Fact]
    public async Task A_path_whose_component_was_swapped_is_refused()
    {
        // The clamp walks the path for symlinks and the file is opened AFTER — two syscalls with the agent, which
        // has write access to its own bind-mounted config home, still running between them. A component swapped in
        // that window points this read at a file the clamp never approved, and its bytes would be uploaded into the
        // team's store and restored into the next attempt. The kernel's own answer for the OPEN handle is the check.
        //
        // Driven through CheckpointAsync, not the predicate, so the WIRING is what is pinned: a symlink is exactly
        // the swap the clamp exists to refuse, and it makes the mismatch real rather than argued.
        // MUTATION: replace OpenedTheResolvedFile's body with `return true;` → the decoy's bytes are stored → red.
        if (!OperatingSystem.IsLinux()) return;

        var honest = WriteTranscript("{\"type\":\"init\",\"session_id\":\"s-1\"}\n", "honest.jsonl");
        var swapped = Path.Combine(_root, "swapped.jsonl");
        File.CreateSymbolicLink(swapped, honest);

        (await NewCheckpointer().CheckpointAsync(Request(swapped), CancellationToken.None))
            .ShouldBeNull("the handle opened the symlink's TARGET, which is not the path the clamp resolved");
        _store.Writes.ShouldBeEmpty($"nothing may be stored for a path whose component was swapped; the store received {_store.Writes.Count} write(s)");

        (await NewCheckpointer().CheckpointAsync(Request(honest), CancellationToken.None))
            .ShouldNotBeNull("the same file read by its own real path IS checkpointed — without this the refusal would be indistinguishable from a checkpointer that never works");
    }

    [Fact]
    public async Task A_torn_trailing_line_is_never_uploaded()
    {
        // MUTATION this pins: upload the whole file instead of its complete prefix. The CLI appends while this reads,
        // so a read that lands mid-append ends in half a line — and the harness restores those bytes verbatim while
        // ResolveRestoredTranscriptAsync fails CLOSED on an unusable transcript, by which point the continuation's
        // budget is already spent. A torn checkpoint does not degrade to a cold start; it burns the one attempt.
        var whole = "{\"type\":\"init\",\"session_id\":\"s-1\"}\n{\"type\":\"assistant\",\"text\":\"done\"}\n";
        var path = WriteTranscript(whole + "{\"type\":\"assis");

        var taken = (await NewCheckpointer().CheckpointAsync(Request(path), CancellationToken.None)).ShouldNotBeNull();

        Encoding.UTF8.GetString(_store.Writes.Single().Bytes.ToArray()).ShouldBe(whole, "only the complete lines may be stored — the half-written tail is a line the CLI has not finished");
        taken.Bytes.ShouldBe(Encoding.UTF8.GetByteCount(whole), "and the checkpoint reports the prefix it actually stored, not the file's length");
    }

    [Fact]
    public async Task A_file_with_no_complete_line_yet_is_not_checkpointed()
    {
        // The boundary of the same rule: a first line still being written is not a shorter conversation, it is none.
        var path = WriteTranscript("{\"type\":\"ini");

        (await NewCheckpointer().CheckpointAsync(Request(path), CancellationToken.None)).ShouldBeNull();
        _store.Writes.ShouldBeEmpty("there is no whole turn to restore, so nothing may be stored or stamped");
        _runs.Stamps.ShouldBeEmpty();
    }

    [Fact]
    public async Task Growth_confined_to_the_torn_tail_is_not_a_new_checkpoint()
    {
        // The file grew, but every new byte is inside the line still being written — so the COMPLETE prefix is
        // unchanged and re-uploading it would store a byte-identical copy. The growth gate has to measure the prefix,
        // not the file. MUTATION: compare stream.Length instead of the prefix length → a second write appears.
        var whole = "{\"type\":\"init\",\"session_id\":\"s-1\"}\n";
        var path = WriteTranscript(whole);
        var checkpointer = NewCheckpointer();

        var taken = (await checkpointer.CheckpointAsync(Request(path), CancellationToken.None)).ShouldNotBeNull();

        AppendTranscript(path, "{\"type\":\"assistant\",\"tex");

        (await checkpointer.CheckpointAsync(Request(path, previousBytes: taken.Bytes), CancellationToken.None)).ShouldBeNull("the only new bytes are an unfinished line");
        _store.Writes.Count.ShouldBe(1, $"the store must have seen exactly the first checkpoint; it saw {_store.Writes.Count}");
    }

    [Fact]
    public async Task Checkpoint_respects_the_transcript_byte_cap()
    {
        // MUTATION this pins: remove the "length > MaxSessionTranscriptBytes()" gate and the checkpointer reads a
        // pathological session file whole into the worker's heap — once per cadence, per running agent, unbounded
        // across concurrency. The cap is the SAME one the end-of-run capture applies (deliberately not a second
        // knob), so it is read from the executor here rather than restated.
        var over = WriteTranscript(Filler(AgentRunExecutor.MaxSessionTranscriptBytes() + 1));
        var checkpointer = NewCheckpointer();

        var refused = await checkpointer.CheckpointAsync(Request(over), CancellationToken.None);

        refused.ShouldBeNull("a session past the cap is not read whole into memory; the run keeps going and a later continuation cold-starts");
        _store.Writes.ShouldBeEmpty($"nothing may be stored for an over-cap transcript; the store received {_store.Writes.Count} write(s)");
        _runs.Stamps.ShouldBeEmpty("and nothing may be stamped either — a row pointing at bytes that were never written is worse than no row");
    }

    [Fact]
    public async Task An_under_cap_transcript_is_checkpointed_so_the_cap_is_not_a_dead_path()
    {
        // The other half of the pair: without it, "over the cap stores nothing" would still pass on a checkpointer
        // that stores nothing ever.
        var under = WriteTranscript(Filler(64 * 1024));

        var taken = await NewCheckpointer().CheckpointAsync(Request(under), CancellationToken.None);

        taken.ShouldNotBeNull();
        _store.Writes.Single().Bytes.ToArray().ShouldBe(await File.ReadAllBytesAsync(under), "the stored bytes are the transcript verbatim — a CLI session restored from a partial file is not a shorter conversation, it is a corrupt one");
        _store.Writes.Single().RetentionClass.ShouldBe(ArtifactRetentionClass.SessionTranscriptCheckpoint, "a checkpoint gets its OWN short-floor class — on the event class's seven-day floor a long run would hold every superseded copy of its transcript for over a week");
    }

    [Fact]
    public async Task A_new_rounds_smaller_transcript_is_still_checkpointed()
    {
        // The growth watermark is keyed on the PATH by the caller. A revise round opens a NEW config home whose
        // transcript legitimately starts smaller than the finished previous round's; a count-only gate would silently
        // stop checkpointing round two until it outgrew round one, which is precisely the window a lost host would
        // fall into. MUTATION: in AgentRunExecutor, pass the watermark without comparing paths → this reds.
        var first = WriteTranscript(Filler(8 * 1024), "round-0.jsonl");
        var checkpointer = NewCheckpointer();

        var round0 = (await checkpointer.CheckpointAsync(Request(first), CancellationToken.None)).ShouldNotBeNull();

        var second = WriteTranscript("{\"type\":\"init\",\"session_id\":\"s-2\"}\n", "round-1.jsonl");

        // The CALLER keys the watermark on the path, so a new round's request carries none — which is the whole
        // reason the watermark is the caller's and not this instance's.
        round0.Bytes.ShouldBeGreaterThan(new FileInfo(second).Length, "this arm is only meaningful while round one's transcript is the LARGER of the two");
        var taken = await checkpointer.CheckpointAsync(Request(second, previousBytes: null) with { SessionId = "s-2" }, CancellationToken.None);

        taken.ShouldNotBeNull("a new round's transcript is a different conversation, not a shrunken one");
        taken.Bytes.ShouldBe(new FileInfo(second).Length);
    }

    [Fact]
    public async Task A_transcript_that_is_not_on_disk_yet_is_not_a_failure()
    {
        var absent = Path.Combine(_root, "never-written.jsonl");

        (await NewCheckpointer().CheckpointAsync(Request(absent), CancellationToken.None)).ShouldBeNull("the CLI may not have written its session yet; a tick that finds nothing declines rather than throwing into the observer loop");
        _store.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stamp_that_loses_its_fence_takes_no_checkpoint()
    {
        // The row write is fenced to the owner token, so a worker whose ownership was already reclaimed cannot point
        // a live run's recovery at ITS stale conversation. When that race is lost there is no checkpoint — and the
        // local cadence state must NOT advance, or the winning generation's next tick would be suppressed by a
        // checkpoint that never landed.
        var path = WriteTranscript("{\"type\":\"init\",\"session_id\":\"s-1\"}\n");
        _runs.Wins = false;
        var checkpointer = NewCheckpointer();

        (await checkpointer.CheckpointAsync(Request(path), CancellationToken.None)).ShouldBeNull();

        _runs.Wins = true;
        (await checkpointer.CheckpointAsync(Request(path), CancellationToken.None)).ShouldNotBeNull("the retry is immediate — a refused stamp must not advance the growth watermark it never earned");
    }

    private ArtifactSessionTranscriptCheckpointer NewCheckpointer() =>
        new(_store, _runs, _clock, NullLogger<ArtifactSessionTranscriptCheckpointer>.Instance);

    private static SessionTranscriptCheckpointRequest Request(string path, long? previousBytes = null) =>
        new(Guid.NewGuid(), new AgentRunOwnerToken(Guid.NewGuid(), Guid.NewGuid(), 7), path, "s-1", previousBytes);

    private string WriteTranscript(string content, string name = "session.jsonl") => WriteTranscript(Encoding.UTF8.GetBytes(content), name);

    private string WriteTranscript(byte[] content, string name = "session.jsonl")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);

        return path;
    }

    private static void AppendTranscript(string path, string line) => File.AppendAllText(path, line);

    /// <summary>A transcript of at least <paramref name="atLeastBytes"/>, repeating a realistic mixed-script line so a byte comparison is not satisfied by pure ASCII.</summary>
    private static byte[] Filler(long atLeastBytes)
    {
        var block = Encoding.UTF8.GetBytes("{\"role\":\"user\",\"text\":\"重構結帳流程，先寫測試 🚀\"}\n");

        using var session = new MemoryStream();

        while (session.Length < atLeastBytes) session.Write(block);

        return session.ToArray();
    }

    /// <summary>Records every declaring write so the tests can COUNT uploads — the only signal that distinguishes a gate that holds from a gate that was deleted.</summary>
    private sealed class RecordingRetentionWriter : IArtifactRetentionWriter
    {
        public List<ArtifactRetentionWriteRequest> Writes { get; } = [];

        public Task<ArtifactRetentionWrite> PutDeclaredAsync(ArtifactRetentionWriteRequest request, CancellationToken cancellationToken)
        {
            Writes.Add(request);

            return Task.FromResult(new ArtifactRetentionWrite(Guid.NewGuid(), Declared: true));
        }
    }

    /// <summary>Minimal IAgentRunService: records the checkpoint stamps and can be told to lose the fenced race. Every other member throws — the checkpointer calls none of them.</summary>
    private sealed class StampingRuns : IAgentRunService
    {
        public List<(SessionTranscriptCheckpoint Checkpoint, string? SessionId)> Stamps { get; } = [];

        public bool Wins { get; set; } = true;

        public Task<bool> StampSessionTranscriptCheckpointAsync(AgentRunOwnerToken owner, SessionTranscriptCheckpoint checkpoint, string? sessionId, CancellationToken cancellationToken)
        {
            if (!Wins) return Task.FromResult(false);

            Stamps.Add((checkpoint, sessionId));
            return Task.FromResult(true);
        }

        public Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, string iterationKey = "", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRun> CreateReviewAsync(CodeSpace.Core.Services.Agents.Review.AgentReviewCreation request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRun> GetAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResumableSession?> FindResumableSessionAsync(Guid teamId, Guid? parentRunId, string nodeId, string iterationKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResumableSession?> FindResumableSubtaskAttemptAsync(Guid teamId, Guid supervisorRunId, string subtaskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AppendEventsAsync(Guid runId, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunEvent> AppendSystemEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RejectQueuedAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunReattachReservation?> ReserveReattachAsync(AgentRunReconciliationCandidate candidate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunOwnerToken?> ClaimOwnershipAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunReattachReservation?> ReserveReattachAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunOwnerToken?> ActivateReattachAsync(AgentRunReattachReservation reservation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AssertOwnershipAsync(AgentRunOwnerToken owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HeartbeatAsync(AgentRunOwnerToken owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetRunnerHandleAsync(AgentRunOwnerToken owner, string handleJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetSandboxConfinementAsync(AgentRunOwnerToken owner, string confinementJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunEvent> AppendEventAsync(AgentRunOwnerToken owner, AgentEvent @event, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AppendEventsAsync(AgentRunOwnerToken owner, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(AgentRunOwnerToken owner, AgentRunResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ReclaimForReattachAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetRunnerHandleAsync(Guid runId, string handleJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetSandboxConfinementAsync(Guid runId, string confinementJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Guid runId, AgentRunResult result, long expectedEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelQueuedAsync(Guid runId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelRunningAsync(Guid runId, string reason, AgentRunAbandonCause cause, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CodeSpace.Messages.Dtos.Agents.AgentRunSummary?> GetSummaryForTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentRunEvent>> GetEventsAsync(Guid runId, Guid teamId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
