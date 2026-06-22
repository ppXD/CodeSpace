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
    private static readonly Guid Resolver = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static IReadOnlyList<SupervisorGoldenScenario> All { get; } = new[]
    {
        FirstTurn(),
        MixedResults(),
        AllSucceeded(),
        MergeConflict(),
        VerifiedResolution(),
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

    private static SupervisorPriorDecision ConflictedMerge()
    {
        var outcome = JsonSerializer.Serialize(new
        {
            integration = new
            {
                status = "Conflicted",
                reason = "two agents edited the same file",
                outcomes = new[]
                {
                    new { conflictedFiles = new[] { "src/Feature.cs" }, fallbackBranch = "agent/s1" },
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

    private static SupervisorPriorDecision PriorDecision(string kind, long sequence, string payloadJson, string outcomeJson) =>
        new() { Id = Guid.Empty, Sequence = sequence, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson };

    private static Func<SupervisorDecision, (bool Ok, string Note)> RetryTargets(string expectedSubtaskId) => decision =>
    {
        var subtaskId = JsonDocument.Parse(decision.PayloadJson).RootElement.TryGetProperty("subtaskId", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        return subtaskId == expectedSubtaskId ? (true, "ok") : (false, $"retry targeted '{subtaskId}', expected the failed subtask '{expectedSubtaskId}'");
    };
}
