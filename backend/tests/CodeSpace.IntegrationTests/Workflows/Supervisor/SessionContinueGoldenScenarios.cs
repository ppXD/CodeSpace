using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Projection.Builders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Session CONTEXT-HANDOFF golden points — the multi-run complement to the single-decision kill-gate. Each is a
/// CONTINUING turn whose supervisor <c>Goal</c> is the REAL <see cref="AgentNodeMapping.ComposeGoal"/> of a follow-up
/// over a prior-turn DIGEST rendered in the <c>SessionContextBuilder</c> shape (mixing prior turns of DIFFERENT modes
/// — a single-agent <c>summary</c>, a standard <c>combined</c>, a deep <c>reason</c>). The rubric scores whether the
/// brain USES the handed-off context: it must NOT re-do work the digest says is already shipped, and it must address
/// the NEW ask (read the follow-up, not just echo prior work). The real <see cref="Core.Services.Supervisor.Deciders.LlmSupervisorDecider"/>
/// is replayed over each by <c>RealModelSessionContinueFlowTests</c>; the always-on <c>SessionContinueEvalTests</c>
/// pins the rubric + that the digest actually reaches the prompt. Reuses the supervisor decision-eval harness.
///
/// <para>SCOPE: this exercises the SUPERVISOR (deep) handoff surface — the one whose prompt the in-process gateway
/// model drives, so it is gateway-runnable on the real-model CI lane. The single-agent / map agent-CLI surfaces
/// inject the SAME grounding (via the shared <see cref="AgentNodeMapping.ComposeGoal"/>, proven by S4a's tests), but
/// a real CLI agent reading it is not gateway-runnable in CI — that is a future real-CLI E2E, not this gate.</para>
///
/// <para>TEETH: <c>continue-redundant-complete</c> is the SHARP handoff test (a clean decision-kind: does the brain
/// override the default plan-first rail because the digest says the work is already done). The other three accept
/// <c>plan</c> and confirm the brain proceeds coherently over a (mixed-mode) digest and ADDRESSES the new ask — a
/// weaker signal (a model that read only the follow-up would also pass), but together they cover both directions.</para>
/// </summary>
public static class SessionContinueGoldenScenarios
{
    public static IReadOnlyList<SupervisorGoldenScenario> All { get; } = new[]
    {
        ContinueRedundantComplete(),
        ContinueIncremental(),
        ContinueMixedModes(),
        ContinueAfterFailure(),
    };

    /// <summary>The follow-up re-requests work the digest says is ALREADY shipped + verified. The brain must NOT re-plan / spawn to redo it — recognise completion (stop) or clarify (ask_human). The sharpest "did it read the prior context" teeth (it must override the default plan-first rail because the context says the work is done).</summary>
    private static SupervisorGoldenScenario ContinueRedundantComplete()
    {
        const string done = "Add server-side email-format validation to the signup endpoint: reject malformed addresses with HTTP 400 and a clear error message, and cover it with unit tests.";

        return new()
        {
            Name = "continue-redundant-complete",
            Context = Continue(
                digest: Digest(Turn(1, WorkflowRunStatus.Success, done,
                    "Implemented email-format validation; malformed addresses now get HTTP 400 + a clear message; added unit tests — all green; merged to main and opened PR #42.", "feat/email-validation")),
                followUp: done),   // the EXACT same ask the digest reports as done
            AcceptedKinds = new[] { SupervisorDecisionKinds.Stop, SupervisorDecisionKinds.AskHuman },
        };
    }

