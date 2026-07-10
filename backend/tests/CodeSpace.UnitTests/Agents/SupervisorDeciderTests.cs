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

    private static SupervisorTurnContext Context(int turnNumber = 0, params SupervisorPriorDecision[] prior) =>
        new() { Goal = "ship the feature", TurnNumber = turnNumber, PriorDecisions = prior, SupervisorModelId = BrainModelId };

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
        prompt.ShouldContain("even though the agent itself reported failure", Case.Insensitive, "the under-claim must be called out, not silently rendered as a clean pass");
        prompt.ShouldContain("do NOT retry", Case.Insensitive, "the work is objectively fine — retrying it wastes a round-trip");
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

        LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, spawn))
            .ShouldNotContain("acceptance", Case.Insensitive, "an ungraded unit renders no acceptance line — byte-identical to pre-slice");
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

    [Fact]
    public void The_user_prompt_renders_a_conflicted_merge_as_a_legible_actionable_block()
    {
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, MergeDecision(ConflictedMergeOutcome)));

        prompt.ShouldContain("INTEGRATION CONFLICTED", Case.Insensitive, "the decider sees the conflict framed as actionable, not buried in raw jsonb");
        prompt.ShouldContain("src/Foo.cs", Case.Insensitive);
        prompt.ShouldContain("src/Bar.cs", Case.Insensitive, "the conflicted files are named so a resolver knows what to reconcile");
        prompt.ShouldContain("codespace/agent/bbb", Case.Insensitive, "the preserved branches are named — the resolver's inputs");
        prompt.ShouldContain("spawn ONE agent", Case.Insensitive, "the resolve move is spelled out");
        prompt.ShouldContain("stop to leave the conflict for a human", Case.Insensitive, "the fail-safe move is offered too");
    }

    [Fact]
    public void A_clean_merge_does_NOT_render_the_conflict_block()
    {
        // Behavior-preserving: only a CONFLICTED integration gets the legible block; a clean merge keeps the compact line.
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, MergeDecision("""{"integration":{"status":"Clean","integratedBranch":"b","outcomes":[]}}""")));

        prompt.ShouldNotContain("INTEGRATION CONFLICTED");
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
    }

    [Fact]
    public async Task A_deployment_with_no_structured_provider_fails_closed_to_a_terminal_stop()
    {
        var decider = new LlmSupervisorDecider(new FakeRegistry(structured: null), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("no model → a clean one-turn no-op stop, never a crash");
    }

    [Fact]
    public async Task A_run_with_no_selected_brain_model_fails_closed_to_a_terminal_stop()
    {
        // supervisorModelId is REQUIRED — the operator must pick the brain model; the supervisor never guesses its own.
        var decider = new LlmSupervisorDecider(new FakeRegistry(new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan })), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan })), FakeSelector.Empty(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(new RawJsonStructuredClient(raw)), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(new ThrowingStructuredClient(category)), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(new ThrowingStructuredClient(category)), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

        await Should.ThrowAsync<LlmApiException>(() => decider.DecideAsync(Context(), CancellationToken.None));
    }

    [Fact]
    public async Task The_decider_calls_with_the_model_the_selector_picked_from_the_pool()
    {
        var fake = new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan });
        var decider = new LlmSupervisorDecider(new FakeRegistry(fake), FakeSelector.WithModel("claude-opus-4-8"), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var client = new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan });
        var decider = new LlmSupervisorDecider(
            new FakeRegistry(client),
            FakeSelector.WithModelAndPool("claude-sonnet-4-5", new PoolModelInfo("metis-coder-max", "Anthropic")),
            new FakeHarnesses(new CatalogHarness("claude-code", "Anthropic", "Custom")), FakePersonas.Empty(), new FakeTapeStore());

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
        var client = new FakeStructuredClient(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan });
        var decider = new LlmSupervisorDecider(
            new FakeRegistry(client),
            FakeSelector.WithModel(),
            new FakeHarnesses(new CatalogHarness("claude-code", "Anthropic", "Custom")),
            FakePersonas.With(("security-reviewer", "Security Reviewer", "Audits for vulnerabilities")), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "the repaired reply lands as the decision");
        client.Requests.Count.ShouldBe(2, "exactly one bounded repair round-trip");
        client.Requests[1].UserPrompt.ShouldContain("$.spawn", customMessage: "the repair prompt NAMES the bind path so the model fixes the right leaf");
        client.Requests[1].UserPrompt.ShouldContain("\"oops\"", customMessage: "the model repairs its OWN reply, not a fresh decide");
        client.Requests[1].SystemPrompt.ShouldContain("corrected decision JSON", customMessage: "repair-only framing — same intent, no new decisions");
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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        new(new FakeRegistry(new FakeStructuredClient(model)), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), new FakeTapeStore());

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
        private FakeSelector(ModelPoolPick? pick, IReadOnlyList<PoolModelInfo>? pool = null) { _pick = pick; _pool = pool ?? Array.Empty<PoolModelInfo>(); }

        public static FakeSelector WithModel(string modelId = "claude-sonnet-4-5") =>
            new(new ModelPoolPick { ModelId = modelId, Credential = new ResolvedModelCredential { Provider = "TestSupervisor", ApiKey = "sk-test" } });

        public static FakeSelector WithModelAndPool(string modelId, params PoolModelInfo[] pool) =>
            new(new ModelPoolPick { ModelId = modelId, Credential = new ResolvedModelCredential { Provider = "TestSupervisor", ApiKey = "sk-test" } }, pool);

        public static FakeSelector Empty() => new(null);

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => Task.FromResult(_pick);

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult(_pick);

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
        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) => throw new NotSupportedException();
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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), store);

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
    public async Task A_tape_too_small_to_fold_degrades_to_the_existing_clean_stop()
    {
        // 6 priors → foldable = max(0, 6−8) = 0 < MinCompactFold: compaction can't shrink the prompt, so the
        // overflow falls to the existing fail-closed clean stop (honest Stopped downstream) and nothing persists.
        var store = new FakeTapeStore();
        var client = new CompactionScriptClient("unused", new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan }) { AlwaysOverflow = true };
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), store);

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
        var decider = new LlmSupervisorDecider(new FakeRegistry(client), FakeSelector.WithModel(), new FakeHarnesses(), FakePersonas.Empty(), store);

        await decider.DecideAsync(Context(turnNumber: 12, Tape(12)), CancellationToken.None);

        client.LastUserPrompt.ShouldNotBeNull();
        client.LastUserPrompt!.ShouldContain("DIGEST-FROM-EARLIER");
        client.LastUserPrompt.ShouldNotContain("marker-4-seq", customMessage: "folded head rows never render raw again");
        client.LastUserPrompt.ShouldContain("marker-5-seq", customMessage: "rows after the digest boundary render verbatim");
    }

    /// <summary>A tape of <paramref name="count"/> merged decisions, Sequence 1..count, each payload carrying a distinct marker.</summary>
    private static SupervisorPriorDecision[] Tape(int count) =>
        Enumerable.Range(1, count).Select(i => new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(),
            Sequence = i,
            DecisionKind = SupervisorDecisionKinds.Merge,
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

}
