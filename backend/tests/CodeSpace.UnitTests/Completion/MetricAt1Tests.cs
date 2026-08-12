using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: the metric@1 half of the P0-A dual projection — same admission rules, same reducer, receipts admitted
/// against the FIRST authorized attempt per unit. Pins: retry earns the terminal but never the metric (both
/// divergence directions), the zero-staked clean Success reads Unknown on this plane too (no status fallback
/// exists to reintroduce), a waived @1 verdict abstains rather than solves, the frozen statistical unit and
/// projection version, and the self-describing bindings (@1 attempt refs + obligation set).
/// </summary>
[Trait("Category", "Unit")]
public class MetricAt1Tests
{
    private static readonly Guid PlanId = Guid.NewGuid();

    [Fact]
    public void A_retry_that_passes_moves_the_terminal_but_never_the_metric()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var attempts = new[] { Attempt("s1", first, ordinal: 1), Attempt("s1", second, ordinal: 2) };
        var requirements = new[] { Requirement("acceptance:s1") };
        var receipts = new[] { Receipt("acceptance:s1", first, "s1", VerificationDisposition.Failed), Receipt("acceptance:s1", second, "s1", VerificationDisposition.Passed) };
        var facts = Facts(WorkflowRunStatus.Success);

        var operational = CompletionReducer.Reduce(requirements, Admit(receipts, requirements, attempts, AttemptSelectors.SelectOperationalActive), facts);
        var metric = MetricAt1.Project(requirements, receipts, Set("s1"), attempts, facts, completionPolicyVersion: 2);

