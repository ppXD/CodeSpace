using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Optional sibling capability of <see cref="IAgentHarness"/> (Rule 7 / ISP — never a widening of it, and never a
/// widening of <see cref="IAgentGroundedFrameReader"/> either): read the ONE MODEL CALL this frame is the harness's own
/// record of.
///
/// <para><b>The hole it closes.</b> A workflow LLM node's calls reach <c>workflow_run_model_call</c> because they pass
/// through <c>ILLMClient</c> and a recording decorator. A harness's own calls never touch it — the CLI talks to the
/// provider itself — so for the work agents actually do, the platform keeps one derived per-run token aggregate and, for
/// a harness that reports no usage, not even a cost. This seam is the only honest source for a per-call row, because the
/// only evidence that a particular call happened is a frame the harness printed about it.</para>
///
/// <para><b>The rule an implementation may not break</b>, which is <see cref="IAgentGroundedFrameReader"/>'s rule with
/// more at stake: a fact inferred from unstructured output is <see cref="SemanticProjectionQuality.Heuristic"/> and must
/// never be reported here. A frame that merely NAMES a model states nothing about any call — not a session line
/// announcing the configured model, not an assistant sentence quoting one. Nor may a cumulative per-turn total be
/// reported as a call: it is the sum of calls the harness never enumerated, so a row built from it would claim one call
/// burned a whole turn's tokens. A harness with no per-call record does not implement this interface, this plane records
/// nothing for it, and the capture path is byte-identical to one without it.</para>
///
/// <para><b>Why nothing is better than something here.</b> A missing attempt row is a visible gap. A fabricated one is
/// a cost figure that looks measured, and a cost report that is quietly wrong is the one that gets trusted.</para>
///
/// <para>Pure, total and stateless: one frame in, at most one call out, null for everything else — which is the answer
/// for almost every frame. A throw is CONTAINED by the caller rather than propagating, for the same reason the grounded
/// session reader's is: reading a model call is work that did not exist before this plane, so letting it throw would
/// make a run's outcome depend on whether the plane is deployed.</para>
/// </summary>
public interface IAgentModelCallFrameReader
{
    /// <summary>The model call this frame IS the harness's record of, or null when it records none — which includes every frame that only mentions a model, and every frame whose usage the harness did not state.</summary>
    GroundedModelCallFrame? ReadModelCallFrame(string nativeFrame);
}
