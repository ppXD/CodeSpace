namespace CodeSpace.Messages.Decisions;

/// <summary>What the supervisor arbiter (D4c) decided to do with a pending child decision — answer it itself, or escalate it to a human.</summary>
public static class ArbiterVerdictKinds
{
    /// <summary>The arbiter answers the decision itself (only ever a low/med-risk, floor-passing decision it is confident about).</summary>
    public const string Answer = "answer";

    /// <summary>The arbiter sends the decision to a human (unsure, high-stakes, or insufficient context) — the safe default.</summary>
    public const string Escalate = "escalate";
}

/// <summary>
/// WHY an <c>escalate</c> verdict happened, machine-readable — so a consumer (the run log, a real-model eval gate, a
/// future queue-card projection) can tell a GATEWAY fault ("the brain could not be reached") from every other escalate
/// reason ("no brain model configured", "the model's reply did not conform", "the model itself judged this too
/// risky") without parsing <see cref="ArbiterVerdict.Rationale"/> prose. Real run 34108260233: a 429 RateLimited storm
/// read as a plain behavioural escalate, so a real-model eval scored "the arbiter punted an obvious decision" instead
/// of the gateway-infra skip it actually was.
///
/// <para>Lives here rather than as <c>LlmErrorCategory</c> directly: the Core→Messages dependency direction (Rule
/// 18.1) means this Messages DTO cannot reference the Core enum, and the arbiter's escalate path only ever needs the
/// ONE binary split it acts on — not the full failure-category taxonomy.</para>
/// </summary>
public enum ArbiterEscalateCause
{
    /// <summary>Every escalate reason that is NOT a gateway fault — no brain model configured/available, no structured
    /// provider, an unparseable/non-conformant verdict, or the model itself chose to escalate. The default: carries no
    /// new information beyond what <see cref="ArbiterVerdict.Rationale"/> already said.</summary>
    Unspecified,

    /// <summary>The brain call itself failed for an INFRA reason — a rate limit, a transient 5xx/timeout, or an auth
    /// failure (the same categories <c>LlmSupervisorDecider</c> propagates rather than fail-closes). The arbiter still
    /// escalates — a human can always answer, and there is no "clean stop" for one blocked child decision — but the
    /// CAUSE is the gateway being unavailable, never a decision anyone made.</summary>
    GatewayInfra,

    /// <summary>The arbiter's OWN brain call was refused by the run's budget ledger (a spent cap, or an unpriced model
    /// under a cap) — never a decision the model made about the child's question. Distinct from
    /// <see cref="GatewayInfra"/>: the gateway was reachable and the model was priceable/affordable in principle, but
    /// THIS run has no headroom left, or no price to enforce its cap with.</summary>
    BudgetRefused,
}

/// <summary>
/// The supervisor arbiter's verdict on ONE pending decision (Decision substrate D4c, Rule 18.1 noun) — the projected,
/// already-fail-closed output of the arbiter brain. <see cref="Kind"/> is answer/escalate; an <c>answer</c> carries the
/// chosen option(s) / free text; BOTH carry a <see cref="Rationale"/> (AC3 — never silent: an auto-answer records why,
/// an escalation tells the human why it was raised). A malformed / unknown model verdict projects to <c>escalate</c>.
/// </summary>
public sealed record ArbiterVerdict
{
    public required string Kind { get; init; }

    public IReadOnlyList<string> SelectedOptions { get; init; } = Array.Empty<string>();

    public string? FreeText { get; init; }

    public required string Rationale { get; init; }

    /// <summary>Machine-readable WHY for an <c>escalate</c> verdict (see <see cref="ArbiterEscalateCause"/>). Meaningless for an <c>answer</c> — always <see cref="ArbiterEscalateCause.Unspecified"/> there.</summary>
    public ArbiterEscalateCause Cause { get; init; } = ArbiterEscalateCause.Unspecified;

    public bool IsAnswer => Kind == ArbiterVerdictKinds.Answer;

    public static ArbiterVerdict Escalate(string rationale) => new() { Kind = ArbiterVerdictKinds.Escalate, Rationale = rationale };

    /// <summary>An escalate caused by a GATEWAY fault (rate limit / transient / auth) rather than a model-side reason — see <see cref="ArbiterEscalateCause.GatewayInfra"/>.</summary>
    public static ArbiterVerdict EscalateInfra(string rationale) => new() { Kind = ArbiterVerdictKinds.Escalate, Rationale = rationale, Cause = ArbiterEscalateCause.GatewayInfra };

    /// <summary>An escalate caused by the run's OWN budget ledger refusing the arbiter's brain call — see <see cref="ArbiterEscalateCause.BudgetRefused"/>.</summary>
    public static ArbiterVerdict EscalateBudgetRefused(string rationale) => new() { Kind = ArbiterVerdictKinds.Escalate, Rationale = rationale, Cause = ArbiterEscalateCause.BudgetRefused };

    public static ArbiterVerdict Answer(IReadOnlyList<string> selectedOptions, string? freeText, string rationale) =>
        new() { Kind = ArbiterVerdictKinds.Answer, SelectedOptions = selectedOptions, FreeText = freeText, Rationale = rationale };
}
