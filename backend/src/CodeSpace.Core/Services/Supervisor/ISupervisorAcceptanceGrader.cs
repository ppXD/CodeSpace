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

    /// <summary>P3a-3 (B+V0+): grade with ORACLE RESTORE — when the spec names <c>ProtectedPaths</c> and the attempt's base sha is known, the grader restores those paths from the base before running, voiding any candidate tamper of its own judge (recorded in the evidence). Default forwards to the plain overload (fakes and non-git graders are unaffected).</summary>
    Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, string? oracleBaseSha, CancellationToken cancellationToken) =>
        GradeAsync(repositoryId, teamId, branch, spec, timeoutSeconds, cancellationToken);

    /// <summary>
    /// S2 — the BRANCH-LESS twin of <see cref="GradeAsync"/>: clone <paramref name="repositoryId"/> at
    /// <paramref name="baseSha"/> (team-scoped, agent-independent — the SAME clone-fresh guarantee, just anchored on
    /// a commit instead of a pushed ref), apply the unit's own recorded patch (<paramref name="inlinePatch"/> or,
    /// when offloaded, <paramref name="patchArtifactId"/> resolved team-scoped) with NO commit and NO push (this
    /// grade is read-only by construction), then grade the resulting working tree with the same oracle
    /// <see cref="GradeAsync"/> uses. For a unit whose producer never pushed a branch (patch-only publish policy, or
    /// a repository-policy guard) — the exact gap a branch-only grader cannot close. A patch that resolves to nothing
    /// (missing/cross-team artifact) or that fails to apply onto its own recorded base is a failed grade with a
    /// legible detail (fail-closed), mirroring <see cref="GradeAsync"/>'s contract exactly.
    /// </summary>
    Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken);

    /// <summary>
    /// S3 — grade the BASE tree itself: clone <paramref name="repositoryId"/> at <paramref name="baseSha"/>
    /// (team-scoped, detached — the S1 immutable base every participant materialized) and run the SAME oracle with
    /// NO candidate work applied. The baseline-health capture V0+'s differential rides: a candidate failing a check
    /// the base ALREADY failed is not a regression, and a candidate passing a check the base failed is a FIX worth
    /// crediting. Fail-closed like its siblings — an ungradable base yields a failed grade with a legible detail
    /// (the recorded detail's <c>clone-failed:</c>/<c>grade-error:</c> prefixes let a typed consumer separate
    /// infra-unknown from a genuine baseline failure until F0's dispositions land).
    /// </summary>
    Task<BenchmarkGrade> GradeBaseAsync(Guid repositoryId, Guid teamId, string baseSha, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken);
}
