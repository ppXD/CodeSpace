using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The three-way outcome of a live-model WHOLE-LOOP run, so the blessed wire can gate SAFELY — only a code regression
/// fails the job, never the gateway model's capability. The classifying TEST maps the run's terminal state to one of
/// these (a faulted run → <see cref="CodeFault"/>; a fully-driven run → <see cref="Drove"/>; a clean-but-short run →
/// <see cref="CapabilityMiss"/>); <see cref="RealModelGate"/> only gates on <see cref="CodeFault"/>.
/// </summary>
public enum RealModelOutcome
{
    /// <summary>The live brain produced conformant decisions that drove the engine to the intended terminal. The gate is satisfied.</summary>
    Drove,

    /// <summary>The live brain did NOT drive the arc — it produced no conformant decision, or stopped/force-stopped short of the outcome. A MODEL precondition (the gateway model's capability), NOT a code bug → REPORTED, never gates the job.</summary>
    CapabilityMiss,

    /// <summary>The engine/substrate FAULTED while executing the live brain's (valid) decisions — an unhandled exception left the run Failed. A real CODE regression → gates the blessed wire.</summary>
    CodeFault,
}

/// <summary>
/// The real-model gate's per-wire policy: which provider wires are REQUIRED (blessed — a bad verdict FAILS the job)
/// versus INFORMATIONAL (still driven against the live model and their verdict reported, but never gating CI). This
/// lets a stronger wire be the kill-gate while a weaker model on another protocol surfaces its verdict without
/// blocking main — an honest split, not a silenced one. Default blessed set: Anthropic only. An operator widens or
/// changes it via the env var (comma-separated provider names) with no code change.
/// </summary>
public static class RealModelGate
{
    /// <summary>Comma-separated provider names whose real-model verdict GATES CI. Absent/blank → the default blessed set. Env-overridable so an operator can bless a different/extra wire without a code change (pinned by test).</summary>
    public const string RequiredProvidersEnvVar = "CODESPACE_REALMODEL_REQUIRED_PROVIDERS";

    /// <summary>The GitHub Actions step-summary FILE path (GitHub sets it per step). An informational wire's verdict is appended here so it lands in the job-summary UI — a channel immune to xUnit's Console capture, so the "reports its verdict" promise is actually kept.</summary>
    public const string StepSummaryEnvVar = "GITHUB_STEP_SUMMARY";

    private static readonly string[] DefaultRequiredProviders = { "Anthropic" };

    /// <summary>Apply the gate to ONE wire's verdict: a REQUIRED wire asserts (a bad verdict fails the job); an INFORMATIONAL wire reports its verdict where CI shows it and returns WITHOUT gating.</summary>
    public static void Assess(string provider, bool ok, string verdict)
    {
        if (IsRequired(provider))
        {
            ok.ShouldBeTrue($"REQUIRED wire — {verdict}");
            return;
        }

        ReportInformational(ok, verdict, Environment.GetEnvironmentVariable(StepSummaryEnvVar));
    }

    /// <summary>
    /// Drive a live-model gate and <see cref="Assess"/> its verdict, BUT treat a GATEWAY-level failure — an HttpClient
    /// timeout or an unreachable/transport error, i.e. "no response from the gateway" — as NON-GATING infra: it is
    /// reported to the step-summary as informational and never fails the job, even for a blessed wire. A clean
    /// completion still gates as usual, so the blessed gate hard-fails ONLY on a genuine wrong-decision / wiring
    /// verdict — it blocks main on bad INTELLIGENCE, never on the owner's gateway being slow or down. A non-infra
    /// exception (a real bug, an assertion) PROPAGATES so it is never swallowed. Mirrors the trajectory's bounded-clean
    /// philosophy: a slow endpoint surfaces a clean signal instead of a flaky RED. The infra skip is surfaced LOUDLY so
    /// a persistently-slow gateway is visible in the job summary rather than a silent green.
    /// </summary>
    public static Task AssessLiveAsync(string provider, Func<Task<(bool Ok, string Verdict)>> drive, bool gating = true) =>
        AssessLiveAsync(provider, drive, gating, Environment.GetEnvironmentVariable(StepSummaryEnvVar));

