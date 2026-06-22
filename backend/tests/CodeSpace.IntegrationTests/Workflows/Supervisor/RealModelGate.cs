using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The real-model gate's per-wire policy: which provider wires are REQUIRED (blessed — a bad verdict FAILS the job)
/// versus INFORMATIONAL (still driven against the live model and their verdict reported, but never gating CI). This
/// lets a stronger wire be the kill-gate while a weaker model on another protocol surfaces its verdict without
/// blocking main — an honest split, not a silenced one. Default blessed set: Anthropic only. An operator widens or
/// changes it via the env var (comma-separated provider names) with no code change.
/// </summary>
internal static class RealModelGate
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
