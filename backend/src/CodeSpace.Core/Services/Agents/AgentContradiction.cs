namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Whether a self-reported outcome CONTRADICTS an objective acceptance grade (P4-1) — the ONE shared classifier
/// every self-report/gate pair in the codebase folds through, so "contradiction" means exactly the same thing no
/// matter which lane detects it: the supervisor's per-unit spawn/retry fold (<c>SupervisorAgentResult</c>) and the
/// single-agent lane's own grade (<c>AgentRunResult</c>) both project their own status representation (a bare
/// string vs. <c>AgentRunStatus</c>) into a normalized <c>selfReportedSuccess</c> bool before calling this, so the
/// classifier itself stays representation-agnostic.
///
/// <para>Lives under <c>Services.Agents</c> (not <c>Services.Supervisor</c>) deliberately: <c>Supervisor</c> already
/// depends on <c>Agents</c> throughout this codebase (e.g. <c>SupervisorOutcome</c> reads agent-cost pricing), never
/// the reverse, and <see cref="Agents.AgentAcceptanceContract"/> — an <c>Agents</c>-namespace type — needs to call
/// this too. Pure + DB-free + exhaustively unit-testable, mirroring <see cref="AgentAcceptanceContract.IsInfraFailure"/>'s
/// "one place, can't drift" precedent.</para>
///
/// <para><b>Deliberately NOT wired into two other self-report/gate pairs that exist elsewhere in the supervisor
/// lane</b> — a <c>resolve</c> decision's resolver-authored success marker vs. its folded <c>acceptanceGrade</c>
/// (<c>SupervisorOutcome.ReadResolutionVerdict</c>, its own three-way <c>SupervisorResolutionVerdict</c>), and a
/// <c>stop</c> decision's model-declared outcome label vs. its folded <c>acceptanceGrade</c>
/// (<c>SupervisorOutcome.ClassifyStop</c>). Both are a DIFFERENT shape (free-text marker / declared label, not a
/// per-unit <c>SupervisorAgentResult</c> row) with their own established verdict types and
/// consumers — folding them through THIS classifier would mean two representations for the same concept. If a
/// future change wants "contradiction" to mean the same thing across all three shapes, that's a deliberate,
/// separate design decision, not an oversight here.</para>
/// </summary>
public static class AgentContradiction
{
    /// <summary>The self-report claimed success but the objective gate said it FAILED — the agent believes it's done; the check disagrees.</summary>
    public const string OverClaim = "over_claim";

    /// <summary>The self-report claimed failure but the objective gate said it PASSED — the agent gave up on work that was actually fine.</summary>
    public const string UnderClaim = "under_claim";

    /// <summary>
    /// Classify a self-report against its grade. Null when there's nothing to compare
    /// (<paramref name="acceptancePassed"/> is null — no oracle authored, or the grade was deferred) or when the two
    /// AGREE (a true self-report with a passing grade, or a false self-report with a failing grade).
    /// </summary>
    public static string? Detect(bool selfReportedSuccess, bool? acceptancePassed)
    {
        if (acceptancePassed is not { } passed) return null;

        if (selfReportedSuccess && !passed) return OverClaim;
        if (!selfReportedSuccess && passed) return UnderClaim;

        return null;
    }
}
