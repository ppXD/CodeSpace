using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Artifacts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace CodeSpace.Core.Services.Agents.Recovery.Checkpoints;

/// <summary>
/// The artifact-store implementation of <see cref="IAgentSessionTranscriptCheckpointer"/>: upload the live session
/// transcript's COMPLETE prefix, then stamp the run row with the reference.
///
/// <para>Complete prefix rather than "whatever bytes were there", and that distinction is the difference between a
/// working continuation and a wasted one. The file is a CLI's own JSONL session, appended to while this reads it, so
/// a read that lands mid-append ends in half a line. The harness restores the bytes verbatim for its
/// <c>--resume</c>, and the executor's <c>ResolveRestoredTranscriptAsync</c> fails CLOSED on an unusable transcript
/// — and the continuation's budget is already spent by then, so a torn checkpoint does not degrade to a cold start,
/// it burns the one attempt. Cutting at the last newline yields a shorter conversation, which is exactly the
/// acceptable outcome; keeping the tail yields a corrupt one, which is not.</para>
///
/// <para>Bounded by <see cref="AgentRunExecutor.MaxSessionTranscriptBytes"/> — the SAME cap the end-of-run capture
/// reads, deliberately not a second knob. Over it the checkpoint skips exactly as that capture does.</para>
///
/// <para>STATELESS, and resolved in a DI scope of the caller's own. The executor dispatches this OFF its drain tick,
/// and the tick is using the executor's scoped <c>DbContext</c> every 250 ms for its event flush and spool-offset
/// write — so a checkpointer sharing that context would put two operations on one EF context concurrently. EF refuses
/// that, and it refuses it on whichever statement starts second: as often the TICK's, which is unhandled inside the
/// runner's attach loop and would kill a healthy run for a best-effort recovery aid. Holding no state is what lets the
/// caller give this its own scope per call; the cadence window and the growth watermark live with the caller.</para>
/// </summary>
public sealed class ArtifactSessionTranscriptCheckpointer : IAgentSessionTranscriptCheckpointer
{
    /// <summary>The content type the CLIs' session files are: newline-delimited JSON. Same value the end-of-run offload stores them under, so both writes of one run's transcript dedupe against each other in the content-addressed store.</summary>
    private const string TranscriptContentType = "application/x-ndjson";

    /// <summary>The producer identity carried on the retention declaration for diagnosis (never the reference check — the oracle probes every reference site). The holder is the RUN, because the run row is what names the artifact.</summary>
    internal const string CheckpointHolderKind = "agent-run-session-transcript-checkpoint";

    private readonly IArtifactRetentionWriter _artifacts;
    private readonly IAgentRunService _runs;
    private readonly TimeProvider _clock;
    private readonly ILogger<ArtifactSessionTranscriptCheckpointer> _logger;

    public ArtifactSessionTranscriptCheckpointer(IArtifactRetentionWriter artifacts, IAgentRunService runs, TimeProvider clock, ILogger<ArtifactSessionTranscriptCheckpointer> logger)
    {
        _artifacts = artifacts;
        _runs = runs;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SessionTranscriptCheckpoint?> CheckpointAsync(SessionTranscriptCheckpointRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.TranscriptPath)) return null;

        // The ordinary early-run state — the CLI has not written its session yet, or writes it somewhere this run
        // cannot address. Checked before the open so it costs a probe rather than an exception and a warning; the
        // open below is still authoritative (a file deleted in between simply throws into the catch).
        if (!File.Exists(request.TranscriptPath)) return null;

