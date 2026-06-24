using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
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
        Context = Context(turn: 4, new[]
        {
            Plan("s1", "s2"),
            Spawn(new[] { "s1", "s2" },
                Agent(Agent1, "Succeeded", summary: "s1", branch: "agent/s1"),
                Agent(Agent2, "Succeeded", summary: "s2", branch: "agent/s2")),
            ConflictedMerge(),
            Resolve(Agent(Resolver, "Succeeded", summary: "attempted to reconcile the conflict, but the build still fails and the tests do not pass", branch: "resolve/head")),
        }),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Resolve, SupervisorDecisionKinds.Stop },
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

    /// <summary>The operator-picked brain model row id — non-null so the real <c>LlmSupervisorDecider</c> proceeds past its fail-closed "no brain model" guard.</summary>
    public static readonly Guid BrainModelRowId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>The canonical real-model fixture goal — deliberately SPECIFIC and unambiguous (clear deliverable + acceptance) so the only correct first move is to PLAN, never to ask a clarifying question. Shared with the trajectory eval so both gates score the same well-specified task.</summary>
    public const string FixtureGoal = "Add server-side email-format validation to the signup endpoint: reject malformed addresses with HTTP 400 and a clear error message, and cover it with unit tests.";

    private static SupervisorTurnContext Context(int turn, IReadOnlyList<SupervisorPriorDecision> priors) =>
        new() { Goal = FixtureGoal, TurnNumber = turn, PriorDecisions = priors, SupervisorModelId = BrainModelRowId };

    private static SupervisorAgentResult Agent(Guid id, string status, string? summary = null, string? error = null, string? branch = null) =>
        new() { AgentRunId = id, Status = status, Summary = summary, Error = error, ProducedBranch = branch };

    private static SupervisorPriorDecision Plan(params string[] subtaskIds) =>
        PriorDecision(SupervisorDecisionKinds.Plan, 0, JsonSerializer.Serialize(new { subtasks = subtaskIds }, AgentJson.Options), JsonSerializer.Serialize(new { planned = subtaskIds }, AgentJson.Options));

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

    private static SupervisorPriorDecision CleanMerge() =>
        PriorDecision(SupervisorDecisionKinds.Merge, 2, "{}", JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch = "codespace/integration/head" } }, AgentJson.Options));

    private static SupervisorPriorDecision PriorDecision(string kind, long sequence, string payloadJson, string outcomeJson) =>
        new() { Id = Guid.Empty, Sequence = sequence, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson };

    private static Func<SupervisorDecision, (bool Ok, string Note)> RetryTargets(string expectedSubtaskId) => decision =>
    {
        var subtaskId = JsonDocument.Parse(decision.PayloadJson).RootElement.TryGetProperty("subtaskId", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        return subtaskId == expectedSubtaskId ? (true, "ok") : (false, $"retry targeted '{subtaskId}', expected the failed subtask '{expectedSubtaskId}'");
    };
}
