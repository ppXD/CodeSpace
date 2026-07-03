using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The OBJECTIVE acceptance gate (L4 arc A → triad S7): given a repository, a branch the run produced, and an
/// acceptance SPEC, reach an agent-INDEPENDENT pass/fail verdict by re-cloning the repo at that branch and running
/// the spec's oracle against the clone — the spec's <c>Kind</c> routes the grader (tests-pass argv, deliverable
/// paths, rubric judge, citations, schema) and its kind-specific payload (rubric / schema) rides the spec itself.
/// Replaces a self-reported "it passed" marker with a server-run check. A narrow contract (Rule 7) so the turn loop
/// and the executor fold the verdict once and tests can mock the grade.
///
/// <para>Fail-closed by construction: anything that prevents reaching a verdict — a repo/branch that cannot be
/// cloned, OR a check that cannot be RUN (a binary not on PATH, a judge with no model) — returns a FAILED grade (not
/// an exception), because acceptance that cannot be verified is "not accepted", never a silent pass and never a
/// crash that strands the caller. Only a genuine cancellation propagates.</para>
/// </summary>
public interface ISupervisorAcceptanceGrader
{
    /// <summary>
    /// Clone <paramref name="repositoryId"/> at <paramref name="branch"/> (team-scoped) and grade it with the oracle
    /// the spec names (<c>Kind</c> null ⇒ <c>TestsPass</c>) against the spec's command + kind-specific payload, capped
    /// at <paramref name="timeoutSeconds"/>, then remove the clone. A repo/branch that can't be cloned, or a check that
    /// can't be run, yields a failed grade with a legible detail (fail-closed); only a genuine cancellation propagates.
    /// </summary>
    Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken);
}
