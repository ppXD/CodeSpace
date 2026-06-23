namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Resolves the git ref a CONTINUING run should clone the primary repo at — session branch continuity: a follow-up
/// turn starts from the prior turn's PRODUCED branch, so the agent builds on earlier code instead of a fresh checkout
/// from the default branch. The code-state companion to <see cref="ISessionContextBuilder"/> (which carries the
/// textual narrative); together they make "build on prior work" literal in both the prompt and the working tree.
/// </summary>
public interface ISessionBranchResolver
{
    /// <summary>
    /// The ref to clone <paramref name="primaryRepositoryId"/> at: the most-recent prior top-level turn (in this
    /// session, that targeted this repo) which produced a branch — that branch is the latest code state to continue
    /// from (a later analysis-only turn produces none, so the code state is still the last code turn's branch).
    /// Returns <c>null</c> when no prior turn produced a branch for this repo (⇒ the repo's default branch — the safe
    /// fallback). Team-scoped (defence in depth).
    /// </summary>
    Task<string?> ResolveStartRefAsync(Guid sessionId, Guid teamId, Guid primaryRepositoryId, CancellationToken cancellationToken);
}
