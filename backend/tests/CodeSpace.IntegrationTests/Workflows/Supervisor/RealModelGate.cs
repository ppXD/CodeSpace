using Shouldly;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Raised by a whole-loop EVALUATOR when the brain's spawned agents could not EXECUTE on the runner at all (the
/// deterministic fake agent is an <c>exit 0</c> script, so an all-failed fan-out is an OS/sandbox/process/capture
/// infra fault — NOT a model decision). The gate treats it EXACTLY like a gateway timeout: a non-gating LOUD skip that
/// does not consume a best-of-N capability slot (<see cref="RealModelGate.IsGatewayInfraFailure"/> recognises it), so a
/// runner-side execution break can never red main as a false CapabilityMiss. Distinct from a gateway timeout only in
/// the surfaced reason; the routing is identical.
/// </summary>
public sealed class AgentExecutionInfraException : Exception
{
    public AgentExecutionInfraException(string message) : base(message) { }
}

/// <summary>
/// The three-way outcome of a live-model WHOLE-LOOP run. The classifying TEST maps the run's terminal state to one of
/// these (a faulted run → <see cref="CodeFault"/>; a fully-driven run → <see cref="Drove"/>; a clean-but-short run →
/// <see cref="CapabilityMiss"/>). TWO gate policies consume it: the legacy <see cref="RealModelGate.AssessLiveAsync(string, Func{Task{ValueTuple{RealModelOutcome, string}}})"/>
/// reds only on <see cref="CodeFault"/> (CapabilityMiss reported) — used by the report-only reaction arcs; the STRICT
/// <see cref="RealModelGate.AssessLiveWholeLoopAsync(string, Func{Task{ValueTuple{RealModelOutcome, string}}}, int?)"/>
/// reds on CapabilityMiss too (real-model-drove-to-completion = the only pass), flake-safed by a best-of-N floor.
/// </summary>
public enum RealModelOutcome
{
    /// <summary>The live brain produced conformant decisions that drove the engine to the intended terminal. The gate is satisfied (the ONLY pass under the strict whole-loop gate).</summary>
    Drove,

    /// <summary>The live brain did NOT drive the arc — it produced no conformant decision, or stopped/force-stopped short of the outcome. A MODEL precondition, NOT a code bug. The report-only reaction arcs REPORT it (never gate); the STRICT whole-loop gate REDS it after a best-of-N floor (real-model-drove-to-completion is the criterion — a model that ran but parked short is not a pass).</summary>
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

    /// <summary>The env var that overrides the STRICT whole-loop gate's best-of-N attempt budget (Rule 8 escape hatch — an operator can raise N if the gateway's single-arc park-short rate p is high enough that p^2 still flakes main, trading cost for stability, with no code change). Pinned by test.</summary>
    public const string WholeLoopAttemptsEnvVar = "CODESPACE_REALMODEL_WHOLE_LOOP_ATTEMPTS";

    /// <summary>The default best-of-N attempt budget for the strict whole-loop gate: the live model gets this many INDEPENDENT runs to drive the arc to the accept head before a CapabilityMiss gates (flake ~p^N). 2 balances flake-resistance against per-PR token cost.</summary>
    public const int DefaultWholeLoopAttempts = 2;

    /// <summary>Extra attempts allowed ON TOP of the capability budget so a slow/dropping gateway (non-gating infra) never EXHAUSTS the capability budget and forces a false skip — a gateway-infra attempt does not consume a capability slot, but total attempts are still bounded so an always-infra gateway can't loop forever.</summary>
    private const int InfraRetryBudget = 2;

    /// <summary>The effective best-of-N attempt budget: the env override when positive + parseable, else <see cref="DefaultWholeLoopAttempts"/> (Rule 8 — read only here).</summary>
    public static int WholeLoopAttempts()
    {
        var raw = Environment.GetEnvironmentVariable(WholeLoopAttemptsEnvVar)?.Trim();

        return int.TryParse(raw, out var n) && n > 0 ? n : DefaultWholeLoopAttempts;
    }

    /// <summary>The env var that overrides the boolean live-EVAL best-of-N budget (trajectory / arbiter) — Rule 8 escape hatch, pinned by test.</summary>
    public const string EvalAttemptsEnvVar = "CODESPACE_REALMODEL_EVAL_ATTEMPTS";

    /// <summary>The default best-of-N budget for a BOOLEAN live eval (trajectory / arbiter): N independent attempts on the BLESSED wire absorb a non-deterministic model's run-to-run variance so a single off-run can't flaky-red main, while a persistent miss still REDs. 2 balances flake-resistance against per-attempt cost (a trajectory attempt can be minutes).</summary>
    public const int DefaultEvalAttempts = 2;