    /// <summary>Testable core of <see cref="AssessLiveAsync(string, Func{Task{ValueTuple{bool, string}}}, bool)"/> — takes the step-summary path explicitly so a test pins the behaviour without mutating process env. When <paramref name="gating"/> is false the clean verdict is REPORTED (informational), never asserted — for a lane whose live result is observed but must not block main (e.g. a precondition the blessed decision-eval already measures); an infra failure is non-gating regardless.</summary>
    internal static async Task AssessLiveAsync(string provider, Func<Task<(bool Ok, string Verdict)>> drive, bool gating, string? stepSummaryPath)
    {
        try
        {
            var (ok, verdict) = await drive().ConfigureAwait(false);

            if (gating) Assess(provider, ok, verdict);
            else ReportInformational(ok, verdict, stepSummaryPath);
        }
        catch (Exception ex) when (IsGatewayInfraFailure(ex))
        {
            ReportInfraSkip(provider, ex, stepSummaryPath);
        }
    }

    /// <summary>
    /// Drive a live-model WHOLE-LOOP gate whose verdict is THREE-WAY, so the blessed wire gates SAFELY: ONLY a
    /// <see cref="RealModelOutcome.CodeFault"/> — the engine/substrate FAULTED while executing the live brain's valid
    /// decisions (a real code regression) — fails the job. A <see cref="RealModelOutcome.CapabilityMiss"/> (the gateway
    /// model produced no conformant decision / drove the arc short of the outcome) is a MODEL precondition, NOT a code
    /// bug, so it is REPORTED loudly and never gates — main can't red because the gateway model couldn't drive.
    /// <see cref="RealModelOutcome.Drove"/> passes. The brain BEHAVIOUR (Drove vs CapabilityMiss) is ALWAYS surfaced, so
    /// a persistent capability miss is visible rather than a silent green. An informational wire never gates regardless
    /// of outcome; a gateway infra failure is non-gating regardless (same as the boolean overload). This is the generic
    /// seam that lets the real-brain WHOLE-LOOP lanes be gating without a model-capability miss ever reddening main.
    /// </summary>
    public static Task AssessLiveAsync(string provider, Func<Task<(RealModelOutcome Outcome, string Note)>> drive) =>
        AssessLiveAsync(provider, drive, Environment.GetEnvironmentVariable(StepSummaryEnvVar));

    /// <summary>Testable core of the three-way <see cref="AssessLiveAsync(string, Func{Task{ValueTuple{RealModelOutcome, string}}})"/> — takes the step-summary path explicitly so a test pins the behaviour without mutating process env. The outcome is ALWAYS reported; the blessed wire asserts only that it is NOT a <see cref="RealModelOutcome.CodeFault"/>; an informational wire never asserts; a gateway infra failure is a non-gating skip.</summary>
    internal static async Task AssessLiveAsync(string provider, Func<Task<(RealModelOutcome Outcome, string Note)>> drive, string? stepSummaryPath)
    {
        try
        {
            var (outcome, note) = await drive().ConfigureAwait(false);

            ReportThreeWay(outcome, note, stepSummaryPath);

            if (IsRequired(provider))
                (outcome != RealModelOutcome.CodeFault).ShouldBeTrue($"REQUIRED wire — the engine FAULTED driving the live brain (a CODE regression, NOT a model-capability miss): {note}");
        }
        catch (Exception ex) when (IsGatewayInfraFailure(ex))
        {
            ReportInfraSkip(provider, ex, stepSummaryPath);
        }
    }

    /// <summary>Surface a three-way whole-loop outcome (ALWAYS — a CapabilityMiss must never read as a silent green) to the step-summary FILE when present (capture-immune → the job-summary UI), else the console. Pure given <paramref name="stepSummaryPath"/>.</summary>
    internal static void ReportThreeWay(RealModelOutcome outcome, string note, string? stepSummaryPath)
    {
        var (icon, label) = outcome switch
        {
            RealModelOutcome.Drove => ("✅", "DROVE the whole loop"),
            RealModelOutcome.CapabilityMiss => ("ℹ️", "CAPABILITY MISS — the model did not drive the arc (REPORTED, NOT gating)"),
            _ => ("⚠️", "CODE FAULT — the engine faulted on the live brain's decisions (gates the blessed wire)"),
        };
        var line = $"{icon} real-model whole-loop: {label} — {note}";

        if (!string.IsNullOrWhiteSpace(stepSummaryPath))
            File.AppendAllText(stepSummaryPath, line + Environment.NewLine);
        else
            Console.WriteLine(line);
    }

