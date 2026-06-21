using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// A golden supervisor-decision scenario: a fixed turn CONTEXT (built with fixed ids/strings so the cassette key is
/// deterministic — see <c>SupervisorDecisionGoldenScenarios</c>), the set of decision KINDS that are acceptable at
/// that point, and an optional teeth-bearing PAYLOAD check (e.g. a retry must target the FAILED subtask). The
/// driver replays the REAL <c>LlmSupervisorDecider</c> over the context and scores the produced decision against
/// this rubric. The rubric scores DECISION SELECTION only — it does NOT measure plan decomposition quality,
/// division of labor, instruction quality, or merge-synthesis quality; pass/total is the floor that catches a
/// brain that plans-then-stops or accepts an unverified resolution, not a verdict that "the brain is good".
/// </summary>
public sealed record SupervisorGoldenScenario
{
    public required string Name { get; init; }

    public required SupervisorTurnContext Context { get; init; }

    /// <summary>The decision kinds acceptable at this point (e.g. a first turn must <c>plan</c>; an all-succeeded spawn must <c>merge</c>).</summary>
    public required IReadOnlyList<string> AcceptedKinds { get; init; }

    /// <summary>An optional teeth-bearing check on the decision's payload (e.g. <c>retry.subtaskId</c> targets the failed subtask). Null = kind alone.</summary>
    public Func<SupervisorDecision, (bool Ok, string Note)>? PayloadCheck { get; init; }
}

/// <summary>One scenario's scored outcome — the row a <c>SupervisorDecisionScorecard</c> aggregates.</summary>
public sealed record SupervisorDecisionScore
{
    public required string Scenario { get; init; }
    public required string ActualKind { get; init; }
    public required IReadOnlyList<string> AcceptedKinds { get; init; }
    public required bool Pass { get; init; }
    public required string Note { get; init; }
}

/// <summary>
/// Pure, deterministic scorer for a supervisor decision against a golden scenario's rubric (kind ∈ accepted set,
/// then the optional payload check). No model, no I/O — the CI-active part that the always-on test exercises and
/// the real-model replay scores against. Aggregates to a pass/total scorecard.
/// </summary>
public static class SupervisorDecisionEval
{
    public static SupervisorDecisionScore Score(SupervisorGoldenScenario scenario, SupervisorDecision decision)
    {
        if (!scenario.AcceptedKinds.Contains(decision.Kind))
            return Fail(scenario, decision.Kind, $"kind '{decision.Kind}' is not in the accepted set {{{string.Join(", ", scenario.AcceptedKinds)}}}");

        if (scenario.PayloadCheck is { } check)
        {
            var (ok, note) = check(decision);
            if (!ok) return Fail(scenario, decision.Kind, note);
        }

        return new SupervisorDecisionScore { Scenario = scenario.Name, ActualKind = decision.Kind, AcceptedKinds = scenario.AcceptedKinds, Pass = true, Note = "ok" };
    }

    private static SupervisorDecisionScore Fail(SupervisorGoldenScenario scenario, string actualKind, string note) =>
        new() { Scenario = scenario.Name, ActualKind = actualKind, AcceptedKinds = scenario.AcceptedKinds, Pass = false, Note = note };

    /// <summary>Aggregate a run's per-scenario scores; <see cref="AllPassed"/> is the kill-gate the replay test asserts.</summary>
    public static (int Passed, int Total, bool AllPassed) Aggregate(IReadOnlyList<SupervisorDecisionScore> scores)
    {
        var passed = scores.Count(s => s.Pass);
        return (passed, scores.Count, passed == scores.Count && scores.Count > 0);
    }
}