        try
        {
            // ONE open, shared with the writing CLI (and with a delete, so a rotating harness cannot wedge this).
            // Everything below reads THIS handle — the path is never resolved a second time, which is what closes the
            // check-then-read window the clamp alone leaves open on a live config home.
            using var stream = new FileStream(request.TranscriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (!OpenedTheResolvedFile(stream, request.TranscriptPath))
            {
                _logger.LogWarning("Agent run {RunId}: the opened session transcript is not the path the clamp resolved (a component was swapped underneath); skipping this checkpoint", request.Owner.RunId);
                return null;
            }

            if (!IsDue(request.PreviousBytes, stream.Length)) return null;

            return await TakeAsync(request, stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Agent run {RunId}: a session-transcript checkpoint could not be taken; this run stays recoverable only from its previous checkpoint, or cold-starts if it has none", request.Owner.RunId);

            return null;
        }
    }

    /// <summary>
    /// Whether this attempt should pay for a checkpoint. Two gates, each naming a different waste: bytes that have
    /// not changed carry no new conversation, and a file over the cap cannot be read at all. The CADENCE is not here
    /// — the caller applies it before it even locates the file, so a harness that must SEARCH for its transcript
    /// (Codex globs its rollout directory) does not pay for that search on every poll.
    /// </summary>
    private static bool IsDue(long? previousBytes, long length)
    {
        if (previousBytes is { } previous && length <= previous) return false;

        return length <= AgentRunExecutor.MaxSessionTranscriptBytes();
    }

    /// <summary>
    /// Read the complete prefix, store it and stamp it. Returns null when the file holds no complete line yet, when
    /// the prefix has not grown past the last checkpoint, or when the fenced stamp is lost — and the local state
    /// advances only on a STAMPED checkpoint, so a failed attempt is retried at the next cadence rather than
    /// suppressed by one it never earned.
    /// </summary>
    private async Task<SessionTranscriptCheckpoint?> TakeAsync(SessionTranscriptCheckpointRequest request, FileStream stream, CancellationToken cancellationToken)
    {
        var runId = request.Owner.RunId;
        var buffer = new byte[stream.Length];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        var complete = CompletePrefixLength(buffer);

        if (complete == 0) return null;   // the CLI has written only a partial first line — there is no whole turn to restore yet

        if (request.PreviousBytes is { } previous && complete <= previous) return null;   // the growth was entirely inside the torn tail

        var write = await _artifacts.PutDeclaredAsync(new ArtifactRetentionWriteRequest(request.TeamId, buffer.AsMemory(0, complete), TranscriptContentType, ArtifactRetentionClass.SessionTranscriptCheckpoint, CheckpointHolderKind, runId), cancellationToken).ConfigureAwait(false);
        var checkpoint = new SessionTranscriptCheckpoint(write.ArtifactId, _clock.GetUtcNow(), complete);

        if (!await _runs.StampSessionTranscriptCheckpointAsync(request.Owner, checkpoint, request.SessionId, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Agent run {RunId}: a session-transcript checkpoint was stored but its row was won by another generation, so it was not stamped; the owning worker's own checkpoint stands", runId);

            return null;
        }

        _logger.LogDebug("Agent run {RunId}: checkpointed {Bytes} bytes of session transcript as artifact {ArtifactId}; another host can continue this conversation if this one dies", runId, checkpoint.Bytes, checkpoint.ArtifactId);

        return checkpoint;
    }

    /// <summary>
    /// The length of the COMPLETE prefix: everything up to and including the last newline. Zero when the file holds
    /// no newline at all. The CLIs append whole JSON lines, so a tail after the last newline is a line still being
    /// written — restoring it would hand the next CLI a session it cannot parse.
    /// </summary>
    internal static int CompletePrefixLength(ReadOnlySpan<byte> transcript)
    {
        var last = transcript.LastIndexOf((byte)'\n');

        return last < 0 ? 0 : last + 1;
    }

    /// <summary>
    /// Whether the handle actually opened the file the clamp resolved. The clamp walks the path for symlinks and then
    /// the file is opened — two syscalls, and between them the AGENT is still running with write access to its own
    /// bind-mounted config home, so a swapped component would otherwise let it point this read at a worker-readable
    /// host file and have the bytes uploaded into its team's store and restored into the next attempt.
    ///
    /// <para>Verified through the kernel's own answer for the OPEN handle (<c>/proc/self/fd/&lt;n&gt;</c>), which no
    /// later rename can change. RESIDUAL, stated rather than hidden: a host with no <c>/proc</c> (macOS development)
    /// cannot be asked, and there the check passes. That is the honest bound — the attack needs an agent confined
    /// inside a bind-mounted config home, which is a Linux-only posture (<c>BubblewrapSandbox</c>), so the platform
    /// that can be exploited is the platform that can be verified.
    /// </summary>
    internal static bool OpenedTheResolvedFile(FileStream stream, string resolvedPath)
    {
        var descriptor = $"/proc/self/fd/{DescriptorOf(stream.SafeFileHandle)}";

        if (!OperatingSystem.IsLinux() || !File.Exists(descriptor) && !Directory.Exists(descriptor)) return true;

        return string.Equals(new FileInfo(descriptor).LinkTarget, resolvedPath, StringComparison.Ordinal);
    }

    private static int DescriptorOf(SafeFileHandle handle) => (int)handle.DangerousGetHandle();
}