    /// <summary>A prior turn finished X; the follow-up asks for a NEW thing Y (rate-limiting). No in-run supervisor decisions yet (the session's prior turn lives in the digest, not in <c>PriorDecisions</c>) ⇒ the brain must PLAN, and the plan must ADDRESS the new ask (proof it read the follow-up, not just echoed the digest).</summary>
    private static SupervisorGoldenScenario ContinueIncremental() => new()
    {
        Name = "continue-incremental",
        Context = Continue(
            digest: Digest(Turn(1, WorkflowRunStatus.Success, "Add server-side email-format validation to the signup endpoint",
                "Added email-format validation with unit tests; shipped to main.", "feat/email-validation")),
            followUp: "Now ALSO add per-IP rate limiting to the same signup endpoint: at most 5 requests per minute, return HTTP 429 when exceeded, with unit tests."),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Plan },
        PayloadCheck = PlanAddresses("rate", "limit", "429", "throttl", "per-ip", "per ip"),
    };

    /// <summary>The digest mixes prior turns of DIFFERENT modes (single-agent summary / standard 'combined' / deep 'reason'); the follow-up integrates all three. The brain must consume the mixed-mode digest and PLAN the integration.</summary>
    private static SupervisorGoldenScenario ContinueMixedModes() => new()
    {
        Name = "continue-mixed-modes",
        Context = Continue(
            digest: Digest(
                Turn(1, WorkflowRunStatus.Success, "Build the pricing calculator module", "Implemented PricingCalculator with unit tests.", "feat/pricing"),
                Turn(2, WorkflowRunStatus.Success, "Refactor the checkout flow across the codebase", "Combined result: split CheckoutService into three components and updated twelve call sites.", null),
                Turn(3, WorkflowRunStatus.Success, "Plan and execute the discounts subsystem", "Modelled discounts as composable rules; delegated three sub-agents; integrated a DiscountEngine.", "feat/discounts")),
            followUp: "Now wire the pricing calculator, the refactored checkout, and the discount engine together end to end, and add an integration test that exercises a discounted purchase."),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Plan },
        PayloadCheck = PlanAddresses("integrat", "wire", "together", "end to end", "end-to-end", "checkout", "pricing", "discount"),
    };

    /// <summary>The prior turn FAILED at a migration; the follow-up retries with a different approach. The brain must PLAN the migration AWARE the prior attempt failed — the digest carries the failed turn (status Failure, no Result line, matching a real failed run whose terminal never produced one); the corrective approach rides in the follow-up.</summary>
    private static SupervisorGoldenScenario ContinueAfterFailure() => new()
    {
        Name = "continue-after-failure",
        Context = Continue(
            // A real failed run never reaches its terminal, so its OutputsJson is empty — the digest shows the turn
            // FAILED with no Result line. The "what to do differently" lives in the follow-up, as a real one would.
            digest: Digest(Turn(1, WorkflowRunStatus.Failure, "Migrate the user store from the legacy SQL schema to the new event-sourced model",
                result: null, branch: null)),
            followUp: "Retry the user-store migration, but this time do it incrementally in small batches so the backfill cannot time out."),
        AcceptedKinds = new[] { SupervisorDecisionKinds.Plan },
        PayloadCheck = PlanAddresses("migrat", "batch", "increment", "backfill", "user"),
    };

    // ── Builders ──────────────────────────────────────────────────────────────

    private static SupervisorTurnContext Continue(string digest, string followUp) => new()
    {
        // The REAL projection composition (AgentNodeMapping.ComposeGoal) — so the digest reaches the brain's prompt
        // EXACTLY as a live continuing run would inject it (grounding first, then the follow-up, with the continue framing).
        Goal = AgentNodeMapping.ComposeGoal(followUp, digest),
        TurnNumber = 0,
        PriorDecisions = Array.Empty<SupervisorPriorDecision>(),
        SupervisorModelId = SupervisorDecisionGoldenScenarios.BrainModelRowId,
    };

    /// <summary>Render prior turns in the <c>SessionContextBuilder</c> shape — the exact digest the projection injects.</summary>
    private static string Digest(params string[] turns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Earlier turns in this work thread");
        sb.AppendLine("You are continuing an existing thread. Build on the work below — do not redo it.");

        foreach (var t in turns)
        {
            sb.AppendLine();
            sb.Append(t);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// One prior turn, rendered EXACTLY as <c>SessionContextBuilder</c> does: the header interpolates the
    /// <see cref="WorkflowRunStatus"/> enum (so the success token is <c>Success</c>, not "Succeeded"). A failed turn
    /// has NO <c>Result:</c> line — a real failed run never reached its terminal, so its <c>OutputsJson</c> is empty
    /// and the builder reads no result (<paramref name="result"/> null ⇒ omit, matching that).
    /// </summary>
    private static string Turn(int n, WorkflowRunStatus status, string goal, string? result, string? branch)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Turn {n} ({status})");
        sb.AppendLine($"Asked: {goal}");
        if (result != null) sb.AppendLine($"Result: {result}");
        if (branch != null) sb.AppendLine($"Produced branch: {branch}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The plan must ADDRESS the new ask: the combined subtask text mentions at least one new-work term — proof the brain read the follow-up rather than echoing the prior (done) work.</summary>
    private static Func<SupervisorDecision, (bool Ok, string Note)> PlanAddresses(params string[] anyOf) => decision =>
    {
        SupervisorPlanPayload? plan;
        try { plan = JsonSerializer.Deserialize<SupervisorPlanPayload>(decision.PayloadJson, AgentJson.Options); }
        catch (JsonException) { return (false, "plan payload did not deserialize"); }

        if (plan is null || plan.Subtasks.Count == 0) return (false, "plan had no subtasks");

        var text = string.Join(" ", plan.Subtasks.Select(s => $"{s.Title} {s.Instruction}")).ToLowerInvariant();

        return anyOf.Any(term => text.Contains(term.ToLowerInvariant(), StringComparison.Ordinal))
            ? (true, "ok")
            : (false, $"plan subtasks address none of [{string.Join(", ", anyOf)}] — the brain did not act on the new follow-up ask");
    };
}
