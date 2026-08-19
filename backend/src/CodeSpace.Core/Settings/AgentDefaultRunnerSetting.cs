using CodeSpace.Core.Services.Agents.Sandbox;
using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings;

/// <summary>
/// The sandbox runner kind an agent run / sandbox command falls back to when the request pins none — read from the
/// <c>Agents:DefaultRunnerKind</c> configuration key, so a deployment sets it through appsettings, a k8s ConfigMap,
/// or the standard <c>Agents__DefaultRunnerKind</c> environment variable.
///
/// <para>Defaults to <see cref="SandboxKinds.Local"/>, which is what both fallback sites hard-coded before this key
/// existed: a deployment that does not set it behaves exactly as it did. The escape hatch is for the fork or
/// air-gapped operator who registers their own <see cref="ISandboxRunner"/> (a docker / k8s / remote backend) and
/// wants it to be the default without editing every call site. Naming a kind that no runner is registered for makes
/// <see cref="ISandboxRunnerRegistry.Resolve"/> throw at dispatch — loud, per run, and never a silent fallback to
/// local, because running an agent somewhere the operator did not choose is the worse failure.</para>
///
/// <para>Consumed by <c>AgentRunExecutor</c> and <c>RunCommandService</c> only — the two paths whose request carries
/// an optional runner kind. The host-side git / grading paths (<c>SupervisorAcceptanceGrader</c>,
/// <c>BenchmarkRunner</c>, <c>RemoteTipResolver</c>, <c>PackCloneFetcher</c>) stay pinned to
/// <see cref="SandboxKinds.Local"/>: they never read a caller-supplied kind, and they run on the worker host itself
/// rather than in the agent's sandbox.</para>
/// </summary>
public class AgentDefaultRunnerSetting : IConfigurationSetting<string>
{
    /// <summary>The configuration key. Pinned by test (Rule 8) — an operator who selected an alternative runner through this key would silently revert to local on a rename.</summary>
    public const string ConfigurationKey = "Agents:DefaultRunnerKind";

    public AgentDefaultRunnerSetting(IConfiguration configuration)
    {
        Value = Resolve(configuration[ConfigurationKey]);
    }

    public string Value { get; set; }

    /// <summary>Pure resolution (no configuration read) so the default + the trim are unit-pinned directly. Absent or blank resolves to <see cref="SandboxKinds.Local"/> — an operator clearing a ConfigMap entry must land on the default, not on an empty kind no registry resolves.</summary>
    public static string Resolve(string? raw) => string.IsNullOrWhiteSpace(raw) ? SandboxKinds.Local : raw.Trim();
}
