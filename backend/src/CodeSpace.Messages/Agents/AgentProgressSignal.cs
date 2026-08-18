namespace CodeSpace.Messages.Agents;

/// <summary>
/// The vocabulary of things that count as EVIDENCE A RUN IS STILL WORKING — the signals that renew an agent run's
/// progress lease and so hold off the no-progress watchdog (<see cref="SandboxStatus.Stalled"/>).
///
/// <para>Before this enum existed there was exactly ONE signal: byte growth of the run's stdout/stderr spool. That
/// made "silent" synonymous with "stalled", which is false — a run parked on an AUTHORISED human decision is making
/// progress while emitting nothing. The single worst case was structural: the default no-progress window and the MCP
/// tool-approval bound are both 600s, so a run legitimately waiting for a human reached the watchdog at the very moment
/// its approval could still arrive. Naming the signals separates "no output" from "no progress" so each can be answered
/// on its own evidence.</para>
///
/// <para><b>Every member here must be evidence of WORK, never of mere EXISTENCE.</b> That is the line that keeps this
/// a watchdog: a genuinely wedged run — deadlocked, blocked forever on a prompt it cannot answer, waiting on a pipe
/// nobody will write — produces NONE of these and still dies. Notably absent, deliberately: "the supervised process is
/// alive". A live pid is the definition of mere existence; admitting it would renew the lease for every wedged run and
/// neuter the watchdog entirely.</para>
///
/// <para><b>And a renewal is only ever honoured while the run has an execution wall deadline</b>
/// (<see cref="SandboxSpec.TimeoutSeconds"/>). That deadline is what makes granting a renewal safe — something stops
/// the run regardless of what this enum says. In the supported no-wall-clock configuration (<c>TimeoutSeconds</c> null
/// or ≤0) this watchdog is the run's ONLY bound, so the observer refuses the lease entirely and keeps its pre-lease
/// form (spool bytes only). Honouring a renewal there would make a wedged run unkillable: nothing else terminates it,
/// and the reconciler cannot collect a run whose observer is still heartbeating. The rule is enforced in
/// <c>LocalProcessRunner.ProgressWatch</c>, not merely documented here.</para>
///
/// <para>DEFERRED — named, deliberately NOT built, each because it would renew UNBOUNDEDLY on evidence weaker than it
/// looks:</para>
/// <list type="bullet">
/// <item><c>SupervisedCpu</c> (the supervised tree's consumed-CPU delta). The honest version needs a DUTY-CYCLE floor,
/// not a strict inequality: <c>ps -o time=</c> is whole-second resolution, so an advance of 1s over a five-minute
/// sampling gap — a 0.3% duty cycle — would renew, which is far closer to "the pid exists" than to "work is happening",
/// and a parked Node harness accrues timer/GC CPU. It also cost a <c>ps</c> spawn inside the loop that IS the liveness
/// backstop. Both must be answered together (a rate floor, plus a probe with its own budget) before it earns a member.</item>
/// <item><c>WorkspaceMutation</c> (any entry under the run's workspace written since the lease last advanced). Same
/// unbounded shape: a run that churns a log or a lockfile while making no progress renews forever. It needs the same
/// treatment — evidence of a CHANGING tree, not of a touched mtime.</item>
/// <item><c>ModelStream</c> (a dedicated signal for OUR llm plane's own requests). The agent runs today are CLI
/// harnesses whose model traffic is the CLI's, not ours, so there is nothing host-side to heartbeat; the recording llm
/// decorator is the future carrier.</item>
/// </list>
/// </summary>
public enum AgentProgressSignal
{
    /// <summary>
    /// Bytes appeared on EITHER spool file (stdout or stderr) since the last observation. Evidence: the run emitted
    /// output, so it reached at least one more instruction than the last time we looked. Byte growth rather than
    /// completed-line delivery, so a newline-less progress bar or a single slow long line still counts. This is the
    /// ORIGINAL and only pre-lease signal, and the only one honoured when a run has no execution wall deadline.
    /// </summary>
    SpoolOutput,

    /// <summary>
    /// A request FROM inside the sandbox is in flight against this run's own platform endpoint — an MCP tool call
    /// executing, or one parked on an authorised human approval / decision. Evidence: the agent asked the platform to
    /// do something and is waiting on US. It is blocked on a party that is answering, which is the opposite of wedged;
    /// killing it would be the platform terminating a run for the platform's own latency. This is the signal that
    /// removes the 600s-versus-600s collision: a parked approval RENEWS the lease for as long as it is genuinely
    /// parked, instead of racing it. It is EXTERNALLY OBSERVABLE — the platform, not the observer, is the witness, so a
    /// wedged run cannot manufacture it. It cannot renew forever: the hold ends with the request, and above everything
    /// the run's execution wall deadline is checked BEFORE the lease is consulted — and where there is no such
    /// deadline this signal is refused outright (see the type remarks).
    /// </summary>
    PlatformRequest,
}
