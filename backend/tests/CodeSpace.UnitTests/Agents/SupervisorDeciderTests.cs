using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Shouldly;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PR-E E3 real decider (<see cref="LlmSupervisorDecider"/>) + the projector
/// (<see cref="SupervisorDecisionProjector"/>), driven against a DETERMINISTIC fake at the
/// <see cref="IStructuredLLMClient"/> boundary (the honest seam — only the network call is replaced). Pins:
/// the decider folds the turn context into a prompt + projects a schema-valid model decision into a canonical
/// <see cref="SupervisorDecision"/>; each verb projects to its canonical payload; a missing model + an unknown
/// kind both FAIL CLOSED to a terminal stop (no crash).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDeciderTests
{
    private static readonly Guid BrainModelId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SubstituteRowId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static SupervisorTurnContext Context(int turnNumber = 0, params SupervisorPriorDecision[] prior) =>
        new() { Goal = "ship the feature", TurnNumber = turnNumber, PriorDecisions = prior, SupervisorModelId = BrainModelId };

    /// <summary>A plan the way a live model authors one — at least one subtask. Tests that only need SOME decision to reach the prompt use this rather than an EMPTY plan, which is itself an incoherent shape the decider now re-asks about (and which no useful model reply ever looks like).</summary>
    private static SupervisorPlanPayload OnePlannedSubtask(string subtaskId = "s1") =>
        new() { Goal = "ship", Subtasks = new[] { new SupervisorPlannedSubtask { Id = subtaskId, Title = "Audit", Instruction = "audit it" } } };

    // ── The decider folds context → a schema-valid canonical decision ────────────────

    [Fact]
    public async Task The_decider_projects_a_plan_model_decision_into_a_canonical_plan()
    {
        var model = new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Plan,
            Plan = new SupervisorPlanPayload
            {
                Goal = "ship",
                Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "Audit", Instruction = "audit it" } },
            },
        };

        var decision = await Decider(model).DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan);
        decision.IsTerminal.ShouldBeFalse();

        var subtasks = JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("subtasks");
        subtasks.GetArrayLength().ShouldBe(1);
        subtasks[0].GetProperty("id").GetString().ShouldBe("s1");
    }

    [Fact]
    public async Task The_user_prompt_folds_goal_turn_and_prior_decisions()
    {
        var prior = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtasks":[]}""", OutcomeJson = """{"planned":[]}""",
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 1, prior));

        prompt.ShouldContain("ship the feature", Case.Insensitive);
        prompt.ShouldContain("Turn: 1");
        prompt.ShouldContain(SupervisorDecisionKinds.Plan, Case.Insensitive, "the prior plan is folded into the prompt so the decider can spawn over it");
    }

    // ── DC-2a: the operator's pre-declared DeliverySpec is told to the model, so it stops re-proposing a vetoed contract ──

    [Fact]
    public void The_prompt_tells_the_model_the_operators_declared_true_and_its_target_branch()
    {
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context() with { DeliverySpec = new DeliverySpec { OpenPullRequest = true, TargetBranch = "release" } });

        prompt.ShouldContain("FINAL");
        prompt.ShouldContain("Automatically open a pull request against release");
    }

    [Fact]
    public void The_prompt_tells_the_model_the_operators_declared_false()
    {
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context() with { DeliverySpec = new DeliverySpec { OpenPullRequest = false } });

        prompt.ShouldContain("Do NOT automatically open a pull request");
    }

    [Fact]
    public void The_prompt_omits_the_delivery_block_when_the_operator_declared_nothing() =>
        LlmSupervisorDecider.BuildUserPromptForTest(Context()).ShouldNotContain("delivery preference", Case.Insensitive);

    [Fact]
    public void The_prompt_never_fabricates_a_decline_when_the_operator_only_pinned_a_target_branch()
    {
        // SupervisorDeliveryClamp.Clamp lets the operator pin ONLY TargetBranch and leave OpenPullRequest to the
        // model (per-field independence). OpenPullRequest null must NOT collapse into "false" — that would tell
        // the model a veto the operator never declared, wrongly suppressing a PR it would otherwise propose.
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context() with { DeliverySpec = new DeliverySpec { TargetBranch = "release" } });

        prompt.ShouldNotContain("Do NOT automatically open a pull request", customMessage: "the operator expressed no opinion on OpenPullRequest — it must not read as an explicit decline");
        prompt.ShouldContain("release", customMessage: "the operator's branch pin should still reach the model somehow");
    }

    // ── P1e compaction ladder: a re-planned run renders only the LATEST plan full; superseded plans collapse to a digest ──

    [Fact]
    public void A_superseded_plan_collapses_to_a_digest_while_the_latest_renders_full()
    {
        var v1 = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"goal":"g","subtasks":[{"id":"a","title":"A","instruction":"AAAA_OLD_INSTRUCTION"},{"id":"b","title":"B","instruction":"bee"}]}""",
            OutcomeJson = """{"outcome":"planned"}""",
        };
        var v2 = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"goal":"g","subtasks":[{"id":"a","title":"A","instruction":"CCCC_NEW_INSTRUCTION"},{"id":"c","title":"C","instruction":"see"}]}""",
            OutcomeJson = """{"outcome":"planned"}""",
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, v1, v2));

        prompt.ShouldNotContain("AAAA_OLD_INSTRUCTION", customMessage: "the superseded plan's full subtask payload is dropped — the single biggest source of the run's monotone prompt growth");
        prompt.ShouldContain("plan (superseded by a later re-plan): 2 subtask(s) [a, b]", customMessage: "the superseded plan collapses to a one-line digest that keeps its subtask ids (the re-plan history stays legible)");
        prompt.ShouldContain("CCCC_NEW_INSTRUCTION", customMessage: "the LATEST plan still renders full — only superseded plans compact");
    }

    [Fact]
    public void A_single_plan_renders_full_and_is_never_flagged_superseded()
    {
        var only = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"goal":"g","subtasks":[{"id":"a","title":"A","instruction":"SOLO_INSTRUCTION_MARKER"}]}""",
            OutcomeJson = """{"outcome":"planned"}""",
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 1, only));

        prompt.ShouldContain("SOLO_INSTRUCTION_MARKER", customMessage: "a lone plan IS the latest — it renders full (byte-identical to the pre-compaction prompt)");
        prompt.ShouldNotContain("superseded", customMessage: "nothing is superseded when there is a single plan");
    }

    [Fact]
    public void The_user_prompt_surfaces_each_spawned_agents_status_summary_and_error()
    {
        // SOTA #2 — the decider must SEE what its agents produced. A spawn decision whose outcome carries the
        // folded agentResults[] renders each agent's status + summary + error verbatim into the prompt.
        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1","s2"]}""",
            OutcomeJson = """{"agentRunIds":["a","b"],"agentCount":2,"agentResults":[{"agentRunId":"a","status":"Succeeded","summary":"added the endpoint"},{"agentRunId":"b","status":"Failed","error":"tests failed: NRE in FooService"}]}""",
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, spawn));

        prompt.ShouldContain("Succeeded");
        prompt.ShouldContain("added the endpoint", Case.Insensitive, "the decider sees a successful agent's summary");
        prompt.ShouldContain("Failed");
        prompt.ShouldContain("NRE in FooService", Case.Insensitive, "the decider sees the failed agent's error — the signal it needs to retry");
    }

    [Fact]
    public void The_user_prompt_surfaces_each_agents_git_ground_truth_changed_files_and_branch()
    {
        // The brain must verify completion + detect cross-agent file OVERLAP against the GIT GROUND TRUTH it holds,
        // not just the self-reported summary. A spawn's folded agentResults render each agent's changed files + branch.
        // Built via the REAL fold helper (valid Guids) so the labeled path — not the raw-jsonb fallback — is exercised.
        var agentId = Guid.NewGuid();
        var outcome = SupervisorOutcome.FoldAgentResults(
            $$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""",
            new[]
            {
                new SupervisorAgentResult
                {
                    AgentRunId = agentId, Status = "Succeeded", Summary = "did it",
                    ChangedFiles = new[] { "src/Api/Foo.cs", "src/Api/Bar.cs" }, ProducedBranch = "codespace/agent/foo",
                },
            });

        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcome,
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, spawn));

        prompt.ShouldContain("src/Api/Foo.cs", Case.Insensitive, "the decider sees the real changed files — for completion + cross-agent overlap detection");
        prompt.ShouldContain("src/Api/Bar.cs", Case.Insensitive);
        prompt.ShouldContain("2 changed file(s)", Case.Insensitive, "the labeled artifacts path renders the file count — proves it's not the raw-jsonb fallback");
        prompt.ShouldContain("codespace/agent/foo", Case.Insensitive, "the decider sees the produced branch");
    }

    [Fact]
    public void The_user_prompt_banners_an_outstanding_amendment()
    {
        // B6: after a human approved an oracle amendment, three live rounds re-amended five times each instead of
        // retrying — every render still showed the dead oracle's verdict. The banner is the missing hand-off.
        var agentId = Guid.NewGuid();
        var outcome = SupervisorOutcome.FoldAgentResults(
            $$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""",
            new[] { new SupervisorAgentResult { AgentRunId = agentId, Status = "Succeeded", ProducedBranch = "codespace/agent/s1", AcceptancePassed = false, AcceptanceDetail = "grade-error: npm not found" } });

        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcome,
        };
        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s1", Reason = "missing tooling", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } },
        });
        var approved = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.AskHuman, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = card.PayloadJson, OutcomeJson = """{"question":"q","answer":"approve"}""",
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, spawn, approved));

        prompt.ShouldContain("OUTSTANDING ORACLE AMENDMENT", customMessage: "the model is told the repair is signed");
        prompt.ShouldContain("RETRY 's1'", customMessage: "and the one next action");
    }

    [Theory]
    [InlineData(true, "acceptance PASSED", "objectively verified")]
    [InlineData(false, "acceptance FAILED", "RETRY this exact subtask")]
    public void The_user_prompt_surfaces_a_units_per_unit_acceptance_verdict(bool passed, string expectedHeadline, string expectedGuidance)
    {
        // Loopability slice 3: a unit's OBJECTIVE per-unit verdict must reach the brain so it RETRIES a rejected unit
        // (objective truth overrides the agent's self-report) and trusts a verified one.
        var agentId = Guid.NewGuid();
        var outcome = SupervisorOutcome.FoldAgentResults(
            $$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""",
            new[]
            {
                new SupervisorAgentResult
                {
                    AgentRunId = agentId, Status = "Succeeded", Summary = "did it", ProducedBranch = "codespace/agent/foo",
                    AcceptancePassed = passed, AcceptanceDetail = passed ? "tests-passed" : "tests-failed-exit-1",
                },
            });

        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcome,
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, spawn));

        prompt.ShouldContain(expectedHeadline, Case.Sensitive, "the per-unit verdict reaches the decide prompt");
        prompt.ShouldContain(expectedGuidance, Case.Insensitive, "the verdict carries the act-on-it guidance");
        if (!passed) prompt.ShouldContain("tests-failed-exit-1", Case.Insensitive, "a failed verdict surfaces its detail");
    }

    [Fact]
    public void The_user_prompt_names_a_retrys_tier_escalation()
    {
        // A2 (P4-2): the decider must see that a stronger model was already tried this retry — both so it doesn't
        // wonder why the same model wasn't retried again, and so it can reason about whether escalating further
        // (were it still failing) would help at all.
        var agentId = Guid.NewGuid();
        var outcome = JsonSerializer.Serialize(new
        {
            agentRunIds = new[] { agentId },
            agentCount = 1,
            escalation = new { from = "claude-haiku-4-5", to = "claude-sonnet-4-5", reason = "the prior attempt's self-report contradicted its acceptance grade (over_claim)" },
            agentResults = new[] { new { agentRunId = agentId, status = "Succeeded", summary = "did it" } },
        }, CodeSpace.Core.Services.Agents.AgentJson.Options);

        var retry = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Retry, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskId":"s1"}""", OutcomeJson = outcome,
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, retry));

        prompt.ShouldContain("ESCALATED model for this retry", customMessage: "the decider must be told a stronger model was already tried");
        prompt.ShouldContain("claude-haiku-4-5");
        prompt.ShouldContain("claude-sonnet-4-5");
        prompt.ShouldContain("contradicted its acceptance grade");
    }

    [Fact]
    public void The_user_prompt_says_when_an_escalation_was_requested_but_no_stronger_model_exists()
    {
        // D3: the trigger fired and the pool held nothing above the prior model's tier. Before, the retry silently
        // re-ran the SAME model with no trace at all — so the decider read a still-failing result and could not tell
        // "we already tried harder" from "nobody tried". Now the no-op is on the tape and in the prompt.
        var agentId = Guid.NewGuid();
        var outcome = JsonSerializer.Serialize(new
        {
            agentRunIds = new[] { agentId },
            agentCount = 1,
            escalation = new { from = "claude-haiku-4-5", to = (string?)null, reason = "the prior attempt's self-report contradicted its acceptance grade (over_claim)" },
            agentResults = new[] { new { agentRunId = agentId, status = "Failed", summary = "still failing" } },
        }, CodeSpace.Core.Services.Agents.AgentJson.Options);

        var retry = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Retry, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskId":"s1"}""", OutcomeJson = outcome,
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, retry));

        prompt.ShouldContain("no stronger model", customMessage: "the decider must be told escalation was ATTEMPTED and the pool had nothing above the prior tier — retrying again buys nothing");
        prompt.ShouldContain("claude-haiku-4-5", customMessage: "the model it stayed on is named");
        prompt.ShouldContain("contradicted its acceptance grade", customMessage: "the trigger that requested the escalation is still named");
        prompt.ShouldNotContain("ESCALATED model for this retry", customMessage: "nothing was escalated — the affirmative line would be a lie");
    }

    [Fact]
    public void The_user_prompt_names_no_escalation_for_an_ordinary_retry()
    {
        var agentId = Guid.NewGuid();
        var outcome = JsonSerializer.Serialize(new
        {
            agentRunIds = new[] { agentId },
            agentCount = 1,
            agentResults = new[] { new { agentRunId = agentId, status = "Succeeded", summary = "did it" } },
        }, CodeSpace.Core.Services.Agents.AgentJson.Options);

        var retry = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Retry, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskId":"s1"}""", OutcomeJson = outcome,
        };

        LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, retry)).ShouldNotContain("ESCALATED");
    }

    [Fact]
    public void The_user_prompt_calls_out_an_under_claim_where_the_agent_reported_failure_but_the_check_passed()
    {
        // P4-1: the inverse of the over-claim case above — the agent itself reported FAILURE, but its OWN check
        // actually PASSED. Previously this rendered identically to a clean pass with no signal that the agent
        // disagreed with its own verified result; the decider must be told NOT to retry a unit that is already fine.
        var agentId = Guid.NewGuid();
        var outcome = SupervisorOutcome.FoldAgentResults(
            $$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""",
            new[]
            {
                new SupervisorAgentResult
                {
                    AgentRunId = agentId, Status = "Failed", Error = "the agent gave up", ProducedBranch = "codespace/agent/foo",
                    AcceptancePassed = true, AcceptanceDetail = "tests-passed",
                },
            });

        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcome,
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, spawn));

        prompt.ShouldContain("acceptance PASSED", Case.Sensitive);
        prompt.ShouldNotContain("OUTSTANDING ORACLE AMENDMENT", customMessage: "no amendment on this tape — the banner never renders spuriously");
        prompt.ShouldContain("even though the agent itself reported failure", Case.Insensitive, "the under-claim must be called out, not silently rendered as a clean pass");
        prompt.ShouldContain("do NOT retry", Case.Insensitive, "the work is objectively fine — retrying it wastes a round-trip");
    }

    /// <summary>
    /// C2's refutation evidence, in the one place it was visible: a REPO-LESS research/report unit whose deliverable
    /// was captured and PASSED must not be told to retry itself. The fold used to hand every repo-less unit
    /// <c>no-branch-or-repo</c> with no work present — which classifies GENUINE, not infra (the sibling test above
    /// covers the work-present arm) — so a correctly written report produced the "RETRY this exact subtask" steer,
    /// forever, and non-code work was a first-class supervisor path only inside a repo. Two arms, one fixture: the
    /// PASSED verdict the fold now reaches, and the fail-closed grade it used to reach, so the difference in what the
    /// brain is told is asserted rather than assumed.
    /// </summary>
    [Theory]
    [InlineData(true, "artifact-present", "objectively verified", "RETRY this exact subtask")]
    [InlineData(false, "no-branch-or-repo", "RETRY this exact subtask", "objectively verified")]
    public void A_repo_less_units_captured_deliverable_verdict_decides_whether_the_brain_hears_retry(bool passed, string detail, string expectedSteer, string forbiddenSteer)
    {
        var agentId = Guid.NewGuid();
        var outcome = SupervisorOutcome.FoldAgentResults(
            $$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""",
            new[]
            {
                // No repo ⇒ no produced branch and no changed files: work-present is FALSE, which is exactly why
                // 'no-branch-or-repo' reads GENUINE here instead of infra.
                new SupervisorAgentResult
                {
                    AgentRunId = agentId, Status = "Succeeded", Summary = "wrote the findings report",
                    AcceptancePassed = passed, AcceptanceDetail = detail,
                },
            });

        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcome,
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, spawn));

        prompt.ShouldContain(expectedSteer, Case.Insensitive);
        prompt.ShouldNotContain(forbiddenSteer, Case.Insensitive,
            customMessage: passed
                ? "a repo-less unit whose captured deliverable PASSED must never be told to retry itself — that is the exact refutation evidence C2 must not leave behind"
                : "the fail-closed arm is the OLD behaviour this PR removes from the repo-less lane; it must not also claim the work is verified");
    }

    [Fact]
    public void The_user_prompt_renders_an_infra_classed_rejection_as_unverified_never_as_retry_bait()
    {
        // P0: 'no-branch-or-repo' with work present means the CHECK could not run — telling the brain to RETRY it
        // re-bills an agent and fails identically forever (the loop that marched a real run into its no-progress
        // kill). The prompt must steer to re-plan/ask instead.
        var agentId = Guid.NewGuid();
        var outcome = SupervisorOutcome.FoldAgentResults(
            $$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""",
            new[]
            {
                new SupervisorAgentResult
                {
                    AgentRunId = agentId, Status = "Succeeded", Summary = "wrote the report", ChangedFiles = new[] { "report.md" },
                    AcceptancePassed = false, AcceptanceDetail = "no-branch-or-repo",
                },
            });

        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcome,
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, spawn));

        prompt.ShouldContain("acceptance UNVERIFIED", Case.Sensitive, "an unrunnable check is NOT a verdict on the work");
        prompt.ShouldContain("Do NOT retry the agent", Case.Sensitive, "the one instruction that breaks the infinite-retry loop");
        prompt.ShouldContain("Re-plan this item", Case.Insensitive, "the steer: fix the CHECK, not the work");
        prompt.ShouldNotContain("RETRY this exact subtask", Case.Sensitive, "the retry bait line must not render for an infra-classed failure");
    }

    // ── The infra steer vs. a co-signed amendment: re-planning DISCARDS the co-sign, so it must not be the steer ──

    /// <summary>One graded unit whose CHECK could not run — the infra arm's fixture, folded exactly as the rehydrate fold folds it.</summary>
    private static string InfraFailedUnit(Guid agentId, string detail = "grade-error: npm not found") =>
        SupervisorOutcome.FoldAgentResults(
            $$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""",
            new[] { new SupervisorAgentResult { AgentRunId = agentId, Status = "Succeeded", Summary = "did it", ProducedBranch = "codespace/agent/s1", AcceptancePassed = false, AcceptanceDetail = detail } });

    /// <summary>An amend card the human APPROVED — the PRODUCTION card builder, so the marker and the structured proposal are exactly what the overlay and the obligation walk read back.</summary>
    private static SupervisorPriorDecision ApprovedAmendCard(long sequence, string subtaskId)
    {
        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = subtaskId, Reason = "the check shells out to tooling this repository never had",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "dotnet", "test" } },
        });

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.AskHuman, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = card.PayloadJson, OutcomeJson = """{"question":"q","answer":"approve"}""" };
    }

    /// <summary>A spawn or retry that staged 's1' and folded one infra-failed result for it.</summary>
    private static SupervisorPriorDecision StagedInfraFailure(long sequence, string decisionKind, string detail) =>
        new()
        {
            Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = decisionKind, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = decisionKind == SupervisorDecisionKinds.Spawn ? """{"subtaskIds":["s1"]}""" : """{"subtaskId":"s1"}""",
            OutcomeJson = InfraFailedUnit(Guid.NewGuid(), detail),
        };

    /// <summary>A plan prior declaring ONE subtask — the anchor every approved amendment lives or dies by (MAJOR-8), so the amend tapes can put one AFTER a co-sign and read what the steer does then. <paramref name="subtaskId"/> is what the plan DECLARES, which is how a tape says a newer plan DROPPED the unit its earlier attempt was graded under.</summary>
    private static SupervisorPriorDecision PlanAt(long sequence, string subtaskId = "s1") =>
        new()
        {
            Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(OnePlannedSubtask(subtaskId), AgentJson.Options), OutcomeJson = $$"""{"planned":["{{subtaskId}}"],"count":1}""",
        };

    /// <summary>
    /// The four steers, swept — the arm the live miss picked is the AwaitingRetry cell, and its whole content is
    /// that a co-signed check is retried rather than re-planned. Pinned as a full mapping (the #1795 convention)
    /// because the defect was a single cell rendering the wrong one of four.
    /// </summary>
    [Theory]
    [InlineData(SupervisorAmendStanding.None, "Do NOT retry the agent — another pass cannot fix the check. Re-plan this item with a check its agent can satisfy, or ask a human to rule.")]
    [InlineData(SupervisorAmendStanding.AwaitingRetry, "Its check was AMENDED by an approved human co-sign that is NOT yet consumed — RETRY this exact subtask so the amended check grades it; do NOT author a new plan for it.")]
    [InlineData(SupervisorAmendStanding.Consumed, "Do NOT retry the agent — another pass cannot fix the check. Its check was already AMENDED by an approved human co-sign and this unit has ALREADY been re-staged under it, so propose 'amend_acceptance' once more or 'ask_human' to rule; do NOT author a new plan for it.")]
    [InlineData(SupervisorAmendStanding.Discarded, "Do NOT retry the agent — another pass cannot fix the check. Its check WAS amended by an approved human co-sign, and a later re-plan DISCARDED that amendment — so do NOT author another plan: propose 'amend_acceptance' again, re-anchoring the repaired check to THIS plan, or 'ask_human' to rule.")]
    public void Each_amend_standing_maps_to_one_infra_steer(SupervisorAmendStanding standing, string steer)
    {
        LlmSupervisorDecider.InfraSteerFor(standing, SupervisorReplanExit.None).ShouldBe(steer);

        if (standing != SupervisorAmendStanding.None)
        {
            steer.ShouldNotContain("Re-plan this item", Case.Insensitive,
                "every amended arm sits one line from the None arm's copy — a model picks its verb off the wording, so no amended arm may read as that instruction");
            steer.ShouldContain("plan", Case.Insensitive,
                "…and every amended arm must still name the plan verb it forbids: the prohibition is the point, the mechanism behind it is stated once per prompt");
        }
    }

    [Fact]
    public void Every_amend_standing_has_its_own_steer()
    {
        // A new reading that silently falls through to the None arm is exactly the defect this arc is about — the
        // Discarded state existed on the tape long before it had a steer, and the fallthrough re-rendered "Re-plan
        // this item" on units a re-plan had already robbed. Swept over the enum so a fifth state cannot land mute.
        var steers = Enum.GetValues<SupervisorAmendStanding>().ToDictionary(v => v, v => LlmSupervisorDecider.InfraSteerFor(v, SupervisorReplanExit.None));

        steers.Values.Distinct(StringComparer.Ordinal).Count().ShouldBe(steers.Count,
            "two standings render the same steer — one of them is falling through to another arm, which is how a reading gets added without ever reaching the model");
    }

    [Fact]
    public void An_infra_verdict_under_an_unconsumed_cosign_steers_to_the_retry_and_names_the_re_plan_cost()
    {
        // Run 34066916864, arm The_real_model_repairs_a_broken_oracle_through_the_cosign_loop: the human co-signed,
        // the banner said RETRY, and this line — in the same prompt — said "Re-plan this item". The brain re-planned
        // eight times into the no-progress kill, and every re-plan silently discarded the amendment it was steered
        // away from consuming.
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3,
            StagedInfraFailure(2, SupervisorDecisionKinds.Spawn, "grade-error: npm not found"), ApprovedAmendCard(3, "s1")));

        prompt.ShouldContain("acceptance UNVERIFIED", Case.Sensitive, "an unrunnable check is still not a verdict on the work");
        prompt.ShouldContain("RETRY this exact subtask so the amended check grades it", Case.Sensitive, "the co-signed repair is consumed by a retry and by nothing else");
        prompt.ShouldNotContain("Re-plan this item", Case.Insensitive, "the steer that killed the run — a re-plan is the ONE move that throws the co-sign away");
        prompt.ShouldContain(LlmSupervisorDecider.ReplanDiscardsTheCosign, Case.Sensitive, "and the cost is named, not left to be inferred from the anchoring rule");
    }

    [Fact]
    public void An_infra_verdict_whose_cosign_was_already_retried_steers_to_a_second_cosign_or_a_human()
    {
        // The amended check ran and STILL could not grade. A third retry buys nothing, but a re-plan is worse than
        // nothing — it discards the ruling that is already in hand. The precondition (SupervisorAmendPrecondition)
        // admits a second card here for exactly this reason, so the steer names it.
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 4,
            StagedInfraFailure(2, SupervisorDecisionKinds.Spawn, "grade-error: npm not found"),
            ApprovedAmendCard(3, "s1"),
            StagedInfraFailure(4, SupervisorDecisionKinds.Retry, "grade-error: dotnet not found")));

        prompt.ShouldContain("propose 'amend_acceptance' once more or 'ask_human' to rule", Case.Sensitive, "the two moves the precondition actually leaves open");
        prompt.ShouldContain(LlmSupervisorDecider.ReplanDiscardsTheCosign, Case.Sensitive, "the cost survives the retry — the amendment is still anchored to this plan");
        prompt.ShouldNotContain("Re-plan this item", Case.Insensitive);
        prompt.ShouldNotContain("OUTSTANDING ORACLE AMENDMENT", Case.Sensitive, "the retry consumed the obligation — the banner is silent, and so is the steer's retry offer");
    }

    [Fact]
    public void An_infra_verdict_whose_cosign_a_re_plan_discarded_steers_back_to_the_amendment_not_at_another_plan()
    {
        // THE hole the first cut of this fix left open, and the one the observed plan×8 lived in: after the first
        // re-plan the standing fell back to None, so every historical infra verdict re-rendered "Re-plan this item"
        // — the same fuel, one turn later, on a unit whose repair that very plan had just eaten.
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 5,
            PlanAt(1),
            StagedInfraFailure(2, SupervisorDecisionKinds.Spawn, "grade-error: npm not found"),
            ApprovedAmendCard(3, "s1"),
            PlanAt(4)));

        prompt.ShouldContain("a later re-plan DISCARDED that amendment", Case.Sensitive, "the unit's own line must say what happened to the repair it was given");
        prompt.ShouldContain("propose 'amend_acceptance' again", Case.Sensitive, "…and send it at the verb that re-anchors the repair to the current plan");
        prompt.ShouldNotContain("Re-plan this item", Case.Insensitive, "asking for another plan here is asking to lose the next co-sign the same way");
        prompt.ShouldNotContain("RETRY this exact subtask so the amended check grades it", Case.Sensitive, "there is no amended check left to grade under — the retry offer belongs to the outstanding arm alone");
        prompt.ShouldNotContain("OUTSTANDING ORACLE AMENDMENT", Case.Sensitive, "a discarded amendment owes no retry, so the banner stays silent: Discarded is steer-only by design");
    }

    [Fact]
    public void The_re_plan_cost_is_stated_once_per_prompt_however_many_amended_results_it_carries()
    {
        // The render loop is per-prior × per-result, so an inline cost sentence repeated for every historical infra
        // verdict of the SAME subtask — about six copies (~300 tokens a turn) in the shape run 34066916864 reached.
        // The prohibition stays on each arm; the mechanism behind it is stated once, beside the amendment banner.
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 4,
            StagedInfraFailure(2, SupervisorDecisionKinds.Spawn, "grade-error: npm not found"),
            ApprovedAmendCard(3, "s1"),
            StagedInfraFailure(4, SupervisorDecisionKinds.Retry, "grade-error: dotnet not found")));

        prompt.Split(LlmSupervisorDecider.ReplanDiscardsTheCosign).Length.ShouldBe(2,
            "two infra results for the same subtask must not buy two copies of the same paragraph — it is rendered once per prompt");
        prompt.Split("acceptance UNVERIFIED").Length.ShouldBe(3,
            "…and the tape really does carry two of them, or the count above proves nothing");
    }

    [Fact]
    public void The_cost_note_stays_off_a_prompt_no_human_has_co_signed()
    {
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3,
            StagedInfraFailure(2, SupervisorDecisionKinds.Spawn, "grade-error: npm not found")));

        prompt.ShouldNotContain(LlmSupervisorDecider.ReplanDiscardsTheCosign, Case.Sensitive,
            "a run with no co-sign on its tape has nothing a plan could discard — telling it otherwise is a rule it cannot act on");
        prompt.ShouldContain("Re-plan this item", Case.Insensitive, "…and the un-amended steer is untouched, byte for byte");
    }

    [Fact]
    public void The_evidence_tail_under_an_amended_verdict_points_at_the_amendment_not_at_a_re_plan()
    {
        // The tail's preamble follows the verdict's OWN directive (the M0 note). Under a Consumed/Discarded verdict
        // the forbidden verb is the plan, so a tail still offering "(re-plan)" reinstates the contradiction one line
        // below the steer that removed it.
        var tail = SupervisorOutcome.FoldAgentResults(
            """{"agentRunIds":["33333333-3333-3333-3333-333333333333"],"agentCount":1}""",
            new[] { new SupervisorAgentResult { AgentRunId = Guid.Parse("33333333-3333-3333-3333-333333333333"), Status = "Succeeded", Summary = "did it", ProducedBranch = "codespace/agent/s1", AcceptancePassed = false, AcceptanceDetail = "grade-error: npm not found", AcceptanceEvidenceTail = "sh: npm: command not found" } });

        var staged = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = tail,
        };

        var amended = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 5, PlanAt(1), staged, ApprovedAmendCard(3, "s1"), PlanAt(4)));
        var plain = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, PlanAt(1), staged));

        amended.ShouldContain("author the replacement check in an 'amend_acceptance'", Case.Sensitive, "the tail names the authoring verb the verdict above it actually left open");
        amended.ShouldNotContain("(re-plan)", Case.Sensitive, "…and never the one it just forbade");
        plain.ShouldContain("(re-plan)", Case.Sensitive, "an un-amended infra verdict keeps its original tail preamble, byte for byte");
    }

    /// <summary>
    /// The BOND. The per-unit verdict and the outstanding-amendment banner sit in ONE prompt and are rendered by two
    /// different methods; the live miss was precisely them disagreeing about the same subtask. Both read
    /// <see cref="SupervisorAmendObligation"/>, and this sweeps the three tapes that separate the states to prove it
    /// — derived from the obligation walk rather than restated, so a copy here cannot drift into blessing the split.
    /// </summary>
    [Theory]
    [InlineData(false, false)]   // no co-sign          → no banner, and the steer re-plans the check
    [InlineData(true, false)]    // co-signed, unstaged → the banner names the retry and so does the steer
    [InlineData(true, true)]     // co-signed, staged   → both go quiet about the retry
    public void The_infra_steer_and_the_amendment_banner_read_the_same_state(bool cosigned, bool retried)
    {
        var tape = new List<SupervisorPriorDecision> { StagedInfraFailure(2, SupervisorDecisionKinds.Spawn, "grade-error: npm not found") };

        if (cosigned) tape.Add(ApprovedAmendCard(3, "s1"));
        if (retried) tape.Add(StagedInfraFailure(4, SupervisorDecisionKinds.Retry, "grade-error: dotnet not found"));

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 5, tape.ToArray()));
        var outstanding = SupervisorAmendObligation.IsOutstanding(tape, "s1");

        outstanding.ShouldBe(cosigned && !retried, "the walk itself must separate the three tapes, or the bond below proves nothing");

        prompt.Contains("OUTSTANDING ORACLE AMENDMENT", StringComparison.Ordinal)
            .ShouldBe(outstanding, "the banner renders exactly on an outstanding obligation");
        prompt.Contains(LlmSupervisorDecider.InfraSteerFor(SupervisorAmendStanding.AwaitingRetry, SupervisorReplanExit.None), StringComparison.Ordinal)
            .ShouldBe(outstanding, "the results block offers the retry exactly when the banner does — one prompt, one answer");
        prompt.Contains("Re-plan this item", StringComparison.OrdinalIgnoreCase)
            .ShouldBe(!cosigned, "a co-signed unit is never steered at the plan the co-sign is anchored to");
    }

    // ── The re-plan exit ramp: a plan already authored over this verdict stops being offered again ──

    /// <summary>
    /// The ramp's three arms, pinned as a full mapping (the #1795 convention) — and the None arm pinned as NULL,
    /// because "null ⇒ each steer renders its own original sentence" is the whole reason a scenario with no re-plan
    /// on its tape stays byte-identical. Every non-None arm must carry the prohibition, must name a verb, and must
    /// not read as "re-plan".
    /// </summary>
    [Theory]
    [InlineData(SupervisorReplanExit.None, null)]
    [InlineData(SupervisorReplanExit.ToStaging, "'spawn' this item so its re-planned check grades it.")]
    [InlineData(SupervisorReplanExit.ToStagingBehindADependency, "spawn what the dependency frontier says this item waits on, then 'spawn' this item so its re-planned check grades it.")]
    [InlineData(SupervisorReplanExit.ToAmendment, "Propose 'amend_acceptance' for this item's check, or 'ask_human' to rule.")]
    [InlineData(SupervisorReplanExit.ToHuman, "repairing its check cannot move it either, so do not propose that: 'ask_human' to rule.")]
    public void Each_replan_exit_maps_to_one_exit_ramp(SupervisorReplanExit replanExit, string? namedExit)
    {
        var ramp = LlmSupervisorDecider.ReplanExitRampFor(replanExit);

        if (namedExit is null)
        {
            ramp.ShouldBeNull("no re-plan ⇒ no substitution ⇒ both steers render the copy they always did");
            return;
        }

        ramp.ShouldNotBeNull();
        ramp!.ShouldEndWith(namedExit);
        ramp.ShouldContain("do NOT author another", Case.Sensitive, "the prohibition is what the ramp is for; the verb it names is the way out of it");
        ramp.ShouldContain("ALREADY", Case.Sensitive, "the reason leads, so the prohibition is not a bare assertion");
        ramp.ShouldNotContain("Re-plan this item", Case.Insensitive,
            "the ramp sits exactly where the re-plan sentence used to — a model picks its verb off the copy, so it must not read as that instruction");
    }

    /// <summary>
    /// A fourth exit that fell through to null would silently re-render "Re-plan this item" on the very tape it was
    /// added to withdraw it from — the same mute-arm defect the amend standings' sweep pins one screen up. Swept
    /// over the enum, so a new reading cannot land without copy.
    /// </summary>
    [Fact]
    public void Every_replan_exit_but_none_carries_its_own_ramp()
    {
        var ramps = Enum.GetValues<SupervisorReplanExit>().Where(v => v != SupervisorReplanExit.None)
            .Select(LlmSupervisorDecider.ReplanExitRampFor).ToList();

        ramps.ShouldAllBe(r => r != null, "an exit with no ramp falls back to the re-plan sentence it exists to replace");
        ramps.Distinct(StringComparer.Ordinal).Count().ShouldBe(ramps.Count, "two exits render the same ramp — one of them is falling through to the other's arm");
    }

    /// <summary>
    /// THE arm split, at the render site: <c>plan → spawn → plan</c> is the tape one turn after the model obeyed
    /// "re-plan this item with a check its agent can satisfy". The re-plan is authored and UNRUN, so the prompt must
    /// send the unit at the staging that grades it — not tell it a re-plan changed nothing (nothing re-graded it) and
    /// offer only amend/ask, which withdraws the plan verb the turn after it was correctly used and names no verb
    /// that could ever run the repaired check.
    /// </summary>
    [Fact]
    public void An_unrun_re_plan_is_steered_at_the_staging_it_is_waiting_for()
    {
        var graded = StagedInfraFailure(2, SupervisorDecisionKinds.Spawn, "grade-error: npm not found");

        var firstTime = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, PlanAt(1), graded));
        var context = Context(turnNumber: 4, PlanAt(1), graded, PlanAt(3));
        var afterReplan = LlmSupervisorDecider.BuildUserPromptForTest(context);

        firstTime.ShouldContain(LlmSupervisorDecider.ReplanThisItemWithASatisfiableCheck, Case.Sensitive,
            "the FIRST time a check comes back unrunnable, authoring a satisfiable one is honest advice — and its wording must not move");

        afterReplan.ShouldNotContain("Re-plan this item", Case.Insensitive, "the plan it asks for has already been authored");
        afterReplan.ShouldContain(LlmSupervisorDecider.ReplanExitRampFor(SupervisorReplanExit.ToStaging)!, Case.Sensitive);
        afterReplan.ShouldNotContain("re-plan the check", Case.Insensitive,
            "the plan-state recitation must not ask for the move the results block one screen above just withdrew");
        afterReplan.ShouldContain("SPAWN it under that plan", Case.Sensitive, "…it recites the same exit instead");

        SupervisorActionRoster.Offerable(context).ShouldContain(SupervisorDecisionKinds.Spawn,
            "the ramp may only name a verb the same prompt's menu offers — spawn is never maskable, which is what makes this arm always honest");
    }

    /// <summary>
    /// THE attractor this ramp exists for, in its live shape: the re-planned check RAN and came back saying exactly
    /// what it said before, with no co-sign anywhere on the tape — so every amended arm is inapplicable and the
    /// un-amended steer re-rendered "Re-plan this item" verbatim, forever. Arm
    /// <c>The_real_model_observes_a_real_conflict_and_chooses_to_resolve</c> reached it in ~25-40% of its attempts
    /// (<c>plan→spawn→plan×6→stop</c>, runs 34104701023 and 34101026801 attempt 2).
    ///
    /// <para>Asserted as a PAIR against the same graded row, so this cannot pass by rewording the first-time steer.</para>
    /// </summary>
    [Fact]
    public void An_infra_verdict_a_re_plan_already_failed_to_move_stops_being_offered_another_plan()
    {
        var graded = StagedInfraFailure(2, SupervisorDecisionKinds.Spawn, "grade-error: npm not found");
        var reGraded = StagedInfraFailure(4, SupervisorDecisionKinds.Retry, "grade-error: npm not found");

        var firstTime = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, PlanAt(1), graded));
        var context = Context(turnNumber: 5, PlanAt(1), graded, PlanAt(3), reGraded);
        var afterReplan = LlmSupervisorDecider.BuildUserPromptForTest(context);

        firstTime.ShouldContain(LlmSupervisorDecider.ReplanThisItemWithASatisfiableCheck, Case.Sensitive,
            "the FIRST time a check comes back unrunnable, authoring a satisfiable one is honest advice — and its wording must not move");

        afterReplan.ShouldNotContain("Re-plan this item", Case.Insensitive, "the second time, it is the fixed point");
        afterReplan.ShouldContain(LlmSupervisorDecider.ReplanExitRampFor(SupervisorReplanExit.ToAmendment)!, Case.Sensitive,
            "…and the exit is named: the check could not RUN, so the server's gate admits an amendment for it");
        afterReplan.ShouldNotContain("re-plan the check", Case.Insensitive,
            "the plan-state recitation must not ask for the move the results block one screen above just withdrew");

        SupervisorActionRoster.Offerable(context).ShouldContain(SupervisorDecisionKinds.AmendAcceptance,
            "the ramp may only name a verb the same prompt's menu offers — the two read one gate, so this cannot be a coincidence");
    }

    /// <summary>
    /// The same fixed point on a WORK-classed verdict — the measured-red-baseline steer. Here the check RAN and
    /// rejected the work, so <see cref="SupervisorAmendPrecondition"/> refuses a proposal and the turn's roster
    /// withholds the verb: the ramp must name only the human. A ramp that named <c>amend_acceptance</c> anyway would
    /// be the two-rosters defect (#1795) re-opened one screen apart.
    /// </summary>
    [Fact]
    public void A_work_classed_verdict_a_re_plan_already_failed_to_move_is_sent_only_to_a_human()
    {
        var graded = StagedRedBaseline(2, SupervisorDecisionKinds.Spawn);
        var reGraded = StagedRedBaseline(4, SupervisorDecisionKinds.Retry);

        var firstTime = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, PlanAt(1), graded));
        var context = Context(turnNumber: 5, PlanAt(1), graded, PlanAt(3), reGraded);
        var afterReplan = LlmSupervisorDecider.BuildUserPromptForTest(context);

        firstTime.ShouldContain(LlmSupervisorDecider.ReplanOrRescopeTheBaseline, Case.Sensitive, "the first-time baseline steer's wording must not move either");

        afterReplan.ShouldContain("BASE tree ALSO FAILS this same check", Case.Sensitive, "the differential is still named — only its steer changed");
        afterReplan.ShouldNotContain("Re-plan this item", Case.Insensitive);
        afterReplan.ShouldContain(LlmSupervisorDecider.ReplanExitRampFor(SupervisorReplanExit.ToHuman)!, Case.Sensitive, "a human ruling is the only exit left");
        afterReplan.ShouldNotContain("Propose 'amend_acceptance'", Case.Sensitive, "the gate refuses one here, and the roster below says so");

        SupervisorActionRoster.Withheld(context).ShouldContain(SupervisorDecisionKinds.AmendAcceptance,
            "…which is the fact that makes naming it wrong, read off the roster itself rather than restated");
    }

    /// <summary>
    /// The plan-MEMBERSHIP conjunct at the render site: the newest plan dropped this unit, so neither live exit is
    /// reachable — a spawn cannot stage a unit the plan does not declare, and a co-sign minted for it could never be
    /// consumed by a retry. The steer must send it at the human rather than spend one for nothing.
    ///
    /// <para>…and it must do that WITHOUT claiming the amend gate refuses it, because the gate does not: a dropped
    /// unit's superseded attempt is still infra-classed, so <c>amend_acceptance</c> stays on the turn's own menu
    /// (<c>SupervisorAmendPrecondition</c>'s named residual — the model may know something about the unit the
    /// plan's shape does not say). The ramp's job is to stop STEERING a human's co-sign at a unit no retry can
    /// consume it for; asserting inadmissibility one screen from a menu that offers it would be the two-rosters
    /// defect the ramp was built to avoid.</para>
    /// </summary>
    [Fact]
    public void A_unit_the_newest_plan_no_longer_declares_is_steered_only_at_a_human()
    {
        var context = Context(turnNumber: 4,
            PlanAt(1),
            StagedInfraFailure(2, SupervisorDecisionKinds.Spawn, "grade-error: npm not found"),
            PlanAt(3, subtaskId: "s2"));

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(context);

        prompt.ShouldContain(LlmSupervisorDecider.ReplanExitRampFor(SupervisorReplanExit.ToHuman)!, Case.Sensitive);
        prompt.ShouldNotContain("'spawn' this item", Case.Sensitive, "there is no plan item left for a spawn to stage");
        prompt.ShouldNotContain("Propose 'amend_acceptance'", Case.Sensitive, "and a co-sign for a dropped unit is a human spent on a retry that can never come");

        SupervisorActionRoster.Offerable(context).ShouldContain(SupervisorDecisionKinds.AmendAcceptance,
            "the gate still admits a proposal for this unit's superseded infra verdict — so the ramp may withhold the STEER, never assert the verb is unavailable");
        LlmSupervisorDecider.ReplanExitRampFor(SupervisorReplanExit.ToHuman)!.ShouldNotContain("admissible", Case.Insensitive,
            "…which is exactly the claim this arm must not make, because it is false of one of the two causes that reach it");
    }

    /// <summary>
    /// The ramp is not a one-way door: a re-plan that actually MOVED the verdict gives the plan verb back. Without
    /// this the ramp would be "one plan per unit, ever", and a genuinely new check whose fresh attempt reports a
    /// genuinely new diagnosis would be steered away from the re-plan that is now the honest next move.
    /// </summary>
    [Fact]
    public void A_verdict_the_re_plan_actually_moved_gets_its_first_time_steer_back()
    {
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 5,
            PlanAt(1),
            StagedInfraFailure(2, SupervisorDecisionKinds.Spawn, "grade-error: npm not found"),
            PlanAt(3),
            StagedInfraFailure(4, SupervisorDecisionKinds.Retry, "grade-error: dotnet not found")));

        prompt.ShouldContain(LlmSupervisorDecider.ReplanThisItemWithASatisfiableCheck, Case.Sensitive,
            "the re-planned check ran and produced a DIFFERENT verdict — the run is not at a fixed point");
        prompt.ShouldNotContain("ALREADY authored", Case.Sensitive);
        prompt.ShouldNotContain("A re-plan ALREADY left this verdict unchanged", Case.Sensitive);
    }

    /// <summary>
    /// The withdrawal must survive the model IGNORING it. The tape is the amendment arm's, plus the one plan the
    /// steer just told the model not to author — the commonest next tape there is. Read against the newest
    /// generation only, that plan moved the boundary past the identical re-grade and the prompt flipped back to
    /// "STAGE the plan this run already has", handing the run a longer cycle of the same fixed point:
    /// <c>spawn → identical re-grade → amend → plan → …</c>, bounded only by the total-spawn and cost caps.
    /// </summary>
    [Fact]
    public void The_withdrawal_survives_one_more_plan_authored_over_the_same_verdict()
    {
        var graded = StagedInfraFailure(2, SupervisorDecisionKinds.Spawn, "grade-error: npm not found");
        var reGraded = StagedInfraFailure(4, SupervisorDecisionKinds.Retry, "grade-error: npm not found");

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 6, PlanAt(1), graded, PlanAt(3), reGraded, PlanAt(5)));

        prompt.ShouldContain(LlmSupervisorDecider.ReplanExitRampFor(SupervisorReplanExit.ToAmendment)!, Case.Sensitive,
            "the spent re-plan is a tape fact — authoring another plan does not un-spend it");
        prompt.ShouldNotContain(LlmSupervisorDecider.ReplanExitRampFor(SupervisorReplanExit.ToStaging)!, Case.Sensitive,
            "…and it must not fall back to 'stage the plan you already have', which is the same loop one turn longer");
        prompt.ShouldNotContain("Re-plan this item", Case.Insensitive);
        prompt.ShouldNotContain("re-plan the check", Case.Insensitive, "…in the plan-state recitation either");
    }

    /// <summary>
    /// The two re-plan steers are ASYMMETRIC and this pins WHY that is safe rather than lucky: the infra arm reads
    /// the amend standing first (a co-signed unit must never be steered at the plan that discards the co-sign),
    /// while the measured-red-baseline arm substitutes the ramp unconditionally and never reads it. What closes the
    /// gap is a SERVER ruling — <see cref="SupervisorAmendPrecondition"/> refuses a proposal whose target's latest
    /// verdict is work-classed, so no co-sign can be minted against the verdict shape that arm renders, and the
    /// turn's roster withholds the verb on the same reading. The day the gate admits a work-classed target, that arm
    /// silently offers a re-plan to a unit whose co-sign the re-plan destroys, and this test is what says so.
    /// </summary>
    [Fact]
    public void No_cosign_can_be_minted_for_the_verdict_the_baseline_arm_renders()
    {
        var context = Context(turnNumber: 3, PlanAt(1), StagedRedBaseline(2, SupervisorDecisionKinds.Spawn));

        SupervisorAmendPrecondition.IsAmendable(context.PriorDecisions, "s1").ShouldBeFalse(
            "a check that RAN and rejected the work is evidence against the WORK — the gate refuses to amend it away");
        SupervisorActionRoster.Withheld(context).ShouldContain(SupervisorDecisionKinds.AmendAcceptance,
            "…and the turn's own menu says the same, off the same gate");
        SupervisorAmendObligation.StandingFor(context.PriorDecisions, "s1").ShouldBe(SupervisorAmendStanding.None,
            "so this arm's indifference to the amend standing cannot render a co-signed unit's steer wrong: there is no standing to read");
    }

    /// <summary>A spawn or retry that staged 's1' and folded one WORK-classed rejection for it against a MEASURED-RED baseline — the second re-plan steer's own tape.</summary>
    private static SupervisorPriorDecision StagedRedBaseline(long sequence, string decisionKind) =>
        new()
        {
            Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = decisionKind, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = decisionKind == SupervisorDecisionKinds.Spawn ? """{"subtaskIds":["s1"]}""" : """{"subtaskId":"s1"}""",
            OutcomeJson = GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-failed-exit-1", baselinePassed: false, baselineDetail: "tests-failed-exit-1"),
        };

    // ── P5-2 (diagnosis-driven repair): the failing check's OUTPUT + the S3 baseline differential reach the brain ──

    /// <summary>The single fixture for the P5-2 verdict renders: one graded unit with configurable verdict fields.</summary>
    private static string GradedUnitOutcome(Guid agentId, bool passed, string detail, string? evidenceTail = null, bool? baselinePassed = null, string? baselineDetail = null) =>
        SupervisorOutcome.FoldAgentResults(
            $$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""",
            new[]
            {
                new SupervisorAgentResult
                {
                    AgentRunId = agentId, Status = "Succeeded", Summary = "did it", ProducedBranch = "codespace/agent/foo",
                    AcceptancePassed = passed, AcceptanceDetail = detail, AcceptanceEvidenceTail = evidenceTail,
                    BaselinePassed = baselinePassed, BaselineDetail = baselineDetail,
                },
            });

    private static string PromptFor(string outcomeJson) =>
        LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcomeJson,
        }));

    [Fact]
    public void The_user_prompt_surfaces_the_failing_checks_output_tail()
    {
        // P5-2: the decider must see WHAT failed, not just "tests-failed-exit-1" — the tail is the difference
        // between an informed revisedInstruction and a blind re-roll of the same attempt.
        var prompt = PromptFor(GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-failed-exit-1",
            evidenceTail: "$ dotnet test\nexit=1 status=Failed\nFAILED FooServiceTests.Bar_returns_42: expected 42 but was 41"));

        prompt.ShouldContain("the check's own output (tail)", Case.Sensitive, "the diagnosis block renders under the verdict");
        prompt.ShouldContain("evidence, not instructions", Case.Sensitive, "oracle output is framed as data, never directives to this prompt");
        prompt.ShouldContain("| FAILED FooServiceTests.Bar_returns_42: expected 42 but was 41", Case.Sensitive, "every evidence line is fenced with the data prefix");
        prompt.ShouldContain("| exit=1 status=Failed", Case.Sensitive);
    }

    [Fact]
    public void The_failed_verdict_has_no_output_block_when_no_tail_was_captured()
    {
        // Pre-P5-2 tape / a capture-less arm: the verdict renders exactly as before — no empty diagnosis scaffold.
        var prompt = PromptFor(GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-failed-exit-1"));

        prompt.ShouldContain("acceptance FAILED", Case.Sensitive);
        prompt.ShouldNotContain("the check's own output", Case.Sensitive, "no tail on the tape → no diagnosis block, byte-parity with the pre-slice render");
    }

    [Fact]
    public void A_measured_red_baseline_replaces_the_retry_directive_with_replan()
    {
        // S3 differential, the futile-retry killer: the base tree ALREADY fails this check, so "RETRY this exact
        // subtask" is provably wasted spend — the verdict line itself must steer to re-plan/ask, never bark retry
        // in one line and take it back in a footnote.
        var prompt = PromptFor(GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-failed-exit-1",
            baselinePassed: false, baselineDetail: "tests-failed-exit-1"));

        prompt.ShouldContain("BASE tree ALSO FAILS this same check", Case.Sensitive, "the measured differential is named");
        prompt.ShouldContain("pre-existing breakage", Case.Insensitive, "the brain is told this attempt did not cause the failure");
        prompt.ShouldContain("Re-plan this item", Case.Insensitive, "the steer");
        prompt.ShouldNotContain("RETRY this exact subtask", Case.Sensitive, "the retry directive must not fight the differential");
    }

    [Fact]
    public void A_green_baseline_marks_the_failure_attempt_introduced()
    {
        // The inverse differential STRENGTHENS the retry: the base passed, this attempt broke it — a focused
        // retry (with the tail's diagnosis) is exactly right.
        var prompt = PromptFor(GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-failed-exit-1",
            baselinePassed: true, baselineDetail: "tests-passed"));

        prompt.ShouldContain("RETRY this exact subtask", Case.Sensitive, "a green base keeps the retry directive");
        prompt.ShouldContain("INTRODUCED by this attempt's work", Case.Sensitive, "the differential credits the breakage to this attempt");
    }

    [Fact]
    public void An_unmeasurable_baseline_claims_nothing()
    {
        // BaselinePassed=false with an infra-classed detail means "could not measure", NEVER "was already broken" —
        // the pinned BaselineDetail convention. The verdict must render exactly as if no baseline existed.
        var prompt = PromptFor(GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-failed-exit-1",
            baselinePassed: false, baselineDetail: "clone-failed: remote unreachable"));

        prompt.ShouldContain("RETRY this exact subtask", Case.Sensitive, "an unmeasured baseline leaves the ordinary failed verdict in place");
        prompt.ShouldNotContain("pre-existing breakage", Case.Insensitive, "unmeasurable must never read as already-broken");
        prompt.ShouldNotContain("INTRODUCED by this attempt", Case.Sensitive);
    }

    [Fact]
    public void Only_the_latest_spawns_tail_renders_older_rounds_keep_their_verdict_lines_only()
    {
        // Prompt economy: a long repair loop must not pay 2KB per historical round — the diagnosis the next action
        // targets is the LATEST round's; older rounds keep the one-line verdict (state), never the stale tail.
        var oldSpawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""",
            OutcomeJson = GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-failed-exit-1", evidenceTail: "STALE-ROUND-OUTPUT"),
        };
        var latestRetry = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Retry, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskId":"s1"}""",
            OutcomeJson = GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-failed-exit-1", evidenceTail: "FRESH-ROUND-OUTPUT"),
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, oldSpawn, latestRetry));

        prompt.ShouldContain("| FRESH-ROUND-OUTPUT", Case.Sensitive, "the latest round's diagnosis renders");
        prompt.ShouldNotContain("STALE-ROUND-OUTPUT", Case.Sensitive, "an older round's tail is dead prompt weight — its verdict line alone remains");
    }

    [Fact]
    public void The_infra_branch_still_renders_a_captured_tail_with_a_replan_preamble()
    {
        // tests-timed-out is infra-classed (the check could not COMPLETE) yet the partial output tail exists and
        // names which test hung — exactly what a re-planned check needs to avoid the same hang. The preamble must
        // NOT say "retry": the verdict two lines up just forbade it, and the live golden eval proved a model picks
        // the verb off the copy (the M0 resolve-verdict lesson).
        var prompt = PromptFor(GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-timed-out",
            evidenceTail: "$ dotnet test\nrunning SlowIntegrationTests.Full_system_boot ..."));

        prompt.ShouldContain("acceptance UNVERIFIED", Case.Sensitive, "timeout stays infra-classed");
        prompt.ShouldContain("| running SlowIntegrationTests.Full_system_boot ...", Case.Sensitive, "the partial output still reaches the brain");
        prompt.ShouldContain("author a check this unit can satisfy", Case.Sensitive, "the preamble follows the verdict's re-plan directive");
        prompt.ShouldNotContain("revisedInstruction", Case.Sensitive, "no retry verb under a do-not-retry verdict");
    }

    [Fact]
    public void A_red_baseline_tail_carries_the_replan_preamble_not_the_retry_verb()
    {
        // The contradictory-copy hazard: the verdict says "a blind retry cannot fix it" — the tail's preamble one
        // line later must not say "target it in the retry's revisedInstruction".
        var prompt = PromptFor(GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-failed-exit-1",
            evidenceTail: "exit=1\nFAILED Legacy.Flaky", baselinePassed: false, baselineDetail: "tests-failed-exit-1"));

        prompt.ShouldContain("pre-existing breakage", Case.Insensitive);
        prompt.ShouldContain("| FAILED Legacy.Flaky", Case.Sensitive, "the diagnosis still renders — re-planning needs it as much as a retry would");
        prompt.ShouldContain("author a check this unit can satisfy", Case.Sensitive, "the preamble follows the re-plan steer");
        prompt.ShouldNotContain("revisedInstruction", Case.Sensitive, "no retry verb under a blind-retry-cannot-fix verdict");
    }

    [Fact]
    public void A_green_baseline_tail_keeps_the_retry_preamble()
    {
        var prompt = PromptFor(GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-failed-exit-1",
            evidenceTail: "exit=1\nFAILED Foo.Bar", baselinePassed: true, baselineDetail: "tests-passed"));

        prompt.ShouldContain("INTRODUCED by this attempt's work", Case.Sensitive);
        prompt.ShouldContain("retry's revisedInstruction", Case.Sensitive, "an attempt-introduced failure is exactly what a focused retry fixes");
    }

    [Fact]
    public void An_unmeasurable_baseline_keeps_the_retry_preamble()
    {
        // The baseline could not be measured — the verdict stays the ordinary FAILED+retry, so the preamble does too.
        var prompt = PromptFor(GradedUnitOutcome(Guid.NewGuid(), passed: false, detail: "tests-failed-exit-1",
            evidenceTail: "exit=1\nFAILED Foo.Bar", baselinePassed: false, baselineDetail: "clone-failed: remote unreachable"));

        prompt.ShouldContain("RETRY this exact subtask", Case.Sensitive);
        prompt.ShouldContain("retry's revisedInstruction", Case.Sensitive);
    }

    [Fact]
    public void The_user_prompt_has_no_acceptance_line_for_an_ungraded_unit()
    {
        // A unit whose subtask authored no acceptance carries no verdict — the prompt is byte-identical to before the slice.
        var agentId = Guid.NewGuid();
        var outcome = SupervisorOutcome.FoldAgentResults(
            $$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""",
            new[] { new SupervisorAgentResult { AgentRunId = agentId, Status = "Succeeded", Summary = "did it", ProducedBranch = "b" } });

        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcome,
        };

        // Scoped to the per-unit verdict's own indent, not to the word anywhere in the prompt: the turn roster names
        // the 'amend_acceptance' verb on every turn, and a bare substring match would read that as a unit verdict.
        LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, spawn))
            .ShouldNotContain("      acceptance", Case.Insensitive, "an ungraded unit renders no acceptance line — byte-identical to pre-slice");
    }

    [Fact]
    public void The_user_prompt_renders_the_dependency_frontier_when_the_plan_declares_depends_on()
    {
        // a (done) → b ready → c blocked on b. The frontier guides the model to spawn in DependsOn order.
        var plan = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded, OutcomeJson = "{}",
            PayloadJson = """{"goal":"g","subtasks":[{"id":"a","title":"a","instruction":"do"},{"id":"b","title":"b","instruction":"do","dependsOn":["a"]},{"id":"c","title":"c","instruction":"do","dependsOn":["b"]}]}""",
        };
        var agentId = Guid.NewGuid();
        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["a"]}""",
            OutcomeJson = SupervisorOutcome.FoldAgentResults($$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""", new[] { new SupervisorAgentResult { AgentRunId = agentId, Status = "Succeeded" } }),
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, plan, spawn));

        prompt.ShouldContain("Dependency frontier", Case.Insensitive, "the model sees the server-enforced ordering");
        prompt.ShouldContain("ready to spawn now: b", Case.Insensitive, "a is done → b's dependency is satisfied");
        prompt.ShouldContain("blocked: c (waiting on b)", Case.Insensitive, "c waits on the not-done b");
    }

    [Fact]
    public void The_user_prompt_renders_no_dependency_frontier_for_a_flat_plan()
    {
        var plan = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded, OutcomeJson = "{}",
            PayloadJson = """{"goal":"g","subtasks":[{"id":"a","title":"a","instruction":"do"},{"id":"b","title":"b","instruction":"do"}]}""",
        };

        LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 1, plan))
            .ShouldNotContain("Dependency frontier", Case.Insensitive, "a flat plan (no DependsOn) renders no frontier — byte-identical to before");
    }

    [Fact]
    public void The_user_prompt_is_UNCHANGED_by_per_repo_RepositoryResults()
    {
        // Resolver loop #379 S7-B — surfacing per-repo RepositoryResults into the compact must NOT change what the
        // decider SEES: the labeled rendering reads agentResults field-selectively (status / summary / error), never
        // the raw per-repo array. So a multi-repo spawn's prompt is byte-identical to the same spawn without per-repo
        // data — single-repo decider behaviour is preserved and a multi-repo run adds no prompt bloat. Built via the
        // REAL fold helper (valid Guids) so the labeled path — not the raw-jsonb fallback — is exercised.
        var agentId = Guid.NewGuid();
        var spawnOutcome = $$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""";

        SupervisorAgentResult Result(IReadOnlyList<RepositoryRunResult> repos) => new()
        {
            AgentRunId = agentId, Status = "Succeeded", Summary = "coordinated change", ProducedBranch = "codespace/agent/api", RepositoryResults = repos,
        };

        var withoutPerRepo = SupervisorOutcome.FoldAgentResults(spawnOutcome, new[] { Result(Array.Empty<RepositoryRunResult>()) });
        var withPerRepo = SupervisorOutcome.FoldAgentResults(spawnOutcome, new[] { Result(new[]
        {
            new RepositoryRunResult { Alias = "repo", RepositoryId = Guid.NewGuid(), ProducedBranch = "codespace/agent/api", BaseBranch = "main", Access = WorkspaceAccess.Write },
            new RepositoryRunResult { Alias = "web", RepositoryId = Guid.NewGuid(), ProducedBranch = "codespace/agent/web", BaseBranch = "develop", Access = WorkspaceAccess.Write },
        }) }) ;

        SupervisorPriorDecision Spawn(string outcome) => new()
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcome,
        };

        var promptWithout = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, Spawn(withoutPerRepo)));
        var promptWith = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, Spawn(withPerRepo)));

        promptWith.ShouldBe(promptWithout, "per-repo RepositoryResults are not rendered into the decider prompt — the field-selective labeled rendering keeps single-repo behaviour identical and adds no multi-repo bloat");
        promptWith.ShouldNotContain("codespace/agent/web", Case.Insensitive, "the per-repo branches never leak into the prompt as raw text");
    }

    [Fact]
    public void The_system_prompt_instructs_inspect_and_retry_without_an_unconditional_merge_then_stop_rail()
    {
        // The visibility fold is necessary-but-not-sufficient: the rails must INSTRUCT the model to act on what it
        // sees. The old fixed "plan, then spawn, then merge, then stop" rail is REPLACED (not appended) — co-presence
        // would leave two conflicting directives and the rail would win at temp 0.2.
        var system = LlmSupervisorDecider.SystemPromptForTest;

        system.ShouldContain("retry", Case.Insensitive, "the supervisor is instructed to retry a failed subtask");
        system.ShouldContain("inspect", Case.Insensitive, "...after inspecting each agent's status/error");
        system.ShouldNotContain("then merge, then stop", Case.Insensitive, "the unconditional merge-then-stop rail is GONE — it would override the conditional retry guidance");
    }

    [Fact]
    public void The_system_prompt_surfaces_the_optional_per_agent_dispatch_override_and_its_clamp()
    {
        // L4 model-authored dispatch: the model can only AUTHOR heterogeneous agents[] (distinct role/repo/autonomy per
        // subtask) if the prompt tells it the option exists — the schema + executor already accept it, but the brain was
        // never told. Pinned so the guidance can't be dropped silently (the real-model dispatch arm depends on it), and so
        // the OPTIONAL framing + the server clamp are both stated (omitting either would either suppress dispatch or
        // invite an escalation the model expects to stick).
        var system = LlmSupervisorDecider.SystemPromptForTest;

        system.ShouldContain("agents[]", Case.Insensitive, "the per-agent dispatch override is named so the model knows it can author heterogeneous agents");
        system.ShouldContain("omit", Case.Insensitive, "...and it is explicitly OPTIONAL — omit it for the homogeneous default, so plain-spawn stays the model's natural choice");
        system.ShouldContain("clamp", Case.Insensitive, "...and the server clamp is stated so the model doesn't expect to escalate repos/autonomy past the operator's grant");
    }

    [Fact]
    public void The_system_prompt_surfaces_the_optional_stop_acceptance_definition_of_done_tightly_scoped()
    {
        // L4 model-authored DoD: the model can only author an objective stop 'acceptance' command (its own
        // definition-of-done, graded by the server AND-ed with the operator floor) if the prompt tells it the option
        // exists — the schema + the terminal-stop grader already accept it, but the brain was never told. Pinned so the
        // guidance can't be dropped silently (the real-model DoD arm depends on it), AND so the TIGHT scoping survives:
        // 'acceptance' is OPTIONAL and authored ONLY when the goal names a concrete runnable check — without that scoping,
        // a model could author a FAILING command on a generic goal and red the gated headline whole-loop (acceptance feeds
        // the Drove verdict, unlike the dispatch override).
        var system = LlmSupervisorDecider.SystemPromptForTest;

        system.ShouldContain("acceptance", Case.Insensitive, "the optional model-authored definition-of-done is named so the model knows it can author a stop acceptance command");
        system.ShouldContain("definition-of-done", Case.Insensitive, "...framed as the goal's objective definition of done");
        system.ShouldContain("ONLY when the goal", Case.Insensitive, "...TIGHTLY scoped — authored only when the goal names a concrete runnable check, so a generic goal omits it and the gated headline arc can't regress on a model-authored failing command");
    }

    [Fact]
    public void The_system_prompt_surfaces_the_optional_plan_phases()
    {
        // L4 ARC C model-authored SEMANTIC PHASES: the model can only group subtasks into named 'phases' on a plan if the
        // prompt tells it the option exists — the schema + executor fold + projection already accept them, but the brain
        // was never told (the same root cause #682 fixed for agents[] and #692 for acceptance). Pinned so the guidance
        // can't be dropped silently (the real-model phase-authorship arm depends on it), AND so the OPTIONAL framing
        // survives — phases are projection-only (they never feed the gated Drove verdict), but 'omit for a flat plan'
        // keeps a simple goal's plan byte-identical.
        var system = LlmSupervisorDecider.SystemPromptForTest;

        system.ShouldContain("phases", Case.Insensitive, "the optional model-authored semantic phases are named so the model knows it can group subtasks into named stages");
        system.ShouldContain("omit 'phases'", Case.Insensitive, "...and it is explicitly OPTIONAL — omit it for the flat subtask plan, so a simple goal stays byte-identical");
    }

    [Fact]
    public void The_system_prompt_names_the_plans_abandon_earlier_results_signal()
    {
        // A re-plan that changes DIRECTION used to leave the abandoned generation's finished branches mergeable, and
        // the recitation promised the model they would be folded — so a brain steering away from wrong work shipped it
        // anyway. The schema description is the primary carrier; the rails only have to tell the model the field is
        // there, and that saying nothing keeps the old conservation.
        var system = LlmSupervisorDecider.SystemPromptForTest;

        system.ShouldContain("abandonEarlierResults", Case.Sensitive, "the model cannot use a signal the rails never name");
        system.ShouldContain("changes direction", Case.Insensitive, "...scoped to the direction-change case, so an ordinary re-plan still conserves finished work");
    }

    [Fact]
    public void The_system_prompt_names_what_a_plan_costs_an_approved_acceptance_amendment()
    {
        // The verb's OWN description is the last thing a model reads before choosing it, and it is the ONLY carrier
        // that survives the move it warns about: a re-plan discards the amendment, so the per-unit steer that named
        // the cost is gone from the very next prompt. Run 34066916864 chose 'plan' eight times over two co-signed
        // amendments. Pinned, because a sentence no test asserts is a sentence the next edit drops for free.
        var system = LlmSupervisorDecider.SystemPromptForTest;

        system.ShouldContain("DISCARDS every approved acceptance amendment", Case.Sensitive, "the plan verb's cost to a co-signed oracle must be stated where the verb itself is described");
        system.ShouldContain("anchored to the plan it was", Case.Insensitive, "...together with the anchoring rule that makes it true, so the model reads a mechanism and not a bare prohibition");
    }

    [Fact]
    public void The_system_prompt_guides_recognising_an_already_completed_ask_without_re_planning_redundant_work()
    {
        // Redundant-complete handoff: a session-continue whose follow-up re-requests work the prior context already
        // shipped+verified. The model must override the plan-first rail and recognise completion (stop / ask_human),
        // NOT re-plan to redo merged work — while a NEW/additional ask still plans. Pinned so the guidance can't be
        // dropped silently (the live golden eval continue-redundant-complete depends on it).
        var system = LlmSupervisorDecider.SystemPromptForTest;

        system.ShouldContain("already", Case.Insensitive, "the rail names the already-delivered case");
        system.ShouldContain("do NOT re-plan", Case.Insensitive, "...and instructs NOT to redo it");
        system.ShouldContain("recognise completion", Case.Insensitive, "...stop on a satisfied goal");
        system.ShouldContain("NEW or ADDITIONAL work", Case.Insensitive, "...but a genuinely new ask is NOT redundant and must still be planned (guards the incremental/mixed/after-failure scenarios)");

        // The first-turn user-prompt line carries the same exception, so a fresh continue with no prior decisions still sees it.
        var firstTurn = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 1));
        firstTurn.ShouldContain("Start by planning", Case.Insensitive, "the default rail is still plan-first");
        firstTurn.ShouldContain("already completed and verified", Case.Insensitive, "...with the redundant-complete exception");
    }

    // ── Resolver loop #379 S1: the decider PERCEIVES an integration conflict ─────────

    /// <summary>
    /// A merge outcome carrying a conflicted on-disk integration block, shaped EXACTLY as ProjectIntegrationResult emits
    /// it: an APPLIED contribution carries fallbackBranch null (LocalGitBranchIntegrator.Applied() sets none); only the
    /// CONFLICTED contribution carries its preserved branch (FallbackBranch = ProducedBranch). The reader must collect
    /// only the non-applied branch — the applied agent's branch is not surfaced here (the resolver's full re-merge set
    /// is assembled from the spawn's agent results, not this block).
    /// </summary>
    private const string ConflictedMergeOutcome = """
        {"synthesis":{"text":"combined"},"integration":{"status":"Conflicted","integratedBranch":null,"appliedCount":0,"reason":"a contribution conflicted while integrating","excludedAgents":[],"outcomes":[
          {"label":"agent-a","disposition":"Applied","reason":null,"conflictedFiles":[],"fallbackBranch":null},
          {"label":"agent-b","disposition":"Conflicted","reason":"textual conflict","conflictedFiles":["src/Foo.cs","src/Bar.cs"],"fallbackBranch":"codespace/agent/bbb"}
        ]}}
        """;

    private static SupervisorPriorDecision MergeDecision(string outcomeJson) => new()
    {
        Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = "{}", OutcomeJson = outcomeJson,
    };

    private static SupervisorPriorDecision Decision(string kind, long sequence, string? outcomeJson) => new()
    {
        Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcomeJson,
    };

    private static string PublishOutcome(RoomPullRequestDisposition disposition, int? number = null, string? url = null, string? error = null) =>
        JsonSerializer.Serialize(new RoomPullRequestResult
        {
            PullRequests = new[] { new RoomPullRequestOpened { Alias = "primary", Disposition = disposition, Number = number, Url = url, Error = error } },
        }, AgentJson.Options);

    [Fact]
    public void ReadIntegration_parses_a_conflicted_block_aggregating_files_and_preserved_branches()
    {
        var integration = SupervisorOutcome.ReadIntegration(ConflictedMergeOutcome);

        integration.ShouldNotBeNull();
        integration!.IsConflicted.ShouldBeTrue();
        integration.Status.ShouldBe("Conflicted");
        integration.ConflictedFiles.ShouldBe(new[] { "src/Foo.cs", "src/Bar.cs" });
        integration.PreservedBranches.ShouldBe(new[] { "codespace/agent/bbb" }, "only a NON-applied contribution carries a fallbackBranch (the integrator's contract); the applied agent's branch is not in this block");
        integration.Reason.ShouldBe("a contribution conflicted while integrating");
        integration.IntegratedBranch.ShouldBeNull();
    }

    [Fact]
    public void ReadIntegration_reads_a_clean_integration_as_not_conflicted()
    {
        var clean = SupervisorOutcome.ReadIntegration("""{"integration":{"status":"Clean","integratedBranch":"codespace/integration/run/turn1","appliedCount":2,"reason":null,"outcomes":[]}}""");

        clean.ShouldNotBeNull();
        clean!.IsConflicted.ShouldBeFalse();
        clean.IntegratedBranch.ShouldBe("codespace/integration/run/turn1");
        clean.ConflictedFiles.ShouldBeEmpty();
    }

    /// <summary>A MULTI-repo integration block (resolver loop #379 S7-C): the aggregate status + a per-repo repositories[] array, each with its own outcomes. One repo conflicts, one is clean.</summary>
    private const string MultiRepoConflictedOutcome = """
        {"integration":{"status":"Conflicted","reason":"1 of 2 repositories could not be auto-combined","repositories":[
          {"repositoryId":"11111111-1111-1111-1111-111111111111","alias":"web","status":"Clean","integratedBranch":"codespace/integration/run/turn1","appliedCount":2,"reason":null,"excludedAgents":[],"outcomes":[
            {"label":"agent-a","disposition":"Applied","reason":null,"conflictedFiles":[],"fallbackBranch":null}]},
          {"repositoryId":"22222222-2222-2222-2222-222222222222","alias":"api","status":"Conflicted","integratedBranch":null,"appliedCount":0,"reason":"a contribution conflicted while integrating","excludedAgents":[],"outcomes":[
            {"label":"agent-b","disposition":"Conflicted","reason":"textual conflict","conflictedFiles":["api/Svc.cs","api/Dto.cs"],"fallbackBranch":"codespace/agent/b-api"}]}
        ]}}
        """;

    [Fact]
    public void ReadIntegration_aggregates_conflicts_across_a_multi_repo_block()
    {
        // S7-C — a multi-repo conflict is legible off the SAME ReadIntegration the single-repo path feeds: the aggregate
        // status is Conflicted, and the conflicted files + preserved branches are unioned across every repo's outcomes,
        // so the decider's existing conflict rendering shows the multi-repo conflict without any decider change.
        var integration = SupervisorOutcome.ReadIntegration(MultiRepoConflictedOutcome);

        integration.ShouldNotBeNull();
        integration!.IsConflicted.ShouldBeTrue("the aggregate status is Conflicted when ANY repo conflicts");
        integration.Status.ShouldBe("Conflicted");
        integration.Reason.ShouldBe("1 of 2 repositories could not be auto-combined");
        integration.ConflictedFiles.ShouldBe(new[] { "api/Svc.cs", "api/Dto.cs" }, "conflicted files are unioned across every repo's outcomes");
        integration.PreservedBranches.ShouldBe(new[] { "codespace/agent/b-api" }, "the conflicted repo's preserved branch is surfaced for the resolver");
        integration.IntegratedBranch.ShouldBeNull("a multi-repo block has no single integrated branch — the per-repo branches live in repositories[] (S7-D)");
    }

    [Fact]
    public void ReadIntegration_reads_an_all_clean_multi_repo_block_as_not_conflicted()
    {
        var clean = SupervisorOutcome.ReadIntegration("""
            {"integration":{"status":"Clean","reason":null,"repositories":[
              {"repositoryId":"11111111-1111-1111-1111-111111111111","alias":"web","status":"Clean","integratedBranch":"codespace/integration/run/turn1/web","outcomes":[{"label":"a","disposition":"Applied","conflictedFiles":[],"fallbackBranch":null}]},
              {"repositoryId":"22222222-2222-2222-2222-222222222222","alias":"api","status":"Clean","integratedBranch":"codespace/integration/run/turn1/api","outcomes":[{"label":"a","disposition":"Applied","conflictedFiles":[],"fallbackBranch":null}]}
            ]}}
            """);

        clean.ShouldNotBeNull();
        clean!.IsConflicted.ShouldBeFalse("all repos integrated cleanly");
        clean.ConflictedFiles.ShouldBeEmpty();
        clean.PreservedBranches.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"planned":[]}""")]            // a merge/plan outcome with no integration block
    [InlineData("""{"integration":{"reason":"x"}}""")]  // integration object but no status
    [InlineData("""{"integration":"x"}""")]            // integration present but not an object
    [InlineData("""{"integration":{"status":5}}""")]    // status present but not a string
    public void ReadIntegration_returns_null_when_absent_or_malformed(string? outcomeJson)
    {
        SupervisorOutcome.ReadIntegration(outcomeJson).ShouldBeNull();
    }

    /// <summary>
    /// A rejected spawn/retry reached the tape before this block existed, but only through the raw-jsonb fallback —
    /// undifferentiated from any other entry, while every comparable outcome (a conflicted merge, a server-authored
    /// publish, a tier escalation) already got a named block. Live run 30809950520 showed what that costs: the model
    /// re-authored the SAME no-subtaskId retry EIGHT times across turns 3-9 until the no-progress bound killed the
    /// run. The futility line is the load-bearing part — a reason alone reads as commentary, and a decider that
    /// treats it as commentary re-sends the same decision.
    /// </summary>
    [Fact]
    public void The_user_prompt_renders_a_rejected_decision_as_an_actionable_correction()
    {
        const string rejected = """{"retry":"rejected","reason":"the retry decision named no subtaskId — a retry must name the plan-local subtask id to re-run"}""";

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 4, Decision(SupervisorDecisionKinds.Retry, 3, rejected)));

        prompt.ShouldContain("REJECTED by the server", Case.Insensitive, "the refusal is framed as a correction, not buried in raw jsonb");
        prompt.ShouldContain("named no subtaskId", Case.Insensitive, "the server's own reason reaches the model verbatim");
        prompt.ShouldContain("staged NOTHING", Case.Insensitive, "the model must know the turn produced no work, not merely that something was 'rejected'");
        prompt.ShouldContain("will be rejected again", Case.Insensitive, "the futility of re-sending the same shape is the line that has to change the next decision");
    }

    [Fact]
    public void An_accepted_decision_does_NOT_render_the_rejection_block()
    {
        // Behaviour-preserving: only a decision the server actually refused gets the correction block.
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 4, Decision(SupervisorDecisionKinds.Retry, 3, """{"agentRunIds":[],"agentCount":0,"note":"no subtasks to spawn"}""")));

        prompt.ShouldNotContain("REJECTED by the server", Case.Insensitive, "a zero-agent spawn that was ACCEPTED is not a refusal — calling it one would teach the model to fix a defect that is not there");
    }

    /// <summary>
    /// A spawn whose dependency staging WITHHELD units reached the prompt as one raw-jsonb line — indistinguishable
    /// from a spawn that simply had nothing to do, so the model could not tell that a planned unit is still owed.
    /// </summary>
    [Fact]
    public void The_user_prompt_names_the_subtasks_a_spawn_withheld()
    {
        const string blockedOutcome = """{"agentRunIds":[],"agentCount":0,"blockedSubtasks":[{"subtaskId":"dependent","reason":"producer p1 recorded a diff but neither a branch nor a patch was captured for it"}]}""";

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 4, Decision(SupervisorDecisionKinds.Spawn, 3, blockedOutcome)));

        prompt.ShouldContain("WITHHELD by the server", Case.Insensitive, "a withheld unit is framed as such, not buried in raw jsonb");
        prompt.ShouldContain("dependent", Case.Insensitive, "the model must know WHICH planned unit it is still owed");
        prompt.ShouldContain("neither a branch nor a patch", Case.Insensitive, "the server's own reason reaches the model verbatim");
        prompt.ShouldContain("withhold them again", Case.Insensitive, "re-sending the same spawn is futile — the block is about the producers' work, not this attempt");
    }

    /// <summary>
    /// The asymmetry this closes: <c>BuildBlockedSpawnOutcome</c> deliberately writes the SAME integration shape a
    /// merge writes — its doc-comment says it does so "so the EXISTING resolve verb can reconcile a staging-time
    /// conflict exactly as it reconciles a merge-time one" — and <c>FindMostRecentConflictDecision</c> honours that
    /// with no kind filter. Only the PROMPT rendering was gated to Merge, so the resolve path could act on a conflict
    /// the model was never shown as one, and would therefore never choose 'resolve' for it.
    /// </summary>
    [Fact]
    public void A_staging_time_conflict_on_a_spawn_renders_the_same_conflict_block_a_merge_gets()
    {
        const string stagingConflict = """{"agentRunIds":[],"agentCount":0,"blockedSubtasks":[{"subtaskId":"dependent","reason":"the producers' work could not be auto-integrated onto one branch"}],"integration":{"status":"Conflicted","reason":"the producers' work could not be auto-integrated onto one branch","outcomes":[{"label":"dependent","fallbackBranch":"codespace/agent/p1","conflictedFiles":["src/App.cs"]}]}}""";

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 4, Decision(SupervisorDecisionKinds.Spawn, 3, stagingConflict)));

        prompt.ShouldContain("INTEGRATION CONFLICTED", Case.Insensitive, "a staging-time conflict is a conflict — the model must see it as one, exactly as it sees a merge-time one");
        prompt.ShouldContain("src/App.cs", Case.Insensitive, "the conflicted files are named");
        prompt.ShouldContain("codespace/agent/p1", Case.Insensitive, "the preserved branch is named — the resolver's input");
        prompt.ShouldContain("choose 'resolve'", Case.Insensitive, "the verb the server can actually act on is offered, which is the whole point of writing the merge-shaped block");
        prompt.ShouldNotContain("- merge:", Case.Insensitive, "it was a SPAWN — labelling it 'merge' would describe an action the model never took");
    }

    [Fact]
    public void The_user_prompt_renders_a_conflicted_merge_as_a_legible_actionable_block()
    {
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, MergeDecision(ConflictedMergeOutcome)));

        prompt.ShouldContain("INTEGRATION CONFLICTED", Case.Insensitive, "the decider sees the conflict framed as actionable, not buried in raw jsonb");
        prompt.ShouldContain("src/Foo.cs", Case.Insensitive);
        prompt.ShouldContain("src/Bar.cs", Case.Insensitive, "the conflicted files are named so a resolver knows what to reconcile");
        prompt.ShouldContain("codespace/agent/bbb", Case.Insensitive, "the preserved branches are named — the resolver's inputs");
        // The conflict block must name the VERB, not describe the server's mechanics in another verb's words: the
        // M0 golden eval (2026-07-11) proved a model picks its verb off this copy, and a model that emits 'spawn'
        // here needs a plan-local subtask id it does not have for a reconciliation.
        prompt.ShouldContain("choose 'resolve'", Case.Insensitive, "the reconciliation move is named by its own verb");
        prompt.ShouldNotContain("To resolve: spawn ONE agent", Case.Insensitive, "the verb collision is gone — 'spawn' must not be the word offered for the resolve move");
        prompt.ShouldContain("stop to leave the conflict for a human", Case.Insensitive, "the fail-safe move is offered too");
    }

    [Fact]
    public void A_clean_merge_does_NOT_render_the_conflict_block()
    {
        // Only a CONFLICTED integration gets the conflict block; a clean merge gets the bounded completed block.
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, MergeDecision("""{"integration":{"status":"Clean","integratedBranch":"b","outcomes":[]}}""")));

        prompt.ShouldNotContain("INTEGRATION CONFLICTED");
    }

    [Fact]
    public void A_clean_merge_renders_bounded_operational_facts_without_reinjecting_its_patch()
    {
        var agentRunId = Guid.NewGuid();
        var outcome = JsonSerializer.Serialize(new
        {
            merged = new[] { new { agentRunId, status = "Succeeded", producedBranch = "codespace/agent/a", patch = new string('P', 200_000) + "UNBOUNDED_PATCH_MARKER" } },
            count = 1,
            synthesis = new { text = "combined" },
            integration = new { status = "Clean", integratedBranch = "codespace/integration/run/turn2", outcomes = Array.Empty<object>() },
        }, AgentJson.Options);

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, MergeDecision(outcome)));

        prompt.ShouldContain("merge: COMPLETED", Case.Insensitive);
        prompt.ShouldContain(agentRunId.ToString(), customMessage: "the exact durable contributors remain visible to the next decision");
        prompt.ShouldContain("codespace/integration/run/turn2", customMessage: "the reviewable integration head remains visible");
        prompt.ShouldNotContain("UNBOUNDED_PATCH_MARKER", customMessage: "a persisted full patch is execution evidence, not next-turn prompt input");
        prompt.Length.ShouldBeLessThan(20_000, "one large merge outcome must not dominate the supervisor context window");
    }

    [Fact]
    public void A_gate_off_merge_still_renders_a_bounded_fold_without_raw_outcome_json()
    {
        var agentRunId = Guid.NewGuid();
        var outcome = JsonSerializer.Serialize(new
        {
            merged = new[] { new { agentRunId, status = "Succeeded", patch = "RAW_PATCH_MUST_NOT_RENDER" } },
            count = 1,
            synthesisInstruction = "finalize",
        }, AgentJson.Options);

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, MergeDecision(outcome)));

        prompt.ShouldContain("merge: COMPLETED", Case.Insensitive);
        prompt.ShouldContain(agentRunId.ToString());
        prompt.ShouldContain("integration not requested", Case.Insensitive);
        prompt.ShouldNotContain("RAW_PATCH_MUST_NOT_RENDER");
        prompt.ShouldNotContain("synthesisInstruction", customMessage: "the raw persistence schema never leaks into the model prompt");
    }

    // ── DC-2d: a prior `publish` decision (server-authored only) renders legibly, not as raw jsonb ──

    [Fact]
    public void The_user_prompt_explains_a_prior_publish_decision_was_never_the_models_own_choice()
    {
        var publish = Decision(SupervisorDecisionKinds.Publish, sequence: 2, outcomeJson: PublishOutcome(RoomPullRequestDisposition.Opened, number: 42, url: "https://example.test/pr/42"));

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, publish));

        prompt.ShouldContain("did not choose this", Case.Insensitive, "publish is server-authored only — the model must not think it decided to publish");
        prompt.ShouldContain("opened #42", Case.Insensitive);
        prompt.ShouldContain("https://example.test/pr/42");
    }

    [Fact]
    public void The_user_prompt_names_a_failed_publish_target_plainly()
    {
        var publish = Decision(SupervisorDecisionKinds.Publish, sequence: 2, outcomeJson: PublishOutcome(RoomPullRequestDisposition.Failed, error: "the provider rejected the request"));

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, publish));

        prompt.ShouldContain("FAILED", Case.Insensitive);
        prompt.ShouldContain("the provider rejected the request", Case.Insensitive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void The_user_prompt_never_throws_on_a_publish_decision_with_malformed_outcome(string? outcomeJson)
    {
        var publish = Decision(SupervisorDecisionKinds.Publish, sequence: 2, outcomeJson: outcomeJson);

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, publish));

        prompt.ShouldContain("no targets resolved", Case.Insensitive, "a malformed/absent outcome degrades to an explicit empty state, never a raw jsonb dump or a throw");
    }

    [Theory]
    [InlineData(RoomPullRequestDisposition.AlreadyOpened, "already open #42")]
    [InlineData(RoomPullRequestDisposition.Skipped, "skipped")]
    public void The_user_prompt_names_every_publish_disposition_plainly(RoomPullRequestDisposition disposition, string expectedFragment)
    {
        var publish = Decision(SupervisorDecisionKinds.Publish, sequence: 2, outcomeJson: PublishOutcome(disposition, number: 42, error: "no source branch"));

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, publish));

        prompt.ShouldContain(expectedFragment, Case.Insensitive);
    }

    [Fact]
    public void The_system_prompt_offers_the_resolve_or_stop_choice_and_forbids_an_unverified_resolution()
    {
        var system = LlmSupervisorDecider.SystemPromptForTest;

        system.ShouldContain("INTEGRATION CONFLICTED", Case.Insensitive);
        system.ShouldContain("never accept an unverified resolution", Case.Insensitive, "the safety floor is in the rails — no blind accept");
        system.ShouldContain("choose 'resolve'", Case.Insensitive, "the conflict rail names the verb the schema actually accepts");
        system.ShouldNotContain("you may spawn ONE agent", Case.Insensitive, "the rail must not offer 'spawn' as the word for the resolve move");
    }

    [Fact]
    public void The_turn_roster_names_every_verb_the_schema_accepts_when_nothing_is_masked()
    {
        // The vocabulary a verb is missing from reads to the model as a verb that does not exist — 'resolve' was
        // once omitted while the schema accepted it, so the only guidance pointing at a conflicted integration named
        // a DIFFERENT verb (the M0 verb-off-the-copy failure class). The obligation moved with the roster: it is now
        // the per-TURN block that must name every verb, on a turn where the mask withholds none of them. The verbs
        // are read off the SCHEMA rather than listed here, so a new one cannot be added to the contract and quietly
        // left off the menu — which is exactly how 'amend_acceptance' was missing while a golden graded it.
        var unmasked = Context(turnNumber: 3, AmendableSpawn(sequence: 1), MergeDecision(ConflictedMergeOutcome)) with { MaxResolveAttempts = 2 };
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(unmasked);

        var verbs = SupervisorDecisionSchema.ResponseSchema.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()!).ToList();

        verbs.Count.ShouldBe(8, "the schema's verb enum is a pinned commit contract — a change to it is a change to this menu");

        foreach (var verb in verbs)
            prompt.ShouldContain($"- {verb} — ", Case.Sensitive, $"the turn's roster must name {verb} — the schema accepts it and nothing on this tape withholds it");

        prompt.ShouldNotContain(SupervisorActionMask.Header, Case.Sensitive, "…and nothing may be withheld on this tape, or the assertion above is measuring a different turn");
    }

    /// <summary>A spawn whose one unit's check COULD NOT RUN — the shape that leaves <c>amend_acceptance</c> genuinely available. Folded by the production folder, so a fixture the server could not produce cannot make this green.</summary>
    private static SupervisorPriorDecision AmendableSpawn(long sequence)
    {
        var unit = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "codespace/agent/s1", AcceptancePassed = false, AcceptanceDetail = "grade-error: npm: command not found" };
        var outcome = SupervisorOutcome.FoldAgentResults(JsonSerializer.Serialize(new { agentRunIds = new[] { unit.AgentRunId }, agentCount = 1 }, AgentJson.Options), new[] { unit });

        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtaskIds = new[] { "s1" } }, AgentJson.Options), OutcomeJson = outcome,
        };
    }

    [Fact]
    public async Task A_deployment_with_no_structured_provider_fails_closed_to_a_terminal_stop()
    {
        var decider = new LlmSupervisorDecider(new FakeRegistry(structured: null), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("no model → a clean one-turn no-op stop, never a crash");
    }

    [Fact]
    public async Task The_decider_grounds_its_prompt_in_the_repo_tree_at_the_runs_immutable_base()
    {
        // S2 (G1): the brain plans over the repository's ACTUAL top-level tree, listed at the S1 pin — the SAME
        // tree every spawned agent materializes. The lookup keys on the profile repo + pin; the summary lands in
        // the user prompt; no repo ⇒ no lookup ⇒ byte-identical.
        var repoId = Guid.NewGuid();
        var grounding = new RecordingRepoGrounding();
        var client = new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = OnePlannedSubtask() });
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), grounding, NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context() with { AgentProfile = new CodeSpace.Messages.Dtos.Agents.SupervisorAgentProfile { RepositoryId = repoId, PinnedSha = "abc123def456" } }, CancellationToken.None);

        grounding.RepositoryId.ShouldBe(repoId);
        grounding.Reference.ShouldBe("abc123def456", "the listing is anchored at the run's immutable base pin, never a drifting tip");
        client.LastUserPrompt.ShouldNotBeNull();
        client.LastUserPrompt!.ShouldContain("Repository top-level layout", customMessage: "the grounding summary reaches the brain's prompt");
    }

    [Fact]
    public async Task An_unpinned_run_grounds_at_the_operators_branch_never_a_drifting_default_tip()
    {
        // Scan M2: with no pin, the operator's launch-pinned branch is the next-most-stable anchor — grounding on
        // the default branch would show the brain a DIFFERENT tree than the one its agents clone.
        var grounding = new RecordingRepoGrounding();
        var decider = new LlmSupervisorDecider(new FakeRegistry(new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = OnePlannedSubtask() })), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), grounding, NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context() with { AgentProfile = new CodeSpace.Messages.Dtos.Agents.SupervisorAgentProfile { RepositoryId = Guid.NewGuid(), BaseRef = "release/2.x" } }, CancellationToken.None);

        grounding.Reference.ShouldBe("release/2.x");
    }

    [Fact]
    public async Task A_run_without_a_repo_never_looks_up_grounding()
    {
        var grounding = new RecordingRepoGrounding();
        var decider = new LlmSupervisorDecider(new FakeRegistry(new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = OnePlannedSubtask() })), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), grounding, NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(), CancellationToken.None);

        grounding.RepositoryId.ShouldBeNull("an analysis-only run has nothing to ground against — no provider call, byte-identical prompt");
    }

    [Fact]
    public void The_prompt_renders_the_grounding_section_only_when_present()
    {
        var grounded = LlmSupervisorDecider.BuildUserPromptForTest(Context() with { RepoGrounding = "Repository top-level layout for org/repo at this run's immutable base (abc123def456)." });
        grounded.ShouldContain("Repository top-level layout for org/repo at this run's immutable base (abc123def456).");

        LlmSupervisorDecider.BuildUserPromptForTest(Context()).ShouldNotContain("Repository top-level layout", customMessage: "no grounding ⇒ no section ⇒ byte-identical prompt");
    }

    [Fact]
    public async Task A_run_with_no_selected_brain_model_fails_closed_to_a_terminal_stop()
    {
        // supervisorModelId is REQUIRED — the operator must pick the brain model; the supervisor never guesses its own.
        var decider = new LlmSupervisorDecider(new FakeRegistry(new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = OnePlannedSubtask() })), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context() with { SupervisorModelId = null }, CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("no brain model selected → a clean fail-closed stop");
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("outcome").GetString().ShouldBe("no-model");
    }

    [Fact]
    public async Task A_run_whose_credentialed_pool_has_no_model_fails_closed_to_a_terminal_stop()
    {
        // The structured provider IS registered, but the team's credentialed-model pool yields nothing (none
        // configured, or none within the allowed pool) → the brain stops cleanly rather than guess a model or key.
        var decider = new LlmSupervisorDecider(new FakeRegistry(new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = OnePlannedSubtask() })), FakeSelector.Empty(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("an empty pool → a clean fail-closed stop");
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("outcome").GetString().ShouldBe("no-model");
    }

    [Theory]
    [InlineData("")]      // an object with a blank kind
    [InlineData("   ")]   // whitespace-only kind
    [InlineData(null)]    // an object with NO kind at all
    public async Task A_non_conformant_model_response_fails_closed_to_a_terminal_stop(string? kind)
    {
        // The structured client returned a reply that does NOT conform to the decision schema (no usable kind). This is a
        // model-side MISS — handled the SAME way as no-model and an unknown verb: a clean fail-closed stop, NEVER a crash
        // that would fault the durable run. (Closes the one gap in the decider's "fail closed, never crash" contract; the
        // same guard covers a reply that does not deserialize to a decision at all.)
        var decider = Decider(new SupervisorModelDecision { Kind = kind! });

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("a non-conformant model reply → a clean fail-closed stop, never an unhandled crash mid-run");
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("outcome").GetString().ShouldBe("no-decision");
    }

    [Fact]
    public async Task A_malformed_shape_reply_also_fails_closed_to_a_terminal_stop()
    {
        // The gateway returned a structurally WRONG reply (a bare JSON string, not a decision object) — deserialization
        // would throw. The decider must still fail closed to a clean stop, never crash the durable run on a degraded reply.
        var raw = JsonSerializer.SerializeToElement("not a decision object");
        var decider = new LlmSupervisorDecider(new FakeRegistry(new RawJsonStructuredClient(raw)), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("a malformed-shape reply → a clean fail-closed stop, never a crash");
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("outcome").GetString().ShouldBe("no-decision");
    }

    [Theory]
    [InlineData(LlmErrorCategory.Malformed)]              // schema-invalid after a re-ask / no extractable JSON
    [InlineData(LlmErrorCategory.ContextLengthExceeded)] // the prompt + tape overflowed the model's window
    [InlineData(LlmErrorCategory.ContentFiltered)]       // the gateway blocked the reply on policy
    [InlineData(LlmErrorCategory.BadRequest)]            // a 400 the gateway rejected even on the prompt-only floor
    public async Task A_model_capability_category_transport_failure_fails_closed_to_a_terminal_stop(LlmErrorCategory category)
    {
        // The gateway THREW a typed LlmApiException for a MODEL-side reason (it could not produce a usable structured
        // decision) — the decider fails closed to a clean stop, NEVER crashing the durable run. This is THE canonical
        // capability miss ("no conformant decision"): it must end as a clean stop so the whole-loop reads it as a
        // CapabilityMiss (non-gating), not a code fault.
        var decider = new LlmSupervisorDecider(new FakeRegistry(new ThrowingStructuredClient(category)), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue($"a {category} capability miss → a clean fail-closed stop, never a crash");
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("outcome").GetString().ShouldBe("no-decision");
    }

    [Theory]
    [InlineData(LlmErrorCategory.Transient)]    // a 5xx / 408 / client-side timeout / connection reset
    [InlineData(LlmErrorCategory.RateLimited)]  // a 429
    [InlineData(LlmErrorCategory.AuthFailed)]   // a 401/403 — a rotated/revoked credential
    public async Task An_infra_category_transport_failure_propagates_so_the_engine_fails_the_run(LlmErrorCategory category)
    {
        // A genuine gateway/credential INFRA fault is NOT swallowed into a stop — it PROPAGATES so the engine fails the
        // run (visible + rerunnable) and the live-gate treats it as non-gating infra (consistent with the decision-eval
        // lane), never a silent "completed" no-op that masks an outage.
        var decider = new LlmSupervisorDecider(new FakeRegistry(new ThrowingStructuredClient(category)), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await Should.ThrowAsync<LlmApiException>(() => decider.DecideAsync(Context(), CancellationToken.None));
    }

    [Fact]
    public async Task A_throttled_brain_whose_substitute_cannot_decide_surfaces_the_throttle_instead_of_stopping()
    {
        // Real-model run 33930904059 (arm reacts_to_a_failed_subtask_by_retrying, 3/3): the brain 429'd, the pool
        // failover hopped onto a pool row that is not a decision model at all, and its unbindable reply became a clean
        // terminal stop at turn 0 — no retry, no park, and the whole-loop gate scored the throttle a CapabilityMiss.
        // The stop's own words ("the supervisor model returned a response that did not conform") are a claim about a
        // model that was never reached, so the decider must surface the fault the hop was FORCED by.
        var throttled = new LlmApiException("TestSupervisor", 429, LlmErrorCategory.RateLimited, "No deployments available");
        var pool = new ThrottledBrainWithSubstitute(throttled);
        var decider = new LlmSupervisorDecider(new FakeRegistry(pool), FakeSelector.WithTwoBrainRows(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var ex = await Should.ThrowAsync<LlmApiException>(() => decider.DecideAsync(Context(), CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.RateLimited, "the run's story is the throttle on its own brain — the substitute's shrug is not a capability verdict");
        ex.StatusCode.ShouldBe(429);
        ex.InnerException.ShouldBeSameAs(throttled, "the fault is RE-RAISED, not re-thrown: a bare `throw ex` would overwrite the original's stack with this line and lose the frame that says which call was throttled");
        pool.SubstituteCalls.ShouldBe(2, "the repair round-trip was spent FIRST — the throttle is surfaced only once a second reply also failed to bind");
    }

    [Fact]
    public async Task A_hop_whose_substitute_only_NEARLY_misses_is_repaired_rather_than_parked()
    {
        // A hop does not always land on a non-decision model — the pool's other rows are usually weaker models that CAN
        // decide and merely fumble the shape. That near-miss is precisely what the bounded repair round-trip exists to
        // correct, so it must run BEFORE the throttle floor: surfacing the fault first would park a run one cheap
        // re-ask away from a real decision, and spend a whole 24h window on an alternate that was already answering.
        var pool = new ThrottledBrainWithSubstitute(
            brain: new[] { ThrottledBrainWithSubstitute.Throttles(new LlmApiException("TestSupervisor", 429, LlmErrorCategory.RateLimited, "throttled")) },
            substitute: new[] { ThrottledBrainWithSubstitute.Unbindable(), ThrottledBrainWithSubstitute.Decides(SubstitutePlan) });
        var decider = new LlmSupervisorDecider(new FakeRegistry(pool), FakeSelector.WithTwoBrainRows(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan, "the repaired reply IS a decision — a run that can keep moving must never be parked as an outage");
        pool.SubstituteCalls.ShouldBe(2, "the repair was actually spent on the alternate, not skipped");
    }

    [Fact]
    public async Task A_hop_followed_by_a_CLEAN_retry_stops_on_the_reply_in_hand_never_the_stale_throttle()
    {
        // The truncated-completion retry and the repair each issue a FRESH pool call with its own hop — or none. Here
        // the retry reaches the run's OWN brain with the throttle already over, so the unbindable reply it brings back
        // is a real non-conformance by the model the operator chose. Parking on the earlier hop's 429 would suspend a
        // run for an outage that is over AND hide the model's actual miss behind an infra skip, so the cause must be
        // read off the completion in hand, never captured before those calls run.
        var pool = new ThrottledBrainWithSubstitute(
            brain: new[] { ThrottledBrainWithSubstitute.Throttles(new LlmApiException("TestSupervisor", 429, LlmErrorCategory.RateLimited, "slow down")), ThrottledBrainWithSubstitute.Unbindable() },
            substitute: new[] { ThrottledBrainWithSubstitute.Unbindable(truncated: true) });
        var decider = new LlmSupervisorDecider(new FakeRegistry(pool), FakeSelector.WithTwoBrainRows(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("outcome").GetString()
            .ShouldBe("no-decision", "the last reply came from the brain itself on a healthy wire — that is a capability verdict, and it must gate as one");
        pool.BrainCalls.ShouldBe(3, "the raised-budget retry AND the repair both reached the brain — the hop happened only on the first call");
    }

    [Fact]
    public async Task A_hop_that_happens_only_on_the_raised_budget_RETRY_still_surfaces_the_throttle()
    {
        // The mirror image, and the half a captured cause silently drops: the FIRST call was clean, so there was no
        // fault to capture up front — the throttle arrives later, on the retry's own pool call, and the reply finally
        // in hand is a substitute's after all. Reading the cause at the use site is what sees it.
        var throttled = new LlmApiException("TestSupervisor", 429, LlmErrorCategory.RateLimited, "slow down", retryAfter: TimeSpan.FromSeconds(7));
        var pool = new ThrottledBrainWithSubstitute(
            brain: new[] { ThrottledBrainWithSubstitute.Unbindable(truncated: true), ThrottledBrainWithSubstitute.Throttles(throttled) },
            substitute: new[] { ThrottledBrainWithSubstitute.Unbindable() });
        var decider = new LlmSupervisorDecider(new FakeRegistry(pool), FakeSelector.WithTwoBrainRows(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var ex = await Should.ThrowAsync<LlmApiException>(() => decider.DecideAsync(Context(), CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.RateLimited);
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(7), "the provider's own backoff hint rides through — it is what the bounded retry waits on");
        pool.SubstituteCalls.ShouldBeGreaterThan(0, "the retry's call DID hop — otherwise this pins nothing about a cause read late");
    }

    /// <summary>The decision a weaker pool alternate produces once its near-miss is repaired — a real, executable plan.</summary>
    private static readonly SupervisorModelDecision SubstitutePlan = new()
    {
        Kind = SupervisorDecisionKinds.Plan,
        Plan = new SupervisorPlanPayload { Goal = "ship", Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "Audit", Instruction = "audit it" } } },
    };

    [Fact]
    public async Task A_substitute_that_CAN_decide_still_answers_normally_after_a_hop()
    {
        // The failover's whole point (#1737/#1738) is that a usable alternate keeps the run moving. The rule above is
        // scoped to an UNUSABLE answer, so a substitute that authors a real decision must be unaffected by it.
        var pool = new ThrottledBrainWithSubstitute(new LlmApiException("TestSupervisor", 429, LlmErrorCategory.RateLimited, "throttled"),
            substituteDecision: new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = new SupervisorPlanPayload { Goal = "ship", Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "Audit", Instruction = "audit it" } } } });
        var decider = new LlmSupervisorDecider(new FakeRegistry(pool), FakeSelector.WithTwoBrainRows(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan);
        decision.IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public async Task A_throttled_pool_is_retried_with_backoff_then_left_parkable_never_stopped()
    {
        // The composition the run depends on: the decider surfaces the throttle, the bounded in-call retry rides the
        // short blips (cause-aware backoff), and what finally escapes is a PARKABLE fault — so AgentSupervisorNode's
        // infra park suspends the run resumably instead of the whole arc ending on a stop nobody can resume.
        var pool = new ThrottledBrainWithSubstitute(new LlmApiException("TestSupervisor", 429, LlmErrorCategory.RateLimited, "throttled"));
        var decider = new LlmSupervisorDecider(new FakeRegistry(pool), FakeSelector.WithTwoBrainRows(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);
        var options = new SupervisorDecisionRetryOptions { MaxAttempts = 3, BaseBackoff = TimeSpan.Zero };

        var ex = await Should.ThrowAsync<LlmApiException>(() => new RetryingSupervisorDeciderDecorator(decider, options, NullLogger<RetryingSupervisorDeciderDecorator>.Instance).DecideAsync(Context(), CancellationToken.None));

        pool.BrainCalls.ShouldBe(6, "the whole in-call budget was spent on the throttle before it was allowed to escape — 3 attempts, each spending the primary call plus the ONE bind repair the throttle floor now runs first");
        CodeSpace.Core.Services.Supervisor.SupervisorInfraPark.IsParkable(ex.Category).ShouldBeTrue("what escapes must be a fault the node parks on — the resumable ending, not a terminal stop");
    }

    /// <summary>
    /// A pool of exactly the shape that broke: the run's own brain and the alternate the failover hops to, each driven
    /// by its OWN script of per-call behaviours. One fake plays both because the failover resolves a candidate by its
    /// pool ROW and both rows name this client; the LAST scripted entry repeats, so "throttles every call" is a
    /// one-entry script. Per-call scripting is what lets a test place a hop on a SPECIFIC round-trip — the decider
    /// makes up to three (the primary call, the truncated-budget retry, the bind repair) and each is a fresh pool call.
    /// </summary>
    private sealed class ThrottledBrainWithSubstitute : ILLMClient, IStructuredLLMClient
    {
        private readonly IReadOnlyList<Func<StructuredLLMCompletion>> _brain;
        private readonly IReadOnlyList<Func<StructuredLLMCompletion>> _substitute;

        /// <summary>The shape the bug wore: the brain throttles EVERY call, and the alternate answers something that is not a supervisor decision — or, when given one, a real decision.</summary>
        public ThrottledBrainWithSubstitute(LlmApiException throttle, SupervisorModelDecision? substituteDecision = null)
            : this(new[] { Throttles(throttle) }, new[] { substituteDecision is { } decision ? Decides(decision) : Unbindable() }) { }

        public ThrottledBrainWithSubstitute(IReadOnlyList<Func<StructuredLLMCompletion>> brain, IReadOnlyList<Func<StructuredLLMCompletion>> substitute)
        {
            _brain = brain;
            _substitute = substitute;
        }

        public const string BrainModel = "throttled-brain";
        public const string SubstituteModel = "not-a-decision-model";

        public int BrainCalls { get; private set; }
        public int SubstituteCalls { get; private set; }

        public string Provider => "TestSupervisor";

        /// <summary>A candidate that faults on the wire — what forces the failover to hop.</summary>
        public static Func<StructuredLLMCompletion> Throttles(LlmApiException throttle) => () => throw throttle;

        /// <summary>A reply that is well-formed JSON carrying no decision kind anywhere — the shrug a non-decision pool row answers with. <paramref name="truncated"/> stamps the finish reason that buys the decider's ONE raised-budget retry.</summary>
        public static Func<StructuredLLMCompletion> Unbindable(bool truncated = false) => () => new StructuredLLMCompletion
        {
            Json = JsonSerializer.SerializeToElement(new { plan = new { title = "a planner reply — no decision kind anywhere in it" } }),
            Model = "",
            Usage = truncated ? new LlmUsage { FinishReason = "max_tokens" } : LlmUsage.None,
        };

        /// <summary>A reply that IS a bindable supervisor decision.</summary>
        public static Func<StructuredLLMCompletion> Decides(SupervisorModelDecision decision) => () => new StructuredLLMCompletion
        {
            Json = JsonSerializer.SerializeToElement(decision, AgentJson.Options),
            Model = "",
        };

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            var isBrain = request.Model == BrainModel;
            var script = isBrain ? _brain : _substitute;
            var index = isBrain ? BrainCalls++ : SubstituteCalls++;

            return Task.FromResult(script[Math.Min(index, script.Count - 1)]() with { Model = request.Model });
        }
    }

    [Fact]
    public async Task The_decider_calls_with_the_model_the_selector_picked_from_the_pool()
    {
        var fake = new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = OnePlannedSubtask() });
        var decider = new LlmSupervisorDecider(new FakeRegistry(fake), FakeSelector.WithModel("claude-opus-4-8"), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(), CancellationToken.None);

        // The brain's model is whatever the pool selector chose — there is no default; the pool/pin logic lives in
        // the selector (integration-tested), and the decider simply uses the chosen row's model id.
        fake.LastModel.ShouldBe("claude-opus-4-8");
    }

    // ── The projector maps each verb to its canonical payload ────────────────────────

    [Fact]
    public void Spawn_projects_the_subtask_ids()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Spawn,
            Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "s1", "s2" } },
        });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn);
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("subtaskIds").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void Stop_is_terminal_and_retry_merge_ask_human_are_not()
    {
        Project(SupervisorDecisionKinds.Stop, m => m with { Stop = new SupervisorStopPayload { Outcome = "done" } }).IsTerminal.ShouldBeTrue();
        Project(SupervisorDecisionKinds.Retry, m => m with { Retry = new SupervisorRetryPayload { SubtaskId = "s1" } }).IsTerminal.ShouldBeFalse();
        Project(SupervisorDecisionKinds.Merge, m => m with { Merge = new SupervisorMergePayload() }).IsTerminal.ShouldBeFalse();
        Project(SupervisorDecisionKinds.AskHuman, m => m with { AskHuman = new SupervisorAskHumanPayload { Question = "?" } }).IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_kind_projects_to_a_terminal_stop()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = "wat" });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("an unrecognized verb fails closed to a terminal stop");
    }

    [Fact]
    public void Projection_is_deterministic_in_the_model_decision()
    {
        var model = new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Spawn, Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "a" } } };

        SupervisorDecisionProjector.Project(model).PayloadJson.ShouldBe(SupervisorDecisionProjector.Project(model).PayloadJson, "same model decision → byte-identical canonical payload (the idempotency-key stability the ledger relies on)");
    }

    [Fact]
    public async Task DecideAsync_renders_the_capability_catalog_into_the_structured_request_user_prompt()
    {
        // End-to-end through DecideAsync (not just the static helper): a populated pool + a registered harness must
        // flow through BuildCapabilityCatalogAsync → the LLM request's user prompt, so the live brain is informed.
        var client = new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = OnePlannedSubtask() });
        var decider = new LlmSupervisorDecider(
            new FakeRegistry(client),
            FakeSelector.WithModelAndPool("claude-sonnet-4-5", new PoolModelInfo("metis-coder-max", "Anthropic")),
            new FakeHarnesses(new CatalogHarness("claude-code", "Anthropic", "Custom")), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(), CancellationToken.None);

        client.LastUserPrompt.ShouldNotBeNull();
        client.LastUserPrompt!.ShouldContain("metis-coder-max — Anthropic", Case.Sensitive, "the run's pool reaches the live decide prompt");
        client.LastUserPrompt.ShouldContain("claude-code — drives: Anthropic, Custom", Case.Sensitive, "the harness↔provider map reaches the live decide prompt");
    }

    [Fact]
    public async Task DecideAsync_renders_the_team_persona_pool_into_the_request_user_prompt()
    {
        // P3 — the brain authors a per-agent persona by slug, so the team's persona library must reach the live decide
        // prompt (slug + name + description), end-to-end through BuildCapabilityCatalogAsync.
        var client = new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = OnePlannedSubtask() });
        var decider = new LlmSupervisorDecider(
            new FakeRegistry(client),
            FakeSelector.WithModel(),
            new FakeHarnesses(new CatalogHarness("claude-code", "Anthropic", "Custom")),
            FakePersonas.With(("security-reviewer", "Security Reviewer", "Audits for vulnerabilities")), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(), CancellationToken.None);

        client.LastUserPrompt.ShouldNotBeNull();
        client.LastUserPrompt!.ShouldContain("security-reviewer — Security Reviewer — Audits for vulnerabilities", Case.Sensitive, "the team's personas reach the live decide prompt so the brain can author one per agent");
    }

    // ── G②: the bind-salvage ladder — lenient id proposals → one repair call → a precise stop ──

    [Fact]
    public async Task A_repo_name_proposal_degrades_to_no_override_never_a_dead_decision()
    {
        // The exact miss that killed a real run: schema-valid `"repositoryId": "backend"` (a NAME where a uuid
        // belongs) — the lenient converter drops the FIELD (the run-level repo applies), never the whole decision.
        var reply = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["scan"],"agents":[{"subtaskId":"scan","role":"scanner","repositoryId":"backend"}]},"rationale":{"why":"fan out the ready subtask"}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(reply);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "one bad optional leaf must not kill the decision");
        decision.PayloadJson.ShouldNotContain("backend", customMessage: "the unhonorable proposal is dropped — the server-clamped run-level repository applies");
        decision.PayloadJson.ShouldContain("scanner", customMessage: "the rest of the per-agent spec survives");
        client.Requests.Count.ShouldBe(1, "a leniency save needs no repair round-trip");
    }

    [Fact]
    public async Task An_unbindable_reply_buys_ONE_repair_call_carrying_the_bind_error()
    {
        var broken = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":"oops"}}""").RootElement;   // a string where an array belongs — validator-shaped drift
        var repaired = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["oops"]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(broken, repaired);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "the repaired reply lands as the decision");
        client.Requests.Count.ShouldBe(2, "exactly one bounded repair round-trip");
        client.Requests[1].UserPrompt.ShouldContain("$.spawn", customMessage: "the repair prompt NAMES the bind path so the model fixes the right leaf");
        client.Requests[1].UserPrompt.ShouldContain("\"oops\"", customMessage: "the model repairs its OWN reply, not a fresh decide");
        client.Requests[1].SystemPrompt.ShouldContain("corrected decision JSON", customMessage: "repair-only framing — same intent, no new decisions");
    }

    // ── The coherence gate: a BOUND decision whose kind names a payload it doesn't carry buys ONE repair ──
    // (the schema-inexpressible invariant — top-level `required` can't be conditional on `kind`, and the forced-
    // tool wire steers without constraining, so `{"kind":"spawn"}` is schema-valid, binds cleanly, and would
    // otherwise project to an empty payload the executor rejects a full turn later)

    [Fact]
    public async Task A_payload_less_spawn_buys_ONE_repair_call_echoing_the_raw_reply()
    {
        var bare = JsonDocument.Parse("""{"kind":"spawn","rationale":{"why":"fan out st-1"}}""").RootElement;
        var repaired = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["st-1"]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare, repaired);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "the repaired reply lands as the decision");
        decision.PayloadJson.ShouldContain("st-1", customMessage: "the repaired payload is the one projected — never the substituted empty one");
        client.Requests.Count.ShouldBe(2, "exactly one bounded repair round-trip");
        client.Requests[1].UserPrompt.ShouldContain("fan out st-1", customMessage: "the model repairs its OWN raw reply — the echo is what lets it see the defect instead of inferring it");
        client.Requests[1].UserPrompt.ShouldContain("'spawn' object", customMessage: "the defect names the exact sub-object the payload must ride in");
        client.Requests[1].SystemPrompt.ShouldContain("complete decision JSON object", customMessage: "correction framing — a COMPLETE decision, and (unlike the bind repair) no clause forbidding the different action the prompt invites");
    }

    [Theory]
    [InlineData("""{"kind":"spawn","subtaskIds":["st-1"]}""", SupervisorDecisionKinds.Spawn, "st-1")]
    [InlineData("""{"kind":"retry","subtaskId":"st-1","revisedInstruction":"try harder"}""", SupervisorDecisionKinds.Retry, "st-1")]
    [InlineData("""{"kind":"stop","outcome":"completed","summary":"shipped it"}""", SupervisorDecisionKinds.Stop, "shipped it")]
    public async Task A_top_level_flattened_payload_is_nested_deterministically_with_no_repair_round_trip(string flattenedJson, string kind, string marker)
    {
        // The live-probed defect shape (2026-08-07, still dominant on 2026-08-19: 68 in one eval run — 46 spawn, 21
        // retry, 1 stop): the payload's fields EXIST, under their own names, at the decision's top level where nothing
        // reads them. Every value the payload needs is therefore already in the FIRST reply, so the nesting is corrected
        // deterministically by SupervisorDecisionPayloadLift and the model is not asked again. The fake supplies only ONE
        // reply on purpose: a second request would dequeue nothing and the assertion below would be vacuous.
        var client = new SequencedRawJsonStructuredClient(JsonDocument.Parse(flattenedJson).RootElement);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(kind);
        decision.PayloadJson.ShouldContain(marker, customMessage: "the correctly-nested payload lands, carrying the values the model authored");
        client.Requests.Count.ShouldBe(1, "a flattened payload is a nesting error, not missing information — spending a round-trip to recover what the first reply already carried is waste, and it only ever worked while the repair model complied");
    }

    [Fact]
    public async Task A_payload_that_is_genuinely_absent_still_buys_the_repair_round_trip()
    {
        // The other half, and the reason the lift declines rather than guesses: no payload field is present anywhere, so
        // nesting cannot invent one. This is the case the model must still answer, and it must keep echoing the raw reply.
        var bare = JsonDocument.Parse("""{"kind":"spawn"}""").RootElement;
        var repaired = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["st-1"]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare, repaired);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.PayloadJson.ShouldContain("st-1");
        client.Requests.Count.ShouldBe(2, "exactly one bounded repair round-trip — the lift cannot fix a payload that is not there");
        client.Requests[1].UserPrompt.ShouldContain("anywhere else", customMessage: "the defect still says top-level fields are never read");
    }

    [Fact]
    public async Task A_stop_the_repair_could_not_fix_still_lands_carrying_the_summary_the_model_wrote()
    {
        // LIVE shape from real-model run 33755336097 (2026-09-03), refused on all three attempts of the headline arc:
        // the model chose 'stop' and wrote only 'kind' + 'rationale'. The repair round-trip is still spent (it may yet
        // return an honest 'failed' outcome), but when it misses too, the terminal stop must not reach the publish gate
        // summary-less — that gate substitutes an ask_human and parks a finished run on a question no human owes.
        var bare = JsonDocument.Parse("""{"kind":"stop","rationale":{"why":"Both plan units are accepted and every contract dimension reads settled.","evidence":"acceptance PASSED on both units"}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare, bare);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        StopField(decision, "summary").ShouldContain("every contract dimension reads settled", customMessage: "the projected stop carries the model's OWN words as its summary, not the projector's empty substitute (the rationale the projector also injects sits at the payload root, where SupervisorPublishGate does not look)");
        SupervisorStopPayload.IsSuccessOutcome(StopField(decision, "outcome")).ShouldBeFalse("the model authored no outcome, so the server's fill fails closed — a confident-sounding rationale is reasoning, not a verdict");
        StopField(decision, "outcomeAssumed").ShouldNotBeNullOrWhiteSpace("…and the payload says the label was assumed, so the journal does not present it as the model's own");
        client.Requests.Count.ShouldBe(1 + LlmSupervisorDecider.MaxPayloadReaskAttempts, "the model still gets its bounded repairs — the floor only catches what they drop");
    }

    [Fact]
    public async Task A_stop_that_nested_an_outcome_but_no_summary_is_narrated_with_no_round_trip()
    {
        // Coherence passes (the 'stop' object IS present), so nothing above this would ever fire — yet the publish gate
        // rejects it for exactly the same reason. The words are in the rationale; the authored outcome is left alone.
        var terse = JsonDocument.Parse("""{"kind":"stop","stop":{"outcome":"failed"},"rationale":{"why":"The baseline build is broken and three attempts could not fix it."}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(terse);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        StopField(decision, "summary").ShouldContain("The baseline build is broken", customMessage: "the summary is recovered from the reply's own rationale");
        // C4: 'failed' is outside the closed stop.outcome enum, so it is repaired onto it. The MEANING is preserved
        // exactly (a non-success stop stays a non-success stop) and the model's own label is kept in the payload, so
        // the floor still restores words and still never manufactures a verdict.
        StopField(decision, "outcome").ShouldBe(SupervisorStopPayload.GaveUpOutcome, "a terminal label outside the enum is repaired onto it, never terminalized as free text");
        StopField(decision, "outcomeRepairedFrom").ShouldBe("failed", "…and the journal still shows what the model actually wrote");
        StopField(decision, "outcomeAssumed").ShouldBeEmpty("nothing was assumed here — the model DID author a label, it was just not a conformant one");
        client.Requests.Count.ShouldBe(1, "recovering words the first reply already carried never costs a round-trip");
    }

    /// <summary>
    /// C4: a LIVE model answering with a legacy success word is REPAIRED onto the closed enum, not accepted as
    /// authored — the words <c>IsSuccessOutcome</c> still honours for old tapes never enter a fresh decision. The
    /// terminal MEANING is unchanged (this really was a success), so the repair costs no round-trip and no verdict.
    /// </summary>
    [Fact]
    public async Task A_live_stop_authoring_a_legacy_success_word_is_repaired_onto_the_enum()
    {
        var legacy = JsonDocument.Parse("""{"kind":"stop","stop":{"outcome":"done","summary":"Both units merged and the suite is green."}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(legacy);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        StopField(decision, "outcome").ShouldBe(SupervisorStopPayload.CompletedOutcome, "'done' is not a member of the closed enum — a new decision may not terminalize with it");
        StopField(decision, "outcomeRepairedFrom").ShouldBe("done");
        StopField(decision, "summary").ShouldBe("Both units merged and the suite is green.", "the repair rewrites the LABEL only");
        SupervisorStopPayload.IsSuccessOutcome(StopField(decision, "outcome")).ShouldBeTrue("the model really did claim success — the repair preserves that, it only makes it sayable in one way");
        client.Requests.Count.ShouldBe(1, "a deterministic label repair never costs a round-trip");
    }

    /// <summary>One field of a projected stop's canonical payload — read explicitly, because the projector ALSO injects the decision-level rationale at the payload root, so a substring probe over the whole payload passes whether or not the summary was ever filled.</summary>
    private static string StopField(SupervisorDecision decision, string field) =>
        JsonDocument.Parse(decision.PayloadJson).RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    [Fact]
    public async Task A_spawn_with_an_EMPTY_subtaskIds_array_is_repaired_before_projection()
    {
        // The shape that slips every earlier net: the sub-object is PRESENT (no bind error) and minItems is not
        // validated client-side — without this gate it sails through to the executor's rejection a turn later.
        var empty = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":[]}}""").RootElement;
        var repaired = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["st-1"]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(empty, repaired);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.PayloadJson.ShouldContain("st-1");
        client.Requests.Count.ShouldBe(2, "an empty fan-out is the model's own authorship at decide time (the dependency clamp runs later, server-side) — repairable, exactly like a missing sub-object");
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan, """{"kind":"plan"}""", """{"kind":"plan","plan":{"goal":"g","subtasks":[{"id":"a","title":"A","instruction":"do a"}]}}""", "do a")]
    [InlineData(SupervisorDecisionKinds.Retry, """{"kind":"retry"}""", """{"kind":"retry","retry":{"subtaskId":"st-1"}}""", "st-1")]
    [InlineData(SupervisorDecisionKinds.AskHuman, """{"kind":"ask_human"}""", """{"kind":"ask_human","askHuman":{"question":"which db?"}}""", "which db?")]
    [InlineData(SupervisorDecisionKinds.Stop, """{"kind":"stop"}""", """{"kind":"stop","stop":{"outcome":"completed","summary":"shipped it"}}""", "shipped it")]
    public async Task Every_payload_bearing_kind_missing_its_payload_buys_the_same_repair(string kind, string bare, string repaired, string marker)
    {
        var client = new SequencedRawJsonStructuredClient(JsonDocument.Parse(bare).RootElement, JsonDocument.Parse(repaired).RootElement);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(kind);
        decision.PayloadJson.ShouldContain(marker, customMessage: "the repaired payload lands — ONE generic gate covers every payload-bearing verb, no per-verb special case");
        client.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_repair_that_is_still_payload_less_keeps_the_original_decision()
    {
        var bare = JsonDocument.Parse("""{"kind":"spawn"}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare);   // the repair replays the same bare reply
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "the ORIGINAL decision proceeds — the executor's rejection path is unchanged by a missed repair");
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("subtaskIds").GetArrayLength().ShouldBe(0, "the canonical empty payload, exactly as before the gate existed");
        client.Requests.Count.ShouldBe(1 + LlmSupervisorDecider.MaxPayloadReaskAttempts, "the repair is BOUNDED — it stops at the pinned attempt count, never loops");
    }

    [Fact]
    public async Task A_capability_miss_during_the_payload_repair_keeps_the_original_decision()
    {
        var client = new ReplyThenCapabilityMissClient(JsonDocument.Parse("""{"kind":"spawn"}""").RootElement);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "a model-side miss during the repair fails toward the original decision — never a crash, never a loop");
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Merge)]     // schema: required [] — an empty merge legitimately means "merge everything mergeable"
    [InlineData(SupervisorDecisionKinds.Resolve)]   // schema: no payload sub-object at all
    public async Task A_payload_less_exempt_kind_burns_no_repair_call(string kind)
    {
        var raw = JsonDocument.Parse($$"""{"kind":"{{kind}}"}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(raw);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(kind);
        client.Requests.Count.ShouldBe(1, "a kind whose payload is legitimately optional must never be 'repaired' — that would burn a call correcting a defect the model did not commit");
    }

    // ── The re-ask is TARGETED (it quotes the shape) and LEGIBLE (the row says a re-ask paid for the decision) ──

    [Fact]
    public async Task A_recovered_payload_less_decision_records_the_re_ask_and_the_kind_it_started_from()
    {
        // LIVE shape, real-model run 33943475246: the brain answered a spawn-ready turn with a bare '{"kind":"plan"}'.
        // The re-ask reply DECIDES — here it re-authors the plan it meant — and the row must say the decision cost a
        // second round-trip, otherwise a reader scoring the eval cannot tell an outright answer from a rescued one.
        var bare = JsonDocument.Parse("""{"kind":"plan","rationale":{"why":"decompose the goal"}}""").RootElement;
        var full = JsonDocument.Parse("""{"kind":"plan","plan":{"goal":"ship","subtasks":[{"id":"s1","title":"A","instruction":"do a"}]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare, full);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan);
        decision.PayloadJson.ShouldContain("do a", customMessage: "the re-asked payload is the one projected");
        decision.PayloadReaskedFromKind.ShouldBe(SupervisorDecisionKinds.Plan, "the accepted decision carries the kind the payload-less first reply named, so the ledger row can say a re-ask paid for it");
        client.Requests.Count.ShouldBe(2, "exactly one bounded re-ask");
    }

    [Fact]
    public async Task A_decision_the_model_got_right_first_time_records_no_re_ask()
    {
        var whole = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["st-1"]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(whole);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.PayloadReaskedFromKind.ShouldBeNull("the overwhelmingly common path must stay byte-identical — a re-ask marker on a decision that never needed one is a false signal in every read of the tape");
    }

    [Fact]
    public async Task A_re_ask_that_changes_the_verb_still_records_the_kind_it_started_from()
    {
        // The turn the live miss actually wasted: the plan was already authored, so the correct move was 'spawn'.
        // The re-ask reply DECIDES — the server never invents a payload for the kind the model first named — and the
        // row keeps the abandoned verb so a reader can see the brain corrected itself rather than answering cleanly.
        var bare = JsonDocument.Parse("""{"kind":"plan"}""").RootElement;
        var spawn = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["s1","s2"]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare, spawn);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "the re-ask reply decides — the server never fabricates a payload for the verb the model abandoned");
        decision.PayloadReaskedFromKind.ShouldBe(SupervisorDecisionKinds.Plan);
    }

    [Fact]
    public async Task A_reply_that_stays_payload_less_through_every_attempt_fails_open_and_says_how_many_it_spent()
    {
        var bare = JsonDocument.Parse("""{"kind":"plan"}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare);   // every re-ask replays the same bare reply
        var logger = new CapturingLogger<LlmSupervisorDecider>();
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), logger);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan, "the ORIGINAL decision proceeds, exactly as before the targeted re-ask existed");
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("subtasks").GetArrayLength().ShouldBe(0, "the canonical empty payload — unchanged");
        decision.PayloadReaskedFromKind.ShouldBeNull("nothing was recovered, so nothing may claim a recovery");
        decision.PayloadReaskAttempts.ShouldBe(LlmSupervisorDecider.MaxPayloadReaskAttempts, "…but the round-trips it DID spend are on the row — otherwise this fail-open reads exactly like a ladder that never ran");
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan })
            .ShouldBe("the decision chose kind 'plan' but carries NO 'plan' object — its payload is only read from INSIDE a 'plan' object carrying 'goal' and 'subtasks'; fields written anywhere else (e.g. at the top level of the decision) are never read",
                customMessage: "the named defect the journal and the re-ask both quote is pinned verbatim — it is the only thing a reader gets when the re-ask misses too");
        client.Requests.Count.ShouldBe(1 + LlmSupervisorDecider.MaxPayloadReaskAttempts, "the ladder is BOUNDED — it stops at the pinned attempt count, never loops");

        logger.Entries.ShouldContain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("payload repair missed for kind 'plan' after 2 bounded re-ask(s)"),
            customMessage: "the fail-open still warns, and now names how many attempts were spent — the operator's only clue that the executor is about to refuse a payload the model never wrote");
    }

    [Fact]
    public async Task A_payload_recovered_on_the_SECOND_bounded_attempt_is_the_decision_and_the_row_counts_both()
    {
        // The evidence this bound exists for: across the last ten real-model decision evals FOUR turns answered with a
        // payload-less verb and 6 of the 8 re-asks they bought recovered — a single attempt throws away the second ask
        // that empirically moves a degenerate reply off its own shape.
        var bare = JsonDocument.Parse("""{"kind":"retry","rationale":{"why":"retry s3"}}""").RootElement;
        var full = JsonDocument.Parse("""{"kind":"retry","retry":{"subtaskId":"s3"}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare, bare, full);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Retry);
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("subtaskId").GetString()
            .ShouldBe("s3", "the SECOND re-ask's payload is the one projected — before this bound it was thrown away and the executor was handed the empty substitute");
        decision.PayloadReaskedFromKind.ShouldBe(SupervisorDecisionKinds.Retry, "the kind the FIRST reply named still rides along — a later attempt's verb never overwrites it");
        decision.PayloadReaskAttempts.ShouldBe(2, "both round-trips are accounted for, not just the one that landed");
        client.Requests.Count.ShouldBe(3, "one first call plus the two bounded re-asks");
    }

    [Fact]
    public async Task Each_bounded_attempt_corrects_the_LATEST_payload_less_reply_not_the_first()
    {
        // A second defective answer is a different reply with its own defect. Echoing the superseded one would ask the
        // model to fix a shape it has already moved off — and would quote a verb it no longer named.
        var firstMiss = JsonDocument.Parse("""{"kind":"plan"}""").RootElement;
        var secondMiss = JsonDocument.Parse("""{"kind":"spawn"}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(firstMiss, secondMiss);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(), CancellationToken.None);

        client.Requests[1].UserPrompt.ShouldContain("omitted or left incomplete the 'plan' object", customMessage: "the first correction quotes the first reply's own defect");
        client.Requests[2].UserPrompt.ShouldContain("omitted or left incomplete the 'spawn' object", customMessage: "…and the second quotes the SECOND reply's, the shape the model actually has to fix now");
        client.Requests[2].UserPrompt.ShouldContain("""{"kind":"spawn"}""", customMessage: "…and ECHOES the second reply, not the superseded first — the echo is the load-bearing half of the correction, and a header that names the new verb over a stale echo asks the model to fix a shape it never wrote");
        client.Requests[2].UserPrompt.ShouldNotContain("""{"kind":"plan"}""", customMessage: "…so the first reply, which the model has already moved off, is nowhere in the second correction");
        client.Requests[2].UserPrompt.ShouldContain("Plan-local subtask ids", customMessage: "…including that kind's own schema fragment, exactly as the first attempt renders it — no new prompt text");
    }

    [Fact]
    public async Task A_re_ask_reply_that_FLATTENS_the_payload_is_nested_deterministically_without_spending_another_ask()
    {
        // The deterministic lift is the dominant repair (68 flattened decisions in one eval run) but it ran ONCE, on the
        // first reply. A correction that answers with the payload at the ROOT is exactly as salvageable — every field is
        // present under its own name — yet it scored as still-missing, burned the next attempt, and on the LAST one was
        // discarded in favour of the projector's empty substitute for a payload the model demonstrably wrote.
        var absent = JsonDocument.Parse("""{"kind":"retry","rationale":{"why":"s3 failed"}}""").RootElement;
        var flattened = JsonDocument.Parse("""{"kind":"retry","subtaskId":"s3"}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(absent, flattened);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Retry);
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("subtaskId").GetString()
            .ShouldBe("s3", "the re-ask's own fields are nested where the contract reads them — the model authored the target, so the server never has to invent one");
        decision.PayloadReaskedFromKind.ShouldBe(SupervisorDecisionKinds.Retry, "a re-ask DID produce this decision, so the row still credits the round-trip it cost");
        decision.PayloadReaskAttempts.ShouldBe(1, "…exactly ONE, because the reply was deterministically salvageable and the ladder stops the moment it lands");
        client.Requests.Count.ShouldBe(2, "one first call plus the single re-ask — a second correction would spend a round-trip recovering information the first one already delivered");
    }

    [Fact]
    public void The_payload_re_ask_bound_is_pinned()
    {
        // Rule 8 pin: the bound IS the behaviour. Dropping it back to one re-narrows the exact window the second
        // attempt exists to widen (6 of 8 live re-asks recovered), and nothing else in the ladder would go red.
        LlmSupervisorDecider.MaxPayloadReaskAttempts.ShouldBe(2, "two bounded payload re-asks — changing this changes how many degenerate replies a turn can survive");
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan, "plan", "Decompose into 'subtasks'")]
    [InlineData(SupervisorDecisionKinds.Spawn, "spawn", "Plan-local subtask ids")]
    [InlineData(SupervisorDecisionKinds.Retry, "retry", "The plan-local subtask id to re-run")]
    [InlineData(SupervisorDecisionKinds.AskHuman, "askHuman", "The question to ask the human")]
    [InlineData(SupervisorDecisionKinds.Stop, "stop", "Short summary of what the supervisor accomplished")]
    [InlineData(SupervisorDecisionKinds.AmendAcceptance, "amendAcceptance", "The ONE plan-declared subtask whose acceptance check this proposal targets")]
    public async Task The_re_ask_quotes_the_named_kind_own_schema_fragment(string kind, string property, string fragmentMarker)
    {
        // The echo alone tells the model WHAT it wrote; it never tells it what the missing object must LOOK like.
        // The first reply already proved the model could not recall that shape, so the re-ask hands it over.
        var bare = JsonDocument.Parse($$"""{"kind":"{{kind}}"}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(), CancellationToken.None);

        var reask = client.Requests[1].UserPrompt;

        reask.ShouldContain($"'{property}'", customMessage: "the re-ask names the sub-object by the key the contract reads");
        reask.ShouldContain($"omitted or left incomplete the '{property}' object", customMessage: "the header states the shortfall in terms true of BOTH shapes the coherence check catches — an ABSENT object and a PRESENT one missing a required field — so it is never contradicted by the reply echoed below it; the 'Defect:' line says which one it was");
        reask.ShouldContain(fragmentMarker, customMessage: $"the re-ask quotes the '{property}' object's own JSON-schema fragment, so the model is shown the shape rather than asked to recall it");
        reask.ShouldContain($"\"kind\": \"{kind}\"", customMessage: "…and it spells out the COMPLETE decision envelope the reply must have");
    }

    [Fact]
    public async Task A_stop_the_re_asks_could_not_fix_is_still_filled_by_the_narration_lift_and_costs_no_extra_call()
    {
        // The stop floor is A7's, not this arc's: the summary is recovered from the model's own rationale. The bounded
        // payload re-asks that already existed still run (one may yet return an honest outcome), and NOTHING here adds
        // a call of its own — a lift that can fill the payload must never be pre-empted or duplicated by a re-ask.
        var bare = JsonDocument.Parse("""{"kind":"stop","rationale":{"why":"Both plan units are accepted."}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare, bare);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        StopField(decision, "summary").ShouldContain("Both plan units are accepted", customMessage: "#1755's narration lift still wins — the words the model wrote become the summary");
        decision.PayloadReaskedFromKind.ShouldBeNull("the re-asks recovered nothing; the LIFT did — the row must not credit a round-trip that missed");
        client.Requests.Count.ShouldBe(1 + LlmSupervisorDecider.MaxPayloadReaskAttempts, "the pre-existing bounded payload ladder, and no more");
    }

    [Fact]
    public async Task A_plan_carrying_an_EMPTY_subtasks_array_is_re_asked_before_projection()
    {
        // The sibling of the empty-spawn gate, and the shape SupervisorPlanValidator cannot see (it validates EDGES,
        // and a plan with no subtasks has none): a present-but-empty 'plan' object projects to a plan that can never
        // spawn anything, so the run spins to its no-progress bound instead of being asked once for the real plan.
        var empty = JsonDocument.Parse("""{"kind":"plan","plan":{"goal":"ship","subtasks":[]}}""").RootElement;
        var full = JsonDocument.Parse("""{"kind":"plan","plan":{"goal":"ship","subtasks":[{"id":"s1","title":"A","instruction":"do a"}]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(empty, full);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.PayloadJson.ShouldContain("do a");
        client.Requests.Count.ShouldBe(2, "an empty plan is unexecutable authorship at decide time — repairable, exactly like an empty fan-out");

        client.Requests[1].UserPrompt.ShouldContain("omitted or left incomplete the 'plan' object",
            customMessage: "the correction's header is honest about the shape the model ACTUALLY wrote — half the coherence check's arms are a PRESENT object missing a required field, and this is one of them");
        client.Requests[1].UserPrompt.ShouldNotContain("omitted the 'plan' object",
            customMessage: "…never the flat omission claim, which the model's own reply — echoed two lines below it in the same prompt — visibly contradicts");
    }

    [Fact]
    public async Task The_re_ask_carries_the_RUN_context_so_a_real_subtask_id_is_authorable()
    {
        // The echo + the schema fragment tell the model WHAT shape to write. Neither tells it WHICH ids exist — and a
        // spawn's payload is nothing but plan-local ids. Asked without the run in front of it, the only ids the model
        // can produce are invented ones, and the correction becomes a shape production can satisfy only by accident.
        var bare = JsonDocument.Parse("""{"kind":"spawn"}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(turnNumber: 2, PlanPrior(1, "s1", "s2"), SpawnPrior(2, ("s1", "Succeeded", null), ("s2", "Failed", "build failed: missing symbol"))), CancellationToken.None);

        var reask = client.Requests[1].UserPrompt;

        reask.ShouldContain("ship the feature", customMessage: "the re-ask carries the run's GOAL — the same prompt the first call had, exactly as the re-plan rung does");
        reask.ShouldContain(SupervisorRecitation.Header, customMessage: "…and the live plan state, so the model can see which ids exist and what each one did");
        reask.ShouldContain("[s1]");
        reask.ShouldContain("[s2]");
        reask.ShouldContain("build failed: missing symbol", customMessage: "…and the evidence the corrected decision should act on");
        reask.ShouldContain("omitted or left incomplete the 'spawn' object", customMessage: "the correction itself still rides at the TAIL — the recency-biased position");
    }

    [Fact]
    public async Task The_re_ask_never_forbids_the_new_decision_it_invites()
    {
        // The bind repair genuinely wants the SAME decision re-emitted with a path fixed, so "no new decisions" is
        // right there. This rung's whole point is the opposite — the reply DECIDES, and its own last sentence says
        // "if a different action now fits, choose it instead". Carrying the bind repair's clause here instructed
        // against the headline behaviour in the same request.
        var bare = JsonDocument.Parse("""{"kind":"plan"}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(bare);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(), CancellationToken.None);

        var system = client.Requests[1].SystemPrompt;

        system.ShouldNotContain("no new decisions", Case.Insensitive, "the correction invites a different action in the SAME request — forbidding one here is a self-contradicting instruction");
        system.ShouldContain("Your reply IS the decision", customMessage: "…and says plainly whose call it is");
        client.Requests[1].UserPrompt.ShouldContain("choose it instead", customMessage: "fixture check — the user prompt really does invite the different action the system prompt must not forbid");
    }

    // ── The retry's TARGET: a retry aimed at finished work while real failures wait buys ONE bounded re-ask ──

    [Fact]
    public async Task A_retry_aimed_at_finished_work_is_re_asked_once_and_the_re_aimed_decision_is_recorded()
    {
        // LIVE: golden 'five-subtask-middle-failed' failed on two consecutive main runs (33945398336, 33946934743) —
        // one retried 's1', a succeeded and accepted unit, while s2 sat failed. The payload is whole and the id is
        // plan-declared, so nothing downstream can see it: the executor re-runs finished work and the failure stands.
        var misaimed = JsonDocument.Parse("""{"kind":"retry","retry":{"subtaskId":"s1"}}""").RootElement;
        var reaimed = JsonDocument.Parse("""{"kind":"retry","retry":{"subtaskId":"s2"}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(misaimed, reaimed);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(MixedFanOut(), CancellationToken.None);

        decision.PayloadJson.ShouldContain("s2", customMessage: "the re-aimed decision is the one projected — the retry now targets the unit that actually failed");
        decision.RetryTargetReasked.ShouldBeTrue("the ledger row says the decision cost a second round-trip, like payloadReasked");
        client.Requests.Count.ShouldBe(2, "exactly one bounded re-ask");
        client.Requests[1].UserPrompt.ShouldContain("s2", customMessage: "the correction QUOTES the failed unit ids rather than leaving the model to re-derive them");
        client.Requests[1].UserPrompt.ShouldContain(SupervisorRecitation.Header, customMessage: "…on top of the run context it decides from");
    }

    [Fact]
    public async Task A_re_ask_that_re_emits_the_SAME_retry_target_is_accepted_as_the_model_answer()
    {
        // The server never re-aims a retry: picking the id would be the server deciding. Shown the failed units and
        // its own reply, a model that still means s1 has answered on the evidence — that answer stands, and the row
        // still records that a round-trip bought it.
        var misaimed = JsonDocument.Parse("""{"kind":"retry","retry":{"subtaskId":"s1"}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(misaimed);   // the re-ask replays the same target
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(MixedFanOut(), CancellationToken.None);

        decision.PayloadJson.ShouldContain("s1");
        decision.RetryTargetReasked.ShouldBeTrue();
        client.Requests.Count.ShouldBe(2, "BOUNDED — the repeat is an answer, not a defect to correct twice");
    }

    [Fact]
    public async Task A_retry_target_re_ask_that_misses_keeps_the_original_decision_and_records_nothing()
    {
        var misaimed = JsonDocument.Parse("""{"kind":"retry","retry":{"subtaskId":"s1"}}""").RootElement;
        var client = new ReplyThenCapabilityMissClient(misaimed);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(MixedFanOut(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Retry, "a model-side miss during the re-ask fails toward the ORIGINAL decision — never a crash, never a loop");
        decision.PayloadJson.ShouldContain("s1");
        decision.RetryTargetReasked.ShouldBeFalse("nothing came back, so nothing may claim a round-trip bought this decision");
    }

    [Fact]
    public async Task A_retry_that_targets_the_FAILED_unit_costs_exactly_one_call()
    {
        var aimed = JsonDocument.Parse("""{"kind":"retry","retry":{"subtaskId":"s2"}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(aimed);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(MixedFanOut(), CancellationToken.None);

        decision.RetryTargetReasked.ShouldBeFalse("the overwhelmingly common path must stay byte-identical — a marker here is a false signal in every read of the tape");
        client.Requests.Count.ShouldBe(1, "a correctly aimed retry must never buy a correction it did not earn");
    }

    /// <summary>The live fan-out shape: s1 succeeded and is accepted, s2 failed. A retry is owed to s2 and to nothing else.</summary>
    private static SupervisorTurnContext MixedFanOut() =>
        Context(turnNumber: 2, PlanPrior(1, "s1", "s2"), SpawnPrior(2, ("s1", "Succeeded", null), ("s2", "Failed", "build failed: missing symbol")));

    private static SupervisorPriorDecision PlanPrior(long sequence, params string[] subtaskIds) => new()
    {
        Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = JsonSerializer.Serialize(new SupervisorPlanPayload { Goal = "ship", Subtasks = subtaskIds.Select(id => new SupervisorPlannedSubtask { Id = id, Title = id, Instruction = $"do {id}" }).ToArray() }, AgentJson.Options),
    };

    private static SupervisorPriorDecision SpawnPrior(long sequence, params (string Id, string Status, string? Error)[] units)
    {
        var results = units.Select(u => new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(), Status = u.Status, Error = u.Error,
            AcceptancePassed = u.Status == "Succeeded" ? true : null,
            AcceptanceDetail = u.Status == "Succeeded" ? "tests-passed" : null,
        }).ToArray();

        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = units.Select(u => u.Id).ToArray() }, AgentJson.Options),
            OutcomeJson = JsonSerializer.Serialize(new { agentRunIds = results.Select(r => r.AgentRunId), agentCount = results.Length, agentResults = results }, AgentJson.Options),
        };
    }

    // ── P1.4: a TRUNCATED completion buys ONE retry with a RAISED output budget before the bind-check flow ──

    [Fact]
    public void TruncatedRetryMaxOutputTokens_is_pinned_double_the_default()
    {
        // Shrinking this re-narrows the exact window the retry exists to widen — the whole point is genuine
        // extra room for a large plan that hit the SAME ceiling once already (Rule 8).
        LlmSupervisorDecider.TruncatedRetryMaxOutputTokens.ShouldBe(8192);
    }

    [Theory]
    [InlineData("length")]      // OpenAI's truncation marker
    [InlineData("max_tokens")]  // Anthropic's truncation marker
    public async Task A_truncated_completion_is_retried_with_a_raised_budget_and_the_retry_lands(string finishReason)
    {
        // The primary call BINDS (many providers' constrained decoding keeps JSON syntactically valid even when
        // content was cut short) but is FLAGGED truncated by its own finish reason — a single-subtask spawn that
        // could just as easily be the FIRST 1-of-many a bigger fan-out ran out of room to finish. The retry (a
        // raised budget) returns a bigger, complete spawn; the decider must use THAT, not the truncated one.
        var truncated = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["only-one"]}}""").RootElement;
        var complete = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["one","two","three"]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(new[] { truncated, complete }, new[] { finishReason, null });
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        client.Requests.Count.ShouldBe(2, "exactly one bounded retry — the primary truncated call + the raised-budget retry");
        client.Requests[1].MaxOutputTokens.ShouldBe(LlmSupervisorDecider.TruncatedRetryMaxOutputTokens, "the retry asks for the RAISED budget, not the same ceiling that just truncated");
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn);
        decision.PayloadJson.ShouldContain("three", customMessage: "the decision reflects the RETRIED (complete) spawn, not the truncated one");
        decision.PayloadJson.ShouldNotContain("only-one", customMessage: "the truncated reply is discarded once the retry succeeds");
    }

    [Fact]
    public async Task A_clean_completion_never_pays_the_truncation_retry()
    {
        // Byte-identical to before this fix on the dominant (non-truncated) path — no wasted extra call.
        var clean = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["a"]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(clean);   // default finish reason: null → Clean
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(), CancellationToken.None);

        client.Requests.Count.ShouldBe(1, "a clean (non-truncated) completion never triggers the retry");
    }

    [Fact]
    public async Task A_truncated_retry_that_itself_capability_misses_falls_back_to_the_original_completion()
    {
        // The retry call itself hits a model-side miss (the SAME class TryRepairAsync already tolerates) — fail
        // TOWARD the original truncated completion rather than crashing the run; the normal bind-check flow then
        // decides its fate exactly as if the retry had never been attempted.
        var truncated = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["only-one"]}}""").RootElement;
        var client = new TruncatedThenCapabilityMissClient(truncated, "length");
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        client.Calls.ShouldBe(2, "the retry was attempted once, then the decider fell back");
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "the ORIGINAL truncated completion still bound fine, so it lands as the decision — never a crash");
        decision.PayloadJson.ShouldContain("only-one");
    }

    /// <summary>First call returns a truncated-but-bindable completion; every call after THROWS a model-capability-miss (simulating the raised-budget retry itself failing) — proves the decider falls back to the original completion rather than propagating.</summary>
    private sealed class TruncatedThenCapabilityMissClient : ILLMClient, IStructuredLLMClient
    {
        private readonly JsonElement _first;
        private readonly string _finishReason;
        public int Calls { get; private set; }

        public TruncatedThenCapabilityMissClient(JsonElement first, string finishReason) { _first = first; _finishReason = finishReason; }

        public string Provider => "TestSupervisor";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1) return Task.FromResult(new StructuredLLMCompletion { Json = _first, Model = request.Model, Usage = new LlmUsage { FinishReason = _finishReason } });

            throw new LlmApiException("TestSupervisor", null, LlmErrorCategory.Malformed, "the retry itself produced no usable reply");
        }
    }

    [Fact]
    public async Task A_repair_that_still_cannot_bind_stops_with_the_precise_path()
    {
        var broken = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":"oops"}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(broken);   // the repair replays the same broken reply
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop, "fail closed after the ladder exhausts — never crash the run");
        decision.PayloadJson.ShouldContain("$.spawn", customMessage: "the stop summary NAMES the drift so it is diagnosable from the run page, not the database");
        client.Requests.Count.ShouldBe(2, "primary + one repair, never an unbounded loop");
    }

    // ── bounded re-plan: a STRUCTURALLY invalid plan (SupervisorPlanValidator) buys ONE re-plan before the caller's terminal PlanInvalid stop ──

    [Fact]
    public async Task A_structurally_invalid_plan_buys_ONE_bounded_replan_and_the_valid_retry_lands()
    {
        // b depends on undeclared 'z' — SupervisorPlanValidator would force-stop this at SupervisorTurnService's
        // gate with no chance to recover. The retry re-authors a VALID dependsOn graph (b now depends on a); the
        // decider must land THAT, not the invalid original.
        var invalid = JsonDocument.Parse("""{"kind":"plan","plan":{"goal":"ship","subtasks":[{"id":"a","title":"A","instruction":"do a"},{"id":"b","title":"B","instruction":"do b","dependsOn":["z"]}]}}""").RootElement;
        var valid = JsonDocument.Parse("""{"kind":"plan","plan":{"goal":"ship","subtasks":[{"id":"a","title":"A","instruction":"do a"},{"id":"b","title":"B","instruction":"do b","dependsOn":["a"]}]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(invalid, valid);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        client.Requests.Count.ShouldBe(2, "exactly one bounded re-plan round-trip");
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan);
        decision.PayloadJson.ShouldContain("\"dependsOn\":[\"a\"]", customMessage: "the decision reflects the RETRIED (valid) plan, not the invalid original");
        client.Requests[1].UserPrompt.ShouldContain("structurally INVALID", customMessage: "the re-plan prompt names the failure so the model targets the right fix");
        client.Requests[1].UserPrompt.ShouldContain("\"z\"", customMessage: "the model sees its OWN invalid plan payload, not a fresh ask");
    }

    [Fact]
    public async Task A_structurally_valid_plan_never_pays_the_replan_retry()
    {
        // Byte-identical to before this fix on the dominant (already-valid) path — no wasted extra call.
        var valid = JsonDocument.Parse("""{"kind":"plan","plan":{"goal":"ship","subtasks":[{"id":"a","title":"A","instruction":"do a"},{"id":"b","title":"B","instruction":"do b","dependsOn":["a"]}]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(valid);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        client.Requests.Count.ShouldBe(1, "a structurally valid plan never triggers the re-plan retry");
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan);
    }

    [Fact]
    public async Task A_non_plan_decision_never_pays_the_replan_retry()
    {
        // SupervisorPlanValidator.Validate is a no-op for every non-plan kind — a spawn/stop/etc. never even
        // consults it, so this path is provably inert outside 'plan'.
        var spawn = JsonDocument.Parse("""{"kind":"spawn","spawn":{"subtaskIds":["a"]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(spawn);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(), CancellationToken.None);

        client.Requests.Count.ShouldBe(1, "a non-plan decision is never validated for plan structure");
    }

    [Fact]
    public async Task A_replan_retry_that_is_still_invalid_falls_back_to_the_original_invalid_decision()
    {
        // The retry ALSO produces a structurally invalid plan (still cites the undeclared 'z') — the decider must
        // fall back to the ORIGINAL invalid decision (not crash, not loop again) so SupervisorTurnService's
        // existing gate still force-stops it exactly as before this fix existed.
        var invalid = JsonDocument.Parse("""{"kind":"plan","plan":{"goal":"ship","subtasks":[{"id":"a","title":"A","instruction":"do a"},{"id":"b","title":"B","instruction":"do b","dependsOn":["z"]}]}}""").RootElement;
        var client = new SequencedRawJsonStructuredClient(invalid, invalid);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        client.Requests.Count.ShouldBe(2, "one bounded re-plan attempt, never an unbounded loop");
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan);
        decision.PayloadJson.ShouldContain("\"z\"", customMessage: "still invalid — the caller's SupervisorPlanValidator gate force-stops this exactly as before");
    }

    [Fact]
    public async Task A_replan_retry_that_capability_misses_falls_back_to_the_original_invalid_decision()
    {
        // The retry itself hits a model-side miss (the SAME class TryRepairAsync/TryRetryWithRaisedBudgetAsync
        // already tolerate) — fail TOWARD the original invalid decision rather than crashing the run.
        var invalid = JsonDocument.Parse("""{"kind":"plan","plan":{"goal":"ship","subtasks":[{"id":"a","title":"A","instruction":"do a"},{"id":"b","title":"B","instruction":"do b","dependsOn":["z"]}]}}""").RootElement;
        var client = new TruncatedThenCapabilityMissClient(invalid, finishReason: null!);
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        client.Calls.ShouldBe(2, "the re-plan retry was attempted once, then the decider fell back");
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan);
        decision.PayloadJson.ShouldContain("\"z\"", customMessage: "the ORIGINAL invalid plan still lands — never a crash");
    }

    [Fact]
    public void The_full_featured_decision_shape_binds_and_projects_end_to_end()
    {
        // The schema↔type drift pin: an instance exercising EVERY agents[] field (the real run's attempt 3, with a
        // proper uuid) must bind and project — the validator and the C# contract can no longer silently disagree.
        var repo = Guid.NewGuid();
        var json = JsonDocument.Parse("""
            {"kind":"spawn","rationale":{"why":"ready","evidence":"frontier shows scan ready"},
             "spawn":{"subtaskIds":["scan"],"agents":[{"subtaskId":"scan","role":"scanner","goalOverride":"scan it",
               "repositoryId":"__REPO__","targetRepos":[{"repositoryId":"__REPO__","alias":"be","access":"read"}],
               "harness":"claude-code","model":"m1","autonomyLevel":"standard","agentDefinition":"backend"}]}}
            """.Replace("__REPO__", repo.ToString())).RootElement;

        var model = json.Deserialize<SupervisorModelDecision>(SupervisorDecisionSchema.Options);

        model.ShouldNotBeNull();
        model!.Spawn!.Agents![0].RepositoryId.ShouldBe(repo);
        model.Spawn.Agents[0].AgentDefinition.ShouldBe("backend");
        SupervisorDecisionProjector.Project(model).Kind.ShouldBe(SupervisorDecisionKinds.Spawn);
    }

    [Fact]
    public void The_catalog_lists_bound_repositories_with_exact_ids()
    {
        var primary = Guid.NewGuid();
        var related = Guid.NewGuid();
        var context = Context() with
        {
            AgentProfile = new CodeSpace.Messages.Dtos.Agents.SupervisorAgentProfile
            {
                RepositoryId = primary,
                RelatedRepositories = JsonDocument.Parse("""[{"repositoryId":"__REPO__","alias":"fe","access":"write"}]""".Replace("__REPO__", related.ToString())).RootElement,
            },
        };

        var section = LlmSupervisorDecider.RenderBoundRepositories(context);

        section.ShouldContain(primary.ToString(), customMessage: "the primary repo's EXACT id is citable — without it the model can only guess a name");
        section.ShouldContain(related.ToString());
        section.ShouldContain("alias 'fe'");
        section.ShouldContain("EXACT ids");

        LlmSupervisorDecider.RenderBoundRepositories(Context()).ShouldBe("", "an analysis-only run appends nothing");
    }

    private static SupervisorDecision Project(string kind, Func<SupervisorModelDecision, SupervisorModelDecision> fill) =>
        SupervisorDecisionProjector.Project(fill(new SupervisorModelDecision { Kind = kind }));

    private static LlmSupervisorDecider Decider(SupervisorModelDecision model) =>
        new(new FakeRegistry(new FakeStructuredClient(model)), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore(), new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

    // ── Fakes at the honest IStructuredLLMClient seam ────────────────────────────────

    private sealed class FakeRegistry : ILLMClientRegistry
    {
        public FakeRegistry(IStructuredLLMClient? structured) =>
            All = structured == null ? Array.Empty<ILLMClient>() : new ILLMClient[] { (ILLMClient)structured };

        public IReadOnlyList<ILLMClient> All { get; }

        public ILLMClient Resolve(string provider) => All.First();
    }

    private sealed class FakeStructuredClient : ILLMClient, IStructuredLLMClient
    {
        private readonly SupervisorModelDecision _model;
        public string? LastModel;
        public string? LastUserPrompt;

        public FakeStructuredClient(SupervisorModelDecision model) => _model = model;

        public string Provider => "TestSupervisor";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            LastModel = request.Model;
            LastUserPrompt = request.UserPrompt;
            return Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(_model, SupervisorDecisionSchema.Options), Model = request.Model });
        }
    }

    /// <summary>Returns a caller-supplied RAW <see cref="JsonElement"/> as the structured reply — used to feed a malformed/wrong-shape response (one the typed fake cannot produce) so the decider's fail-closed deserialization guard is exercised.</summary>
    private sealed class RawJsonStructuredClient : ILLMClient, IStructuredLLMClient
    {
        private readonly JsonElement _raw;
        public RawJsonStructuredClient(JsonElement raw) => _raw = raw;

        public string Provider => "TestSupervisor";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion { Json = _raw, Model = request.Model });
    }

    /// <summary>Returns a SEQUENCE of raw replies (the last one repeats) and records every request — the repair arc's seam: first the unbindable reply, then the repaired one, with the repair prompt pinned off the recorded request. An optional PARALLEL finish-reason queue (default null ⇒ Clean, byte-identical to every pre-existing caller) drives the P1.4 truncated-completion retry.</summary>
    private sealed class SequencedRawJsonStructuredClient : ILLMClient, IStructuredLLMClient
    {
        private readonly Queue<JsonElement> _replies;
        private readonly Queue<string?> _finishReasons;
        public readonly List<StructuredLLMCompletionRequest> Requests = new();

        public SequencedRawJsonStructuredClient(params JsonElement[] replies) : this(replies, finishReasons: null) { }

        public SequencedRawJsonStructuredClient(JsonElement[] replies, string?[]? finishReasons)
        {
            _replies = new Queue<JsonElement>(replies);
            _finishReasons = new Queue<string?>(finishReasons ?? new string?[replies.Length]);
        }

        public string Provider => "TestSupervisor";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var json = _replies.Count > 1 ? _replies.Dequeue() : _replies.Peek();
            var finishReason = _finishReasons.Count > 1 ? _finishReasons.Dequeue() : (_finishReasons.Count == 1 ? _finishReasons.Peek() : null);
            return Task.FromResult(new StructuredLLMCompletion { Json = json, Model = request.Model, Usage = new LlmUsage { FinishReason = finishReason } });
        }
    }

    /// <summary>Records every log entry the decider writes — the seam for pinning a fail-open WARNING, which is the only thing an operator gets when a bounded ladder spends its attempts and recovers nothing.</summary>
    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>First structured call returns the given raw reply; every later call throws a Malformed-category <see cref="LlmApiException"/> — pins that a model-side miss DURING the coherence repair fails toward the original decision.</summary>
    private sealed class ReplyThenCapabilityMissClient : ILLMClient, IStructuredLLMClient
    {
        private readonly JsonElement _first;
        private bool _replied;
        public ReplyThenCapabilityMissClient(JsonElement first) => _first = first;

        public string Provider => "TestSupervisor";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            if (_replied) throw new LlmApiException("TestSupervisor", null, LlmErrorCategory.Malformed, "boom");

            _replied = true;
            return Task.FromResult(new StructuredLLMCompletion { Json = _first, Model = request.Model });
        }
    }

    /// <summary>Throws a typed <see cref="LlmApiException"/> of a given category from the structured call — pins the decider's "fail-closed on a model-capability miss, propagate real infra" split without a real gateway.</summary>
    private sealed class ThrowingStructuredClient : ILLMClient, IStructuredLLMClient
    {
        private readonly LlmErrorCategory _category;
        public ThrowingStructuredClient(LlmErrorCategory category) => _category = category;

        public string Provider => "TestSupervisor";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            throw new LlmApiException("TestSupervisor", null, _category, "boom");
    }

    private sealed class FakeSelector : IModelPoolSelector
    {
        private readonly ModelPoolPick? _pick;
        private readonly IReadOnlyList<PoolModelInfo> _pool;
        private readonly IReadOnlyList<Guid> _brainRows;
        private FakeSelector(ModelPoolPick? pick, IReadOnlyList<PoolModelInfo>? pool = null, IReadOnlyList<Guid>? brainRows = null) { _pick = pick; _pool = pool ?? Array.Empty<PoolModelInfo>(); _brainRows = brainRows ?? Array.Empty<Guid>(); }

        public static FakeSelector WithModel(string modelId = "claude-sonnet-4-5") =>
            new(new ModelPoolPick { ModelId = modelId, Credential = new ResolvedModelCredential { Provider = "TestSupervisor", ApiKey = "sk-test" } });

        public static FakeSelector WithModelAndPool(string modelId, params PoolModelInfo[] pool) =>
            new(new ModelPoolPick { ModelId = modelId, Credential = new ResolvedModelCredential { Provider = "TestSupervisor", ApiKey = "sk-test" } }, pool);

        public static FakeSelector Empty() => new(null);

        /// <summary>A pool with TWO structured brain rows — the failover shape. The row id decides which model resolves, so one fake client can play the throttled brain AND the alternate the hop lands on.</summary>
        public static FakeSelector WithTwoBrainRows() => new(null, brainRows: new[] { BrainModelId, SubstituteRowId });

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => Task.FromResult(_pick);

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) =>
            Task.FromResult(_brainRows.Count == 0 ? _pick : BrainRowPick(modelCredentialModelId));

        public Task<IReadOnlyList<Guid>> ListBrainRowIdsAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult(_brainRows);

        private static ModelPoolPick? BrainRowPick(Guid rowId) => new()
        {
            ModelId = rowId == BrainModelId ? ThrottledBrainWithSubstitute.BrainModel : ThrottledBrainWithSubstitute.SubstituteModel,
            Credential = new ResolvedModelCredential { Provider = "TestSupervisor", ApiKey = "sk-test" },
        };

        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult(_pool);
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class FakeHarnesses : CodeSpace.Core.Services.Agents.IAgentHarnessRegistry
    {
        public FakeHarnesses(params CodeSpace.Core.Services.Agents.IAgentHarness[] harnesses) => All = harnesses;
        public IReadOnlyList<CodeSpace.Core.Services.Agents.IAgentHarness> All { get; }
        public CodeSpace.Core.Services.Agents.IAgentHarness Resolve(string kind) => throw new NotSupportedException();
    }

    /// <summary>Stub persona library — <see cref="ListAsync"/> returns the configured personas (empty by default) so the catalog render is exercised; the CRUD methods are unused by the decider.</summary>
    private sealed class FakePersonas : CodeSpace.Core.Services.Agents.IAgentDefinitionService
    {
        private readonly IReadOnlyList<AgentDefinitionSummary> _list;
        private FakePersonas(IReadOnlyList<AgentDefinitionSummary> list) => _list = list;

        public static FakePersonas Empty() => new(Array.Empty<AgentDefinitionSummary>());

        public static FakePersonas With(params (string Slug, string Name, string? Description)[] personas) => new(personas
            .Select(p => new AgentDefinitionSummary { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Slug = p.Slug, Name = p.Name, Description = p.Description, SystemPrompt = "be a specialist", Origin = AgentDefinitionOrigin.Authored, CreatedDate = DateTimeOffset.UnixEpoch })
            .ToList());

        public Task<IReadOnlyList<AgentDefinitionSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult(_list);
        public Task<AgentDefinitionSummary?> GetAsync(Guid teamId, Guid agentDefinitionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid> CreateAsync(Guid teamId, AgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateAsync(Guid teamId, Guid agentDefinitionId, AgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid> ImportAsync(Guid teamId, ImportedAgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid> InstantiateFromStoreAsync(Guid teamId, Guid sourceSnapshotId, Guid actorUserId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid> AuthorStoreAgentAsync(Guid teamId, AgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(Guid teamId, Guid agentDefinitionId, Guid actorUserId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CatalogHarness : CodeSpace.Core.Services.Agents.IAgentHarness, CodeSpace.Core.Services.Agents.IModelCredentialProjector
    {
        public CatalogHarness(string kind, params string[] providers) { Kind = kind; SupportedProviders = providers; }
        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models => Array.Empty<string>();
        public IReadOnlyList<string> SupportedProviders { get; }
        public SandboxSpec BuildInvocation(AgentTask task) => throw new NotSupportedException();
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => throw new NotSupportedException();
        public IAgentEventFolder CreateFolder() => throw new NotSupportedException();
        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) => throw new NotSupportedException();
    }
    // ── P1.2 auto-compact: a context overflow folds the tape's head and retries, never a clean stop ──

    [Fact]
    public void The_compaction_constants_are_pinned()
    {
        // Shrinking the tail starves the model of its recent moves; raising the fold floor blocks compaction on
        // runs that genuinely need it. Hard-pin both (Rule 8).
        LlmSupervisorDecider.CompactTailKeep.ShouldBe(8);
        LlmSupervisorDecider.MinCompactFold.ShouldBe(4);
    }

    [Fact]
    public async Task A_context_overflow_compacts_the_tape_persists_the_digest_and_the_retry_succeeds()
    {
        // 12 prior decisions; the first decide overflows the window → the decider folds seq 1..4 (12 − tail 8)
        // into a digest, persists it, and retries the SAME decision with [digest + tail] — the run continues as if
        // the window never bound.
        var store = new FakeTapeStore();
        var client = new CompactionScriptClient("DIGEST: planned a/b, a succeeded on branch-a", new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = new SupervisorPlanPayload { Subtasks = new[] { new SupervisorPlannedSubtask { Id = "t", Title = "t", Instruction = "do t" } } } });
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), store, new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(turnNumber: 12, Tape(12)), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan, "the retried decide succeeded — the overflow never surfaced");

        store.Stored.ShouldNotBeNull("the digest is persisted so every LATER turn renders compacted without re-hitting the window");
        store.Stored!.UpToSequence.ShouldBe(4, "12 decisions − the 8-deep raw tail = seq 1..4 folded");

        client.Requests.Count.ShouldBe(3, "decide (overflow) → summarizer → retried decide");
        client.Requests[1].UserPrompt.ShouldContain("marker-1-seq", customMessage: "the summarizer sees the folded head");
        client.Requests[2].UserPrompt.ShouldContain("DIGEST: planned a/b", customMessage: "the retried prompt carries the digest");
        client.Requests[2].UserPrompt.ShouldNotContain("marker-1-seq", customMessage: "the folded head no longer renders raw");
        client.Requests[2].UserPrompt.ShouldContain("marker-12-seq", customMessage: "the recent tail still renders verbatim");
    }

    [Fact]
    public async Task The_summarizer_never_bakes_an_evidence_tail_into_the_digest()
    {
        // P5-2: the foldable head excludes the newest CompactTailKeep decisions, so any tail the summarizer could
        // see is STALE by construction — the digest keeps the one-line verdict (state), never the oracle output.
        var tape = Tape(12);
        var agentId = Guid.NewGuid();
        tape[1] = tape[1] with
        {
            DecisionKind = SupervisorDecisionKinds.Spawn,
            PayloadJson = """{"subtaskIds":["s1"]}""",
            OutcomeJson = GradedUnitOutcome(agentId, passed: false, detail: "tests-failed-exit-1", evidenceTail: "HEAD-TAIL-MARKER"),
        };

        var store = new FakeTapeStore();
        var client = new CompactionScriptClient("DIGEST: s1 failed its check", new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = new SupervisorPlanPayload { Subtasks = new[] { new SupervisorPlannedSubtask { Id = "t", Title = "t", Instruction = "do t" } } } });
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), store, new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(turnNumber: 12, tape), CancellationToken.None);

        client.Requests[1].UserPrompt.ShouldContain("acceptance FAILED", Case.Sensitive, "the digest keeps the unit's verdict state");
        client.Requests[1].UserPrompt.ShouldNotContain("HEAD-TAIL-MARKER", Case.Sensitive, "a stale oracle tail must never be baked into the persisted rolling digest");
    }

    [Fact]
    public async Task A_tape_too_small_to_fold_degrades_to_the_existing_clean_stop()
    {
        // 6 priors → foldable = max(0, 6−8) = 0 < MinCompactFold: compaction can't shrink the prompt, so the
        // overflow falls to the existing fail-closed clean stop (honest Stopped downstream) and nothing persists.
        var store = new FakeTapeStore();
        var client = new CompactionScriptClient("unused", new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = OnePlannedSubtask() }) { AlwaysOverflow = true };
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), store, new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        var decision = await decider.DecideAsync(Context(turnNumber: 6, Tape(6)), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop, "no compaction win available — the fail-closed stop stands");
        store.Stored.ShouldBeNull("nothing was folded");
        client.Requests.Count.ShouldBe(1, "no summarizer call, no retry — the original fault propagated");
    }

    [Fact]
    public async Task A_persisted_digest_renders_compacted_on_every_later_turn_without_an_overflow()
    {
        // A prior turn compacted; this turn loads the rolling digest at decide time — the prompt is
        // [digest + tail] from the start, no window hit, no new summarizer call.
        var store = new FakeTapeStore { Stored = new SupervisorTapeSummary { UpToSequence = 4, Text = "DIGEST-FROM-EARLIER" } };
        var client = new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = new SupervisorPlanPayload { Subtasks = new[] { new SupervisorPlannedSubtask { Id = "t", Title = "t", Instruction = "do t" } } } });
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), store, new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);

        await decider.DecideAsync(Context(turnNumber: 12, Tape(12)), CancellationToken.None);

        client.LastUserPrompt.ShouldNotBeNull();
        client.LastUserPrompt!.ShouldContain("DIGEST-FROM-EARLIER");
        client.LastUserPrompt.ShouldNotContain("marker-4-seq", customMessage: "folded head rows never render raw again");
        client.LastUserPrompt.ShouldContain("marker-5-seq", customMessage: "rows after the digest boundary render verbatim");
    }

    /// <summary>A tape of <paramref name="count"/> generic prior decisions, Sequence 1..count, each payload carrying a distinct marker.</summary>
    private static SupervisorPriorDecision[] Tape(int count) =>
        Enumerable.Range(1, count).Select(i => new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(),
            Sequence = i,
            DecisionKind = SupervisorDecisionKinds.AskHuman,
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = $$"""{"note":"marker-{{i}}-seq"}""",
            OutcomeJson = "{}",
        }).ToArray();

    /// <summary>Scripted client for the compaction arc: the FIRST decide overflows (ContextLengthExceeded), the summarizer call (recognised by its system prompt) returns the digest, the retried decide returns the final decision. <see cref="AlwaysOverflow"/> makes every decide overflow (the too-small-to-fold path).</summary>
    private sealed class CompactionScriptClient : ILLMClient, IStructuredLLMClient
    {
        private readonly string _digest;
        private readonly SupervisorModelDecision _final;
        private int _decides;

        public CompactionScriptClient(string digest, SupervisorModelDecision final) { _digest = digest; _final = final; }

        public bool AlwaysOverflow { get; init; }

        public readonly List<StructuredLLMCompletionRequest> Requests = new();

        public string Provider => "TestSupervisor";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (request.SystemPrompt.StartsWith("You compact", StringComparison.Ordinal))
                return Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(new { summary = _digest }), Model = request.Model });

            _decides++;

            if (AlwaysOverflow || _decides == 1)
                throw new LlmApiException("TestSupervisor", null, LlmErrorCategory.ContextLengthExceeded, "window exceeded");

            return Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(_final, SupervisorDecisionSchema.Options), Model = request.Model });
        }
    }

    /// <summary>In-memory tape-summary store — the compaction paths are pinned by their own tests; every other decide test just needs a working store.</summary>
    private sealed class FakeTapeStore : ISupervisorTapeSummaryStore
    {
        public SupervisorTapeSummary? Stored;

        public Task<SupervisorTapeSummary?> GetAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult(Stored);

        public Task UpsertAsync(Guid supervisorRunId, Guid teamId, long upToSequence, string summary, CancellationToken cancellationToken)
        {
            Stored = new SupervisorTapeSummary { UpToSequence = upToSequence, Text = summary };
            return Task.CompletedTask;
        }
    }


    /// <summary>No grounding — the decider's grounding is fail-soft; null omits the prompt section.</summary>
    private sealed class NullRepoGrounding : CodeSpace.Core.Services.Workflows.Planning.IRepoGroundingProvider
    {
        public Task<string?> BuildGroundingAsync(Guid? repositoryId, Guid teamId, string? reference, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    /// <summary>Records the grounding lookup (repo + reference) and returns a fixed summary — pins the S2 wiring: the decider grounds at the run's immutable base pin.</summary>
    private sealed class RecordingRepoGrounding : CodeSpace.Core.Services.Workflows.Planning.IRepoGroundingProvider
    {
        public Guid? RepositoryId { get; private set; }
        public string? Reference { get; private set; }

        public Task<string?> BuildGroundingAsync(Guid? repositoryId, Guid teamId, string? reference, CancellationToken cancellationToken)
        {
            RepositoryId = repositoryId; Reference = reference;
            return Task.FromResult<string?>("Repository top-level layout for org/repo at this run's immutable base (abc123def456).");
        }
    }
}