    /// <summary>
    /// Whether a persisted node-failure is a GATEWAY/credential INFRA fault that the decider let propagate (an
    /// <c>LlmApiException</c> of category Transient / RateLimited / AuthFailed) rather than an engine or decision fault.
    /// When such a fault happens DURING a supervisor turn the engine swallows it into a run Failure (whose run-level
    /// error is the generic "Node failed."; the transport detail lives on the node-failed ledger record), so the
    /// whole-loop classifier would otherwise read it as a code fault. This lets the lane route that case to the SAME
    /// non-gating infra-skip path the decision-eval lane uses — honoring the lane-wide guarantee that a gateway outage
    /// NEVER gates main; the decider already fails the model-CAPABILITY categories (Malformed / ContextLengthExceeded /
    /// ContentFiltered / BadRequest) closed to a clean stop, so they never reach a run Failure.
    ///
    /// <para>SECURITY: the category is read from the ENGINE-WRITTEN <c>(status, category): </c> slot at the START of
    /// <c>LlmApiException.BuildMessage</c> (<c>"{provider} API error ({status}, {category}): {providerMessage}"</c>),
    /// NOT from anywhere in the message. <paramref name="payloadOrError"/> is first reduced to the node-failed record's
    /// <c>error</c> field (a JSON object) — defending against the JSON wrapper — and the category is then ANCHORED to the
    /// leading slot via <see cref="InfraSlotRegex"/>. The trailing <c>providerMessage</c> is the only attacker/upstream-
    /// controlled part (the raw error body for a non-2xx, an <c>HttpRequestException.Message</c> for a transport drop),
    /// and it sits AFTER the matched slot — so a body that merely CONTAINS <c>", Transient): "</c> can never route a
    /// non-transient fault to the non-gating skip (the prior unanchored substring check could). A genuine engine fault
    /// (a null-ref, a git / DB / merge failure) carries no such leading slot, so a real regression is never mis-skipped.</para>
    /// </summary>
    public static bool IsGatewayInfraError(string? payloadOrError)
    {
        if (string.IsNullOrEmpty(payloadOrError)) return false;

        return InfraSlotRegex.IsMatch(ExtractErrorText(payloadOrError));
    }

