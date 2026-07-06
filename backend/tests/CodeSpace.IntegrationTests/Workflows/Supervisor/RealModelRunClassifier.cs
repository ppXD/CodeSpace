using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
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
/// <para><b>Conservative by design.</b> Only a RECOGNISED gateway/transport/auth/rate signature (grounded in the real
/// errors these lanes actually surface) counts as infra; an <c>executor-error</c>, or an UNRECOGNISED CLI failure,
/// DEFAULTS to a code fault so a regression can never hide as a skip. A one-off misclassification of a novel gateway
/// error as a "miss" is absorbed by the gate's best-of-N floor; a persistent one is worth surfacing anyway.</para>
/// </summary>
public static class RealModelRunClassifier
{
    // Recognised gateway / transport / auth / rate / capacity signatures in the CLI's surfaced error text — the model
    // call could not complete for an ENVIRONMENTAL reason, never a code regression. Lowercased substring match. Kept
    // PRECISE (unambiguous HTTP/transport/auth/rate tokens only) so a code fault — a "usage" / "unknown option" /
    // "unexpected argument" error — can never masquerade as a gateway skip and re-open the false-green.
    private static readonly string[] GatewaySignatures =
    {
        "401", "403", "407", "429", "500", "502", "503", "504",
        "overloaded", "rate limit", "rate-limit", "ratelimit", "too many requests",
        "timeout", "timed out", "timed-out", "deadline",
        "connection", "refused", "reset by peer", "econnreset", "unreachable",
        "temporarily unavailable", "service unavailable", "bad gateway", "gateway timeout",
        "unauthorized", "authentication_error", "invalid_api_key", "insufficient", "quota", "credit balance",
        "404", // Codex on a chat/completions-only gateway: its `responses`-wire POST 404s — an env/wire mismatch, not a code fault
    };

    /// <summary>True when a non-Succeeded run is a GATEWAY/transport/auth infra failure (→ non-gating skip); false when it is an injection/code fault the gate must RED on. Precondition: <paramref name="run"/> is NOT Succeeded.</summary>
    public static bool IsGatewayInfra(AgentRun run)
    {
        if (run.Status == AgentRunStatus.TimedOut) return true;   // the model/gateway was too slow — infra, never a code fault

        // OUR code faulted building/attaching the run (the operating-contract threw, the harness invocation broke) — a
        // real MISS, NOT infra. Reserved even if the message happens to contain a gateway-looking token.
        if (ExitReasonOf(run) is "executor-error" or "reattach-error") return false;

        var error = (run.Error ?? "").ToLowerInvariant();
        return GatewaySignatures.Any(s => error.Contains(s, StringComparison.Ordinal));
        // else: a CLI non-zero exit whose error is NOT a recognised gateway signature (a usage / arg / parse error, or an
        // unknown failure) → default to a CODE FAULT (the caller reds), so an injection-channel regression is never a skip.
    }

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
