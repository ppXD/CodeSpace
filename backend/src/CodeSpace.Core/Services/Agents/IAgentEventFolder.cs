using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// ONE run's result accumulator, created by and owned by the harness whose stream it folds
/// (<see cref="IAgentHarness.CreateFolder"/>). The executor drives it blind — <see cref="Add"/> per normalized
/// event in stream order, then <see cref="BuildResult"/> once at the terminal — and never sees the shape of the
/// reduction behind it.
///
/// <para><b>Why the harness owns it.</b> The seam used to hand EVERY harness the same concrete accumulator, so a
/// reduction only one harness needed could arrive only as a field on a type every harness shares — and that had
/// already happened: a last-text-of-any-kind field neither production harness read, carried purely so test doubles
/// could keep <c>events[^1].Text</c>. Rule 7 / ISP: widening a shared type is cheap and narrowing it breaks every
/// implementer, so the reduction belongs behind this interface, where a new harness's needs land inside its own
/// folder and nothing else moves.</para>
///
/// <para><b>Retention is the implementer's obligation.</b> Whatever a folder keeps must be O(1) in the event count.
/// The executor deliberately no longer holds the run's events — a multi-gigabyte stdout exhausted the heap and
/// failed a run that had actually succeeded — and a folder that accumulates per event puts that bug straight back.
/// The full ordered log is already durable in <c>agent_run_event</c>. <see cref="AgentResultFold"/> is the shared,
/// differentially-tested reduction a folder can compose instead of writing its own.</para>
///
/// <para>Single-threaded by contract: one folder belongs to one run's line-by-line accumulation.</para>
/// </summary>
public interface IAgentEventFolder
{
    /// <summary>Fold ONE normalized event in, in stream order. Retention must stay O(1) in the event count.</summary>
    void Add(AgentEvent normalized);

    /// <summary>
    /// Reduce everything folded so far, plus the harness-INDEPENDENT run facts and the process exit code, into the
    /// normalized <see cref="AgentRunResult"/>. Called once, at the terminal.
    ///
    /// <para>Usage / session id / model arrive as a PARAMETER rather than being folded here because the executor is
    /// their only driver: it reads the same three off its own <see cref="AgentRunFacts"/> on the forced-terminal
    /// branches, where no folder runs at all. Making them a folder's own reduction would let a harness silently drop
    /// them from every timed-out run — and would reduce each event twice on the streaming hot path, once per
    /// accumulator. They belong to every implementation identically, which is what makes the parameter Rule-7 sound.</para>
    /// </summary>
    AgentRunResult BuildResult(AgentRunFacts facts, int exitCode);
}
