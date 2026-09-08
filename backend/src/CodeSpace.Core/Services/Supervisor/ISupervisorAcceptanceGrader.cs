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
    /// <summary>Grade a repository request while preserving the candidate producer's trusted routing and observed identity for model-backed oracles.</summary>
    Task<BenchmarkGrade> GradeAsync(RepositoryAcceptanceGradeRequest request, CancellationToken cancellationToken) =>
        GradeAsync(request.RepositoryId, request.TeamId, request.Branch, request.Spec, request.TimeoutSeconds, request.Anchor, cancellationToken);

    /// <summary>Grade a captured-deliverable request while preserving the producer identity across the delayed fold.</summary>
    Task<BenchmarkGrade> GradeCapturedAsync(CapturedAcceptanceGradeRequest request, CancellationToken cancellationToken) =>
        GradeCapturedAsync(request.AgentRunId, request.TeamId, request.Spec, request.TimeoutSeconds, cancellationToken);

    /// <summary>Grade a patch request while preserving the producer identity across the delayed fold.</summary>
    Task<BenchmarkGrade> GradePatchAsync(PatchAcceptanceGradeRequest request, CancellationToken cancellationToken) =>
        GradePatchAsync(request.RepositoryId, request.TeamId, request.BaseSha, request.InlinePatch, request.PatchArtifactId, request.Spec, request.TimeoutSeconds, request.OracleFloorPrograms, cancellationToken);

    /// <summary>
    /// Clone <paramref name="repositoryId"/> at <paramref name="branch"/> (team-scoped) and grade it with the oracle
    /// the spec names (<c>Kind</c> null ⇒ <c>TestsPass</c>) against the spec's command + kind-specific payload, capped
    /// at <paramref name="timeoutSeconds"/>, then remove the clone. A repo/branch that can't be cloned, or a check that
    /// can't be run, yields a failed grade with a legible detail (fail-closed); only a genuine cancellation propagates.
    /// </summary>
    Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken);

    /// <summary>DC-4 slice 2 (the repo-less lane): grade the oracle DIRECTLY against an existing directory — the scratch workspace a repo-less run produced its declared deliverables in. The agent process has already exited, so grading its left-behind directory is equivalent to grading a clone of it; there is no git world to anchor an independent checkout on. Same per-kind oracles, same fail-closed posture.</summary>
    Task<BenchmarkGrade> GradeDirectoryAsync(string directory, SupervisorAcceptanceSpec spec, Guid teamId, int timeoutSeconds, CancellationToken cancellationToken) =>
        Task.FromResult(new BenchmarkGrade { Passed = false, Detail = "grade-error: directory grading is not supported by this grader", Class = Messages.Agents.Benchmark.GradeFailureClass.GraderFault });

    /// <summary>
    /// The detail a repo-less unit's grade carries when the attempt captured no deliverable at all. GENUINE by class,
    /// never infra: nothing about the check machinery failed — the agent produced nothing to check, which is exactly
    /// what another agent pass CAN fix. Pinned by test (Rule 8); consumers key on the literal.
    /// </summary>
    public const string NoDeliverablesCaptured = "no-deliverables-captured";

    /// <summary>
    /// C2 — the fifth member of this interface's one family (build an independent world, grade it with the spec's
    /// oracle), for the caller that no longer HAS the world: materialize the deliverables
    /// <paramref name="agentRunId"/> durably captured (its <c>artifact_manifest</c> rows + their CAS bytes) into a
    /// fresh temporary directory, grade it exactly as <see cref="GradeDirectoryAsync"/> would, then remove it.
    ///
    /// <para>The supervisor's terminal fold runs after the producing worker's scratch directory is deleted and, on a
    /// multi-worker deployment, on a host that never held it — so the durable rows are the only sound world. Before
    /// this, every repo-less unit was failed closed on <c>no-branch-or-repo</c>, which classifies GENUINE with no
    /// work present, so a correctly-written report was met with "RETRY this exact subtask" forever.</para>
    ///
    /// <para>Fail-closed like its siblings: an attempt that captured NOTHING yields a failed grade carrying
    /// <see cref="NoDeliverablesCaptured"/>, never a silent pass; only a genuine cancellation propagates.</para>
    /// </summary>
    Task<BenchmarkGrade> GradeCapturedAsync(Guid agentRunId, Guid teamId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) =>
        Task.FromResult(new BenchmarkGrade { Passed = false, Detail = "grade-error: captured-deliverable grading is not supported by this grader", Class = Messages.Agents.Benchmark.GradeFailureClass.GraderFault });

    /// <summary>
    /// P3a-3 (B+V0+): grade with ORACLE RESTORE — when <paramref name="anchor"/> carries a base sha and the spec
    /// names oracle bytes the run OWNS, the grader restores those paths from that base before running, voiding any
    /// candidate tamper of its own judge (recorded in the evidence). Owned means an AUTHORED <c>ProtectedPaths</c>
    /// the check does not itself execute, or a program file of the acceptance argv that the anchor's FLOOR PROGRAMS
    /// — the run's own oracle inventory, the OPERATOR FLOOR's program files through
    /// <c>AcceptanceOracleProtection.ProgramCandidates</c> — also name. Every other program file the command
    /// executes is the SUBJECT under test (<c>sh solution.sh 7 5</c> runs the deliverable), so it is graded on the
    /// candidate's own bytes and merely reported: restoring it voids the work the goal asked for and no retry can
    /// pass.
    ///
    /// <para>The base and the inventory arrive as ONE <see cref="OracleAnchor"/> because either alone silently
    /// disables the protection the other half is for, and both have been forgotten in production — the pair cannot
    /// come apart when it is one value. Default forwards to the plain, deliberately UNANCHORED overload (fakes and
    /// non-git graders are unaffected).</para>
    /// </summary>
    Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, OracleAnchor anchor, CancellationToken cancellationToken) =>
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

    /// <summary>The patch lane's ORACLE-RESTORE twin: <paramref name="oracleFloorPrograms"/> is the run's own oracle inventory, exactly as the anchor's floor half is on <see cref="GradeAsync(Guid, Guid, string, SupervisorAcceptanceSpec, int, OracleAnchor, CancellationToken)"/> — a patch-only candidate can rewrite the judge it is graded with just as a branch's can. It takes the inventory ALONE rather than a whole anchor because its restore base IS its own required <paramref name="baseSha"/> argument: there is no half to forget here, and a second base could only disagree with the one the clone is built from. Default forwards to the floor-less overload (fakes and non-git graders are unaffected).</summary>
    Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, IReadOnlyList<string>? oracleFloorPrograms, CancellationToken cancellationToken) =>
        GradePatchAsync(repositoryId, teamId, baseSha, inlinePatch, patchArtifactId, spec, timeoutSeconds, cancellationToken);

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
