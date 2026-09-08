using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Agents;
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
    public void A_merge_that_integrated_still_evidences_the_stage_past_later_un_merged_work()
    {
        // The stale barrier belongs to the "which head may we SHIP" readers, not to this cell. Integration that
        // LANDED is a historical fact about the run; a later spawn / an unverified resolve / a refused stop cannot
        // un-make it. Conflating the two parked a run that merged cleanly and then hit an unverified resolve as
        // though it had never integrated (real-model run 33755336097) — a decider defect billed as missing work.
        var tape = new[]
        {
            Decision(1, SupervisorDecisionKinds.Merge, outcomeJson: """{"integration":{"status":"integrated","integratedBranch":"codespace/integration/x"}}"""),
            Decision(2, SupervisorDecisionKinds.Spawn),
        };

        SupervisorOutcome.ReadFinalIntegratedBranch(tape).ShouldBeNull("the ship-a-head reader KEEPS its stale barrier — this change never widens what may be published");

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(), Array.Empty<PublishManifest>())
            .ShouldContain(CompletionStage.Integrate);
    }

    [Theory]
    [InlineData(false, true)]   // an ordinary re-plan cannot un-make integration that LANDED
    [InlineData(true, false)]   // a plan that abandoned that direction took the head off every publishable floor
    public void A_merge_evidences_integrate_unless_a_later_plan_abandoned_the_work(bool abandoned, bool expected)
    {
        // The barrier-free ledger is what keeps this cell honest past later work — and it is exactly what let the cell
        // credit work the model explicitly threw away. An abandoning plan makes that head unmergeable AND unpublishable,
        // so crediting Integrate off it lets a Success claim rest on a candidate no rung of the ladder may ship.
        var tape = new[]
        {
            Decision(1, SupervisorDecisionKinds.Spawn),
            Decision(2, SupervisorDecisionKinds.Merge, outcomeJson: """{"integration":{"status":"integrated","integratedBranch":"codespace/integration/x"}}"""),
            Plan(3, abandoned),
        };

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(), Array.Empty<PublishManifest>())
            .Contains(CompletionStage.Integrate).ShouldBe(expected, "the completion authority must credit exactly the integration work the run may still deliver");
    }

    [Fact]
    public void A_clean_merge_behind_an_unverified_resolve_and_a_refused_stop_evidences_integrate()
    {
        // The live shape, verbatim in tape order: merge (clean) → ask_human → resolve (verdict NOT verified) → stop.
        // ReadFinalIntegratedBranch stops at the resolve and surfaces nothing to ship — correctly — but the run DID
        // integrate, so the stage is evidenced and the terminal authority must not park on Integrate.
        var tape = new[]
        {
            Decision(1, SupervisorDecisionKinds.Merge, outcomeJson: """{"integration":{"status":"integrated","integratedBranch":"codespace/integration/x"}}"""),
            Decision(2, SupervisorDecisionKinds.AskHuman),
            Decision(3, SupervisorDecisionKinds.Resolve, outcomeJson: """{"resolution":{"verified":false}}"""),
            Decision(4, SupervisorDecisionKinds.Stop),
        };

        SupervisorOutcome.ReadFinalIntegratedBranch(tape).ShouldBeNull("the ship-a-head reader keeps its stale barrier — this change never widens what may be published");

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(), Array.Empty<PublishManifest>())
            .ShouldContain(CompletionStage.Integrate);
    }

    [Theory]
    [InlineData("""{"integration":{"status":"Conflicted"}}""")]        // ran, integrated nothing
    [InlineData("""{"integration":{"status":"integrated"}}""")]        // clean but branch-less — nothing followable
    [InlineData(null)]                                                 // no integration block at all
    public void A_merge_that_integrated_nothing_still_evidences_nothing(string? outcomeJson)
    {
        // The rule is NOT weakened for a run without landed integration work — only a merge that actually produced a
        // branch counts.
        var tape = new[] { Decision(1, SupervisorDecisionKinds.Spawn), Decision(2, SupervisorDecisionKinds.Merge, outcomeJson: outcomeJson), Decision(3, SupervisorDecisionKinds.Stop) };

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(), Array.Empty<PublishManifest>())
            .ShouldNotContain(CompletionStage.Integrate);
    }

    [Fact]
    public void The_new_ledger_reads_only_an_EXECUTED_merge()
    {
        // Asserted on the reader itself: at Derive level the pre-existing final-head walk (which does not filter on
        // decision status) would mask this, and the ledger this change adds must not be the one that stops filtering.
        var claimed = new[] { Decision(1, SupervisorDecisionKinds.Merge, SupervisorDecisionStatus.Failed, """{"integration":{"status":"integrated","integratedBranch":"codespace/integration/x"}}""") };

        SupervisorOutcome.AnyMergeIntegratedABranch(claimed).ShouldBeFalse("a merge decision that never executed integrated nothing, whatever its recorded outcome claims");
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
    public void A_single_accepted_frontier_unit_published_directly_evidences_integrate()
    {
        var agentRunId = Guid.NewGuid();
        var tape = new[] { Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(agentRunId)) };

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(),
                new[] { AgentManifest(agentRunId, PublishState.Pushed, "codespace/agent/a") })
            .ShouldContain(CompletionStage.Integrate,
                "the completion authority must accept the same qualified ledger-direct delivery that I3 already lets terminalize");
    }

    [Fact]
    public void An_orphan_agent_manifest_never_self_certifies_integrate()
    {
        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), Array.Empty<SupervisorPriorDecision>(), Array.Empty<AttemptProjection>(),
                new[] { AgentManifest(Guid.NewGuid(), PublishState.Pushed, "codespace/agent/a") })
            .ShouldNotContain(CompletionStage.Integrate, "a manifest must belong to the accepted frontier on the durable supervisor tape");
    }

    [Fact]
    public void One_published_unit_cannot_certify_a_multi_unit_frontier()
    {
        var published = Guid.NewGuid();
        var unpublished = Guid.NewGuid();
        var tape = new[] { Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(published, unpublished)) };

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(),
                new[] { AgentManifest(published, PublishState.Pushed, "codespace/agent/a") })
            .ShouldNotContain(CompletionStage.Integrate, "ledger-direct delivery is a single-unit terminal shape; a multi-unit wave still needs consolidation");
    }

    [Fact]
    public void A_rejected_unit_cannot_self_certify_by_publishing_early()
    {
        var agentRunId = Guid.NewGuid();
        var tape = new[] { Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcomeWithAcceptance(agentRunId, acceptancePassed: false)) };

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(),
                new[] { AgentManifest(agentRunId, PublishState.Pushed, "codespace/agent/rejected") })
            .ShouldNotContain(CompletionStage.Integrate, "publication happens before the objective grade folds; a rejected head is never delivery evidence");
    }

    [Fact]
    public void A_partially_published_multi_repository_unit_does_not_evidence_integrate()
    {
        var agentRunId = Guid.NewGuid();
        var tape = new[] { Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(agentRunId)) };
        var manifests = new[]
        {
            AgentManifest(agentRunId, PublishState.Pushed, "codespace/agent/api", "api"),
            AgentManifest(agentRunId, PublishState.PatchOnly, branch: null, alias: "web"),
        };

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(), manifests)
            .ShouldNotContain(CompletionStage.Integrate, "one repository's branch cannot hide an unpublished sibling from the all-or-nothing publication fold");
    }

    [Fact]
    public void A_later_diagnosed_integration_failure_outranks_the_earlier_direct_push()
    {
        var agentRunId = Guid.NewGuid();
        var tape = new[]
        {
            Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(agentRunId)),
            Decision(2, SupervisorDecisionKinds.Merge, outcomeJson: """{"integration":{"status":"Failed","reason":"tests failed"}}"""),
        };

        UpstreamStageTrace.Derive(Array.Empty<RequirementEnvelope>(), tape, Array.Empty<AttemptProjection>(),
                new[] { AgentManifest(agentRunId, PublishState.Pushed, "codespace/agent/a") })
            .ShouldNotContain(CompletionStage.Integrate, "a real later integration failure remains authoritative over an earlier contributor push");
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

    // ─── Integrate read as NOT APPLICABLE: the patch-only policy dead end (audit D nail 1) ───

    [Theory]
    [InlineData(true, false, true)]     // a human adjudicated the delivery gate's policy-skip card
    [InlineData(false, true, true)]     // no card was ever contracted, but every frontier unit captured a patch
    [InlineData(false, false, false)]   // neither — one stray patch row cannot excuse work nothing accounts for
    public void A_patch_only_run_reads_integrate_not_applicable_only_with_adjudication_or_full_capture(bool adjudicated, bool captureTheFrontier, bool notApplicable)
    {
        var agentRunId = Guid.NewGuid();
        var tape = new List<SupervisorPriorDecision> { Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(agentRunId)) };

        if (adjudicated) tape.Add(AnsweredPolicySkipCard(2));

        var manifests = new[] { PatchOnlyAgentManifest(captureTheFrontier ? agentRunId : Guid.NewGuid()) };

        UpstreamStageTrace.NotApplicableIntegration(tape, manifests)?.Stage.ShouldBe(CompletionStage.Integrate);

        (UpstreamStageTrace.NotApplicableIntegration(tape, manifests) is not null).ShouldBe(notApplicable,
            "the policy must have been RULED on, or have caught every unit that owed a branch — otherwise 'not applicable' excuses work the capture pipeline may simply have swallowed");
    }

    [Fact]
    public void A_publish_permitting_repository_is_never_read_not_applicable()
    {
        // The invariant this whole reading must not break (#1762/#1771/#1774): a run that reached a branch owes the
        // stage, and a MIXED multi-repo run — one patch-only repo beside a publishing sibling — is exactly that run.
        var agentRunId = Guid.NewGuid();
        var tape = new[] { Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(agentRunId)), AnsweredPolicySkipCard(2) };

        var manifests = new[] { PatchOnlyAgentManifest(agentRunId), IntegrationManifest(PublishState.Pushed, "codespace/agent/sibling", PublishManifestKind.Agent) };

        UpstreamStageTrace.NotApplicableIntegration(tape, manifests).ShouldBeNull("a sibling reached a branch — this run was never policy-bounded");
    }

    [Fact]
    public void A_branchless_row_with_no_BY_CHOICE_record_is_never_read_as_policy()
    {
        // `PublishError is null` is NOT the guard chain's record — it is the ABSENCE of a failure. Three production
        // paths on a publish-PERMITTING repository leave branch AND PublishError null and write no skip reason at
        // all (AgentRunExecutor.PushProducedBranchIfEnabledAsync: a handle that cannot push, the fence refusal on a
        // reclaimed run, and a push that returned null). The POSITIVE record is the winning guard's reason, folded
        // onto the row's Summary (AgentRunResult.PublishSkipReason → BuildManifestUpsert) — without it, every one of
        // those runs read as policy-bounded and completed on a stage nobody had excused.
        var agentRunId = Guid.NewGuid();
        var tape = new[] { Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(agentRunId)), AnsweredPolicySkipCard(2) };

        var branchless = PatchOnlyAgentManifest(agentRunId);
        branchless.Summary = null;

        UpstreamStageTrace.NotApplicableIntegration(tape, new[] { branchless })
            .ShouldBeNull("no guard ever ruled on this row — a push that never happened is not a policy that forbade it");
    }

    [Fact]
    public void One_repositorys_adjudicated_skip_never_speaks_for_a_branchless_sibling()
    {
        // The card the delivery gate mints names EXACTLY the patch-only repositories the publish attempt reached
        // (SupervisorPullRequestOpener.NothingToOpenAsync filters CapturedWorkByRepositoryAsync by publish mode), so
        // a publish-permitting sibling that reached no branch contributes no entry and the human never ruled on it.
        // Matching the blocker's KIND alone let repoA's answer excuse Integrate run-wide, permitting sibling included.
        var patchOnly = Guid.NewGuid();
        var permitting = Guid.NewGuid();

        var tape = new[] { Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(patchOnly, permitting)), AnsweredPolicySkipCard(2) };

        var sibling = PatchOnlyAgentManifest(permitting, alias: "web");
        sibling.Summary = null;   // a repository that PERMITS pushing: no guard fired, so no by-choice record exists

        UpstreamStageTrace.NotApplicableIntegration(tape, new[] { PatchOnlyAgentManifest(patchOnly), sibling })
            .ShouldBeNull("'web' reached no branch and no policy accounts for it — one repository's answer cannot excuse the stage for another");
    }

    [Fact]
    public void An_ATTEMPTED_push_that_failed_is_never_read_as_policy()
    {
        // PublishError non-null is the ledger's own "attempted and failed", not "by choice" (PublishManifest.cs:72).
        // A broken credential is a fixable fault a human must see, never a policy that excuses the stage.
        var agentRunId = Guid.NewGuid();
        var tape = new[] { Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(agentRunId)), AnsweredPolicySkipCard(2) };

        var attempted = PatchOnlyAgentManifest(agentRunId);
        attempted.PublishError = "the remote rejected the push";
        attempted.Summary = null;

        UpstreamStageTrace.NotApplicableIntegration(tape, new[] { attempted })
            .ShouldBeNull("a failed push attempt is a fault to fix, not a policy nobody owes");
    }

    [Fact]
    public void A_run_that_DID_integrate_is_never_read_not_applicable()
    {
        var agentRunId = Guid.NewGuid();
        var tape = new[]
        {
            Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(agentRunId)),
            AnsweredPolicySkipCard(2),
            Decision(3, SupervisorDecisionKinds.Merge, outcomeJson: """{"integration":{"status":"Clean","integratedBranch":"codespace/integration/x"}}"""),
        };

        UpstreamStageTrace.NotApplicableIntegration(tape, new[] { PatchOnlyAgentManifest(agentRunId) })
            .ShouldBeNull("the stage was exercised — calling it 'not applicable' would misreport work that actually happened");
    }

    [Fact]
    public void An_UNANSWERED_policy_card_adjudicates_nothing()
    {
        var agentRunId = Guid.NewGuid();
        var tape = new[] { Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(agentRunId)), AnsweredPolicySkipCard(2, answer: null) };

        UpstreamStageTrace.NotApplicableIntegration(tape, new[] { PatchOnlyAgentManifest(Guid.NewGuid()) })
            .ShouldBeNull("a card nobody answered is a question, not a ruling");
    }

    [Theory]
    [InlineData(1, "integration not applicable — patch-only policy; 1 patch delivered")]
    [InlineData(3, "integration not applicable — patch-only policy; 3 patches delivered")]
    public void The_policy_sentence_is_pinned_verbatim(int patches, string expected)
    {
        // Backend-authored words: the stop recital prints them to the model and the Room prints them to the
        // operator, both verbatim — a rewording here moves both at once, which is the point of pinning it once.
        var agentRunId = Guid.NewGuid();
        var tape = new[] { Decision(1, SupervisorDecisionKinds.Spawn, outcomeJson: SpawnOutcome(agentRunId)), AnsweredPolicySkipCard(2) };

        UpstreamStageTrace.NotApplicableIntegration(tape, Enumerable.Range(0, patches).Select(_ => PatchOnlyAgentManifest(agentRunId)).ToList())!
            .Reason.ShouldBe(expected);
    }

    [Fact]
    public void A_not_applicable_stage_is_no_longer_missing()
    {
        var exercised = new HashSet<CompletionStage> { CompletionStage.Contract, CompletionStage.Plan, CompletionStage.Execute };
        var notApplicable = new UpstreamStageNotApplicable { Stage = CompletionStage.Integrate, Reason = "integration not applicable — patch-only policy; 1 patch delivered" };

        UpstreamStageTrace.MissingRequired(Supervisor, exercised).ShouldBe(new[] { CompletionStage.Integrate }, customMessage: "without the policy reading the stage is owed");
        UpstreamStageTrace.MissingRequired(Supervisor, exercised, notApplicable).ShouldBeEmpty("unevidenced and unowed are different verdicts — only the first parks");
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

    /// <summary>A structurally-valid, non-empty plan — the only shape <see cref="SupervisorPlanWindow.IsValidBoundary"/> lets draw an abandonment line.</summary>
    private static SupervisorPriorDecision Plan(long sequence, bool abandonEarlierResults) => Decision(sequence, SupervisorDecisionKinds.Plan) with
    {
        PayloadJson = $$"""{"goal":"g","subtasks":[{"id":"s1","title":"s1","instruction":"do it"}]{{(abandonEarlierResults ? ""","abandonEarlierResults":true""" : "")}}}""",
    };

    private static AttemptProjection Attempt() => new()
    {
        AttemptId = Guid.NewGuid(), UnitId = "s1", WorkUnit = null, AttemptOrdinal = 1, State = AttemptState.Settled,
    };

    /// <summary>A spawn whose unit(s) produced real, head-eligible work — the frontier the capture check is measured against.</summary>
    private static string SpawnOutcome(params Guid[] agentRunIds) =>
        $$"""{"agentRunIds":[{{string.Join(",", agentRunIds.Select(id => $"\"{id}\""))}}],"agentCount":{{agentRunIds.Length}},"agentResults":[{{string.Join(",", agentRunIds.Select(id => $$"""{"agentRunId":"{{id}}","status":"Succeeded","changedFiles":["a.txt"]}"""))}}]}""";

    private static string SpawnOutcomeWithAcceptance(Guid agentRunId, bool acceptancePassed) =>
        JsonSerializer.Serialize(new
        {
            agentRunIds = new[] { agentRunId },
            agentCount = 1,
            agentResults = new[] { new SupervisorAgentResult { AgentRunId = agentRunId, Status = "Succeeded", ChangedFiles = new[] { "a.txt" }, AcceptancePassed = acceptancePassed } },
        }, AgentJson.Options);

    /// <summary>The delivery gate's OWN card recording a publish-policy skip, answered unless <paramref name="answer"/> is null — the durable record that a human was shown this repository's policy conflict and ruled on it.</summary>
    private static SupervisorPriorDecision AnsweredPolicySkipCard(long sequence, string? answer = "patch-only is deliberate")
    {
        var question = $"{SupervisorDeliveryGate.QuestionPrefix}the required pull request was skipped by policy (primary: the repository requires patch-only publishing)";
        var reason = new SupervisorDeliveryGateReason { Kind = SupervisorDeliveryGateReason.PolicySkipped, Aliases = new[] { "primary" } };

        return Decision(sequence, SupervisorDecisionKinds.AskHuman) with
        {
            PayloadJson = $$"""{"question":{{JsonSerializer.Serialize(question)}},"{{SupervisorGateAdjudication.ReasonNode}}":{{JsonSerializer.Serialize(reason, AgentJson.Options)}}}""",
            OutcomeJson = JsonSerializer.Serialize(new { question, askHumanToken = "tok", answer }, AgentJson.Options),
        };
    }

    /// <summary>The BY-CHOICE branchless row <c>AgentRunExecutor</c> writes when the publish guard chain kept a captured diff off a branch: PatchOnly, no branch, no <c>PublishError</c>, and — the load-bearing detail — the winning guard's reason on <c>Summary</c> (<c>AgentRunResult.PublishSkipReason</c>, the ONE positive record that the skip was a choice).</summary>
    private static PublishManifest PatchOnlyAgentManifest(Guid agentRunId, string alias = "primary") => new()
    {
        Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Kind = PublishManifestKind.Agent, WorkflowRunId = Guid.NewGuid(), AgentRunId = agentRunId,
        RepositoryAlias = alias, PublishStateValue = PublishState.PatchOnly, ChangedFileCount = 1, Summary = "the repository requires patch-only publishing",
    };

    private static PublishManifest IntegrationManifest(PublishState state, string? branch, PublishManifestKind kind = PublishManifestKind.Integration) => new()
    {
        Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Kind = kind, WorkflowRunId = Guid.NewGuid(),
        RepositoryAlias = "primary", Branch = branch, PublishStateValue = state,
    };

    private static PublishManifest AgentManifest(Guid agentRunId, PublishState state, string? branch, string alias = "primary") => new()
    {
        Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Kind = PublishManifestKind.Agent, WorkflowRunId = Guid.NewGuid(), AgentRunId = agentRunId,
        RepositoryAlias = alias, Branch = branch, PublishStateValue = state,
    };
}