    /// <summary>The infra categories the decider PROPAGATES (vs the capability ones it fail-closes), ANCHORED to the leading <c>(status, category): </c> slot of the BuildMessage prefix: <c>^…?API error (&lt;status, no ',' or ')'&gt;, &lt;Category&gt;): </c>. Anchoring at <c>^</c> + a comma/paren-free status means only the engine-written leading slot is read; the untrusted providerMessage that follows the first <c>): </c> can never satisfy it.</summary>
    private static readonly System.Text.RegularExpressions.Regex InfraSlotRegex = new(
        @"^[^(]*?API error \([^,)]*, (?:Transient|RateLimited|AuthFailed)\): ",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Reduce a node-failed PAYLOAD (<c>{"error":"…","outputs":{},…}</c>) to its <c>error</c> string so the category match sees only the message, not the JSON wrapper. A non-JSON input (or one without a string <c>error</c>) is treated as the raw error text itself.</summary>
    private static string ExtractErrorText(string payloadOrError)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadOrError);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == System.Text.Json.JsonValueKind.String)
                return error.GetString() ?? "";
        }
        catch (System.Text.Json.JsonException) { /* not a JSON payload — treat the input as the raw error text */ }

        return payloadOrError;
    }

    /// <summary>SocketError codes that mean "could not establish a connection AT ALL" — a mis-pointed/typo'd endpoint, a wrong port, an unresolvable host. These are WIRING failures the kill-gate must CATCH (gate), so they are deliberately NOT treated as transient infra; an established-then-dropped/aborted connection (any other code) is.</summary>
    private static readonly System.Net.Sockets.SocketError[] WiringSocketErrors =
    {
        System.Net.Sockets.SocketError.HostNotFound, System.Net.Sockets.SocketError.ConnectionRefused,
        System.Net.Sockets.SocketError.HostUnreachable, System.Net.Sockets.SocketError.NetworkUnreachable,
        System.Net.Sockets.SocketError.TryAgain,
    };

    /// <summary>
    /// Whether <paramref name="ex"/> is a TRANSIENT gateway/transport failure ("the gateway was too slow or dropped the
    /// connection") rather than a decision OR WIRING failure — so the gate treats it as non-gating infra. Matches, anywhere
    /// in the (Aggregate-flattened) chain: a <see cref="TimeoutException"/> (the HttpClient.Timeout signature — the gateway
    /// is slow), an <see cref="System.IO.IOException"/> (a mid-stream transport drop, incl. .NET 8+ <c>HttpIOException</c>),
    /// or a <see cref="System.Net.Sockets.SocketException"/> whose code is NOT a connect/DNS failure (a reset/abort).
    /// Deliberately does NOT match a bare <see cref="System.Net.Http.HttpRequestException"/> or a connect/DNS
    /// <c>SocketException</c> (a mis-pointed/unreachable endpoint is a WIRING bug the kill-gate must catch — masking it
    /// would green the gate on a broken wire), and not a bare cancellation (our own deadline — a "did not converge" signal).
    /// </summary>
    internal static bool IsGatewayInfraFailure(Exception ex) => Unwrap(ex).Any(IsTransientTransport);

    private static bool IsTransientTransport(Exception e) => e switch
    {
        TimeoutException => true,
        System.IO.IOException => true,
        System.Net.Sockets.SocketException se => !WiringSocketErrors.Contains(se.SocketErrorCode),
        _ => false,
    };

    /// <summary>Every exception in the chain, flattening an <see cref="AggregateException"/> so a fault in a non-first slot (e.g. from a future parallel drive) is still inspected, not just <c>.InnerException</c>.</summary>
    private static IEnumerable<Exception> Unwrap(Exception ex)
    {
        var roots = ex is AggregateException agg ? agg.Flatten().InnerExceptions : (IEnumerable<Exception>)new[] { ex };

        foreach (var root in roots)
            for (Exception? e = root; e is not null; e = e.InnerException)
                yield return e;
    }

    /// <summary>Report a gateway infra failure LOUDLY as non-gating — to the step-summary FILE when present (so a persistently-slow gateway is VISIBLE in the job-summary UI, never a silent green), else the console. Pure given <paramref name="stepSummaryPath"/>.</summary>
    internal static void ReportInfraSkip(string provider, Exception ex, string? stepSummaryPath)
    {
        var line = $"⚠️ real-model gate NON-GATING infra skip — {provider} gateway timed out / dropped the connection (NOT a decision verdict): {InfraReason(ex)}";

        if (!string.IsNullOrWhiteSpace(stepSummaryPath))
            File.AppendAllText(stepSummaryPath, line + Environment.NewLine);
        else
            Console.WriteLine(line);
    }

    /// <summary>The innermost transient-transport reason (type + message) for a legible infra-skip line.</summary>
    private static string InfraReason(Exception ex) =>
        Unwrap(ex).Where(IsTransientTransport).Select(e => $"{e.GetType().Name}: {e.Message}").FirstOrDefault() ?? ex.GetType().Name;

    /// <summary>Surface an INFORMATIONAL wire's verdict (pass OR fail, so silence never reads as "it ran clean") where it is actually visible: the GitHub step-summary FILE when present (capture-immune → reaches the job-summary UI), else the console for a local run. Pure given <paramref name="stepSummaryPath"/> → pinnable without mutating process env.</summary>
    internal static void ReportInformational(bool ok, string verdict, string? stepSummaryPath)
    {
        var line = $"{(ok ? "✅" : "⚠️")} real-model INFORMATIONAL wire (reported, NOT gating CI) — {verdict}";

        if (!string.IsNullOrWhiteSpace(stepSummaryPath))
            File.AppendAllText(stepSummaryPath, line + Environment.NewLine);
        else
            Console.WriteLine(line);
    }

    /// <summary>Whether <paramref name="provider"/>'s verdict gates CI (it is in the blessed set), reading the override from the process env.</summary>
    public static bool IsRequired(string provider) =>
        IsRequired(provider, Environment.GetEnvironmentVariable(RequiredProvidersEnvVar));

    /// <summary>Testable core: whether <paramref name="provider"/> is blessed given the RAW override string (null/blank → the default set). PURE — touches no process state — so tests pin the policy without mutating global env.</summary>
    internal static bool IsRequired(string provider, string? rawRequiredProviders) =>
        ParseRequiredProviders(rawRequiredProviders).Contains(provider, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ParseRequiredProviders(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultRequiredProviders;

        var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parsed.Length == 0 ? DefaultRequiredProviders : parsed;
    }
}
