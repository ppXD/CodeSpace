using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Recovery;

/// <summary>
/// Makes a RUNNING agent's resumable session transcript durable, so the conversation survives the loss of the host
/// running it. The seam the executor's observer ticks call; the abandon path on any other worker then continues from
/// what it stamped.
///
/// <para>This exists because the ordinary capture is END-of-run only:
/// <c>AgentRunExecutor.CaptureSessionTranscriptAsync</c> reads the file out of
/// <c>LocalProcessRunner.ConfigHomePath(handle.SpoolDirectory)</c> once the process has exited. That spool is on the
/// LAUNCHING host, so a worker that never launched the run cannot read it, and a host that dies takes it with it —
/// which is exactly why "a run whose launch host dies is continued by another host" was false. Everything else a
/// resume needs is already durable (<c>agent_run.task_jsonb</c>, the handle's <c>WorkspaceBaseSha</c>, a
/// <c>PublishManifest</c> row when the attempt pushed); the conversation was the hole.</para>
///
/// <para>Best-effort by contract, and bounded by the SAME cap the end-of-run capture uses. It is dispatched from an
/// observer tick whose real job is flushing the run's events and advancing its spool offset, so nothing here may ever
/// be able to stop, slow or fail that tick: a checkpoint that cannot be taken is a run that retries cold, which is the
/// behaviour that existed before this seam.</para>
///
/// <para>The marker is ON THE INTERFACE because DI here is marker SCANNING — <c>CodeSpaceModule.RegisterDependency</c>
/// walks the Core assembly for concrete classes assignable to <see cref="IDependency"/> and registers each
/// <c>AsImplementedInterfaces</c>. An implementation therefore needs no registration of its own, and cannot be
/// deployed unregistered by accident. Same shape as <c>IArtifactRetentionWriter</c>.</para>
/// </summary>
public interface IAgentSessionTranscriptCheckpointer : IScopedDependency
{
    /// <summary>
    /// Take one checkpoint of <paramref name="request"/>'s transcript, and return it when one was actually taken —
    /// null when this attempt declined, which is an ordinary outcome.
    ///
    /// <para>It declines when the file is absent, is not the file the caller's clamp resolved, holds no complete JSON
    /// line yet, has NOT grown past <see cref="SessionTranscriptCheckpointRequest.PreviousBytes"/>, is over the
    /// transcript byte cap, or loses the fenced row write.</para>
    ///
    /// <para>STATELESS, and that is load-bearing rather than tidy: the caller runs this OFF its drain tick in a DI
    /// scope of its own (an agent's stdout and this upload would otherwise use one scoped <c>DbContext</c>
    /// concurrently, which EF refuses — on the tick's statement as often as on this one, killing a healthy run for a
    /// best-effort aid). A per-call instance can hold no cadence or growth watermark, so the caller owns both and
    /// passes what this needs.</para>
    /// </summary>
    Task<SessionTranscriptCheckpoint?> CheckpointAsync(SessionTranscriptCheckpointRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One checkpoint attempt's coordinates, as a record rather than a parameter list (the five-parameter cap), and
/// beside its interface exactly as <c>ArtifactRetentionWriteRequest</c> is: this is a call envelope between two Core
/// services, never a persisted or wire-facing DTO — the persisted fact is <see cref="SessionTranscriptCheckpoint"/>,
/// which lives in Messages.
/// </summary>
/// <param name="TeamId">The run's team — the scope the artifact is stored under and every read is bound to.</param>
/// <param name="Owner">The calling worker's observation token. The stamp is fenced to it (the brief said run id + epoch; the token is that plus the owner id, which is the fencing every other <c>agent_run</c> write on this path already uses), so a worker whose ownership was reclaimed cannot point a live run's recovery at its own stale conversation.</param>
/// <param name="TranscriptPath">The absolute path of the live session transcript, already clamped within the run's config home by the caller (<c>AgentRunExecutor.ResolveSessionTranscriptPath</c> — the agent can write there, so the path is untrusted until that walk has run).</param>
/// <param name="SessionId">The harness-native session id naming that transcript. Stamped with the checkpoint because a checkpoint nothing can ADDRESS is not resumable — the retry hands this to the CLI as its <c>--resume</c> id.</param>
/// <param name="PreviousBytes">The complete-prefix length of the last checkpoint taken of THIS path, or null when there is none. The growth watermark: bytes that have not changed carry no new conversation, so re-storing them buys nothing. The caller keys it on the path (a revise round opens a new config home whose transcript legitimately starts smaller) and passes null when the path moved.</param>
public sealed record SessionTranscriptCheckpointRequest(Guid TeamId, AgentRunOwnerToken Owner, string TranscriptPath, string? SessionId, long? PreviousBytes);
