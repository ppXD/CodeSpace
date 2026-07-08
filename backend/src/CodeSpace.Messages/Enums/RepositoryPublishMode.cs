namespace CodeSpace.Messages.Enums;

/// <summary>
/// The repo-level override <c>RepositoryPolicyPublishGuard</c> reads: <see cref="Branch"/> (default) pushes a
/// non-empty diff as a branch; <see cref="PatchOnly"/> is the escape hatch for a repository that must never
/// receive an agent-pushed branch (a protected/compliance-sensitive repo) — the diff is still captured and
/// offloaded (I1 holds regardless), just never pushed.
/// </summary>
public enum RepositoryPublishMode
{
    Branch,
    PatchOnly
}
