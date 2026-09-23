using System.Collections.Generic;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Cause-aware retry (the 2026-08-30 wedge's L3 layer): classify a prior attempt's terminal error into the retry
/// dispositions that need to DIFFER from the default resume-and-continue. The marker vocabulary is CLOSED and
/// deliberately tight — only failure shapes seen live and understood mechanically are pinned here; it grows by PR,
/// never by fuzzy matching (an over-broad marker would silently strip resume continuity from ordinary failures).
/// </summary>
public static class AgentRetryCauses
{
    /// <summary>The gateway mangled the Anthropic wire FORMAT (e.g. thinking-block continuation) — deterministic on replay: resuming the conversation re-sends the very history that re-triggers it, so the retry must start FRESH, with extended thinking disabled.</summary>
    public const string GatewayFormatFault = "gateway-format-fault";

    /// <summary>The env var the claude CLI reads as its extended-thinking budget — 0 disables thinking entirely. Pinned by test (Rule 8): the retry degrade writes it into the task environment, and a rename here would silently un-degrade every format-fault retry.</summary>
    public const string MaxThinkingTokensEnvVar = "MAX_THINKING_TOKENS";

    /// <summary>
    /// This deployment took the worker away mid-run: the agent's brokered model lease died with it, and we stopped the
    /// attempt (<see cref="Messages.Failures.FailureCodes.ModelCredentialLeaseLost"/>). INFRA, and of the plainest
    /// kind — the model was never asked, so the attempt is evidence about our rollout and about nothing else.
    ///
    /// <para>Its repair is simply the same attempt on a live worker, so unlike <see cref="GatewayFormatFault"/> it
    /// carries NO mitigation: every consumer that applies one keys on that constant specifically
    /// (<c>AgentCodeNode</c>, <c>BenchmarkRunner</c>, the supervisor's spawn retry), so they are unaffected and the
    /// retry stays warm on the same model. The consumer that keys on a cause being present AT ALL is
    /// <see cref="Agents.AgentModelEscalationTrigger"/>, and that is the one that must see this: without it a rolling
    /// restart reads as the model hitting its limit and buys a more expensive one to fix a deploy.</para>
    /// </summary>
    public const string ModelAccessLost = "model-access-lost";

    /// <summary>
    /// The model refused the request as larger than its context window — deterministic on replay. A retry warm-resumes
    /// the conversation, so its request carries the goal AGAIN and is longer still; a fresh one carries the same goal.
    /// Either way the same refusal comes back, billed, and buries the one fact the author needs: the goal is too big
    /// for this model. No mitigation, like <see cref="ModelAccessLost"/> — its only consumer that matters is
    /// <c>AgentCodeNode</c>, which stops respawning it.
    /// </summary>
    public const string ContextWindowExceeded = "context-window-exceeded";

    /// <summary>Seen live 2026-08-30 (run wedge postmortem): the gateway's Anthropic-compat layer broke thinking-block continuation and killed the agent tail with exactly this text.</summary>
    private static readonly string[] FormatFaultMarkers = { "is not a thinking block" };

    /// <summary>
    /// What the CLIs themselves print for an over-long prompt — each observed from the real binary (Claude Code
    /// 2.1.226, Codex 0.147.0 and 0.142.2) answered with the provider's own error body, and pinned through the real
    /// harness folds by <c>AgentContextWindowRetryTests</c>. Provider- and CLI-authored phrases, not words an agent's
    /// own prose is likely to end on; the vocabulary stays closed like the one above.
    /// </summary>
    private static readonly string[] ContextWindowMarkers =
    {
        "Prompt is too long",                        // Claude Code, for either Anthropic overflow body (terminal_reason=prompt_too_long)
        "maximum context length is",                 // OpenAI-compatible gateways (vLLM, LiteLLM), passed through verbatim
        "context_length_exceeded",                   // OpenAI error code, which Codex passes through
        "exceeds the context window",                // OpenAI's message for the same code
        "out of room in the model's context window", // Codex's rewording of a streaming response.failed
        "input_too_large",                           // Codex refusing an input past its own 1,048,576-character cap
    };

    /// <summary>
    /// The prior attempt's retry-relevant cause, reading its DECLARED exit reason before any text. A typed code is this
    /// codebase's own diagnosis and can never be prose about one, so it settles the question outright; only an attempt
    /// that declared nothing falls through to the marker scan.
    /// </summary>
    public static string? Classify(string? exitReason, string? error) =>
        exitReason == Messages.Failures.FailureCodes.ModelCredentialLeaseLost ? ModelAccessLost : Classify(error);

    /// <summary>The prior attempt's retry-relevant cause read from its error TEXT alone, or null for every ordinary failure (default resume semantics stand unchanged). Prefer the overload above wherever the exit reason is in hand.</summary>
    public static string? Classify(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;

        foreach (var marker in FormatFaultMarkers)
            if (error.Contains(marker, StringComparison.OrdinalIgnoreCase)) return GatewayFormatFault;

        foreach (var marker in ContextWindowMarkers)
            if (error.Contains(marker, StringComparison.OrdinalIgnoreCase)) return ContextWindowExceeded;

        return null;
    }

    /// <summary>A copy of the task environment with extended thinking disabled — the degrade a format-fault retry runs under (harmless on a harness that ignores the variable).</summary>
    public static IReadOnlyDictionary<string, string> WithThinkingDisabled(IReadOnlyDictionary<string, string> environment)
    {
        var copy = new Dictionary<string, string>(environment) { [MaxThinkingTokensEnvVar] = "0" };
        return copy;
    }

    /// <summary>
    /// The WHOLE format-fault mitigation folded onto a retry's task — the ONE composition both retry lanes call
    /// (<c>RealSupervisorActionExecutor.ApplyRetryDisposition</c> and <c>AgentCodeNode</c>'s respawn), so "what a
    /// format-fault retry runs as" can never mean two different things in the two lanes (the
    /// <see cref="AgentRetryContinuity"/> discipline, one level up).
    ///
    /// <para>Both halves are load-bearing. FRESH conversation: the mangled block lives in the very transcript a
    /// <c>--resume</c> re-sends, so a warm retry re-triggers the fault deterministically — it dies in seconds,
    /// before any turn, and burns a whole attempt relearning it. THINKING DISABLED: the shape the gateway's
    /// Anthropic-compat layer cannot mangle. The workspace is untouched on purpose — the degrade drops the broken
    /// conversation, never the preserved work.</para>
    /// </summary>
    public static AgentTask ApplyFormatFaultMitigation(AgentTask task) =>
        task with { ResumeFromSessionId = null, RestoredTranscript = null, RestoredTranscriptArtifactId = null, Environment = WithThinkingDisabled(task.Environment) };

    /// <summary>
    /// Whether a dispatched task IS a mitigated attempt — the fact the dispatcher announces on the timeline, and the
    /// bound the next retry verdict keys on (a mitigated attempt that hits the SAME fault has proven the repair does
    /// not hold, so it is terminal rather than respawned identically).
    ///
    /// <para>The environment is null-guarded because every caller reads an envelope DESERIALIZED from durable JSON —
    /// <c>task_jsonb</c> on each claim, the engine's own suspend payload on each stage — and an explicit
    /// <c>"environment": null</c> lands straight on the property, bypassing the record's default initializer. An
    /// unguarded dereference would kill the dispatch with an NRE before the harness ever starts. No environment
    /// carries no degrade, so the answer is "not mitigated".</para>
    /// </summary>
    public static bool IsFormatFaultMitigated(AgentTask task) =>
        task.Environment is not null && task.Environment.TryGetValue(MaxThinkingTokensEnvVar, out var budget) && budget == "0";
}
