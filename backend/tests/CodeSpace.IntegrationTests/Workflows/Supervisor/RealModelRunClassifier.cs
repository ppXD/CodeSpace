using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

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
/// through <see cref="RealModelGate.IsGatewayInfraError"/> (anchored to the slot <c>LlmApiException</c> writes — kept
/// DEFENSIVE here, since an AgentRun's own exception never reaches this string; see the arm below), an announced HTTP
/// status is graded by <see cref="LlmApiException.Classify"/>, and the transport-code split mirrors
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
    /// Codex's <c>turn.failed</c> <c>error.message</c> in either of the two shapes Codex itself emits: a single failed
    /// call (<c>"unexpected status 401 Unauthorized"</c> — pinned in <c>CodexHarnessTests</c>) or its OWN retry loop
    /// giving up (<c>"exceeded retry limit, last status: 429 Too Many Requests"</c> — the exact text real-model
    /// stop-hook lane runs 34135877074 and 34136267088 captured on a genuine gateway rate-limit that this regex used
    /// to miss, reddening the gate on an outage instead of skipping it). The PHRASE is the anchor and the
    /// status must sit within its slot — same line, at most 40 non-digit characters after it — so prose that merely
    /// mentions a status matches nothing, while an announcement still matches after a stderr tail is folded in front
    /// of it. A three-digit token alone is never enough. Matched case-SENSITIVELY, in each harness's own casing: an
    /// agent writing about "the api error path" is discussing one, not emitting one.
    /// </summary>
    private static readonly Regex AnnouncedStatusRegex = new(
        @"(?:API Error|unexpected status|exceeded retry limit)\b[^0-9\n]{0,40}?(?<status>[1-5][0-9]{2})\b",
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

        var exitReason = ExitReasonOf(run);

        // OUR code faulted building/attaching the run — the operating-contract threw, the harness invocation broke,
        // or a launch threw something that DECLARES its own failure identity (IFailure) and now lands under that
        // identity's code (AgentRunExecutor.ExecutorExitReason) instead of the old blanket "executor-error". Any of
        // the three is a real MISS, NOT infra. Reserved even if the message happens to contain a gateway-looking
        // token: a coded exit is this codebase's own diagnosis of what went wrong (a broker outage, a budget cap,
        // an unreadable artifact…), so it must never fall through to the heuristics below and be reread as weather
        // just because its own text mentions a status code or a transport error.
        if (exitReason is "executor-error" or "reattach-error" || FailureCodes.All.Contains(exitReason)) return false;

        var error = run.Error ?? "";

        // The gateway mangled the Anthropic wire FORMAT (thinking-block continuation). Production ALREADY owns this
        // vocabulary — it is the cause the retry path degrades against — so the gates read it from there instead of
        // keeping a second copy. One definition of "the gateway broke the wire" means a marker pinned for the retry
        // path can never leave a real-model gate reading the same exit as a code regression.
        if (AgentRetryCauses.Classify(error) == AgentRetryCauses.GatewayFormatFault) return true;

        // DEFENSIVE: reads the same anchored slot the engine lane gates on, but unreachable here today — an
        // AgentRun's own LlmApiException never survives to this string. LlmApiException is not an IFailure, so the
        // executor's generic catch still folds it into Failed/exitReason=executor-error, which the check above
        // already returns false for before Error is ever read. Kept so this arm still gates if a future path ever
        // lets that engine-written slot reach a run's Error the way it already reaches a supervisor node-failed
        // payload.
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
    /// or prove that a persona/skill injection failed. Other NeedsReview reasons carry no reply of their own and fall
    /// to the general rule below.
    ///
    /// <para><b>A reply the run FAILED over is still a reply.</b> Statuses alone used to decide this, so everything but
    /// Succeeded / <c>needs-review</c> was "no inspectable reply" → an <c>AgentExecutionInfraException</c> → a
    /// non-gating skip. That swallowed the single most gate-relevant outcome there is:
    /// <c>status=Failed; exitReason=acceptance-failed; error=…artifact-missing: ANSWER.md</c> — the agent RAN, REPLIED,
    /// and failed its own deliverable. Skipping it contradicts this class's whole premise (see the summary above): a
    /// gate that buckets a ran-but-failed run as infra cannot red on the regression class it exists to catch.</para>
    ///
    /// <para>So the general rule is the one the summary states: a run whose result PERSISTED a reply is inspectable
    /// unless some machine vocabulary names its failure environmental (<see cref="IsGatewayInfra"/> — a TimedOut run, a
    /// harness-announced 429, a dropped connection). Only a run that produced NO reply at all, or one the gateway ate,
    /// stays infra. Conservative in the same direction as every other arm here: an unrecognised failure that still
    /// carries the model's words GATES.</para>
    /// </summary>
    public static bool HasInspectableModelReply(AgentRun run) => run.Status == AgentRunStatus.Succeeded
        || run.Status == AgentRunStatus.NeedsReview && ExitReasonOf(run) == "needs-review"
        || HasPersistedModelReply(run) && !IsGatewayInfra(run);

    /// <summary>The result fields a model's own words land in — its final message and the conversation it came from. Any one of them non-empty means output exists for a behavioral gate to read, whatever the run's terminal status.</summary>
    private static bool HasPersistedModelReply(AgentRun run) =>
        !string.IsNullOrWhiteSpace(ReadResultString(run, "summary"))
        || !string.IsNullOrWhiteSpace(ReadResultString(run, "transcript"))
        || !string.IsNullOrWhiteSpace(ReadResultString(run, "sessionTranscript"));

    /// <summary>The run's ExitReason, read from the serialized <c>AgentRunResult</c> in <see cref="AgentRun.ResultJson"/> (there is no ExitReason column on the entity). Empty when absent/unparseable.</summary>
    public static string ExitReasonOf(AgentRun run) => ReadResultString(run, "exitReason");

    /// <summary>One string field of the serialized <c>AgentRunResult</c>, read under either casing a serializer may have written it in. Empty when the field is absent, non-string, or the JSON is unparseable.</summary>
    private static string ReadResultString(AgentRun run, string camelName)
    {
        if (string.IsNullOrWhiteSpace(run.ResultJson)) return "";

        try
        {
            using var doc = JsonDocument.Parse(run.ResultJson);
            foreach (var name in new[] { camelName, char.ToUpperInvariant(camelName[0]) + camelName[1..] })
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
        }
        catch { /* unparseable → treat as unknown → the error-signature path decides */ }

        return "";
    }
}
