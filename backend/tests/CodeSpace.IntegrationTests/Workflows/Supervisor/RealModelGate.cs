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

    private static readonly string[] DefaultRequiredProviders = { "Anthropic" };

    /// <summary>Apply the gate to ONE wire's verdict: a REQUIRED wire asserts (a bad verdict fails the job); an INFORMATIONAL wire reports its verdict to the log and returns without gating.</summary>
    public static void Assess(string provider, bool ok, string verdict)
    {
        if (IsRequired(provider))
        {
            ok.ShouldBeTrue($"REQUIRED wire — {verdict}");
            return;
        }

        if (!ok) Console.WriteLine($"[real-model][INFORMATIONAL wire — reported, NOT gated] {verdict}");
    }

    /// <summary>Whether <paramref name="provider"/>'s verdict gates CI (it is in the blessed set).</summary>
    public static bool IsRequired(string provider) =>
        RequiredProviders().Contains(provider, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> RequiredProviders()
    {
        var raw = Environment.GetEnvironmentVariable(RequiredProvidersEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return DefaultRequiredProviders;

        var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parsed.Length == 0 ? DefaultRequiredProviders : parsed;
    }
}
