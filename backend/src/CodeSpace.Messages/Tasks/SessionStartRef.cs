namespace CodeSpace.Messages.Tasks;

/// <summary>
/// One repo's session-continuity start point (P4 "Session 不盲目繼承 branch"): the prior turn's produced BRANCH
/// (the clone ref, transient — a merged PR auto-deletes it) plus the CONFIRMED commit it pointed at when recorded
/// (<c>PublishManifest.CommitSha</c>, the readback-verified push tip) — the immutable recovery anchor the clone
/// detaches onto when the branch has vanished, so a continuing turn builds on the prior work instead of silently
/// rebasing onto the default branch. Null sha = a legacy turn recorded no confirmed tip (recovery unavailable;
/// the soft fallback degrades to the default branch as before).
/// </summary>
public sealed record SessionStartRef
{
    public required string Branch { get; init; }
    public string? CommitSha { get; init; }
}
