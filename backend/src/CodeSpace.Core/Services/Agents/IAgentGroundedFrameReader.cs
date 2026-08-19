using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Optional sibling capability of <see cref="IAgentHarness"/> (Rule 7 / ISP — never a widening of it): read the facts
/// this harness STATED in one of its own structured frames, as opposed to normalizing that frame onto the shared
/// vocabulary, which is what <see cref="IAgentHarness.ParseEvents"/> does.
///
/// <para><b>Why this cannot be ParseEvents.</b> A normalized <see cref="AgentEvent"/> is a mapping — a native kind onto
/// a shared kind, a payload onto a display line — so a projection built from one may claim only
/// <see cref="SemanticProjectionQuality.Derived"/>. The reduction's named once-only facts are taken ONLY from an
/// exactly grounded projection, so with no reader on this seam they are null on every real run: the counts, the channel
/// set and the prefix digest survive a re-attach and nothing that NAMES anything does. This interface is the narrow
/// seam where a harness may state "this frame IS my session record", which is the only honest source for that claim.</para>
///
/// <para><b>The rule an implementation may not break.</b> A fact inferred from unstructured output is
/// <see cref="SemanticProjectionQuality.Heuristic"/> and must never be reported here — a projector that launders a
/// guess into an exact claim is strictly worse than one that recovers nothing, because the aggregate counts can never
/// afterwards say WHICH field was inferred. A harness that emits no exactly groundable frame simply does not implement
/// this interface, and the capture path is then byte-identical to one without it.</para>
///
/// <para>Pure, total and stateless like <see cref="IAgentHarness.ParseEvents"/>: one frame in, at most one fact out,
/// null for everything else. A throw is CONTAINED by the caller rather than propagating — unlike a parser's, which
/// keeps failing the run exactly as it did before the capture plane existed, because reading a grounded fact is work
/// that did not exist before and must not become the thing that decides a run.</para>
/// </summary>
public interface IAgentGroundedFrameReader
{
    /// <summary>The session this frame STATES, or null when it states none — which is the answer for almost every frame, and the only honest answer for a frame that merely mentions an id.</summary>
    GroundedSessionFrame? ReadSessionFrame(string nativeFrame);
}