        operational.Outcome.ShouldBe(OutcomeDisposition.Solved, customMessage: "operationally the retry answered the contract");
        metric.Outcome.ShouldBe(OutcomeDisposition.Unsolved, customMessage: "the metric reads the FIRST authorized attempt's failure — a retry never earns solve credit");
    }

    [Fact]
    public void A_first_attempt_pass_is_never_unseated_by_a_later_failure()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var attempts = new[] { Attempt("s1", first, ordinal: 1), Attempt("s1", second, ordinal: 2) };
        var requirements = new[] { Requirement("acceptance:s1") };
        var receipts = new[] { Receipt("acceptance:s1", first, "s1", VerificationDisposition.Passed), Receipt("acceptance:s1", second, "s1", VerificationDisposition.Failed) };
        var facts = Facts(WorkflowRunStatus.Failure);

        var metric = MetricAt1.Project(requirements, receipts, Set("s1"), attempts, facts, completionPolicyVersion: 2);

        metric.Outcome.ShouldBe(OutcomeDisposition.Solved, customMessage: "@1 reads exactly the first attempt's verdict in BOTH directions");
    }

    [Fact]
    public void A_zero_staked_clean_success_reads_unknown_on_the_metric_plane()
    {
        var metric = MetricAt1.Project(Array.Empty<RequirementEnvelope>(), Array.Empty<ReceiptEnvelope>(), executableSet: null, Array.Empty<AttemptProjection>(), Facts(WorkflowRunStatus.Success), completionPolicyVersion: 2);

        metric.Outcome.ShouldBe(OutcomeDisposition.Unknown, customMessage: "\"it exited zero\" must never move the solve-rate — there is no status fallback on this plane");
        metric.Verification.ShouldBe(VerificationDisposition.NotApplicable);
    }

    [Fact]
    public void A_waived_first_attempt_abstains_rather_than_solves()
    {
        var first = Guid.NewGuid();
        var attempts = new[] { Attempt("s1", first, ordinal: 1) };
        var requirements = new[] { Requirement("acceptance:s1") };
        var receipts = new[] { Receipt("acceptance:s1", first, "s1", VerificationDisposition.Waived) };

        var metric = MetricAt1.Project(requirements, receipts, Set("s1"), attempts, Facts(WorkflowRunStatus.Success), completionPolicyVersion: 2);

        metric.Outcome.ShouldBe(OutcomeDisposition.Abstained, customMessage: "a waiver makes no objective claim in either direction — never Solved");
    }

    [Fact]
    public void The_frozen_unit_and_version_and_bindings_are_self_describing()
    {
        var first = Guid.NewGuid();
        var attempts = new[] { Attempt("s1", first, ordinal: 1), Attempt("s1", Guid.NewGuid(), ordinal: 2) };
        var requirements = new[] { Requirement("acceptance:s1"), Requirement("delivery:s1", kind: ContractKinds.Delivery) };

        var metric = MetricAt1.Project(requirements, Array.Empty<ReceiptEnvelope>(), Set("s1"), attempts, Facts(WorkflowRunStatus.Success), completionPolicyVersion: 2);

        MetricAt1Projection.RunAt1Unit.ShouldBe("run@1", customMessage: "the FROZEN @1 statistical unit — a per-unit rate is a future unit string, never a silent reinterpretation");
        MetricAt1Projection.CurrentProjectionVersion.ShouldBe(1);
        metric.StatisticalUnit.ShouldBe(MetricAt1Projection.RunAt1Unit);
        metric.ProjectionVersion.ShouldBe(MetricAt1Projection.CurrentProjectionVersion);
        metric.AttemptRefs.ShouldHaveSingleItem().ShouldBe(new MetricAttemptRef { UnitId = "s1", AttemptId = first, AttemptOrdinal = 1 });
        metric.ObligationRefs.ShouldBe(new[] { "acceptance:acceptance:s1", "delivery:delivery:s1" });
        metric.CompletionPolicyVersion.ShouldBe(2);
    }

    [Fact]
    public void A_legacy_run_projects_unknown_with_no_bindings()
    {
        var metric = MetricAt1.ProjectLegacy();

        metric.Outcome.ShouldBe(OutcomeDisposition.Unknown);
        metric.AttemptRefs.ShouldBeEmpty();
        metric.ObligationRefs.ShouldBeEmpty();
        metric.CompletionPolicyVersion.ShouldBeNull();
    }

    // ── Builders ──

    private static IReadOnlyList<ReceiptEnvelope> Admit(IReadOnlyList<ReceiptEnvelope> receipts, IReadOnlyList<RequirementEnvelope> requirements, IReadOnlyList<AttemptProjection> attempts, Func<IReadOnlyList<AttemptProjection>, IReadOnlyDictionary<UnitKey, AttemptProjection>> selector) =>
        ReceiptAdmission.Admit(receipts, requirements, Set("s1"), selector(attempts)).Admitted;

    private static AttemptProjection Attempt(string unitId, Guid attemptId, int ordinal) => new()
    {
        AttemptId = attemptId, UnitId = unitId, WorkUnit = new WorkUnitRef { WorkPlanId = PlanId, PlanVersion = 1, UnitId = unitId }, AttemptOrdinal = ordinal, State = AttemptState.Settled,
    };

    private static ExecutableSet Set(params string[] unitIds) =>
        ExecutableSet.Create(PlanId, 1, unitIds.Select(u => new ExecutableUnit { UnitId = u, ContractHash = null, Disposition = UnitDisposition.New }).ToList());

    private static CompletionRunFacts Facts(WorkflowRunStatus status) => new() { TerminalStatus = status, HadOrderlyTerminal = true };

    private static RequirementEnvelope Requirement(string requirementRef, string kind = ContractKinds.Acceptance) => new()
    {
        RequirementRef = requirementRef, Kind = kind, Requiredness = Requiredness.Required, Authority = ContractAuthority.Operator, ContractSchemaVersion = "1",
    };

    private static ReceiptEnvelope Receipt(string requirementRef, Guid attemptId, string unitId, VerificationDisposition disposition, string kind = ContractKinds.Acceptance) => new()
    {
        RequirementRef = requirementRef, AttemptId = attemptId, Kind = kind, Disposition = disposition,
        WorkUnit = new WorkUnitRef { WorkPlanId = PlanId, PlanVersion = 1, UnitId = unitId },
        Authority = ContractAuthority.ServerPolicy, EvidenceRef = Guid.NewGuid(), ObservedAt = DateTimeOffset.UnixEpoch,
    };
}