    /// <summary>The effective boolean-eval best-of-N budget: the env override when positive + parseable, else <see cref="DefaultEvalAttempts"/> (Rule 8 — read only here).</summary>
    public static int EvalAttempts()
    {
        var raw = Environment.GetEnvironmentVariable(EvalAttemptsEnvVar)?.Trim();

        return int.TryParse(raw, out var n) && n > 0 ? n : DefaultEvalAttempts;
    }

    /// <summary>
    /// Drive a BOOLEAN live eval (trajectory / arbiter) with the SAME best-of-N capability-floor as the whole-loop gate:
    /// the BLESSED wire passes when ANY of <paramref name="attempts"/> independent attempts is Ok (flake ~p^N), gating only
    /// when EVERY non-infra attempt fails; a gateway-infra failure is a non-gating LOUD skip that does NOT consume a slot.
    /// An INFORMATIONAL wire never gates, so it runs ONCE and reports (best-of-N is a gating concern — and this saves N×
    /// cost on the non-blessed wire). A non-infra exception PROPAGATES (never swallowed). The driveOnce factory MUST be
    /// self-contained per call (a fresh run / fresh deadline), since it is invoked up to N times.
    /// </summary>
    public static Task AssessLiveBestOfNAsync(string provider, Func<Task<(bool Ok, string Verdict)>> driveOnce, int? attempts = null) =>
        AssessLiveBestOfNAsync(provider, driveOnce, attempts ?? EvalAttempts(), Environment.GetEnvironmentVariable(StepSummaryEnvVar));

    /// <summary>Testable core of the boolean best-of-N eval — explicit budget + step-summary path so a test pins the logic with no live call. Informational wire → one reported attempt; blessed wire → any Ok passes, all-fail gates (with the per-attempt verdicts), infra is a non-gating skip that does not consume a slot.</summary>
    internal static async Task AssessLiveBestOfNAsync(string provider, Func<Task<(bool Ok, string Verdict)>> driveOnce, int attempts, string? stepSummaryPath)
    {
        if (!IsRequired(provider))   // informational wire never gates → one reported attempt is enough (and avoids N× cost on the non-blessed wire)
        {
            try
            {
                var (ok, verdict) = await driveOnce().ConfigureAwait(false);
                ReportInformational(ok, verdict, stepSummaryPath);
            }
            catch (Exception ex) when (IsGatewayInfraFailure(ex))
            {
                ReportInfraSkip(provider, ex, stepSummaryPath);
            }

            return;
        }

        var budget = Math.Max(1, attempts);
        var failVerdicts = new List<string>();
        var maxAttempts = budget + InfraRetryBudget;

        for (var i = 0; i < maxAttempts && failVerdicts.Count < budget; i++)
        {
            try
            {
                var (ok, verdict) = await driveOnce().ConfigureAwait(false);

                ReportInformational(ok, verdict, stepSummaryPath);   // every attempt's verdict surfaced — a persistent miss is visible

                if (ok) return;   // any Ok among N → PASS

                failVerdicts.Add(verdict);
            }
            catch (Exception ex) when (IsGatewayInfraFailure(ex))
            {
                ReportInfraSkip(provider, ex, stepSummaryPath);   // non-gating infra — does NOT consume a capability slot
            }
        }

        if (failVerdicts.Count >= budget)
            false.ShouldBeTrue($"REQUIRED wire — the live model FAILED the eval in all {budget} attempt(s) (NOT a gateway-infra fault). The blessed wire requires at least one passing attempt. Per-attempt verdict: {string.Join(" || ", failVerdicts)}");
    }

    /// <summary>
    /// Drive the STRICT live-model WHOLE-LOOP gate — the real-model-DROVE-to-completion criterion: the blessed wire
    /// passes ONLY when the live model drove the arc to the genuine accept head (<see cref="RealModelOutcome.Drove"/>).
    /// A <see cref="RealModelOutcome.CapabilityMiss"/> (the model RAN but parked short of the accept head) now REDS the
    /// blessed wire — it is NOT a "reported" footnote — made FLAKE-SAFE by a bounded best-of-N capability-floor:
    /// <paramref name="attempts"/> INDEPENDENT re-runs (a FRESH run per call of <paramref name="driveOnce"/>), gating only
    /// when EVERY non-infra attempt parks short (flake ~p^N). A <see cref="RealModelOutcome.CodeFault"/> reds IMMEDIATELY
    /// and is NEVER retried (a code regression is not capability variance). A gateway-infra failure is a non-gating LOUD
    /// skip that does NOT consume a capability slot (a slow gateway never burns the budget; total attempts stay bounded so
    /// an always-infra gateway can't loop). Every attempt's outcome is ALWAYS reported, so a persistent miss is visible,
    /// never a silent green. An informational wire never gates regardless. SKIP ≠ PASS: the caller's secret guard handles
    /// the no-credentials skip (surfaced via <see cref="ReportSkipped(string, string)"/>) — this method never sees it.
    /// </summary>
    public static Task AssessLiveWholeLoopAsync(string provider, Func<Task<(RealModelOutcome Outcome, string Note)>> driveOnce, int? attempts = null) =>
        AssessLiveWholeLoopAsync(provider, driveOnce, attempts ?? WholeLoopAttempts(), Environment.GetEnvironmentVariable(StepSummaryEnvVar));

