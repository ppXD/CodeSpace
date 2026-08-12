using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// The metric@1 half of the dual projection (P0-A): the SAME admission membrane and the SAME pure reducer the
/// operational assessment runs, with exactly one degree of freedom — receipts are admitted against the FIRST
/// server-authorized attempt per unit (<see cref="AttemptSelectors.SelectFirstAuthorized"/>, Lock Clause 3's other
/// selector). A later attempt's receipt dies at admission here the way a superseded attempt's dies operationally,
/// so a retry can move the terminal but never the solve-rate. Facts are deliberately the run's own (same-facts by
/// spec): the rare no-acceptance contract still reads the run's honest end — the zero-staked clean Success reads
/// <c>Unknown</c> through the reducer's own P0-A1 arm, so no status fallback exists on this plane either.
/// Admission REJECTIONS are discarded, not recorded: on the @1 selection a later-attempt receipt's rejection is
/// the selector working as designed, not an integrity diagnostic — integrity lives on the operational output.
/// </summary>
public static class MetricAt1
{
    public static MetricAt1Projection Project(IReadOnlyList<RequirementEnvelope> requirements, IReadOnlyList<ReceiptEnvelope> receipts, ExecutableSet? executableSet, IReadOnlyList<AttemptProjection> attempts, CompletionRunFacts facts, int? completionPolicyVersion, IReadOnlyDictionary<(string RequirementRef, string Kind), long>? currentRevisions = null)
    {
        var firstAuthorized = AttemptSelectors.SelectFirstAuthorized(attempts);

        var admission = ReceiptAdmission.Admit(receipts, requirements, executableSet, firstAuthorized, currentRevisions);

        var assessment = CompletionReducer.Reduce(requirements, admission.Admitted, facts);

        return new MetricAt1Projection
        {
            ProjectionVersion = MetricAt1Projection.CurrentProjectionVersion,
            StatisticalUnit = MetricAt1Projection.RunAt1Unit,
            Outcome = assessment.Outcome,
            Verification = assessment.Verification,
            AttemptRefs = firstAuthorized.Values.OrderBy(a => a.UnitId, StringComparer.Ordinal)
                .Select(a => new MetricAttemptRef { UnitId = a.UnitId, AttemptId = a.AttemptId, AttemptOrdinal = a.AttemptOrdinal }).ToList(),
            ObligationRefs = requirements.Select(r => $"{r.Kind}:{r.RequirementRef}").OrderBy(o => o, StringComparer.Ordinal).ToList(),
            CompletionPolicyVersion = completionPolicyVersion,
        };
    }

    /// <summary>A pre-protocol run's @1 row: <c>Unknown</c> with no bindings — old tape is never re-derived into a metric verdict, mirroring <see cref="CompletionReducer.ReduceLegacy"/>.</summary>
    public static MetricAt1Projection ProjectLegacy() => new()
    {
        ProjectionVersion = MetricAt1Projection.CurrentProjectionVersion,
        StatisticalUnit = MetricAt1Projection.RunAt1Unit,
        Outcome = OutcomeDisposition.Unknown,
        Verification = VerificationDisposition.Unknown,
        AttemptRefs = Array.Empty<MetricAttemptRef>(),
        ObligationRefs = Array.Empty<string>(),
        CompletionPolicyVersion = null,
    };
}
