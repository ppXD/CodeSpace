using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// 🟢 THE live-brain DECISION-ARBITER kill-gate (real-scenario coverage B1) — a REAL model answers a REAL agent-raised
/// decision. The decision-substrate sibling of <see cref="RealModelSupervisorDecisionFlowTests"/> (the delivery-decider
/// gate): it drives the production <see cref="LlmDecisionArbiter"/> against the owner's live gateway (the SAME
/// <c>CODESPACE_LLM_*</c> secrets, a [Theory] over the Anthropic + OpenAI wires) on an OBVIOUS, low-risk, fully-reversible
/// choose-one whose recommended option is the conventional safe one. The gate (per the owner) scores the ROBUST
/// invariant: the live arbiter AUTO-ANSWERED (it did not fail-closed-escalate) AND chose a VALID option — i.e. it made
/// the call a real arbiter exists to make, rather than punting a clear decision to a human. The exact option chosen is
/// REPORTED, not asserted (a competent model could legitimately differ; the gate measures answer-vs-escalate
/// intelligence, which is stable on the blessed wire).
///
/// <para>Driving <see cref="LlmDecisionArbiter.DecideAsync"/> directly (no DB) mirrors the decision-eval lane exactly —
/// the REAL rehydrate→drain→ledger SUBSTRATE that actuates a verdict is covered deterministically + always-green by
/// <c>SupervisorArbiterDrainFlowTests</c>; this lane adds ONLY the live-wire verdict signal. Self-skips when the
/// <c>CODESPACE_LLM_*</c> secrets are absent (CI/forks stay green at zero cost). A gateway timeout is non-gating infra
/// via <see cref="RealModelGate.AssessLiveAsync"/>; the blessed wire (Anthropic) GATES, OpenAI is informational.</para>
/// </summary>
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelSupervisorArbiterFlowTests
{
    private static readonly Guid TeamId = Guid.NewGuid();

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task The_real_arbiter_answers_an_obvious_low_risk_decision_rather_than_escalating(string provider)
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)

        // best-of-N capability-floor (blessed wire gets N independent attempts, passes if ANY answers validly) so a single
        // non-deterministic escalate-instead-of-answer can't flaky-red main; a persistent escalate still REDs. The closure
        // builds a FRESH arbiter + decision each attempt, so the re-runs are independent. Informational wire runs once.
        await RealModelGate.AssessLiveBestOfNAsync(provider, async () =>
        {
            var credential = RealModelLiveWire.Credential(provider, baseUrl, apiKey);
            var arbiter = new LlmDecisionArbiter(RealModelLiveWire.Registry(), RealModelLiveWire.Selector(model, credential));

            var decision = ObviousLowRiskDecision();

            // A non-null brain model id → the arbiter actually calls the live brain (a null would short-circuit to escalate).
            var verdict = await arbiter.DecideAsync(decision, TeamId, supervisorModelId: Guid.NewGuid(), "ship a small, well-tested change", CancellationToken.None);

            var validOptions = decision.Options.Select(o => o.Id).ToHashSet();
            var ok = verdict.IsAnswer && verdict.SelectedOptions.Count > 0 && verdict.SelectedOptions.All(validOptions.Contains);

            var note = $"{provider} model '{model}' arbiter — kind={verdict.Kind}, selected=[{string.Join(",", verdict.SelectedOptions)}], rationale={Truncate(verdict.Rationale)}";
            return (ok, note);
        });
    }

    /// <summary>An OBVIOUS, low-risk, fully-reversible choose-one: reuse a tested helper (recommended) vs. hand-roll a one-off. A competent arbiter answers (and almost always picks the recommended "a"); the gate only requires it ANSWERED with a valid option.</summary>
    private static PendingDecision ObviousLowRiskDecision() => new()
    {
        Id = Guid.NewGuid(),
        Grain = DecisionResumeBackends.ToolLedger,
        RootTraceId = Guid.NewGuid(),
        DecisionType = DecisionTypes.ChooseOne,
        Question = "The change needs to format a timestamp. Reuse the existing, unit-tested DateUtils.FormatIso helper, or hand-roll a new one-off inline string concatenation?",
        Options = new[]
        {
            new DecisionOption { Id = "a", Label = "Reuse the existing unit-tested DateUtils.FormatIso helper" },
            new DecisionOption { Id = "b", Label = "Hand-roll a new inline string concatenation" },
        },
        RecommendedOption = "a",
        BlockingReason = "the agent must pick how to format the timestamp before it can continue",
        ContextSummary = "A low-stakes, fully reversible style choice; the recommended option is the conventional, safer one.",
        RiskLevel = DecisionRiskLevels.Low,
        Policy = DecisionPolicies.SupervisorFirst,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static string Truncate(string s) => s.Length <= 160 ? s : s[..160] + "…";
}
