using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Classifies a NON-Succeeded real-model agent run as GATEWAY/transport/auth INFRA (→ a non-gating skip) versus an
/// INJECTION/CODE fault (→ a real MISS the blessed gate must RED on).
///
/// <para><b>Why this exists.</b> A behavioral injection gate that buckets EVERY non-Succeeded status as infra is
/// UNSOUND: it cannot red on the exact regression class it exists to catch. A malformed <c>--append-system-prompt</c>,
/// an <c>AgentOperatingContract.Compose</c> that throws, or arg-ordering that swallows the Goal all make the CLI exit
/// non-zero → <c>Status=Failed</c> → (naively) a silent infra skip → GREEN. This classifier splits the two so a code
/// regression REDS while a genuine gateway hiccup skips — mirroring the whole-loop gate's CodeFault-vs-infra split and
/// the benchmark arm's ran-but-failed handling.</para>
///
/// <para><b>Conservative by design.</b> Only a failure some MACHINE vocabulary names as environmental counts as infra;
/// an <c>executor-error</c>, or an UNRECOGNISED CLI failure, DEFAULTS to a code fault so a regression can never hide
/// as a skip. A one-off misclassification of a novel gateway error as a "miss" is absorbed by the gate's best-of-N
/// floor; a persistent one is worth surfacing anyway.</para>
///
/// <para><b>Markers, never words.</b> Each arm reads a value some machine WROTE — a production marker, an
/// engine-written exception slot, an HTTP status a harness announced, a transport error CODE. A gateway-looking WORD
/// in prose classifies nothing, because the error text is the agent's own diagnostic surface: it carries whatever the
/// task was about (a route that returns 404, a missing connection string, a refusal to overwrite a file) and, once a
/// harness's stderr tail is folded in, whatever the process printed on its way down. A vocabulary of bare substrings
/// over that text cannot tell "the gateway rate-limited us" from "the test asserted a 429", so it would let a genuine
/// CODE fault skip a REQUIRED gate — the false-green this classifier exists to close.</para>
///
/// <para><b>One vocabulary, four readers.</b> Nothing here re-decides what a failure means. The mangled Anthropic wire
/// format is <see cref="AgentRetryCauses.GatewayFormatFault"/>, the engine's own typed transport failure is read
/// through <see cref="RealModelGate.IsGatewayInfraError"/> (anchored to the slot <c>LlmApiException</c> writes), an
/// announced HTTP status is graded by <see cref="LlmApiException.Classify"/>, and the transport-code split mirrors
/// <c>RealModelGate</c>'s wiring-versus-dropped rule on the exception path. A change to any of those moves this gate
/// with it, so the retry path, the engine lane, and the agent lane can never disagree about the same failure.</para>
/// </summary>
public static class RealModelRunClassifier
{
    /// <summary>The one announced status production's table calls a request fault yet this lane must skip: Codex on a chat/completions-only gateway POSTs its <c>responses</c> wire and gets a 404 — the endpoint does not serve that protocol. An env/wire mismatch, never a regression in the injection channel under test. Only ever read from an announcement slot, so a task that ASSERTS a 404 route can no longer borrow it.</summary>
    private const int GatewayWireMismatchStatus = 404;

