using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: P4's stage trace — which UPSTREAM stages a run's durable evidence shows exercised, and which
/// Required cells a profile then finds missing. Pins the jurisdiction split (the trace covers exactly
/// Contract/Plan/Execute/Integrate; the completion-side six belong to the decider), each stage's evidence
/// source, the stale barrier (fresh spawned work past the last merge reads NOT integrated — the same walk the
/// publish readers use), and the fail-close reading of a never-derived trace.
/// </summary>
[Trait("Category", "Unit")]
public class UpstreamStageTraceTests
{
    private static readonly ModeProfile Supervisor = new ModeProfileRegistry().Resolve(RunModeKeys.Supervisor)!;
    private static readonly ModeProfile SingleAgent = new ModeProfileRegistry().Resolve(RunModeKeys.SingleAgent)!;

    [Fact]
    public void An_empty_run_evidences_nothing()
    {
        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), Array.Empty<SupervisorPriorDecision>(), Array.Empty<AttemptProjection>(), Array.Empty<PublishManifest>()).ShouldBeEmpty();
    }

    [Fact]
    public void The_traces_jurisdiction_is_exactly_the_four_upstream_stages()
    {
        UpstreamStageTrace.Stages.ShouldBe(new HashSet<CompletionStage> { CompletionStage.Contract, CompletionStage.Plan, CompletionStage.Execute, CompletionStage.Integrate },
            customMessage: "the completion-side six are the decider's conjuncts — widening this set double-encodes them; narrowing it un-gates a declared stage");
    }

    [Fact]
    public void Staked_requirements_evidence_contract()
    {
        var exercised = UpstreamStageTrace.Derive(new[] { Requirement("acceptance:s1") }, Array.Empty<SupervisorPriorDecision>(), Array.Empty<AttemptProjection>(), Array.Empty<PublishManifest>());

        exercised.ShouldBe(new HashSet<CompletionStage> { CompletionStage.Contract });
    }

    [Theory]
    [InlineData(SupervisorDecisionStatus.Succeeded, true)]   // an authorized, executed plan is the evidence
    [InlineData(SupervisorDecisionStatus.Failed, false)]     // a failed plan decision authorized nothing
    public void Only_a_succeeded_plan_decision_evidences_plan(SupervisorDecisionStatus status, bool expected)
    {
        var exercised = UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), new[] { Decision(1, SupervisorDecisionKinds.Plan, status) }, Array.Empty<AttemptProjection>(), Array.Empty<PublishManifest>());

        exercised.Contains(CompletionStage.Plan).ShouldBe(expected);
    }

    [Fact]
    public void Projected_attempts_evidence_execute()
    {
        var exercised = UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), Array.Empty<SupervisorPriorDecision>(), new[] { Attempt() }, Array.Empty<PublishManifest>());

        exercised.ShouldBe(new HashSet<CompletionStage> { CompletionStage.Execute });
    }

    [Fact]
    public void A_clean_merge_evidences_integrate()
    {
        var tape = new[]
        {
            Decision(1, SupervisorDecisionKinds.Spawn),
            Decision(2, SupervisorDecisionKinds.Merge, outcomeJson: """{"integration":{"status":"integrated","integratedBranch":"codespace/integration/x"}}"""),
        };

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(), Array.Empty<PublishManifest>())
            .ShouldContain(CompletionStage.Integrate);
    }

    [Fact]
    public void Fresh_spawned_work_after_the_last_merge_reads_not_integrated()
    {
        // The stale barrier: the walk is latest-first, and a spawn nothing later merged means the run's newest
        // work is UN-combined — an earlier integrated branch must not evidence the stage past it.
        var tape = new[]
        {
            Decision(1, SupervisorDecisionKinds.Merge, outcomeJson: """{"integration":{"status":"integrated","integratedBranch":"codespace/integration/x"}}"""),
            Decision(2, SupervisorDecisionKinds.Spawn),
        };

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(), Array.Empty<PublishManifest>())
            .ShouldNotContain(CompletionStage.Integrate);
    }

    [Fact]
    public void A_multi_repo_merge_with_a_clean_repo_evidences_integrate()
    {
        var tape = new[]
        {
            Decision(1, SupervisorDecisionKinds.Merge, outcomeJson: """{"integration":{"status":"Clean","repositories":[{"alias":"api","status":"Clean","integratedBranch":"codespace/integration/api"}]}}"""),
        };

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(), Array.Empty<PublishManifest>())
            .ShouldContain(CompletionStage.Integrate);
    }

    [Fact]
    public void A_pushed_integration_manifest_evidences_integrate()
    {
        // The Integrate cell's SECOND ledger (P4, plan-map lane): a tape-less run whose git.integrate_run step
        // recorded the run-level candidate row evidences the stage off that row alone.
        var exercised = UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), Array.Empty<SupervisorPriorDecision>(), Array.Empty<AttemptProjection>(),
            new[] { IntegrationManifest(PublishState.Pushed, branch: "codespace/integration/r") });

        exercised.ShouldBe(new HashSet<CompletionStage> { CompletionStage.Integrate });
    }

    [Theory]
    [InlineData(PublishState.PatchOnly, "codespace/integration/r")]   // never arrived — no reviewable candidate
    [InlineData(PublishState.Pushed, null)]                            // pushed-but-branchless attests nothing followable
    public void A_candidate_that_never_arrived_stays_silent(PublishState state, string? branch)
    {
        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), Array.Empty<SupervisorPriorDecision>(), Array.Empty<AttemptProjection>(),
                new[] { IntegrationManifest(state, branch) })
            .ShouldNotContain(CompletionStage.Integrate);
    }

    [Fact]
    public void An_agent_kind_manifest_never_evidences_integrate()
    {
        // Per-agent pushes are fragments, not the candidate — only the run-level Integration row speaks for the cell.
        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), Array.Empty<SupervisorPriorDecision>(), Array.Empty<AttemptProjection>(),
                new[] { IntegrationManifest(PublishState.Pushed, "codespace/agent/a", PublishManifestKind.Agent) })
            .ShouldNotContain(CompletionStage.Integrate);
    }

    [Fact]
    public void Missing_required_names_the_gaps_in_stage_order()
    {
        var exercised = new HashSet<CompletionStage> { CompletionStage.Contract, CompletionStage.Execute };

        UpstreamStageTrace.MissingRequired(Supervisor, exercised).ShouldBe(new[] { CompletionStage.Plan, CompletionStage.Integrate });
    }

    [Fact]
    public void An_authorized_NA_stage_is_never_owed()
    {
        // Single-agent declares Plan/Integrate ServerPolicy-NA — a trace without them is conformant.
        UpstreamStageTrace.MissingRequired(SingleAgent, new HashSet<CompletionStage> { CompletionStage.Contract, CompletionStage.Execute }).ShouldBeEmpty();
    }

    [Fact]
    public void A_never_derived_trace_evidences_nothing()
    {
        // Fail-close: a legacy compose carries no trace — every Required upstream cell reads missing.
        UpstreamStageTrace.MissingRequired(Supervisor, exercised: null)
            .ShouldBe(new[] { CompletionStage.Contract, CompletionStage.Plan, CompletionStage.Execute, CompletionStage.Integrate });
    }

    [Fact]
    public void The_gate_never_reaches_past_its_jurisdiction()
    {
        // The supervisor profile declares ALL TEN stages Required, and this trace exercises only the four
        // upstream ones — nothing missing, because the completion-side six are the decider's business.
        UpstreamStageTrace.MissingRequired(Supervisor, UpstreamStageTrace.Stages).ShouldBeEmpty();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static RequirementEnvelope Requirement(string requirementRef) => new()
    {
        RequirementRef = requirementRef, Kind = ContractKinds.Acceptance, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1",
    };

    private static SupervisorPriorDecision Decision(long sequence, string kind, SupervisorDecisionStatus status = SupervisorDecisionStatus.Succeeded, string? outcomeJson = null) => new()
    {
        Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = kind, Status = status, PayloadJson = "{}", OutcomeJson = outcomeJson,
    };

    private static AttemptProjection Attempt() => new()
    {
        AttemptId = Guid.NewGuid(), UnitId = "s1", WorkUnit = null, AttemptOrdinal = 1, State = AttemptState.Settled,
    };

    private static PublishManifest IntegrationManifest(PublishState state, string? branch, PublishManifestKind kind = PublishManifestKind.Integration) => new()
    {
        Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Kind = kind, WorkflowRunId = Guid.NewGuid(),
        RepositoryAlias = "primary", Branch = branch, PublishStateValue = state,
    };
}
