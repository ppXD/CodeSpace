namespace CodeSpace.Messages.Agents;

/// <summary>
/// Why a declared deliverable's bytes were NOT read out of the workspace — the three ways the byte-reading workspace
/// guard refuses, named instead of spelled into a sentence. A reason a caller can only recognise by matching a
/// substring is a reason no caller recognises: the over-cap arm is a run's own bytes going uncaptured, and it stayed
/// indistinguishable from a path the agent never wrote for exactly that reason. The capture branches on the member —
/// only the over-cap arm becomes a known-missing span, and each arm's warning names its own cause.
///
/// <para>It sits in Messages because it is a noun with no behaviour, which is where this repository puts those — not
/// because anything outside Core names it today. Nothing does: the guard that produces it and the store that branches
/// on it are both Core-internal. Sitting here is what lets a future response DTO carry the reason to an operator
/// without the enum having to move, which is the change this arc's typed download reason already made once.</para>
/// </summary>
public enum WorkspaceArtifactReadFailure
{
    /// <summary>Nothing readable at that path. A blank path, a <c>../</c> escape, an absolute path, the workspace root itself and a symlink whose final target leaves the clone all land here (fail-closed).</summary>
    Missing,

    /// <summary>The path resolves to a directory, which is not readable content.</summary>
    NotAFile,

    /// <summary>The file is larger than the caller's cap, so NONE of its bytes were taken — a captured artifact's bytes ARE the deliverable, and a silently-clipped one is a lie where absence is honest.</summary>
    OverCap,
}
