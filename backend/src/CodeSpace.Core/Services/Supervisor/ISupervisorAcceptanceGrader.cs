using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The supervisor's OBJECTIVE acceptance gate (L4 arc A): given a repository, a branch the run produced, and a
/// model-authored acceptance command (an argv), reach an agent-INDEPENDENT pass/fail verdict by re-cloning the
/// repo at that branch and running the command in a sandbox — exit 0 = accepted. Replaces a self-reported
/// "it passed" marker with a server-run check. A narrow contract (Rule 7) so the turn loop (A3) folds the verdict
/// once and can mock the grade in tests.
///
/// <para>Fail-closed by construction: anything that prevents reaching a verdict — a repo/branch that cannot be
/// cloned, OR a check command that cannot be RUN (e.g. a binary not on PATH) — returns a FAILED grade (not an
/// exception), because acceptance that cannot be verified is "not accepted", never a silent pass and never a crash
/// that strands the supervisor turn. Only a genuine cancellation propagates.</para>
/// </summary>
public interface ISupervisorAcceptanceGrader
{
    /// <summary>
    /// Clone <paramref name="repositoryId"/> at <paramref name="branch"/> (team-scoped) and grade it with the oracle
    /// named by <paramref name="kind"/> against <paramref name="command"/> — for <c>TestsPass</c> (the default) run the
    /// argv (no shell, cwd = the clone root, capped at <paramref name="timeoutSeconds"/>); for <c>ArtifactPresent</c>
    /// check the declared deliverable paths exist — then remove the clone. A repo/branch that can't be cloned, or a check
    /// that can't be run, yields a failed grade with a legible detail (fail-closed); only a genuine cancellation propagates.
    /// The default keeps every existing caller (the operator floor + resolve path) on the <c>TestsPass</c> oracle.
    /// </summary>
    Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, IReadOnlyList<string> command, int timeoutSeconds, CancellationToken cancellationToken, BenchmarkGradingKind kind = BenchmarkGradingKind.TestsPass);
}
