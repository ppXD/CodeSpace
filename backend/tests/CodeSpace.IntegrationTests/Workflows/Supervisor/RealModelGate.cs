using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

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
    public static Task AssessLiveAsync(string provider, Func<Task<(bool Ok, string Verdict)>> drive) =>
        AssessLiveAsync(provider, drive, Environment.GetEnvironmentVariable(StepSummaryEnvVar));

    /// <summary>Testable core of <see cref="AssessLiveAsync(string, Func{Task{ValueTuple{bool, string}}})"/> — takes the step-summary path explicitly so a test pins the non-gating behaviour without mutating process env.</summary>
    internal static async Task AssessLiveAsync(string provider, Func<Task<(bool Ok, string Verdict)>> drive, string? stepSummaryPath)
    {
        try
        {
            var (ok, verdict) = await drive().ConfigureAwait(false);
            Assess(provider, ok, verdict);
        }
        catch (Exception ex) when (IsGatewayInfraFailure(ex))
        {
            ReportInfraSkip(provider, ex, stepSummaryPath);
        }
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
