namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// Assembles the HONEST grounding string a planner folds into its prompt — a repository's top-level layout,
/// read live and TEAM-SCOPED. A focused helper in the Planning concern (Rule 7 — narrow, not a widened
/// interface); the planning service calls it before invoking the planner.
///
/// <para>Fail-closed by contract: a null <c>repositoryId</c>, a repo outside the caller's team, or any provider
/// failure ALL return <c>null</c> (the planner then runs task-text-only) — never a throw, never a cross-team
/// read. The string is framed as a top-level listing only; it never claims the code was analyzed.</para>
/// </summary>
public interface IRepoGroundingProvider
{
    Task<string?> BuildGroundingAsync(Guid? repositoryId, Guid teamId, string? reference, CancellationToken cancellationToken);
}