    /// <summary>Testable core of the strict whole-loop gate — takes the attempt budget + step-summary path explicitly so a test pins the best-of-N / infra / gate logic with NO live call and without mutating process env. Any Drove → pass; a CodeFault → gate at once (never retried); a gateway-infra failure → non-gating skip that does not consume a slot; only when all <paramref name="attempts"/> non-infra attempts park short → gate (CapabilityMiss).</summary>
    internal static async Task AssessLiveWholeLoopAsync(string provider, Func<Task<(RealModelOutcome Outcome, string Note)>> driveOnce, int attempts, string? stepSummaryPath)
    {
        var budget = Math.Max(1, attempts);   // defend the core: a non-positive budget would otherwise gate on ZERO misses (the public entrypoint already clamps via WholeLoopAttempts, but this core is callable directly)
        var missNotes = new List<string>();   // accumulate each park-short verdict so the gate message names WHY (rounds vs schema), visible in the CI console log — not just the job summary
        var maxAttempts = budget + InfraRetryBudget;

        for (var i = 0; i < maxAttempts && missNotes.Count < budget; i++)
        {
            try
            {
                var (outcome, note) = await driveOnce().ConfigureAwait(false);

                ReportThreeWay(outcome, note, stepSummaryPath);

                if (outcome == RealModelOutcome.Drove) return;   // any Drove among N → PASS (real model drove to completion)

                if (outcome == RealModelOutcome.CodeFault)        // a code regression reds at once — never retried, not capability variance
                {
                    if (IsRequired(provider))
                        false.ShouldBeTrue($"REQUIRED wire — the engine FAULTED driving the live brain's decisions (a CODE regression): {note}");

                    return;
                }

                missNotes.Add(note);   // CapabilityMiss → best-of-N retry on a fresh run
            }
            catch (Exception ex) when (IsGatewayInfraFailure(ex))
            {
                ReportInfraSkip(provider, ex, stepSummaryPath);   // non-gating infra — does NOT consume a capability slot
            }
        }

        if (missNotes.Count >= budget && IsRequired(provider))
            false.ShouldBeTrue($"REQUIRED wire — the live model did NOT drive the arc to the accept head in {budget} attempt(s) (a CapabilityMiss, NOT a gateway-infra fault). The real-model-drove-to-completion gate requires a Drove; a skip is reported separately and is never a pass. Per-attempt verdict: {string.Join(" || ", missNotes)}");
        // else: misses < attempts only because gateway-infra exhausted the bounded attempt budget → non-gating infra skip (already reported loudly).
    }

    /// <summary>Surface a no-credentials / unavailable-binary SKIP LOUDLY as explicitly NOT-A-PASS — so the ONLY honest green-skip (a fork/local run with no live model) is legible in the job summary and can never be mistaken for a real-model pass. Pure given <paramref name="stepSummaryPath"/>.</summary>
    public static void ReportSkipped(string provider, string reason) =>
        ReportSkipped(provider, reason, Environment.GetEnvironmentVariable(StepSummaryEnvVar));

