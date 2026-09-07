using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The high-value golden supervisor-decision points, each built with FIXED ids / strings (so the rendered prompt —
/// and therefore the cassette key — is byte-stable run-to-run) via the REAL fold helpers (so the context is exactly
/// what the engine produces, not a hand-written JSON that could drift from what the decider reads). The real
/// <c>LlmSupervisorDecider</c> is replayed over each context and scored against its rubric.
/// </summary>
public static class SupervisorDecisionGoldenScenarios
{
    // Fixed agent-run ids: NOT rendered into the prompt (the decider renders status/summary/error by index), but
    // fixed anyway so the folded OutcomeJson + the cassette key never drift across runs.
    private static readonly Guid Agent1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Agent2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Agent3 = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Agent4 = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid Agent5 = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid Resolver = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RetryAgent = Guid.Parse("55555555-5555-5555-5555-555555555555");

    // Ordered by the decision PHASE the brain is in (plan → spawn → inspect/retry → merge → conflict → resolve), so the
    // corpus reads as a comprehensive sweep of the single-decision space. Each point has ONE reasonable action (or a
    // tightly-bounded accepted set) so the live gate measures decision quality, not punishes a reasonable variation.
    public static IReadOnlyList<SupervisorGoldenScenario> All { get; } = new[]
    {
        FirstTurn(),                      // no priors                       → plan
        PlannedNotSpawned(),              // planned, nothing spawned        → spawn
        MixedResults(),                   // 2 subtasks, s2 failed           → retry s2 (positional)
        ThreeSubtaskPartialFailure(),     // 3 subtasks, s2 failed           → retry s2 (positional, richer)
        AllFailed(),                      // both subtasks failed            → retry (recover, don't quit)
        RetriedFailureSucceeded(),        // a retry fixed the failure       → merge
        RetriedStillFailed(),             // the retry STILL failed          → retry-again / stop, NEVER merge
        AllSucceeded(),                   // both succeeded                  → merge
        ThreeSubtaskAllSucceeded(),       // three succeeded                 → merge (larger fan-out)
        CleanIntegration(),               // a clean integrated branch       → stop
        MergeConflict(),                  // the merge conflicted            → resolve
        MultiFileConflict(),              // a conflict across many files    → resolve (don't give up on a hard conflict)
        VerifiedResolution(),             // the resolution passed tests     → accept (merge/stop)
        UnverifiedResolution(),           // the resolution did NOT pass     → resolve/stop, NEVER merge
        // Higher-fan-out sweep — the judgment the ≤3-subtask cases above can't exercise: does it hold at 4-5 subtasks?
        FourSubtaskTwoFailed(),           // 4 subtasks, s2+s4 failed        → retry OR spawn (recover both; don't merge incomplete)
        FiveSubtaskMiddleFailed(),        // 5 subtasks, s3 failed           → retry s3 (positional at high fan-out)
        FourSubtaskAllSucceeded(),        // 4 succeeded                     → merge (largest clean fan-out)
        SubsetConflictAcrossThree(),      // 3 agents, a real conflict       → resolve
        // S3 plan-confirmation gate — the answered confirmation card is in the tape; the brain must REACT to it.
        ConfirmationApproved(),           // plan + card answered "approve"  → spawn (release, don't re-plan)
        ConfirmationFeedback(),           // plan + revision feedback        → plan (a REVISED version, never spawn)
        // Item 4.3's own residual: the FIRST time a check comes back unrunnable, before any re-plan or co-sign.
        FirstInfraFailure(),              // fresh infra-classed failure     → plan/ask, amend_acceptance ALSO offered
        // B5 co-sign loop — the human repaired a unit's ORACLE; only a retry consumes it, and a re-plan destroys it.
        AmendedOracleAwaitingRetry(),     // infra-failed unit + co-signed   → retry s2, NEVER re-plan
        AmendedOracleDiscardedByReplan(), // the re-plan already ATE it      → amend again / ask, NEVER another plan
        // The same fixed point with NO human in it — the far commoner tape, and the one the conflict arm's live runs
        // reached: a re-plan was spent on an unrunnable check and the verdict did not move.
        ReplanLeftTheVerdictUnchanged(),  // the re-plan changed nothing       → amend / ask, NEVER another plan
        // A1.5 resolve NEGATIVE controls — the corpus proved resolve-WHEN-conflicted and nothing else. Naming the
        // verb in the rails (#1271) created the opposite risk, and the action mask (#1274) exists to cover it; only
        // a live model can settle whether it obeys a server fact over conflict-flavoured prose.
        ResolveBaitCleanIntegration(),    // clean merge, overlap prose      → stop, NEVER resolve
        AgentReportedConflictNoRecord(),  // agents SAY conflict, no record  → retry/spawn, NEVER resolve
        ResolveCapSpent(),                // conflict, but the cap is spent  → stop/ask, NEVER resolve (it would KILL the run)
    };

    /// <summary>Turn 0, no priors → the brain must PLAN first (it cannot spawn/retry/merge over non-existent subtasks).</summary>
    private static SupervisorGoldenScenario FirstTurn() => new()
    {
        Name = "first-turn",
        Context = Context(turn: 0, Array.Empty<SupervisorPriorDecision>()),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Plan },
    };

    /// <summary>One agent failed, one succeeded → RETRY the FAILED subtask (s2), not blindly s1 (positional teeth).</summary>
    private static SupervisorGoldenScenario MixedResults() => new()
    {
        Name = "mixed-results",
        Context = Context(turn: 2, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "implemented s1; unit tests green"),
                Agent(Agent2, "Failed", error: "build failed: missing symbol referenced by s2")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Retry },
        PayloadCheck = RetryTargets("s2"),
    };

    /// <summary>All agents succeeded → MERGE the results (the rails say merge before stop; a stop-without-merging quits early).</summary>
    private static SupervisorGoldenScenario AllSucceeded() => new()
    {
        Name = "all-succeeded",
        Context = Context(turn: 2, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "implemented s1; unit tests green", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "implemented s2; unit tests green", branch: "agent/s2")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Merge },
    };

    /// <summary>The merge CONFLICTED → spawn a RESOLVE agent to reconcile + verify (never accept an unmerged conflict by stopping).</summary>
    private static SupervisorGoldenScenario MergeConflict() => new()
    {
        Name = "merge-conflict",
        Context = Context(turn: 3, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "s1", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "s2", branch: "agent/s2")),
            ConflictedMerge(),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Resolve },
    };

    /// <summary>The resolution is VERIFIED (build+tests passed, marker present) → ACCEPT it (merge/stop) — do NOT re-resolve a verified conflict.</summary>
    private static SupervisorGoldenScenario VerifiedResolution() => new()
    {
        Name = "verified-resolution",
        Context = Context(turn: 4, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "s1", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "s2", branch: "agent/s2")),
            ConflictedMerge(),
            Resolve(Agent(Resolver, "Succeeded", summary: $"reconciled the conflict; build and the full test suite pass {SupervisorResolverRecipe.TestsPassedMarker}", branch: "resolve/head")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop },
    };

    /// <summary>Planned, nothing spawned yet → SPAWN over the planned subtasks (the rails say plan THEN spawn; re-planning or merging/stopping with no work done quits early).</summary>
    private static SupervisorGoldenScenario PlannedNotSpawned() => new()
    {
        Name = "planned-not-spawned",
        Context = Context(turn: 1, new[] { Plan("s1", "s2") }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Spawn },
    };

    /// <summary>One subtask FAILED, was RETRIED, and the retry SUCCEEDED → every subtask is now green → MERGE (don't retry again, don't stop before merging).</summary>
    private static SupervisorGoldenScenario RetriedFailureSucceeded() => new()
    {
        Name = "retried-failure-succeeded",
        Context = Context(turn: 3, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "implemented s1; unit tests green", branch: "agent/s1"),
                Agent(Agent2, "Failed", error: "build failed: missing symbol referenced by s2")),
            Retry("s2", Agent(RetryAgent, "Succeeded", summary: "fixed s2; unit tests green", branch: "agent/s2-retry")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Merge },
    };

    /// <summary>The merge integrated CLEANLY (a reviewable branch exists, no conflict) → STOP and ship; the goal is met and nothing remains (re-merging / re-spawning is churn).</summary>
    private static SupervisorGoldenScenario CleanIntegration() => new()
    {
        Name = "clean-integration",
        Context = Context(turn: 3, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "s1", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "s2", branch: "agent/s2")),
            CleanMerge(),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Stop },
    };

    /// <summary>The resolution did NOT pass the build/tests (no verified marker) → do NOT ACCEPT it: retry the resolution (within cap) or stop and leave the conflict for a human. NEVER merge an unverified reconciliation — the safety-critical inverse of <see cref="VerifiedResolution"/>.</summary>
    private static SupervisorGoldenScenario UnverifiedResolution() => new()
    {
        Name = "unverified-resolution",
        // The cap is EXPLICIT and > the one resolve already spent: this scenario's teeth are "re-resolve rather
        // than accept an unverified reconciliation", which only means anything while another resolve is available.
        Context = Context(turn: 4, maxResolveAttempts: 2, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "s1", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "s2", branch: "agent/s2")),
            ConflictedMerge(),
            Resolve(Agent(Resolver, "Succeeded", summary: "attempted to reconcile the conflict, but the build still fails and the tests do not pass", branch: "resolve/head")),
        }),
        // Resolve ALONE, deliberately. Accepting Stop here made this scenario share an answer with its own A/B
        // partner, `resolve-cap-spent`, whose tape is the same failed reconciliation with the budget SPENT and which
        // accepts {Stop, AskHuman}: a model that always stopped passed both, so the pair proved nothing about the
        // cap — the one thing it exists to measure. The shipped prompt directs a resolve in this exact state
        // ("Issue another 'resolve' … within the resolve cap"), so anything else is a real miss, not a judgement call.
        AcceptedKinds = new[] { SupervisorDecisionKinds.Resolve },
    };

    /// <summary>THREE subtasks, only s2 FAILED → RETRY the failed one (positional teeth with a wider fan-out — must target s2, not blindly s1/s3).</summary>
    private static SupervisorGoldenScenario ThreeSubtaskPartialFailure() => new()
    {
        Name = "three-subtask-partial-failure",
        Context = Context(turn: 2, new[]
        {
            Plan("s1", "s2", "s3"),
            Spawn(new[] { "s1", "s2", "s3" },
                Agent(Agent1, "Succeeded", summary: "implemented s1; unit tests green", branch: "agent/s1"),
                Agent(Agent2, "Failed", error: "build failed: missing symbol referenced by s2"),
                Agent(Agent3, "Succeeded", summary: "implemented s3; unit tests green", branch: "agent/s3")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Retry },
        PayloadCheck = RetryTargets("s2"),
    };

    /// <summary>EVERY subtask failed → RETRY to recover the work (don't merge nothing, don't quit on the first failure).</summary>
    private static SupervisorGoldenScenario AllFailed() => new()
    {
        Name = "all-failed",
        Context = Context(turn: 2, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Failed", error: "s1 build failed: unresolved symbol"),
                Agent(Agent2, "Failed", error: "s2 tests failed: assertion error")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Retry },
    };

    /// <summary>The failed subtask was RETRIED and the retry STILL FAILED with the SAME error → the brain is genuinely stuck: retry again, stop and leave it, OR escalate to a human (ask_human) are all reasonable when stuck. The one thing it must NEVER do is MERGE a still-broken subtask (the safety inverse of <see cref="RetriedFailureSucceeded"/>). The accepted set deliberately includes ask_human: NO retry-cap is rendered to the model, so escalating a same-error wall to a human is on-rail — narrowing to {retry, stop} would punish that reasonable choice and flake the live gate. The MERGE rejection is the real teeth.</summary>
    private static SupervisorGoldenScenario RetriedStillFailed() => new()
    {
        Name = "retried-still-failed",
        Context = Context(turn: 3, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "implemented s1; unit tests green", branch: "agent/s1"),
                Agent(Agent2, "Failed", error: "build failed: missing symbol referenced by s2")),
            Retry("s2", Agent(RetryAgent, "Failed", error: "s2 still fails after the retry: the same build error persists")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Stop, SupervisorDecisionKinds.AskHuman },
    };

    /// <summary>THREE subtasks all succeeded → MERGE the wider fan-out (the same rail as <see cref="AllSucceeded"/>, with more contributions to combine).</summary>
    private static SupervisorGoldenScenario ThreeSubtaskAllSucceeded() => new()
    {
        Name = "three-subtask-all-succeeded",
        Context = Context(turn: 2, new[]
        {
            Plan("s1", "s2", "s3"),
            Spawn(new[] { "s1", "s2", "s3" },
                Agent(Agent1, "Succeeded", summary: "implemented s1; unit tests green", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "implemented s2; unit tests green", branch: "agent/s2"),
                Agent(Agent3, "Succeeded", summary: "implemented s3; unit tests green", branch: "agent/s3")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Merge },
    };

    /// <summary>The merge conflicted across MANY files → still RESOLVE (a harder, multi-file conflict must not make the brain give up / stop instead of reconciling).</summary>
    private static SupervisorGoldenScenario MultiFileConflict() => new()
    {
        Name = "multi-file-conflict",
        Context = Context(turn: 3, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "s1", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "s2", branch: "agent/s2")),
            ConflictedMerge("src/Auth.cs", "src/Signup.cs", "src/Validation.cs", "src/Routes.cs"),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Resolve },
    };

    // ── Higher-fan-out sweep (4-5 subtasks) — the judgment the ≤3-subtask cases can't exercise ───────────────────

    /// <summary>4 subtasks, s2 + s4 FAILED → RECOVER the two failures: RETRY (re-run one) or SPAWN (re-fan-out over [s2,s4]) — both are on-rail re-run verbs. A merge would ship a 4-way fan-out that is half-broken; at higher fan-out the brain must still not merge incomplete work.</summary>
    private static SupervisorGoldenScenario FourSubtaskTwoFailed() => new()
    {
        Name = "four-subtask-two-failed",
        Context = Context(turn: 2, new[]
        {
            Plan("s1", "s2", "s3", "s4"),
            Spawn(new[] { "s1", "s2", "s3", "s4" },
                Agent(Agent1, "Succeeded", summary: "implemented s1; unit tests green"),
                Agent(Agent2, "Failed", error: "build failed: missing symbol referenced by s2"),
                Agent(Agent3, "Succeeded", summary: "implemented s3; unit tests green"),
                Agent(Agent4, "Failed", error: "test failure: s4 assertion did not hold")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Spawn },
    };

    /// <summary>5 subtasks, only the MIDDLE one (s3) failed → RETRY s3 (positional teeth at the highest fan-out — retrying s1/s5 instead of the actually-failed s3 is wrong).</summary>
    private static SupervisorGoldenScenario FiveSubtaskMiddleFailed() => new()
    {
        Name = "five-subtask-middle-failed",
        Context = Context(turn: 2, new[]
        {
            Plan("s1", "s2", "s3", "s4", "s5"),
            Spawn(new[] { "s1", "s2", "s3", "s4", "s5" },
                Agent(Agent1, "Succeeded", summary: "implemented s1; unit tests green"),
                Agent(Agent2, "Succeeded", summary: "implemented s2; unit tests green"),
                Agent(Agent3, "Failed", error: "build failed: missing symbol referenced by s3"),
                Agent(Agent4, "Succeeded", summary: "implemented s4; unit tests green"),
                Agent(Agent5, "Succeeded", summary: "implemented s5; unit tests green")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Retry },
        PayloadCheck = RetryTargets("s3"),
    };

    /// <summary>4 subtasks ALL succeeded → MERGE (the largest clean fan-out; the brain must integrate the work, not stop short of shipping or churn re-spawning).</summary>
    private static SupervisorGoldenScenario FourSubtaskAllSucceeded() => new()
    {
        Name = "four-subtask-all-succeeded",
        Context = Context(turn: 2, new[]
        {
            Plan("s1", "s2", "s3", "s4"),
            Spawn(new[] { "s1", "s2", "s3", "s4" },
                Agent(Agent1, "Succeeded", summary: "s1", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "s2", branch: "agent/s2"),
                Agent(Agent3, "Succeeded", summary: "s3", branch: "agent/s3"),
                Agent(Agent4, "Succeeded", summary: "s4", branch: "agent/s4")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Merge },
    };

    /// <summary>3 agents all succeeded but the merge CONFLICTED → RESOLVE (a 3-way fan-out conflict must still be reconciled, not abandoned).</summary>
    private static SupervisorGoldenScenario SubsetConflictAcrossThree() => new()
    {
        Name = "subset-conflict-across-three",
        Context = Context(turn: 3, new[]
        {
            Plan("s1", "s2", "s3"),
            Spawn(new[] { "s1", "s2", "s3" },
                Agent(Agent1, "Succeeded", summary: "s1", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "s2", branch: "agent/s2"),
                Agent(Agent3, "Succeeded", summary: "s3", branch: "agent/s3")),
            ConflictedMerge("src/Shared.cs"),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Resolve },
    };

    // ── Builders (fixed strings; real folds) ─────────────────────────────────────────────────────────────────

    /// <summary>The operator-picked brain model row id — non-null so the real <c>LlmSupervisorDecider</c> proceeds past its fail-closed "no brain model" guard. A PROPERTY for the ordering reason spelled out on <see cref="SupervisorProfile"/>: as a <c>static readonly</c> declared below <see cref="All"/> it read back as <c>Guid.Empty</c> in every context the corpus ships, while the E2E that names the same symbol got the real id — the two disagreed silently.</summary>
    public static Guid BrainModelRowId => Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>The canonical real-model fixture goal — deliberately SPECIFIC and unambiguous (clear deliverable + acceptance) so the only correct first move is to PLAN, never to ask a clarifying question. Shared with the trajectory eval so both gates score the same well-specified task.</summary>
    /// <summary>The fixture's authorized plan ref. Production's plan executor records <c>workPlanId</c>/<c>workPlanVersion</c> on every plan outcome, and the spawn executor stakes NOTHING without one — so a fixture omitting it describes a pre-protocol run whose obligations, and therefore whose stopped-now verdict, do not exist. A property for the same ordering reason as <see cref="BrainModelRowId"/>: read from below <see cref="All"/> it staked every fixture's obligations against an all-zeros plan ref.</summary>
    private static Guid FixtureWorkPlanId => Guid.Parse("9f2c7d10-3c1e-4a5b-9f8a-6d2b41e07c55");

    public const string FixtureGoal = "Add server-side email-format validation to the signup endpoint: reject malformed addresses with HTTP 400 and a clear error message, and cover it with unit tests.";

    /// <summary>The operator APPROVED the plan on the confirmation card → the gate released; the ONLY sensible move is to spawn the confirmed subtasks (re-planning ignores the approval; stopping abandons the goal).</summary>
    private static SupervisorGoldenScenario ConfirmationApproved() => new()
    {
        Name = "confirmation-approved",
        Context = Context(turn: 2, new[] { Plan("s1", "s2"), ConfirmationAnswered("approve") }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Spawn },
    };

    /// <summary>The operator answered the confirmation card with REVISION FEEDBACK → the brain must author a REVISED plan incorporating it — never spawn the rejected plan unchanged, never stop.</summary>
    private static SupervisorGoldenScenario ConfirmationFeedback() => new()
    {
        Name = "confirmation-feedback",
        Context = Context(turn: 2, new[] { Plan("s1", "s2"), ConfirmationAnswered("revise: merge both steps into ONE subtask and verify with ./check.sh") }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Plan },
    };

    /// <summary>
    /// Arc-3 item 4.3's own residual, closed as a golden rather than a prompt edit: the FIRST time a subtask's check
    /// comes back UNRUNNABLE — infra-classed, no re-plan authored over the verdict yet, no amendment ever proposed.
    /// <see cref="LlmSupervisorDecider.InfraSteerFor"/>'s <c>None</c>/<c>None</c> arm renders
    /// <see cref="LlmSupervisorDecider.ReplanThisItemWithASatisfiableCheck"/> here — "Re-plan this item with a check
    /// its agent can satisfy, or ask a human to rule" — and that copy is UNIT-pinned as deliberately unmoving
    /// (<c>SupervisorDeciderTests.An_unrun_re_plan_is_steered_at_the_staging_it_is_waiting_for</c> and its sibling:
    /// "the FIRST time a check comes back unrunnable, authoring a satisfiable one is honest advice — and its wording
    /// must not move"). What no test exercised until this scenario is the SAME state through the golden corpus the
    /// real-model gate actually scores: a live regression in this arm — the commonest infra shape a run will ever
    /// hit, reached before any of the co-sign or re-plan-fixed-point arms below — had no way to surface.
    ///
    /// <para>Both units are UNRUNNABLE, not one clean and one infra-failed, for the reason
    /// <see cref="AmendedOracleDiscardedByReplan"/> and <see cref="ReplanLeftTheVerdictUnchanged"/> already paid for:
    /// a mergeable sibling makes 'merge' a live decision-eval answer against a prompt that truthfully offers it
    /// (run 34085079257), and a golden must leave one right move.</para>
    ///
    /// <para>The accepted set is exactly the two verbs <see cref="LlmSupervisorDecider.ReplanThisItemWithASatisfiableCheck"/>
    /// names — <c>plan</c> and <c>ask_human</c> — never <c>amend_acceptance</c>, even though
    /// <see cref="SupervisorAmendPrecondition.AnyAmendableUnit"/> admits a proposal here too and the roster
    /// therefore OFFERS the verb (<see cref="SupervisorGoldenPromptFidelityTests.Exactly_the_amendable_tapes_offer_the_amend_verb"/>
    /// grows by this scenario's name). Offered-but-not-preferred is not a contradiction — <c>plan</c>/<c>ask_human</c>/
    /// <c>stop</c> are offered on nearly every tape without every tape's steer naming them as the best move — and
    /// forcing the FIRST occurrence straight at a human co-sign, before the free self-service re-plan gets a turn,
    /// is exactly what the pinned unit test above refuses to do.</para>
    /// </summary>
    private static SupervisorGoldenScenario FirstInfraFailure() => new()
    {
        Name = "first-infra-failure",
        Context = Context(turn: 2, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Unrunnable(Agent(Agent1, "Succeeded", summary: "added the email-format validation to the signup handler", branch: "agent/s1")),
                Unrunnable(Agent(Agent2, "Succeeded", summary: "returned HTTP 400 naming the malformed address", branch: "agent/s2"))),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.AskHuman },
    };

    /// <summary>
    /// The amend-acceptance co-sign loop's own decision point (B5): s2's CHECK could not RUN — an infra-classed
    /// grade, a verdict on the oracle and not on the work — and a human has CO-SIGNED a replacement check. Nothing
    /// has been staged since, so s2's recorded verdict was graded by the dead oracle and the only move that
    /// consumes the co-sign is a RETRY of s2.
    ///
    /// <para>The wrong answer this measures is <c>plan</c>, and it is wrong in a way no other scenario can catch:
    /// an approved amendment is anchored to the newest plan, so re-planning DISCARDS the human's ruling and hands
    /// the unit back the check that could not run. Live run 34066916864 did exactly that eight times in a row —
    /// with two amendments co-signed — until the no-progress bound force-stopped it with nothing integrated.</para>
    /// </summary>
    private static SupervisorGoldenScenario AmendedOracleAwaitingRetry() => new()
    {
        Name = "amended-oracle-awaiting-retry",
        Context = Context(turn: 3, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "added the email-format validation to the signup handler", branch: "agent/s1"),
                Unrunnable(Agent(Agent2, "Succeeded", summary: "returned HTTP 400 naming the malformed address", branch: "agent/s2"))),
            AmendApproved("s2"),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Retry },
        PayloadCheck = RetryTargets("s2"),
    };

    /// <summary>
    /// The SAME loop one turn later, at the point the first cut of the steer could not see: the co-signs happened,
    /// and then a re-plan DISCARDED them (MAJOR-8 anchors an approved amendment to the newest plan). Both units are
    /// back on the check that could not run, holding repairs the run has already thrown away once.
    ///
    /// <para>The wrong answer this measures is <c>plan</c> AGAIN — and it is the answer the pre-fix prompt actively
    /// invited, because after the re-plan the unit's standing fell back to "never co-signed" and its verdict line
    /// re-rendered "Re-plan this item with a check its agent can satisfy". That is the loop run 34066916864 spent
    /// eight turns in. The moves that go anywhere are re-proposing the amendment (which re-anchors the repaired
    /// check to the CURRENT plan, where a retry can consume it) or handing the oracle to a human. A retry is wrong
    /// too: with the amendment gone there is no repaired check for it to grade under.</para>
    ///
    /// <para>The accepted set names <c>amend_acceptance</c> beside <c>ask_human</c> because that is the verb the
    /// MODEL authors; the projector rewrites it into the ask card (<see cref="SupervisorAmendAcceptance.IntoAskHuman"/>),
    /// so the kind that reaches the scorer is always <c>ask_human</c>.</para>
    ///
    /// <para>BOTH units are unrunnable and both co-signs are discarded, and that is the fixture's whole load-bearing
    /// shape: with one clean sibling the tape carries a mergeable result, the recitation truthfully offers
    /// "'merge' will include them", and <c>merge</c> becomes a defensible move — which is exactly what the live
    /// eval returned on BOTH wires when this scenario shipped with s1 accepted, scoring a corpus point against a
    /// prompt that really did admit two answers. A golden must leave one. Nothing here is mergeable
    /// (<see cref="SupervisorOutcome.IsWithheldFromHead"/> withholds every rejected unit), nothing is retryable
    /// (no co-sign is in force), and nothing is pending — so the amendment is the only door left.</para>
    /// </summary>
    private static SupervisorGoldenScenario AmendedOracleDiscardedByReplan() => new()
    {
        Name = "amended-oracle-discarded-by-replan",
        Context = Context(turn: 4, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Unrunnable(Agent(Agent1, "Succeeded", summary: "added the email-format validation to the signup handler", branch: "agent/s1")),
                Unrunnable(Agent(Agent2, "Succeeded", summary: "returned HTTP 400 naming the malformed address", branch: "agent/s2"))),
            AmendApproved("s1", sequence: 2),
            AmendApproved("s2", sequence: 3),
            RePlan(4, "s1", "s2"),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.AmendAcceptance, SupervisorDecisionKinds.AskHuman },
    };

    /// <summary>
    /// A re-plan already SPENT on an unrunnable check, with NO co-sign anywhere on the tape — so every amended
    /// reading is inapplicable and the un-amended infra steer used to re-render "Re-plan this item with a check its
    /// agent can satisfy" verbatim, turn after turn. Nothing about a re-plan clears the recorded verdict, so the
    /// identical steer re-rendered and the run walked a fixed point into the no-progress bound: arm
    /// <c>The_real_model_observes_a_real_conflict_and_chooses_to_resolve</c> failed ~25-40% of its attempts this way
    /// (<c>plan→spawn→plan×6→stop</c>, runs 34104701023 and 34101026801 attempt 2).
    ///
    /// <para>The re-plan is followed by a RE-SPAWN whose grade comes back identical, and that fourth prior is
    /// load-bearing. Without it the tape stops one turn earlier — a plan authored and never run — where the honest
    /// move is to STAGE it, so <c>spawn</c> is defensible and the accepted pair below would score a corpus point
    /// against a prompt that genuinely admits a third answer. A golden must leave one right move
    /// (<see cref="SupervisorReplanStanding"/> splits those two tapes for exactly this reason; the unrun arm is
    /// pinned at the unit level, where the ambiguity does not exist).</para>
    ///
    /// <para>The sibling of <see cref="AmendedOracleDiscardedByReplan"/> minus the two co-signs, so the accepted set
    /// is the same pair and for the same reason: the checks could not RUN, which is exactly the verdict shape
    /// <see cref="SupervisorAmendPrecondition"/> admits an amendment against, and nothing on the tape is mergeable
    /// (every attempt is acceptance-rejected, so <see cref="SupervisorOutcome.IsWithheldFromHead"/> withholds them),
    /// retryable (another pass cannot fix a check that cannot run) or pending.</para>
    /// </summary>
    private static SupervisorGoldenScenario ReplanLeftTheVerdictUnchanged() => new()
    {
        Name = "re-plan-left-the-verdict-unchanged",
        Context = Context(turn: 4, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Unrunnable(Agent(Agent1, "Succeeded", summary: "added the email-format validation to the signup handler", branch: "agent/s1")),
                Unrunnable(Agent(Agent2, "Succeeded", summary: "returned HTTP 400 naming the malformed address", branch: "agent/s2"))),
            RePlan(2, "s1", "s2"),
            ReSpawn(3, new[] { "s1", "s2" },
                Unrunnable(Agent(Agent3, "Succeeded", summary: "added the email-format validation to the signup handler", branch: "agent/s1")),
                Unrunnable(Agent(Agent4, "Succeeded", summary: "returned HTTP 400 naming the malformed address", branch: "agent/s2"))),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.AmendAcceptance, SupervisorDecisionKinds.AskHuman },
    };

    /// <summary>The SAME plan re-authored as a later version — the re-plan whose MAJOR-8 anchoring discards every approved amendment that predates it. Built from <see cref="Plan"/> so the payload shape cannot drift from the plan the corpus already renders.</summary>
    private static SupervisorPriorDecision RePlan(long sequence, params string[] subtaskIds) =>
        Plan(subtaskIds) with
        {
            Sequence = sequence,
            OutcomeJson = JsonSerializer.Serialize(new { planned = subtaskIds, count = subtaskIds.Length, workPlanId = FixtureWorkPlanId, workPlanVersion = 2 }, AgentJson.Options),
        };

    /// <summary>A SECOND staging of the same units under a later plan — <see cref="Spawn"/>'s sequenced sibling (that one is the tape's first staging and pins sequence 1), so a fixture can show what the re-planned check actually graded.</summary>
    private static SupervisorPriorDecision ReSpawn(long sequence, string[] subtaskIds, params SupervisorAgentResult[] results) =>
        Spawn(subtaskIds, results) with { Sequence = sequence };

    /// <summary>A unit whose CHECK could not run: a FAILED grade whose detail classifies INFRA (<see cref="AgentAcceptanceContract.IsInfraFailure(string?, bool)"/>), which is the only verdict shape an amend proposal is admissible against. No evidence id — an oracle that never ran captured nothing to point at.</summary>
    private static SupervisorAgentResult Unrunnable(SupervisorAgentResult result) =>
        result with { AcceptancePassed = false, AcceptanceDetail = "grade-error: npm not found", AcceptanceEvidenceId = null };

    /// <summary>An amend card the human APPROVED — built from the production card builder (so the marker sentence and the structured proposal are exactly what the co-sign overlay and the retry obligation read back) with a per-target FIXED token for byte-stable prompts. The sequence is explicit because a tape carrying TWO co-signs must order them, and <see cref="SupervisorAmendObligation.StandingFor"/> decides on that order.</summary>
    private static SupervisorPriorDecision AmendApproved(string subtaskId, long sequence = 2)
    {
        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = subtaskId,
            Reason = "the authored check shells out to a package manager this repository does not have, so it fails before it can grade anything",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "dotnet", "test" } },
        });
        var outcome = JsonSerializer.Serialize(new { question = $"amend {subtaskId}'s acceptance", askHumanToken = $"fixed-amend-token-{subtaskId}", answer = "approve" }, AgentJson.Options);

        return PriorDecision(SupervisorDecisionKinds.AskHuman, sequence, card.PayloadJson, outcome);
    }

    /// <summary>The S3 gate's own confirmation card, already ANSWERED — built from the production card builder (so the question is exactly what the gate injects) with a FIXED token for byte-stable prompts.</summary>
    private static SupervisorPriorDecision ConfirmationAnswered(string answer)
    {
        var card = SupervisorPlanConfirmation.IntoAskHuman(planVersion: 1, itemCount: 2, delivery: null, priorApprovedDelivery: null);
        var outcome = JsonSerializer.Serialize(new { question = "confirm plan v1", askHumanToken = "fixed-confirmation-token", answer }, AgentJson.Options);

        return PriorDecision(SupervisorDecisionKinds.AskHuman, 1, card.PayloadJson, outcome);
    }

    /// <summary>
    /// A1.5 NEGATIVE CONTROL — the integration is CLEAN, but its prose says both agents edited the same file. The
    /// only correct move is to finish; a resolve here reconciles nothing and costs a turn. This is the M0 failure
    /// class in its purest form: does the model act on the server's recorded FACT or on the surrounding words?
    /// </summary>
    private static SupervisorGoldenScenario ResolveBaitCleanIntegration() => new()
    {
        Name = "resolve-bait-clean-integration",
        Context = Context(turn: 3, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "reworked the signup validation in src/Signup.cs", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "added the signup telemetry hook in src/Signup.cs", branch: "agent/s2")),
            CleanMergeWithOverlap(),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Stop },
    };

    /// <summary>
    /// A1.5 NEGATIVE CONTROL, the sharpest one — the AGENTS report a merge conflict in their own summaries, but no
    /// integration was ever recorded, so the server knows of no conflict and a resolve would no-op. The work still
    /// needs recovering, so the honest moves are retry/spawn.
    /// </summary>
    private static SupervisorGoldenScenario AgentReportedConflictNoRecord() => new()
    {
        Name = "agent-reported-conflict-no-integration",
        Context = Context(turn: 2, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "landed the auth refactor", branch: "agent/s1"),
                Agent(Agent2, "Failed", error: "merge conflict in src/Auth.cs — this needs reconciling against agent/s1 before it can land")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Spawn },
    };

    /// <summary>
    /// A1.5 NEGATIVE CONTROL whose miss ENDS THE RUN — a real conflict, a resolve already spent, and the cap at
    /// one. Unlike an over-cap spawn wave, which is merely refused, a further resolve force-stops the whole run, so
    /// the only honest moves are to stop or hand the conflict to a human. The cap is EXPLICIT here: this scenario
    /// is about the boundary, so it must not depend on a fallback.
    /// </summary>
    private static SupervisorGoldenScenario ResolveCapSpent() => new()
    {
        Name = "resolve-cap-spent",
        Context = Context(turn: 4, maxResolveAttempts: 1, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "s1", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "s2", branch: "agent/s2")),
            ConflictedMerge(),
            Resolve(Agent(Resolver, "Succeeded", summary: "attempted to reconcile the conflict, but the build still fails and the tests do not pass", branch: "resolve/head")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Stop, SupervisorDecisionKinds.AskHuman },
    };

    private static SupervisorTurnContext Context(int turn, IReadOnlyList<SupervisorPriorDecision> priors) =>
        new()
        {
            Goal = FixtureGoal,
            TurnNumber = turn,
            PriorDecisions = priors,
            SupervisorModelId = BrainModelRowId,
            CompletionRecital = RenderStoppedNowRecital(priors, null),
        };

    /// <summary>
    /// The mode profile these fixtures answer to — the corpus grades the SUPERVISOR lane, so it is that lane's
    /// committed profile, resolved from the production registry rather than a fixture that could declare a stage
    /// story production never enforces.
    ///
    /// <para>A PROPERTY, not a static readonly field, and that is load-bearing. <see cref="All"/> is a static
    /// initializer that calls <see cref="Context(int, IReadOnlyList{SupervisorPriorDecision})"/> for every scenario,
    /// and static field initializers run in TEXTUAL order — a field declared here, below <see cref="All"/>, is
    /// still null while every scenario is being built. The renderer treats a null profile as "this mode declares no
    /// stages" and silently emits no stage line, which is byte-for-byte the dimensions-only rendering this change
    /// exists to remove: the corpus would have looked re-pinned while measuring exactly what it measured before.</para>
    /// </summary>
    private static Messages.Contracts.ModeProfile SupervisorProfile => new Core.Services.Completion.ModeProfileRegistry().Resolve(Core.Services.Completion.RunModeKeys.Supervisor)!;

    /// <summary>
    /// The stopped-now recital, through the SAME projection production's composer reduces to — null until an
    /// authorized wave has staked an obligation, exactly as production omits the block until then. Shared with the
    /// multi-turn trajectory harness, which renders the identical block from the identical inputs.
    ///
    /// <para>All FOUR of the block's inputs are supplied, or the corpus grades the model on a prompt production
    /// does not ship: the assessment, the tape's own upstream STAGE TRACE, the lane's mode profile, and the LANDING
    /// REACH the action mask derives from the same tape and cap. Withholding the middle two kept the corpus a
    /// dimensions-only rendering, so a conflicted-then-unverified fixture read LESS unresolved than the same tape
    /// does in production — the pinned digest could not have detected a regression in a line no scenario was able
    /// to reach. Withholding the last one let the steer name a landing verb off no state at all, which is the miss
    /// <c>resolve-cap-spent</c> reproduced live (run 34027621996): "Land that work" over a recorded conflict whose
    /// resolve cap was spent reads as <c>merge</c>, and merging there just repeats the conflicted integration.</para>
    ///
    /// <para><paramref name="maxResolveAttempts"/> is the run's own cap and MUST be the same value the scenario puts
    /// on its context — a recital rendered against the lane default while the context declares 2 would tell the
    /// model a resolve is spent that the mask three lines below reports as available.</para>
    ///
    /// <para>The enforcement mode is derived the way a launching run derives it — <c>CompletionPolicy.DefaultModeFor</c>
    /// over THIS lane's profile — never the <c>CurrentMode</c> fallback constant. The supervisor profile holds
    /// Enforceable standing, so a default supervisor run is stamped Enforced and reads the REFUSAL lead; pinning the
    /// corpus to the constant would have shown the model an advisory production stopped shipping the day that profile
    /// graduated, and neither the digest nor a drift test pinned to the same constant could have seen it.</para>
    /// </summary>
    public static string? RenderStoppedNowRecital(IReadOnlyList<SupervisorPriorDecision> priors, int? maxResolveAttempts)
    {
        if (SupervisorTapeCompletion.ProjectIfStoppedNow(priors) is not { } stoppedNow) return null;

        var profile = SupervisorProfile;
        var landingReach = SupervisorActionMask.LandingReachFor(priors, maxResolveAttempts);

        return SupervisorStopNowRecital.Render(stoppedNow.Assessment, stoppedNow.ExercisedUpstreamStages, profile, Core.Services.Completion.CompletionPolicy.DefaultModeFor(profile), null, landingReach);
    }

    /// <summary>
    /// A context whose resolve budget is EXPLICIT. A scenario that intends another resolve to be available must say
    /// so: an unset cap falls back to the lane default of ONE, under which the action mask correctly reports that a
    /// further resolve would force-stop the run — so a scenario asking the model to resolve again while silently
    /// leaving the cap at 1 is measuring disobedience to the prompt, not judgement.
    /// </summary>
    private static SupervisorTurnContext Context(int turn, int maxResolveAttempts, IReadOnlyList<SupervisorPriorDecision> priors) =>
        Context(turn, priors) with { MaxResolveAttempts = maxResolveAttempts, CompletionRecital = RenderStoppedNowRecital(priors, maxResolveAttempts) };

    /// <summary>
    /// One folded agent result. These tapes STAKE an acceptance obligation per unit, so production would also fold
    /// the grader's VERDICT for that unit — a staked obligation that is never answered is the shape of a run whose
    /// every grading failed, not of a run that worked. Leaving it unanswered kept every completion dimension at
    /// Unknown forever, which made the stopped-now recital an obligation no action could ever discharge.
    /// The evidence id rides along because admission caps an unevidenced PASS on a required obligation at
    /// InfraUnknown — a pass asserted without it would silently not count.
    /// </summary>
    private static SupervisorAgentResult Agent(Guid id, string status, string? summary = null, string? error = null, string? branch = null)
    {
        var passed = status == "Succeeded";

        return new()
        {
            AgentRunId = id,
            Status = status,
            Summary = summary,
            Error = error,
            ProducedBranch = branch,
            AcceptancePassed = passed,
            AcceptanceDetail = passed ? "tests-passed" : "tests-failed-exit-1",
            AcceptanceEvidenceId = passed ? Guid.NewGuid() : null,
            // The delivery attestation a real pushed result carries: the provider-confirmed tip, its base, and the
            // publish evidence minted where the push was observed. Without these the staked delivery obligation can
            // never settle, and the stopped-now recital reads "you still owe delivery" forever — an instruction no
            // action can discharge, which the live gate answered with plan→spawn loops.
            PushedCommitSha = branch is null ? null : $"cafe{Math.Abs(branch.GetHashCode()):x8}",
            BaseSha = branch is null ? null : "base0000feed",
            PublishEvidenceId = branch is null ? null : Guid.NewGuid(),
        };
    }

    /// <summary>
    /// A plan prior carrying the PRODUCTION payload shape. It used to serialize an anonymous <c>{ subtasks: ["s1"] }</c>
    /// — a string array where <see cref="SupervisorPlanPayload"/> holds objects with required Id/Title/Instruction.
    /// <see cref="SupervisorOutcome.ReadPlanSubtasks"/> threw, was caught, and returned empty, so
    /// <c>SupervisorRecitation</c> rendered nothing: the CURRENT PLAN STATE block — the one that names which subtask
    /// is done, which failed and which is still unfinished — was absent from EVERY golden prompt ever scored, while
    /// production emits it on every turn after a plan. The scenarios graded on naming the failed subtask id were
    /// measuring positional inference off a raw payload dump, not the recited list production actually shows.
    /// Built from the real record rather than another anonymous type, so the shape cannot drift away again.
    /// </summary>
    private static SupervisorPriorDecision Plan(params string[] subtaskIds)
    {
        // Plan-item copy that decomposes FixtureGoal for real. The first version synthesised "subtask s1" /
        // "implement s1", and the live gate answered immediately: three scenarios that had been passing started
        // choosing 'plan' — the model, now finally SHOWN a plan, read a contentless one against a concrete goal and
        // quite reasonably decided to write a better one. Production plan items carry the model's own titles and
        // instructions, so placeholder copy is not a neutral stand-in; it is an active invitation to re-plan.
        // Locals, not static fields: these are consumed by All's own initializer, so a field would have to be
        // declared above it and would break again the moment someone reordered the file.
        string[] titles =
        {
            "Validate the email format on the signup endpoint",
            "Return HTTP 400 with a clear error message",
            "Cover the validation with unit tests",
            "Reject addresses missing a domain part",
            "Document the new 400 response",
        };

        string[] instructions =
        {
            "Add server-side email-format validation to the signup endpoint handler.",
            "Reject a malformed address with HTTP 400 and a message naming what was wrong.",
            "Add unit tests covering valid, malformed, and empty addresses.",
            "Extend the validator to reject addresses with no domain part.",
            "Update the endpoint's API documentation with the new 400 response.",
        };

        var subtasks = subtaskIds.Select((id, i) => new SupervisorPlannedSubtask
        {
            Id = id,
            Title = titles[i % titles.Length],
            Instruction = instructions[i % instructions.Length],
        }).ToList();

        return PriorDecision(SupervisorDecisionKinds.Plan, 0,
            JsonSerializer.Serialize(new SupervisorPlanPayload { Goal = FixtureGoal, Subtasks = subtasks }, AgentJson.Options),
            JsonSerializer.Serialize(new { planned = subtaskIds, count = subtasks.Count, workPlanId = FixtureWorkPlanId, workPlanVersion = 1 }, AgentJson.Options));
    }

    private static SupervisorPriorDecision Spawn(string[] subtaskIds, params SupervisorAgentResult[] results)
    {
        var ids = results.Select(r => r.AgentRunId).ToArray();
        var staged = JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options);

        return PriorDecision(SupervisorDecisionKinds.Spawn, 1, JsonSerializer.Serialize(new { subtaskIds }, AgentJson.Options), SupervisorOutcome.FoldAgentResults(staged, results));
    }

    private static SupervisorPriorDecision ConflictedMerge() => ConflictedMerge("src/Feature.cs");

    private static SupervisorPriorDecision ConflictedMerge(params string[] conflictedFiles)
    {
        var outcome = JsonSerializer.Serialize(new
        {
            integration = new
            {
                status = "Conflicted",
                reason = "the agents edited the same file(s)",
                outcomes = new[]
                {
                    new { conflictedFiles, fallbackBranch = "agent/s1" },
                    new { conflictedFiles = Array.Empty<string>(), fallbackBranch = "agent/s2" },
                },
            },
        }, AgentJson.Options);

        return PriorDecision(SupervisorDecisionKinds.Merge, 2, "{}", outcome);
    }

    private static SupervisorPriorDecision Resolve(SupervisorAgentResult resolver)
    {
        var staged = JsonSerializer.Serialize(new { agentRunIds = new[] { resolver.AgentRunId }, agentCount = 1 }, AgentJson.Options);

        return PriorDecision(SupervisorDecisionKinds.Resolve, 3, "{}", SupervisorOutcome.FoldAgentResults(staged, new[] { resolver }));
    }

    private static SupervisorPriorDecision Retry(string subtaskId, SupervisorAgentResult result)
    {
        var staged = JsonSerializer.Serialize(new { agentRunIds = new[] { result.AgentRunId }, agentCount = 1 }, AgentJson.Options);

        return PriorDecision(SupervisorDecisionKinds.Retry, 2, JsonSerializer.Serialize(new { subtaskId }, AgentJson.Options), SupervisorOutcome.FoldAgentResults(staged, new[] { result }));
    }

    /// <summary>A CLEAN integration whose PROSE is conflict-flavoured — both agents touched the same file and the reason says so. The server fact is "Clean"; only the surface text tempts a resolve.</summary>
    private static SupervisorPriorDecision CleanMergeWithOverlap() =>
        PriorDecision(SupervisorDecisionKinds.Merge, 2, "{}", JsonSerializer.Serialize(new
        {
            integration = new
            {
                status = "Clean",
                integratedBranch = "codespace/integration/head",
                reason = "both agents edited src/Signup.cs; the changes combined without a conflict",
            },
        }, AgentJson.Options));

    private static SupervisorPriorDecision CleanMerge() =>
        PriorDecision(SupervisorDecisionKinds.Merge, 2, "{}", JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch = "codespace/integration/head" } }, AgentJson.Options));

    private static SupervisorPriorDecision PriorDecision(string kind, long sequence, string payloadJson, string outcomeJson) =>
        new() { Id = Guid.Empty, Sequence = sequence, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson };

    /// <summary>
    /// The retry teeth: the target must be the FAILED subtask. A BLANK target is reported as its own miss, because it
    /// is not a targeting error at all — the model authored no <c>retry</c> object, <c>SupervisorDecisionProjector</c>
    /// substituted an empty subtaskId, and the bounded payload re-asks failed to recover one. Read as
    /// "retry targeted ''" that miss looks like a model that named an empty string, sending a reader hunting a
    /// targeting bug that is really a payload-shape one.
    /// </summary>
    internal static Func<SupervisorDecision, (bool Ok, string Note)> RetryTargets(string expectedSubtaskId) => decision =>
    {
        var subtaskId = JsonDocument.Parse(decision.PayloadJson).RootElement.TryGetProperty("subtaskId", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;

        if (subtaskId == expectedSubtaskId) return (true, "ok");

        return (false, string.IsNullOrWhiteSpace(subtaskId)
            ? $"retry authored NO target — the 'retry' object was absent and the bounded payload re-ask never recovered one; expected the failed subtask '{expectedSubtaskId}'"
            : $"retry targeted '{subtaskId}', expected the failed subtask '{expectedSubtaskId}'");
    };
}
