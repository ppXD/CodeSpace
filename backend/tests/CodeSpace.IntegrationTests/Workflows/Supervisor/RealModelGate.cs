using System.Runtime.CompilerServices;
using Shouldly;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
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

    /// <summary>The configured live model id (a repository SECRET, so it is MASKED in the CI log). Read only to derive <see cref="ModelStamp"/>'s fallback fingerprint when no response named a model.</summary>
    public const string ModelIdEnvVar = "CODESPACE_LLM_MODEL_ID";

    private static readonly string[] DefaultRequiredProviders = { "Anthropic" };

    /// <summary>
    /// The per-assessment sink the observing test client writes the PROVIDER-REPORTED model name into. A mutable
    /// holder in an <see cref="AsyncLocal{T}"/> (not a plain AsyncLocal string) because an AsyncLocal WRITE made
    /// deeper in the call flow — inside the drive closure — does not propagate back up to the gate that awaits it;
    /// mutating an object the gate itself installed does.
    /// </summary>
    private sealed class ModelSink { public string? Name; }

    private static readonly AsyncLocal<ModelSink?> Sink = new();

    /// <summary>The side file, written beside the step summaries, naming every model a live response reported this job — read by <c>collect-real-model-verdicts.sh</c> as extra redaction needles. See <see cref="RecordForRedaction"/>.</summary>
    public const string ObservedModelsFileName = "codespace_observed_models";

    private static readonly HashSet<string> RecordedForRedaction = new(StringComparer.Ordinal);

    /// <summary>Record the model name the PROVIDER reported for a live response (<c>StructuredLLMCompletion.Model</c> / <c>LLMCompletion.Model</c>). Called by the observing client the live-wire registry wraps; the sink write is a no-op when no assessment is in flight, but the redaction record is NOT — an unassessed call logs the same name.</summary>
    public static void ObserveModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;

        if (Sink.Value is { } sink) sink.Name = model;

        RecordForRedaction(model.Trim());
    }

    /// <summary>
    /// Append a provider-reported model name to the side file the collect step reads, once per distinct name.
    /// </summary>
    /// <remarks>
    /// The gate itself no longer writes a model name anywhere, but OTHER components still log one:
    /// <c>LlmCompleteNode</c> logs <c>"LLM completion {Model} …"</c> with this very value, and xUnit captures that
    /// into the trx's <c>&lt;StdOut&gt;</c> — which is how run 33754366815's <c>real-model-footer-signals.trx</c>
    /// shipped a provider model name. Redacting by log-message SHAPE would break the moment a template changed, so
    /// the gate instead hands the collect step the exact VALUES to strike. Beside the step summaries because that
    /// directory is the one place a later step can reach (a step summary is per-STEP), and it is not itself
    /// uploaded. Best-effort and silent on failure: this is a diagnostic aid, and the named secret env vars are
    /// redacted independently of it.
    /// </remarks>
    private static void RecordForRedaction(string model)
    {
        var summaryPath = Environment.GetEnvironmentVariable(StepSummaryEnvVar);

        if (string.IsNullOrWhiteSpace(summaryPath)) return;

        lock (RecordedForRedaction)
        {
            if (!RecordedForRedaction.Add(model)) return;

            try
            {
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(summaryPath)!, ObservedModelsFileName), model + Environment.NewLine);
            }
            catch (Exception)
            {
                // A collection detail must never fail a lane whose tests passed.
            }
        }
    }

    /// <summary>Install a fresh sink for one assessment so each gate call reports the model THAT call observed.</summary>
    private static void ArmModelSink() => Sink.Value = new ModelSink();

    /// <summary>
    /// The model provenance appended to every verdict/report line: <c>[model fp=&lt;8-hex&gt; (observed|configured)]</c>,
    /// plus <c>, differs from configured fp=&lt;8-hex&gt;</c> when the gateway answered with something other than the pin.
    /// The fingerprint is taken over the PROVIDER-REPORTED name when a live response was observed, else over the
    /// configured <c>CODESPACE_LLM_MODEL_ID</c>, and the tag says which — so a red streak can be told apart as "the
    /// gateway started answering with another model" by comparing two runs' stamps. <c>model=unknown</c> means nothing
    /// named a model at all.
    ///
    /// <para>It carries NO NAME ANYWHERE, deliberately — not even to stdout, which is not a private channel either:
    /// xUnit captures console output into the trx's <c>&lt;StdOut&gt;</c>, and the trx is uploaded. Every line this
    /// stamp rides on ends up in a FILE, and GitHub masks a secret in the LOG only, never in a file: run
    /// 33723910434's <c>real-model-results</c> artifact shipped the raw configured id, a repository secret, to anyone
    /// who could download it. The observed name is no safer — on the pinned lane it is that same id. Drift is
    /// therefore reported as a fingerprint COMPARISON (<c>differs from configured fp=…</c>), which says the one thing
    /// a name was there to say and survives both masking and redaction.</para>
    /// </summary>
    internal static string ModelStamp() => ModelStamp(Sink.Value?.Name, Environment.GetEnvironmentVariable(ModelIdEnvVar)) + ConfinementStamp();

    /// <summary>Testable core of <see cref="ModelStamp()"/> — explicit observed/configured names, so the written stamp is pinnable without mutating process env.</summary>
    internal static string ModelStamp(string? observed, string? configured)
    {
        var live = string.IsNullOrWhiteSpace(observed) ? null : observed.Trim();
        var pinned = string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        var name = live ?? pinned;

        if (name is null) return "[model=unknown fp=none]";

        // "the gateway started answering with another model" is the whole diagnostic a name used to carry; as a
        // fingerprint comparison it is just as actionable and gives away nothing.
        var drift = live is not null && pinned is not null && live != pinned ? $", differs from configured fp={Fingerprint(pinned)}" : "";

        return $"[model fp={Fingerprint(name)} ({(live is null ? "configured" : "observed")}{drift})]";
    }

    /// <summary>
    /// What confinement THIS lane's runner can apply, appended to every verdict line. INFORMATIONAL — it gates
    /// nothing — but a lane whose agents ran with no severable egress produced its verdict under a materially
    /// different sandbox than the privileged gate does, and the reader of an archived summary has no other way to
    /// tell. Derived from the same probe the runner stamps on each run, so the label cannot claim what the runs did not get.
    /// </summary>
    internal static string ConfinementStamp() => ConfinementStamp(BubblewrapSandbox.Available, BubblewrapSandbox.UnavailableReason);

    /// <summary>Testable core of <see cref="ConfinementStamp()"/> — explicit probe results, so the word is pinnable on any host.</summary>
    internal static string ConfinementStamp(string? available, string? unavailableReason)
    {
        var confinement = BubblewrapSandbox.DeriveConfinement(available, unavailableReason, shareNetwork: false, egressAllowlist: null);

        return confinement.Outcome == SandboxConfinementOutcome.Confined
            ? " [runner=confined]"
            : $" [runner=unconfined ({confinement.Reason})]";
    }

    /// <summary>First 8 hex of SHA-256 of <paramref name="value"/> — a stable, masking-proof identity for a secret model id, comparable across runs.</summary>
    internal static string Fingerprint(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..8];

    /// <summary>Apply the gate to ONE wire's verdict: a REQUIRED wire asserts (a bad verdict fails the job); an INFORMATIONAL wire reports its verdict where CI shows it and returns WITHOUT gating.</summary>
    public static void Assess(string provider, bool ok, string verdict, [CallerMemberName] string? test = null)
    {
        if (IsRequired(provider))
        {
            ok.ShouldBeTrue($"REQUIRED wire — {verdict} {ModelStamp()}");
            return;
        }

        ReportInformational(provider, ok, verdict, Environment.GetEnvironmentVariable(StepSummaryEnvVar), test);
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
    public static Task AssessLiveAsync(string provider, Func<Task<(bool Ok, string Verdict)>> drive, bool gating = true, [CallerMemberName] string? test = null) =>
        AssessLiveAsync(provider, drive, gating, Environment.GetEnvironmentVariable(StepSummaryEnvVar), test: test);

    /// <summary>Testable core of <see cref="AssessLiveAsync(string, Func{Task{ValueTuple{bool, string}}}, bool)"/> — takes the step-summary path explicitly so a test pins the behaviour without mutating process env. When <paramref name="gating"/> is false the clean verdict is REPORTED (informational), never asserted — for a lane whose live result is observed but must not block main (e.g. a precondition the blessed decision-eval already measures); an infra failure is non-gating regardless.</summary>
    internal static async Task AssessLiveAsync(string provider, Func<Task<(bool Ok, string Verdict)>> drive, bool gating, string? stepSummaryPath, TimeSpan? attemptDeadline = null, [CallerMemberName] string? test = null)
    {
        ArmModelSink();

        try
        {
            // BOUNDED (Rule 12.10): an unbounded await here let one hung agent ride to the CI job's wall-clock cap.
            if (await DriveWithinDeadlineAsync(drive, attemptDeadline).ConfigureAwait(false) is not { } outcome)
            {
                ReportInformational(provider, false, DidNotConvergeNote(provider, attemptDeadline), stepSummaryPath, test);
                return;
            }

            var (ok, verdict) = outcome;

            if (gating) Assess(provider, ok, verdict, test);
            else ReportInformational(provider, ok, verdict, stepSummaryPath, test);
        }
        catch (Exception ex) when (IsGatewayInfraFailure(ex))
        {
            // NOTHING was measured, so the trx must say so: a SkipException lands this case as NotExecuted instead of
            // the Passed that let a 1-second "pass" of a 23-live-call gate sit next to a real 5-minute one.
            throw new SkipException(ReportInfraSkip(provider, ex, stepSummaryPath));
        }
        catch (Exception ex) when (!gating && ex is not ShouldAssertException)
        {
            // A gating:false arm that can still RED the job is lying about being non-gating, and it happened: run
            // 30809950520's stop-DoD arm died on a JsonException because the live model authored a stop payload
            // missing a `required` property. Nothing about that is a verdict — the arm never reached one.
            //
            // A ShouldAssertException is EXCLUDED deliberately and must keep propagating. Some report-only arms
            // assert HARD inside the closure on purpose — the S1 handoff arm says so in as many words ("the handoff
            // MECHANISM is asserted HARD (Shouldly, bypassing the soft report-only gate)") — so swallowing those
            // would disarm the very regressions the report-only framing was chosen to keep watching. The split is
            // between the arm SPEAKING (an assertion it authored) and the arm BREAKING (anything else).
            ReportInformational(provider, false, $"{provider} arm FAULTED before reaching a verdict — {ex.GetType().Name}: {ex.Message}. Reported, not gating: a report-only arm cannot red the job, and no verdict was produced to report.", stepSummaryPath, test);
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
    public static Task AssessLiveAsync(string provider, Func<Task<(RealModelOutcome Outcome, string Note)>> drive, [CallerMemberName] string? test = null) =>
        AssessLiveAsync(provider, drive, Environment.GetEnvironmentVariable(StepSummaryEnvVar), test: test);

    /// <summary>Testable core of the three-way <see cref="AssessLiveAsync(string, Func{Task{ValueTuple{RealModelOutcome, string}}})"/> — takes the step-summary path explicitly so a test pins the behaviour without mutating process env. The outcome is ALWAYS reported; the blessed wire asserts only that it is NOT a <see cref="RealModelOutcome.CodeFault"/>; an informational wire never asserts; a gateway infra failure is a non-gating skip.</summary>
    internal static async Task AssessLiveAsync(string provider, Func<Task<(RealModelOutcome Outcome, string Note)>> drive, string? stepSummaryPath, TimeSpan? attemptDeadline = null, [CallerMemberName] string? test = null)
    {
        ArmModelSink();

        try
        {
            // BOUNDED (Rule 12.10) — same reason as the boolean overload. A bust reports as a CapabilityMiss, which this
            // policy never gates on: the arm produced no verdict, and a hang is not evidence of a CODE regression.
            if (await DriveWithinDeadlineAsync(drive, attemptDeadline).ConfigureAwait(false) is not { } verdict)
            {
                ReportThreeWay(RealModelOutcome.CapabilityMiss, DidNotConvergeNote(provider, attemptDeadline), stepSummaryPath, test);
                return;
            }

            var (outcome, note) = verdict;

            ReportThreeWay(outcome, note, stepSummaryPath, test);

            if (IsRequired(provider))
                (outcome != RealModelOutcome.CodeFault).ShouldBeTrue($"REQUIRED wire — the engine FAULTED driving the live brain (a CODE regression, NOT a model-capability miss): {note} {ModelStamp()}");
        }
        catch (Exception ex) when (IsGatewayInfraFailure(ex))
        {
            throw new SkipException(ReportInfraSkip(provider, ex, stepSummaryPath));   // nothing measured → NotExecuted in the trx, never a Passed
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

    /// <summary>The env var that overrides the per-attempt DEADLINE for the strict whole-loop gate (Rule 8 escape hatch). Each best-of-N attempt is bounded by this wall-clock; an attempt that exceeds it is a "did not converge" MISS (a hung agent / gateway call that never returned), NOT a gateway-infra skip and NOT a silent ride to the CI job's wall-clock cap. Pinned by test.</summary>
    public const string WholeLoopAttemptDeadlineEnvVar = "CODESPACE_REALMODEL_WHOLE_LOOP_ATTEMPT_DEADLINE_SECONDS";

    /// <summary>The default per-attempt deadline (seconds) for the strict whole-loop gate. A healthy attempt (real brain turns + fast deterministic fake agents) converges in ~2-7 min, so 600s gives headroom; a hang is bounded to this instead of riding to the CI job's 60-min cap. With the default 2-miss budget a persistent hang REDs in ~2×deadline, well under the job cap.</summary>
    public const int DefaultWholeLoopAttemptDeadlineSeconds = 600;

    /// <summary>The effective per-attempt deadline: the env override when positive + parseable, else <see cref="DefaultWholeLoopAttemptDeadlineSeconds"/> (Rule 8 — read only here).</summary>
    public static TimeSpan WholeLoopAttemptDeadline()
    {
        var raw = Environment.GetEnvironmentVariable(WholeLoopAttemptDeadlineEnvVar)?.Trim();

        return int.TryParse(raw, out var n) && n > 0 ? TimeSpan.FromSeconds(n) : TimeSpan.FromSeconds(DefaultWholeLoopAttemptDeadlineSeconds);
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

    /// <summary>The env var that overrides the per-attempt DEADLINE for the SINGLE-attempt arms — <see cref="AssessLiveAsync(string, Func{Task{ValueTuple{bool, string}}}, bool)"/>, its three-way sibling, and each <see cref="AssessLiveBestOfNAsync(string, Func{Task{ValueTuple{bool, string}}}, int?)"/> attempt (Rule 8 escape hatch: an operator raises it for an arm that legitimately runs long). Pinned by test.</summary>
    public const string AttemptDeadlineEnvVar = "CODESPACE_REALMODEL_ATTEMPT_DEADLINE_SECONDS";

    /// <summary>
    /// The default per-attempt deadline (seconds) for the single-attempt arms. Only the STRICT whole-loop gate was
    /// bounded (<see cref="DefaultWholeLoopAttemptDeadlineSeconds"/>); every other arm awaited its drive closure
    /// UNBOUNDED, so one hung agent rode to the CI job's wall-clock cap and killed the whole job — run 33972713055,
    /// where a wedged CLI session inside the report-only multi-repo arm burned the agent's full 1h default and took
    /// an innocent sibling arm down with it at 120:00. 1200s (20m) sits well clear of the ~15m a HEALTHY multi-repo
    /// drive takes (the slowest arm on the lane) while bounding a hang to a sixth of the job cap.
    /// </summary>
    public const int DefaultAttemptDeadlineSeconds = 1200;

    /// <summary>The effective per-attempt deadline for the single-attempt arms: the env override when positive + parseable, else <see cref="DefaultAttemptDeadlineSeconds"/> (Rule 8 — read only here).</summary>
    public static TimeSpan AttemptDeadline()
    {
        var raw = Environment.GetEnvironmentVariable(AttemptDeadlineEnvVar)?.Trim();

        return int.TryParse(raw, out var n) && n > 0 ? TimeSpan.FromSeconds(n) : TimeSpan.FromSeconds(DefaultAttemptDeadlineSeconds);
    }

    /// <summary>
    /// Await <paramref name="drive"/> bounded by <see cref="AttemptDeadline"/> (Rule 12.10 — every long wait carries an
    /// explicit timeout). Returns the arm's verdict, or <c>null</c> when the attempt BUSTS the deadline: a hung agent or
    /// a gateway call that never returned. Mirrors the bound <see cref="AssessLiveWholeLoopAsync(string, Func{Task{ValueTuple{RealModelOutcome, string}}}, int?)"/>
    /// already has; the CALLER decides how to report the bust, because their verdict vocabularies differ.
    /// </summary>
    private static async Task<T?> DriveWithinDeadlineAsync<T>(Func<Task<T>> drive, TimeSpan? attemptDeadline) where T : struct
    {
        using var cts = new CancellationTokenSource(attemptDeadline ?? AttemptDeadline());

        try { return await drive().WaitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return null; }
    }

    /// <summary>
    /// The verdict line a deadline BUST reports — Rule 12.10's "name the watched signal AND how to diagnose it". A bust is
    /// LOUD but NEVER gating on these arms: unlike the strict whole-loop gate they have no best-of-N floor to absorb a slow
    /// gateway, so redding on one over-long attempt would block main on the owner's gateway being slow — the exact failure
    /// the gate's infra-is-non-gating rule exists to prevent. It is bounded and surfaced instead of silently killing the job.
    /// </summary>
    internal static string DidNotConvergeNote(string provider, TimeSpan? attemptDeadline = null) =>
        $"{provider} arm did NOT converge within {(attemptDeadline ?? AttemptDeadline()).TotalSeconds:0}s — likely a hung agent CLI or a gateway call that never returned (per-attempt deadline). "
      + "Reported, ⚠️ NOT red: a single-attempt arm has no best-of-N floor to absorb a slow gateway, so a bust is bounded + surfaced rather than gating. "
      + $"To diagnose: re-run this arm alone and watch its agent runs' Status/Harness; raise {AttemptDeadlineEnvVar} on the lane if the arm legitimately needs longer.";

    /// <summary>
    /// Drive a BOOLEAN live eval (trajectory / arbiter) with the SAME best-of-N capability-floor as the whole-loop gate:
    /// the BLESSED wire passes when ANY of <paramref name="attempts"/> independent attempts is Ok (flake ~p^N), gating only
    /// when EVERY non-infra attempt fails; a gateway-infra failure is a non-gating LOUD skip that does NOT consume a slot.
    /// An INFORMATIONAL wire never gates, so it runs ONCE and reports (best-of-N is a gating concern — and this saves N×
    /// cost on the non-blessed wire). A non-infra exception PROPAGATES (never swallowed). The driveOnce factory MUST be
    /// self-contained per call (a fresh run / fresh deadline), since it is invoked up to N times.
    /// </summary>
    public static Task AssessLiveBestOfNAsync(string provider, Func<Task<(bool Ok, string Verdict)>> driveOnce, int? attempts = null, [CallerMemberName] string? test = null) =>
        AssessLiveBestOfNAsync(provider, driveOnce, attempts ?? EvalAttempts(), Environment.GetEnvironmentVariable(StepSummaryEnvVar), test: test);

    /// <summary>Testable core of the boolean best-of-N eval — explicit budget + step-summary path so a test pins the logic with no live call. Informational wire → one reported attempt; blessed wire → any Ok passes, all-fail gates (with the per-attempt verdicts), infra is a non-gating skip that does not consume a slot.</summary>
    internal static async Task AssessLiveBestOfNAsync(string provider, Func<Task<(bool Ok, string Verdict)>> driveOnce, int attempts, string? stepSummaryPath, TimeSpan? attemptDeadline = null, [CallerMemberName] string? test = null)
    {
        ArmModelSink();

        if (!IsRequired(provider))   // informational wire never gates → one reported attempt is enough (and avoids N× cost on the non-blessed wire)
        {
            try
            {
                if (await DriveWithinDeadlineAsync(driveOnce, attemptDeadline).ConfigureAwait(false) is not { } attempt)   // BOUNDED (Rule 12.10)
                {
                    ReportInformational(provider, false, DidNotConvergeNote(provider, attemptDeadline), stepSummaryPath, test);
                    return;
                }

                var (ok, verdict) = attempt;
                ReportInformational(provider, ok, verdict, stepSummaryPath, test);
            }
            catch (Exception ex) when (IsGatewayInfraFailure(ex))
            {
                throw new SkipException(ReportInfraSkip(provider, ex, stepSummaryPath));   // nothing measured → NotExecuted
            }

            return;
        }

        var budget = Math.Max(1, attempts);
        var failVerdicts = new List<string>();
        var maxAttempts = budget + InfraRetryBudget;
        string? infraSkip = null;   // the last non-gating infra reason — the SKIP the trx must record if infra eats the budget

        for (var i = 0; i < maxAttempts && failVerdicts.Count < budget; i++)
        {
            try
            {
                // BOUNDED (Rule 12.10). A bust does NOT consume a capability slot — an attempt that never returned
                // produced no verdict, so counting it as a model failure would gate the blessed wire on a hang. Same
                // routing as gateway infra: still bounded by maxAttempts, and a SKIP if nothing else was ever measured.
                if (await DriveWithinDeadlineAsync(driveOnce, attemptDeadline).ConfigureAwait(false) is not { } attempt)
                {
                    infraSkip = ReportInfraSkip(provider, new TimeoutException(DidNotConvergeNote(provider, attemptDeadline)), stepSummaryPath);
                    continue;
                }

                var (ok, verdict) = attempt;

                ReportInformational(provider, ok, verdict, stepSummaryPath, test, informational: false);   // every attempt's verdict surfaced — a persistent miss is visible. NOT "informational": this is the gating wire's attempt N.

                if (ok) return;   // any Ok among N → PASS

                failVerdicts.Add(verdict);
            }
            catch (Exception ex) when (IsGatewayInfraFailure(ex))
            {
                infraSkip = ReportInfraSkip(provider, ex, stepSummaryPath);   // non-gating infra — does NOT consume a capability slot
            }
        }

        if (failVerdicts.Count >= budget)
            false.ShouldBeTrue($"REQUIRED wire — the live model FAILED the eval in all {budget} attempt(s) (NOT a gateway-infra fault). The blessed wire requires at least one passing attempt. Per-attempt verdict: {string.Join(" || ", failVerdicts)} {ModelStamp()}");

        // The budget ran out on gateway INFRA, so the eval never reached a full N-attempt verdict. Non-gating as before
        // — but a SKIP, not a pass: the trx records NotExecuted so "the lane measured nothing" is legible as itself.
        //
        // ONLY when nothing was measured at all. `infra,infra,infra,fail` on a budget of 2 leaves one REAL fail verdict
        // that the model actually produced; calling that NotExecuted would throw away the very measurement the lane
        // exists to take (and hide it from the outcome table the guard prints).
        if (failVerdicts.Count == 0 && infraSkip != null) throw new SkipException(infraSkip);
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
    public static Task AssessLiveWholeLoopAsync(string provider, Func<Task<(RealModelOutcome Outcome, string Note)>> driveOnce, int? attempts = null, [CallerMemberName] string? test = null) =>
        AssessLiveWholeLoopAsync(provider, driveOnce, attempts ?? WholeLoopAttempts(), Environment.GetEnvironmentVariable(StepSummaryEnvVar), test: test);

    /// <summary>Testable core of the strict whole-loop gate — takes the attempt budget + step-summary path explicitly so a test pins the best-of-N / infra / gate logic with NO live call and without mutating process env. Any Drove → pass; a CodeFault → gate at once (never retried); a gateway-infra failure → non-gating skip that does not consume a slot; only when all <paramref name="attempts"/> non-infra attempts park short → gate (CapabilityMiss).</summary>
    internal static async Task AssessLiveWholeLoopAsync(string provider, Func<Task<(RealModelOutcome Outcome, string Note)>> driveOnce, int attempts, string? stepSummaryPath, TimeSpan? attemptDeadline = null, [CallerMemberName] string? test = null)
    {
        var budget = Math.Max(1, attempts);   // defend the core: a non-positive budget would otherwise gate on ZERO misses (the public entrypoint already clamps via WholeLoopAttempts, but this core is callable directly)
        var missNotes = new List<string>();   // accumulate each park-short verdict so the gate message names WHY (rounds vs schema), visible in the CI console log — not just the job summary
        var maxAttempts = budget + InfraRetryBudget;
        var deadline = attemptDeadline ?? WholeLoopAttemptDeadline();   // bound each attempt so a hung agent/gateway call REDs fast, never rides to the CI job's wall-clock cap
        string? infraSkip = null;   // the last non-gating infra reason — the SKIP the trx must record if infra eats the budget

        ArmModelSink();

        for (var i = 0; i < maxAttempts && missNotes.Count < budget; i++)
        {
            using var attemptCts = new CancellationTokenSource(deadline);

            try
            {
                var (outcome, note) = await driveOnce().WaitAsync(attemptCts.Token).ConfigureAwait(false);

                ReportThreeWay(outcome, note, stepSummaryPath, test);

                if (outcome == RealModelOutcome.Drove) return;   // any Drove among N → PASS (real model drove to completion)

                if (outcome == RealModelOutcome.CodeFault)        // a code regression reds at once — never retried, not capability variance
                {
                    if (IsRequired(provider))
                        false.ShouldBeTrue($"REQUIRED wire — the engine FAULTED driving the live brain's decisions (a CODE regression): {note} {ModelStamp()}");

                    return;
                }

                missNotes.Add(note);   // CapabilityMiss → best-of-N retry on a fresh run
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
            {
                // OUR per-attempt DEADLINE fired — the attempt did not converge in time (a hung agent / gateway call
                // that never returned). Treat it as a non-converging MISS, NOT a gateway-infra skip: a bounded hang is a
                // real "did not drive to completion" signal, so it accrues toward the budget and a PERSISTENT hang REDs
                // fast (~budget×deadline), well under the CI job cap — instead of one stuck test silently killing the job.
                var note = $"did not converge within {deadline.TotalMinutes:0}m — likely a hung agent or gateway call (per-attempt deadline)";
                ReportThreeWay(RealModelOutcome.CapabilityMiss, note, stepSummaryPath, test);
                missNotes.Add(note);
            }
            catch (Exception ex) when (IsGatewayInfraFailure(ex))
            {
                infraSkip = ReportInfraSkip(provider, ex, stepSummaryPath);   // non-gating infra — does NOT consume a capability slot
            }
        }

        if (missNotes.Count >= budget && IsRequired(provider))
            false.ShouldBeTrue($"REQUIRED wire — the live model did NOT drive the arc to the accept head in {budget} attempt(s) (a CapabilityMiss, NOT a gateway-infra fault). The real-model-drove-to-completion gate requires a Drove; a skip is reported separately and is never a pass. Per-attempt verdict: {string.Join(" || ", missNotes)} {ModelStamp()}");

        // misses < attempts only because gateway-infra exhausted the bounded attempt budget → non-gating infra skip.
        // Still non-gating, but recorded as a SKIP so the trx cannot pass off an unmeasured lane as a green one.
        //
        // Same rule as the boolean best-of-N above: only when NOTHING was measured. A run that produced a real
        // CapabilityMiss before infra ate the rest of the budget did measure something, and reporting that as
        // NotExecuted would discard it.
        if (missNotes.Count == 0 && infraSkip != null) throw new SkipException(infraSkip);
    }

    /// <summary>Surface a no-credentials / unavailable-binary SKIP LOUDLY as explicitly NOT-A-PASS — so the ONLY honest green-skip (a fork/local run with no live model) is legible in the job summary and can never be mistaken for a real-model pass. Pure given <paramref name="stepSummaryPath"/>.</summary>
    public static SkipException ReportSkipped(string provider, string reason) =>
        ReportSkipped(provider, reason, Environment.GetEnvironmentVariable(StepSummaryEnvVar));

    /// <summary>
    /// Testable core of <see cref="ReportSkipped(string, string)"/> — explicit step-summary path. Writes a
    /// 'NOT EVALUATED … skip ≠ pass' line so an honest skip is visible, and RETURNS the <see cref="SkipException"/> the
    /// caller throws so the trx records NotExecuted rather than the Passed that made an unmeasured lane read as a green
    /// one. Returned rather than thrown so the call site reads <c>throw RealModelGate.ReportSkipped(…)</c> — the guard
    /// stays an obvious exit, and the compiler still sees the method as flow-terminating.
    /// </summary>
    internal static SkipException ReportSkipped(string provider, string reason, string? stepSummaryPath)
    {
        var line = $"⏭️ real-model whole-loop NOT EVALUATED — {provider} skipped ({reason}). A skip is NOT a pass: no live model ran, so nothing was driven to completion. {ModelStamp()}";

        if (!string.IsNullOrWhiteSpace(stepSummaryPath))
            File.AppendAllText(stepSummaryPath, line + Environment.NewLine);

        Console.WriteLine(line);   // ALSO the console: a skip that only ever reached the step summary was invisible in the job log

        return new SkipException(line);
    }

    /// <summary>Surface a three-way whole-loop outcome (ALWAYS — a CapabilityMiss must never read as a silent green) to the step-summary FILE when present (capture-immune → the job-summary UI), else the console. Names the TEST it came from: a whole-loop job runs a dozen arms into ONE step summary, and an unattributed "CAPABILITY MISS" line cannot be traced back to the arm that produced it. Pure given <paramref name="stepSummaryPath"/>.</summary>
    internal static void ReportThreeWay(RealModelOutcome outcome, string note, string? stepSummaryPath, [CallerMemberName] string? test = null)
    {
        var (icon, label) = outcome switch
        {
            RealModelOutcome.Drove => ("✅", "DROVE the whole loop"),
            RealModelOutcome.CapabilityMiss => ("ℹ️", "CAPABILITY MISS — the model did not drive the arc (REPORTED, NOT gating)"),
            _ => ("⚠️", "CODE FAULT — the engine faulted on the live brain's decisions (gates the blessed wire)"),
        };
        var line = $"{icon} real-model whole-loop [{test ?? "(unnamed)"}]: {label} — {note} {ModelStamp()}";

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
    /// Whether the arm still had CONTROL of what its agents ran — the precondition BOTH
    /// <see cref="ClassifyAgentExecution"/> and <see cref="IsCaptureInfraFault"/> silently assume and neither checks.
    /// Their premises ("a deterministic exit-0 fake cannot CHOOSE to fail"; "the fake ALWAYS writes a file on
    /// success") are statements about THE FAKE. A fake arms a harness's command env var, but which harness an agent
    /// runs on is chosen by production code the test cannot pin: the brain authors <c>agents[].harness</c>/
    /// <c>agents[].model</c> per dispatch, and <c>HarnessModelReconciler</c> reconciles again against the team pool's
    /// provider. When an agent lands on a harness this fake did NOT arm, it ran a REAL CLI — so both premises are void
    /// and every downstream verdict is about something the arm never controlled.
    ///
    /// <para>ANY off-stub run loses control, not just an all-off-stub fan-out. The harness is rewritten PER DISPATCH,
    /// so heterogeneous fan-outs are the normal case, and an all-or-nothing predicate is disarmed by a single
    /// surviving stubbed run — leaving e.g. <c>[codex Failed, claude Failed, claude Failed]</c> to launder into the
    /// infra refund exactly as before. Requiring EVERY run to be on a stubbed harness is what makes the premise true
    /// at the point it is relied on, which is why the caller must run this BEFORE the two classifiers: past this
    /// check, every remaining run is a fake run and their reasoning is sound again.</para>
    ///
    /// <para>Opt out with an EMPTY <paramref name="stubbedKinds"/> — the real-coding arm legitimately expects the real
    /// claude binary. Zero agents is not a control loss but a plan-only park, which is a genuine model miss and must
    /// keep gating as one (mirroring <see cref="ClassifyAgentExecution"/>'s own zero-agents rule).</para>
    /// </summary>
    public static (bool LostControl, string Census) ClassifyHarnessControl(IReadOnlyList<string> harnesses, IReadOnlyList<string> stubbedKinds)
    {
        var census = harnesses.Count == 0
            ? "agents=0"
            : string.Join(", ", harnesses.GroupBy(h => h).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}"));

        if (stubbedKinds.Count == 0 || harnesses.Count == 0) return (false, census);

        var offStub = harnesses.Where(h => !stubbedKinds.Contains(h, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return (offStub.Count > 0, census);
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

    /// <summary>
    /// Whether a supervisor run's TERMINAL STOP payload is the model-plane park's own honest ending — the forced stop
    /// the node writes (<c>SupervisorStopReasons.ModelPlaneUnavailable</c>) once a brain-call outage has outlived the
    /// whole 24h park window. It is a clean <c>stop</c> that reaches a Success walk, so every whole-loop evaluator
    /// scores it a CapabilityMiss — a red for a run whose model was never able to answer at all. Reading the reason is
    /// the ONE thing that tells the two apart, and only the ENGINE can write it: no model-authored stop carries a
    /// <c>reason</c> field (the projector emits <c>outcome</c> + <c>summary</c>), so this can never launder a real miss.
    ///
    /// <para>Deliberately narrow (Rule 7): it answers about the STOP only. An attempt that DID reach a conformant model
    /// turn before the plane went down is still scored as it is today — the caller ANDs this with its own "nothing was
    /// measured" fact rather than this helper guessing at one.</para>
    /// </summary>
    public static bool IsModelPlaneUnavailableStop(string? stopPayloadJson)
    {
        if (string.IsNullOrEmpty(stopPayloadJson)) return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(stopPayloadJson);

            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("reason", out var reason)
                && reason.ValueKind == System.Text.Json.JsonValueKind.String
                && reason.GetString() == CodeSpace.Core.Services.Supervisor.SupervisorStopReasons.ModelPlaneUnavailable;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>
    /// Whether a supervisor run's WHOLE decision tape is nothing but the model-plane park's honest ending — the single
    /// forced stop above and no model turn at all. This is the "nothing was measured" fact
    /// <see cref="IsModelPlaneUnavailableStop"/> deliberately refuses to guess at, ANDed with it: an attempt whose model
    /// DID decide before the plane went down has something to score and keeps today's scoring, so only a tape of
    /// EXACTLY one decision — that stop — routes to the non-gating infra skip.
    ///
    /// <para>DEFENCE IN DEPTH, not the live lane's normal exit. A whole-loop arm rides its parks through
    /// <see cref="InfraParkRide"/>, whose budget (<see cref="InfraParkRide.MaxWakes"/> wakes × <see cref="InfraParkRide.WakePause"/>
    /// ≈ 40s) gives up long before the engine's own 24h window can exhaust — an outage that outlives the ride surfaces
    /// as <c>InfraParkUnresolvedException</c> instead. This covers the run that reaches the forced stop by another
    /// route: a resume fired outside a ride (the stranded-wait reconciler), a rerun of a parked run, or a future arm
    /// whose ride budget is raised. Cheap to hold, and the alternative is a red that blames the model for an outage.</para>
    /// </summary>
    public static bool IsWholeWindowModelPlaneOutage(IReadOnlyList<string> decisionPayloadsInOrder) =>
        decisionPayloadsInOrder.Count == 1 && IsModelPlaneUnavailableStop(decisionPayloadsInOrder[0]);

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
        // InfraParkRide spent its WHOLE budget with the run still parked on the engine's own model-plane park — the
        // plane never came back. Same routing as a gateway timeout, because it IS one: the run behaved exactly as
        // designed (park, don't die), so the shortfall is the owner's gateway, never a model CapabilityMiss and never
        // a code fault. Does not consume a best-of-N capability slot.
        InfraParkUnresolvedException => true,
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
    internal static string ReportInfraSkip(string provider, Exception ex, string? stepSummaryPath)
    {
        var line = $"⚠️ real-model gate NON-GATING infra skip — {provider} (infra fault, NOT a decision verdict): {InfraReason(ex)} {ModelStamp()}";

        if (!string.IsNullOrWhiteSpace(stepSummaryPath))
            File.AppendAllText(stepSummaryPath, line + Environment.NewLine);

        Console.WriteLine(line);   // ALSO the console: under CI the summary file swallowed this entirely, so the job log showed a clean green

        return line;
    }

    /// <summary>The innermost transient-transport reason (type + message) for a legible infra-skip line.</summary>
    private static string InfraReason(Exception ex) =>
        Unwrap(ex).Where(IsTransientTransport).Select(e => $"{e.GetType().Name}: {e.Message}").FirstOrDefault() ?? ex.GetType().Name;

    /// <summary>
    /// Surface an INFORMATIONAL wire's verdict (pass OR fail, so silence never reads as "it ran clean") where it is
    /// actually visible: the GitHub step-summary FILE when present (capture-immune → reaches the job-summary UI), else
    /// the console for a local run. A FAILING verdict ALSO writes a grep-able console line
    /// (<c>[realmodel] INFORMATIONAL-FAIL …</c>) — under CI the summary-only branch made an informational fault
    /// completely silent in the job log while the trx recorded a one-second Passed. Pure given
    /// <paramref name="stepSummaryPath"/> → pinnable without mutating process env.
    /// </summary>
    internal static void ReportInformational(string provider, bool ok, string verdict, string? stepSummaryPath, string? test = null, bool informational = true)
    {
        var label = informational
            ? "INFORMATIONAL wire (reported, NOT gating CI)"
            : "best-of-N ATTEMPT on the BLESSED wire (a later attempt can still pass; only an all-attempt failure gates)";
        var line = $"{(ok ? "✅" : "⚠️")} real-model {label} — {verdict} {ModelStamp()}";

        if (!string.IsNullOrWhiteSpace(stepSummaryPath))
            File.AppendAllText(stepSummaryPath, line + Environment.NewLine);
        else
            Console.WriteLine(line);

        // The two failures mean OPPOSITE things to a reader, so they must not share a tag: INFORMATIONAL-FAIL is
        // "ignore this, the wire never gates", ATTEMPT-FAIL is "attempt 1 of N on the wire that DOES gate". Job
        // 100548613534 printed the former for a blessed Anthropic attempt, which reads as a non-event.
        if (!ok) Console.WriteLine($"[realmodel] {(informational ? "INFORMATIONAL-FAIL" : "ATTEMPT-FAIL")} {test ?? "(unnamed)"} wire={provider} reason={verdict} {ModelStamp()}");
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
