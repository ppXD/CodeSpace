using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// Optional DURABLE capability a sandbox runner MAY implement alongside <see cref="ISandboxRunner"/>
/// (Rule 7 / ISP — a sibling interface, never a widening of the base contract). It splits a run into a
/// <see cref="LaunchAsync"/> that starts the command writing its output to a DURABLE spool the launching
/// process does not own, and an <see cref="AttachAsync"/> that OBSERVES that spool — so the run's lifetime
/// is decoupled from any one observer. The point: a backend that restarts mid-run can persist the
/// returned <see cref="SandboxHandle"/>, then later re-discover the run from it and either finish observing
/// it or recover its output, instead of the run dying with the process that launched it.
///
/// <para>Contrast with <see cref="ISandboxStreamRunner"/>, where the runner owns the child's stdout pipe:
/// there the run cannot outlive its observer. Here the launch returns a durable handle and the output
/// lands on a backing store (the local runner: on-disk spool files + an exit marker), so observation is
/// resumable. A caller feature-detects support with <c>runner is ISandboxDurableRunner</c> and falls back
/// to <see cref="ISandboxStreamRunner"/> / <see cref="ISandboxRunner.RunAsync"/> when a runner can't.</para>
///
/// <para><b>Cancellation contract (the durability hinge):</b> cancelling <see cref="AttachAsync"/>'s token
/// (the observer being torn down — e.g. a backend shutdown) STOPS OBSERVING but LEAVES THE PROCESS RUNNING,
/// then throws <see cref="OperationCanceledException"/>. It is NOT a kill. The only thing that terminates
/// the process is the handle's <see cref="SandboxHandle.Deadline"/> (the wall-clock timeout), enforced by
/// whichever observer is attached when it elapses.</para>
/// </summary>
public interface ISandboxDurableRunner
{
    /// <summary>
    /// Launch <paramref name="spec"/> writing stdout/stderr to a durable spool keyed by
    /// <paramref name="spoolKey"/> (the agent-run id), and return a <see cref="SandboxHandle"/> the caller
    /// persists. Returns as soon as the process is started — does NOT wait for exit. The handle's
    /// <see cref="SandboxHandle.Deadline"/> is computed from <see cref="SandboxSpec.TimeoutSeconds"/>.
    /// </summary>
    Task<SandboxHandle> LaunchAsync(SandboxSpec spec, string spoolKey, CancellationToken cancellationToken);

    /// <summary>
    /// Observe a launched run: tail its stdout spool from <see cref="SandboxHandle.StdoutOffset"/> (0 = the
    /// start; a re-attach resumes from the dead observer's checkpoint), invoking <paramref name="onStdoutLine"/>
    /// for each line as it lands, until the process exits (its exit marker appears) or the
    /// <see cref="SandboxHandle.Deadline"/> elapses (the process is terminated → <see cref="SandboxStatus.TimedOut"/>).
    /// Returns the terminal <see cref="SandboxResult"/> (stdout empty — delivered live via the callback;
    /// stderr captured from the spool). Cancelling <paramref name="cancellationToken"/> stops observing and
    /// leaves the process running, throwing <see cref="OperationCanceledException"/> (see the type remarks).
    ///
    /// <para><paramref name="onCheckpoint"/> (optional) is invoked with the advanced byte offset after each
    /// emitted batch (only when it advanced), so the caller can persist it onto the handle and a re-attach
    /// resumes there. It is called AFTER the batch's lines are delivered, so the persisted offset never runs
    /// ahead of the events — a re-attach at worst re-emits the last batch (at-least-once; exactly-once is a
    /// later slice), never loses lines.</para>
    /// </summary>
    Task<SandboxResult> AttachAsync(SandboxHandle handle, Func<string, CancellationToken, Task> onStdoutLine, CancellationToken cancellationToken, Func<long, CancellationToken, Task>? onCheckpoint = null);

    /// <summary>
    /// Snapshot a launched run's liveness from its <paramref name="handle"/> WITHOUT observing it:
    /// <see cref="SandboxRunState.Exited"/> (its exit marker is present, carrying the code),
    /// <see cref="SandboxRunState.Running"/> (the supervised process is still alive, no marker yet), or
    /// <see cref="SandboxRunState.Gone"/> (the process is gone and never recorded a marker — killed). Lets a
    /// reconciler recover a run that finished unobserved, leave one still running, or abandon one truly lost —
    /// instead of blindly abandoning every run whose live observer disappeared.
    /// </summary>
    Task<SandboxProbe> ProbeAsync(SandboxHandle handle, CancellationToken cancellationToken);

    /// <summary>
    /// TERMINATE a launched run from its <paramref name="handle"/>: kill the supervised process tree, guarded by
    /// the recorded pid + start time so a recycled pid (a different process the OS handed our old number) is never
    /// killed. This is the EXPLICIT kill — the opposite of cancelling <see cref="AttachAsync"/>, which only stops
    /// observing and deliberately leaves the process alive. A reconciler issues it when it ABANDONS a stale run
    /// whose process is (or may still be) alive, so the orphaned agent stops holding its workspace and burning the
    /// injected model credential after the run is already marked Failed. Best-effort + idempotent: a run that has
    /// already exited / been reaped is a no-op. Returns once the kill signal has been issued.
    /// </summary>
    Task TerminateAsync(SandboxHandle handle, CancellationToken cancellationToken);
}
