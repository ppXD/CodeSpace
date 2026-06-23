namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Resolves the git ref each repo a CONTINUING run touches should clone at — session branch continuity: a follow-up
/// turn starts each repo from the prior turn's PRODUCED branch for THAT repo, so the agent builds on earlier code
/// instead of a fresh checkout from the default branch. The code-state companion to <see cref="ISessionContextBuilder"/>
/// (which carries the textual narrative); together they make "build on prior work" literal in both the prompt and the
/// working tree.
/// </summary>
public interface ISessionBranchResolver
{
    /// <summary>
    /// The ref to clone each of <paramref name="repositoryIds"/> at: per repo, the most-recent prior top-level turn (in
    /// this session) that PRODUCED a branch for it — its latest code state to continue from. A single-repo turn surfaces
    /// its one repo's branch (<c>OutputsJson.branch</c>); a multi-repo turn surfaces every writable repo's branch
    /// (<c>OutputsJson.repositoryResults[].producedBranch</c>, keyed by repository id). A repo with no prior produced
    /// branch is ABSENT from the result map (⇒ the caller clones it at its default branch — the safe fallback). Newest
    /// turn wins per repo. Team-scoped (defence in depth).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveStartRefsAsync(Guid sessionId, Guid teamId, IReadOnlyCollection<Guid> repositoryIds, CancellationToken cancellationToken);
}
