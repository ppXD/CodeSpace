namespace CodeSpace.Messages.Constants;

/// <summary>
/// The fixed output-key names the engine's container reducers EMIT into a node's output bag — the
/// keys an author MUST NOT collide with via a configurable name (a map <c>resultKey</c>, a loop
/// variable name). A collision is silent-wrong-data: the reducer's indexer write either overwrites
/// the author's value (<c>flow.map</c>: the result array lands under <c>resultKey</c> FIRST, then
/// <c>count</c>/<c>failed</c> are written) or clobbers it (<c>flow.loop</c>: <c>iterations</c> etc.
/// are written AFTER the loop-var spread).
///
/// <para>Rule 8 contract-pin: BOTH the engine's reducers (<c>BuildMapOutputs</c> / <c>BuildLoopOutputs</c>)
/// AND <c>DefinitionValidator</c> reference these so a future key addition can't drift out of the
/// validator's reserved set. A drift-pin test asserts the validator's reserved set equals these
/// literals, making any rename/addition a compile- or test-visible decision.</para>
/// </summary>
public static class WorkflowOutputKeys
{
    /// <summary>Element-branch count of a completed <c>flow.map</c> (always emitted alongside the keyed result array).</summary>
    public const string MapCount = "count";

    /// <summary>Failed-branch count of a completed <c>flow.map</c> (0 under terminate; the continue-mode failure tally).</summary>
    public const string MapFailed = "failed";

    /// <summary>The keys <c>BuildMapOutputs</c> always writes besides the configurable <c>resultKey</c>. A map <c>resultKey</c> equal to one is rejected at save time.</summary>
    public static readonly IReadOnlyList<string> Map = new[] { MapCount, MapFailed };

    /// <summary>Iteration count a completed <c>flow.loop</c> emits (written after the loop-var spread).</summary>
    public const string LoopIterations = "iterations";

    /// <summary>Failed-iteration count a completed <c>flow.loop</c> emits (continue-mode tally).</summary>
    public const string LoopFailedIterations = "failedIterations";

    /// <summary>Why a <c>flow.loop</c> stopped (e.g. <c>maxIterations</c>); written after the loop-var spread.</summary>
    public const string LoopTerminationReason = "terminationReason";

    /// <summary>The current iteration index injected into the per-pass <c>loop.*</c> scope (clobbers a same-named loop var).</summary>
    public const string LoopIndex = "index";

    /// <summary>
    /// The names a <c>flow.loop</c> variable MUST NOT use: the three output keys <c>BuildLoopOutputs</c>
    /// writes after the loop-var spread, plus the iteration-scope <c>index</c> the engine injects into
    /// <c>loop.*</c> (<c>BuildLoopScope</c>). A loop var with any of these is silently clobbered at run time.
    /// </summary>
    public static readonly IReadOnlyList<string> Loop = new[] { LoopIterations, LoopFailedIterations, LoopTerminationReason, LoopIndex };
}