    /// <summary>
    /// A harness's own transport ANNOUNCEMENT and the HTTP status it names: Claude Code's <c>result</c>-line
    /// <c>is_error</c> text (<c>"API Error: 401 Authentication Error"</c>, <c>"API Error (429)"</c>,
    /// <c>"API Error: Request rejected (429) AccountQuotaExceeded"</c> — pinned in <c>ClaudeCodeHarnessTests</c>) and
    /// Codex's <c>turn.failed</c> <c>error.message</c> (<c>"unexpected status 401 Unauthorized"</c> — pinned in
    /// <c>CodexHarnessTests</c>). The PHRASE is the anchor and the status must sit within its slot — same line, at most
    /// 40 non-digit characters after it — so prose that merely mentions a status matches nothing, while an announcement
    /// still matches after a stderr tail is folded in front of it. A three-digit token alone is never enough. Matched
    /// case-SENSITIVELY, in each harness's own casing: an agent writing about "the api error path" is discussing one,
    /// not emitting one.
    /// </summary>
    private static readonly Regex AnnouncedStatusRegex = new(
        @"(?:API Error|unexpected status)\b[^0-9\n]{0,40}?(?<status>[1-5][0-9]{2})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The libc/Node transport error CODES that mean an ESTABLISHED connection died under us — weather, and the only
    /// connection-level shape that skips. Deliberately EXCLUDES the connect/DNS codes (<c>ECONNREFUSED</c>,
    /// <c>ENOTFOUND</c>, <c>EHOSTUNREACH</c>, <c>ENETUNREACH</c>, <c>EAI_AGAIN</c>): a base URL that resolves to
    /// nothing or refuses the connection is a WIRING bug the gate must CATCH, exactly as <c>RealModelGate</c>'s own
    /// <c>WiringSocketErrors</c> refuses the matching <see cref="System.Net.Sockets.SocketError"/> values on the
    /// exception path. Matched case-SENSITIVELY on the whole token: these are machine codes, and the English words they
    /// used to be matched by ("connection", "refused", "unreachable") are exactly what prose is full of.
    /// </summary>
    private static readonly Regex DroppedTransportRegex = new(
        @"\b(?:ECONNRESET|ECONNABORTED|EPIPE|ETIMEDOUT)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when a non-Succeeded run is a GATEWAY/transport/auth infra failure (→ non-gating skip); false when it is an injection/code fault the gate must RED on. Precondition: <paramref name="run"/> is NOT Succeeded.</summary>
    public static bool IsGatewayInfra(AgentRun run)
    {
        if (run.Status == AgentRunStatus.TimedOut) return true;   // the model/gateway was too slow — infra, never a code fault

        // OUR code faulted building/attaching the run (the operating-contract threw, the harness invocation broke) — a
        // real MISS, NOT infra. Reserved even if the message happens to contain a gateway-looking token.
        if (ExitReasonOf(run) is "executor-error" or "reattach-error") return false;

        var error = run.Error ?? "";

        // The gateway mangled the Anthropic wire FORMAT (thinking-block continuation). Production ALREADY owns this
        // vocabulary — it is the cause the retry path degrades against — so the gates read it from there instead of
        // keeping a second copy. One definition of "the gateway broke the wire" means a marker pinned for the retry
        // path can never leave a real-model gate reading the same exit as a code regression.
        if (AgentRetryCauses.Classify(error) == AgentRetryCauses.GatewayFormatFault) return true;

        // OUR OWN typed transport failure, read from the slot the engine wrote (never from the provider's message that
        // follows it) — the same anchored read the engine lane already gates on.
        if (RealModelGate.IsGatewayInfraError(error)) return true;

        if (IsInfraAnnouncedStatus(error)) return true;

        return DroppedTransportRegex.IsMatch(error);
        // else: a CLI non-zero exit no machine vocabulary named (a usage / arg / parse error, an assertion, or an
        // unknown failure) → default to a CODE FAULT (the caller reds), so an injection-channel regression is never a skip.
    }

    /// <summary>True when a harness ANNOUNCED an HTTP status that production's own table calls environmental. The status is read from the announcement slot and graded by <see cref="LlmApiException.Classify"/> — the very function the LLM transport classifies itself with — so this gate cannot bless a status production calls a request fault, and cannot miss one it later starts calling transient.</summary>
    private static bool IsInfraAnnouncedStatus(string error)
    {
        var match = AnnouncedStatusRegex.Match(error);

        if (!match.Success) return false;

        var status = int.Parse(match.Groups["status"].Value, CultureInfo.InvariantCulture);

        if (status == GatewayWireMismatchStatus) return true;

        // The categories the decider PROPAGATES as infra — the same three RealModelGate skips on. The model-CAPABILITY
        // categories (BadRequest / ContextLengthExceeded / ContentFiltered / Malformed) are a real miss and gate.
        return LlmApiException.Classify(status, body: null) is LlmErrorCategory.Transient or LlmErrorCategory.RateLimited or LlmErrorCategory.AuthFailed;
    }

    /// <summary>
    /// True when a behavioral gate can inspect the model's persisted reply. The completion-review form of
    /// <see cref="AgentRunStatus.NeedsReview"/> deliberately remains inspectable: the completion contract can honestly
    /// park an otherwise successful reply when it ends with an unresolved question, but that does not erase the reply
    /// or prove that a persona/skill injection failed. Other NeedsReview reasons remain non-inspectable here so a stalled,
    /// critic-flagged, or decision-blocked run cannot borrow this narrow exception.
    /// </summary>
    public static bool HasInspectableModelReply(AgentRun run) => run.Status == AgentRunStatus.Succeeded
        || run.Status == AgentRunStatus.NeedsReview && ExitReasonOf(run) == "needs-review";

    /// <summary>The run's ExitReason, read from the serialized <c>AgentRunResult</c> in <see cref="AgentRun.ResultJson"/> (there is no ExitReason column on the entity). Empty when absent/unparseable.</summary>
    public static string ExitReasonOf(AgentRun run)
    {
        if (string.IsNullOrWhiteSpace(run.ResultJson)) return "";

        try
        {
            using var doc = JsonDocument.Parse(run.ResultJson);
            foreach (var name in new[] { "exitReason", "ExitReason" })
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
        }
        catch { /* unparseable → treat as unknown → the error-signature path decides */ }

        return "";
    }
}