    /// <summary>Testable core of <see cref="ReportSkipped(string, string)"/> — explicit step-summary path. Writes a 'NOT EVALUATED … skip ≠ pass' line so an honest skip is visible, never a silent green that reads as a pass.</summary>
    internal static void ReportSkipped(string provider, string reason, string? stepSummaryPath)
    {
        var line = $"⏭️ real-model whole-loop NOT EVALUATED — {provider} skipped ({reason}). A skip is NOT a pass: no live model ran, so nothing was driven to completion.";

        if (!string.IsNullOrWhiteSpace(stepSummaryPath))
            File.AppendAllText(stepSummaryPath, line + Environment.NewLine);
        else
            Console.WriteLine(line);
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
    /// Classify the spawned agents' EXECUTION health for a whole-loop verdict, separating a MODEL miss from an
    /// OS/sandbox/process/capture INFRA fault. The whole-loop fake agent is a DETERMINISTIC <c>exit 0</c> script that
    /// cannot CHOOSE to fail, so a fan-out where the brain spawned ≥1 agent yet NONE succeeded is an execution-infra
    /// fault on the runner (the model drove its decisions; its agents broke underneath it) → the caller raises
    /// <see cref="AgentExecutionInfraException"/> to route it to the non-gating infra skip, NEVER a CapabilityMiss red.
    /// When at least one agent succeeded the execution path WORKS, so any shortfall is the model's and gates as usual.
    /// When the brain spawned ZERO agents (parked at plan, never fanned out) it is a genuine model miss — NOT infra —
    /// so the gate still reds it. Returns the legible summary appended to the verdict note in every case.
    ///
    /// <para>The boundary is "NONE succeeded" (not "all <c>Failed</c>") DELIBERATELY: on the strict lane's deterministic
    /// exit-0 fake, ANY non-succeeded terminal (Failed / TimedOut / Stalled→NeedsReview / Cancelled) is a runner-side
    /// execution break the model cannot author, so treating every all-non-succeeded fan-out as infra is the safe,
    /// no-false-red choice — do NOT narrow this to <c>failed == count</c> (a sandbox hang ending all-TimedOut would then
    /// red as a phantom miss). The blast radius is the deterministic-fake gating lane; the real-agent lanes are report-only.</para>
    /// </summary>
    public static (bool ExecutionInfraFault, string Summary) ClassifyAgentExecution(IReadOnlyList<AgentRunStatus> statuses)
    {
        if (statuses.Count == 0) return (false, "agents=0 (never fanned out)");   // a plan-only park is a genuine miss, NOT infra — it gates

        var succeeded = statuses.Count(s => s == AgentRunStatus.Succeeded);
        var failed = statuses.Count(s => s == AgentRunStatus.Failed);

        return (succeeded == 0, $"agents={statuses.Count} ({succeeded} succeeded, {failed} failed)");
    }

    /// <summary>
    /// Whether a whole-loop run that SPAWNED + MERGED with succeeded agents yet captured ZERO real patches is a
    /// workspace-CAPTURE / execution infra fault rather than a model miss. ONLY meaningful when the spawned agents are
    /// DETERMINISTIC fakes that ALWAYS write a file on success (the headline arc's <c>FileWritingFakeCli</c>): the model
    /// cannot make such an agent produce nothing, so a succeeded fan-out with NO captured patch means the file write or
    /// the git-diff capture broke under runner load (fork-starvation on a flaky shared host) — non-gating infra, the
    /// counterpart of <see cref="ClassifyAgentExecution"/>'s all-failed case for the "agents succeeded but their work
    /// was not captured" symptom. NOT applied to a REAL coding agent (claude), where producing no patch is a legitimate
    /// capability outcome that MUST gate — so the caller passes <paramref name="deterministicFakeAgents"/>=false there.
    /// </summary>
    public static bool IsCaptureInfraFault(bool deterministicFakeAgents, bool spawnedAndMerged, int succeededAgents, int realPatchCount) =>
        deterministicFakeAgents && spawnedAndMerged && succeededAgents > 0 && realPatchCount == 0;

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
        // An EVALUATOR-raised execution-infra fault (the brain's spawned agents could not run on the runner — a
        // deterministic exit-0 fake can't CHOOSE to fail) routes through the SAME non-gating infra-skip path as a
        // gateway timeout: the model drove its DECISIONS fine; its agents broke underneath it, so this is infra, never
        // a CapabilityMiss. Does not consume a best-of-N capability slot.
        AgentExecutionInfraException => true,
        System.Net.Sockets.SocketException se => !WiringSocketErrors.Contains(se.SocketErrorCode),
        // The decider classifies a gateway fault into a TYPED LlmApiException and PROPAGATES the infra categories
        // (Transient / RateLimited / AuthFailed) rather than fail-closing them — so the EXCEPTION path (trajectory /
        // arbiter, which catch the throw directly) must treat those exactly as the string-based IsGatewayInfraError
        // already treats the persisted node-failed record: non-gating infra. The model-CAPABILITY categories
        // (Malformed / ContextLengthExceeded / ContentFiltered / BadRequest) are NOT here — they are a real miss and gate.
        LlmApiException { Category: LlmErrorCategory.Transient or LlmErrorCategory.RateLimited or LlmErrorCategory.AuthFailed } => true,
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

    /// <summary>Report an infra failure LOUDLY as non-gating — to the step-summary FILE when present (so a persistently-slow gateway OR a runner-side agent-execution break is VISIBLE in the job-summary UI, never a silent green), else the console. The reason names whether it was the gateway transport or the agents that broke (NOT a decision verdict either way). Pure given <paramref name="stepSummaryPath"/>.</summary>
    internal static void ReportInfraSkip(string provider, Exception ex, string? stepSummaryPath)
    {
        var line = $"⚠️ real-model gate NON-GATING infra skip — {provider} (infra fault, NOT a decision verdict): {InfraReason(ex)}";

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
