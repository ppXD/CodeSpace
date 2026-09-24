namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// What a retried agent is told about the WORLD STATE its restored conversation refers to — the one place both retry
/// lanes read, so "your prior work is not here" can never mean two different things to a model. The quick lane's
/// <c>agent.run</c> respawn and the supervisor's <c>retry</c> resolve their world-state pin from different sources (a
/// resume payload vs. a <c>PublishManifest</c> row), but the SENTENCE they append when nothing was preserved must be
/// identical — a second wording would be a second contract (Rule 7, the same "recognise it in ONE place" discipline
/// as <see cref="AgentModelEscalationTrigger"/>).
/// </summary>
public static class AgentRetryContinuity
{
    /// <summary>The honest-redo line: fires ONLY when a resumed conversation exists but the workspace was NOT pinned to a prior pushed branch — never on a genuine cold-start retry (no prior attempt at all), which stays byte-identical.</summary>
    public const string HonestNoContinuityHint = "Note: your prior attempt's conversation is restored, but its git changes were NOT preserved in this workspace (your prior attempt pushed no branch of its own) — you must redo any relevant file changes from scratch.";

    /// <summary>Append <see cref="HonestNoContinuityHint"/> to a resumed task's goal. One composition, so the two lanes cannot drift on the separator either.</summary>
    public static string WithHonestNoContinuityHint(string goal) => $"{goal}\n\n{HonestNoContinuityHint}";

    /// <summary>
    /// 3c: what a CROSS-HOST continuation is told, and the reason it needs its own sentence. The two lanes above
    /// retry an attempt that FINISHED on a live host, so their only open question is whether a branch was pushed.
    /// This lane continues an attempt whose machine or process was lost mid-run: the conversation comes from a
    /// checkpoint taken some time before the loss, and the working tree is simply gone. Both halves have to be said,
    /// because the restored transcript will describe edits — possibly edits made after the checkpoint — that the new
    /// workspace does not contain, and an agent that is not told will read its own transcript as evidence about files
    /// it cannot see.
    /// </summary>
    public const string LostHostPreamble = "Note: the machine or the process running your previous attempt was lost mid-run. Your conversation is restored from a checkpoint taken before that, so it may describe work you did after the checkpoint, and it may be missing your last few turns.";

    /// <summary>Said when the lost attempt HAD published a branch: the workspace is checked out at it, so the published work is present and only the unpublished remainder is gone. Takes the branch name so the agent can verify rather than take the claim on trust.</summary>
    public static string LostHostPublishedBranchHint(string branch) =>
        $"Your previous attempt published branch `{branch}`, and this workspace is checked out AT that branch — that work is here. Anything you had NOT published to it was lost with that attempt, so check the files before continuing and redo whatever is missing.";

    /// <summary>
    /// Append the cross-host continuation's honesty block to a resumed task's goal: the preamble always, then what
    /// is true about the TREE.
    ///
    /// <para>Three cases, and the third is why this takes a flag rather than inferring from the branch alone. A
    /// published branch means the pushed work IS here and only the unpublished remainder died. No branch on a
    /// repository-backed run means nothing was preserved, which is exactly <see cref="HonestNoContinuityHint"/> —
    /// the sentence the other two lanes already say, reused verbatim so "your prior work is not here" can never mean
    /// two different things. And a run with NO repository (<paramref name="treeOwed"/> false) had no working tree to
    /// lose, so it gets the preamble alone: telling an analysis-only agent to redo file changes would be a claim
    /// about a git fact its run never had.</para>
    /// </summary>
    public static string WithLostHostHint(string goal, string? publishedBranch, bool treeOwed) =>
        $"{goal}\n\n{LostHostPreamble}{TreeStateSentence(publishedBranch, treeOwed)}";

    /// <summary>
    /// 3c: said when the lost host's checkpoint could not be READ — reaped, or its storage unreachable. The attempt
    /// still runs (failing it would spend the retry this whole path exists to improve), but it runs COLD, and an
    /// agent that was going to be handed a conversation must be told it is not getting one. Appended to the lost-host
    /// block rather than replacing it: the machine or the process really was lost, which is still the reason.
    /// </summary>
    public const string LostHostCheckpointUnreadableHint = "Your previous conversation could not be recovered either — the checkpoint it was stored in is no longer readable — so you are starting this task from the beginning.";

    /// <summary>Replace a resumed goal's promise of a restored conversation with the truth that there is none. Used when the checkpoint ref resolves to nothing at launch, which is the only moment that fact is knowable.</summary>
    public static string WithUnreadableCheckpointHint(string goal) => $"{goal}\n\n{LostHostCheckpointUnreadableHint}";

    /// <summary>
    /// Said when a restored conversation is too large for the launch pipe to hand back, so the attempt runs as a
    /// fresh one instead of being refused. Appended after whatever the goal already says, for the same reason as
    /// <see cref="LostHostCheckpointUnreadableHint"/>: the earlier sentence promised a conversation, and the agent must
    /// be told it is not getting one. It says nothing about the tree beyond what holds on every path: a respawn may be
    /// checked out at the branch the earlier attempt pushed, with no other sentence saying so, and "start from the
    /// beginning" there would have the agent redo or overwrite its own half-finished work.
    /// </summary>
    public const string OversizedTranscriptHint = "Your previous conversation is too large to hand back to you, so it was not restored — you are continuing without it. Look at the workspace before you change anything: whatever it already holds beyond the base is your own earlier work on this task.";

    /// <summary>Replace a resumed goal's promise of a restored conversation with the truth that there is none, because the conversation was too large to restore.</summary>
    public static string WithOversizedTranscriptHint(string goal) => $"{goal}\n\n{OversizedTranscriptHint}";

    private static string TreeStateSentence(string? publishedBranch, bool treeOwed) =>
        !treeOwed ? ""
            : string.IsNullOrWhiteSpace(publishedBranch) ? $" {HonestNoContinuityHint}"
                : $" {LostHostPublishedBranchHint(publishedBranch)}";
}
