namespace CodeSpace.Messages.Agents;

/// <summary>
/// A run's resumable CLI session transcript, made durable WHILE the run is still going — the one fact a host that
/// dies mid-run leaves behind that another host can continue from.
///
/// <para>Before this existed the transcript was captured only at run END, from the launching host's own spool
/// (<c>AgentRunExecutor.CaptureSessionTranscriptAsync</c>), so a run whose host vanished left nothing resumable
/// anywhere and the only recovery was a cold restart. The artifact this names is uploaded from the live config home
/// on the observer's own checkpoint ticks, and the run row keeps the reference — which is what makes the artifact
/// reachable from a DIFFERENT worker after the launching one is gone.</para>
/// </summary>
/// <param name="ArtifactId">The stored transcript's <c>workflow_artifact</c> id, referenced by <c>agent_run.session_transcript_checkpoint_artifact_id</c> so the artifact reaper's oracle can see it.</param>
/// <param name="At">When this checkpoint was taken — the clock the next checkpoint's cadence is measured from, and the honesty stamp a resumed attempt cites.</param>
/// <param name="Bytes">The transcript's size at capture, in bytes. The growth signal for the next tick, and the number an operator reads to tell a checkpoint that captured a conversation from one that captured an empty file.</param>
public sealed record SessionTranscriptCheckpoint(Guid ArtifactId, DateTimeOffset At, long Bytes);
