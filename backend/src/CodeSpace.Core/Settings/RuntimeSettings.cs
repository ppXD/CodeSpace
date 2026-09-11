using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings;

/// <summary>
/// The handful of settings that genuinely vary per DEPLOYMENT and are read from call sites too deep to inject into:
/// the sandbox isolation layer, the durable process runner, the artifact backend, the host's shutdown budget. Every
/// one of them used to be a bespoke <c>Environment.GetEnvironmentVariable</c> read at the point of use, so they were
/// invisible in appsettings, undocumented outside their own doc-comment, and impossible to source from a ConfigMap
/// the way every other setting is.
///
/// <para>They come from <see cref="IConfiguration"/> now — appsettings, a ConfigMap, or the standard
/// <c>Section__Key</c> environment form, all through the one pipeline. What is unusual here is only the ACCESS: the
/// values are bound ONCE at startup into <see cref="Current"/> rather than injected, because their consumers are
/// static members (<c>BubblewrapSandbox.IsRequired</c>, <c>LocalProcessRunner.SpoolRoot</c>) reached from code paths
/// that have no container. Threading a container through the isolation layer to deliver six values would be a far
/// larger change than the problem warrants; a normal <see cref="IConfigurationSetting{T}"/> stays the right shape for
/// everything that IS injectable, and nothing should move here that could be injected instead.</para>
///
/// <para>Bound in <c>Program.Main</c> (before DbUp, which runs pre-host) and again in <c>CodeSpaceModule</c> (which
/// every host path including the test host loads). Binding is idempotent, so doing both is deliberate: neither entry
/// point alone covers every way this assembly starts.</para>
/// </summary>
public sealed record RuntimeSettings
{
    /// <summary>
    /// Whether this deployment MANDATES sandbox confinement. On, an agent run refuses to start rather than run
    /// unconfined when bubblewrap or unprivileged user namespaces are unavailable. Off is the default because a host
    /// that cannot confine — macOS development, a container without userns — would otherwise fail every run.
    /// </summary>
    public bool RequireSandboxConfinement { get; init; }

    /// <summary>The DELEGATED cgroup-v2 root the durable launch creates its per-run leaves under. Null (the default) ⇒ no resource cap is applied; an operator opts in by delegating a subtree and naming it here.</summary>
    public string? AgentCgroupRoot { get; init; }

    /// <summary>
    /// The operator's PER-RUN memory budget in MiB — this host's answer to "how much may one agent run take". It can
    /// only NARROW the autonomy tier's committed ceiling (<c>AgentAutonomyPolicy.Ceilings</c>), never raise it, and a
    /// configured value must be a positive integer; malformed values fail startup rather than silently restoring a
    /// broader ceiling. Null (the default) ⇒ every tier keeps its committed row. There is
    /// deliberately no cpu twin: a cpu-quota overrun throttles rather than kills, so overcommitting cpu degrades a
    /// host instead of taking the worker down.
    /// </summary>
    public int? AgentMemoryCeilingMb { get; init; }

    /// <summary>
    /// This deployment's OWN autonomy ceiling — the tier name (<c>Confined</c> / <c>Standard</c> / <c>Trusted</c> /
    /// <c>Unleashed</c>) no run on this host may exceed, however it is launched. It is the operator's answer to the
    /// three paths the per-route ceiling never meets: a team Member enabling network per run, an API client posting
    /// <c>autonomy: "Trusted"</c> straight at the launch command, and an authored / replayed <c>agent.run</c> node
    /// that carries its own tier and its own raw <c>network</c> override with no route at all.
    ///
    /// <para>TIGHTEN-ONLY, like every other ceiling here: it can only LOWER what a route or a node already allowed,
    /// never raise it. An absent value preserves <c>AgentAutonomyPolicy.DefaultDeploymentCeiling</c>. Blank,
    /// unrecognised, numeric or combined tier names fail startup: a typo must not restore a broader ceiling.</para>
    ///
    /// <para>Read through the key <see cref="MaxAutonomyKey"/>, whose literal value is pinned by a unit test: an
    /// operator who lowers this ceiling does it by committing a value here, and a rename that looked harmless would
    /// silently restore the top tier on every deployment that had pinned the old name.</para>
    /// </summary>
    public string? MaxAutonomy { get; init; }

    /// <summary>The configuration key <see cref="MaxAutonomy"/> is read from. Pinned by a unit test (Rule 8) — see <see cref="MaxAutonomy"/> for why a rename is not a harmless refactor.</summary>
    public const string MaxAutonomyKey = "Sandbox:MaxAutonomy";

    public const string RequireConfinementKey = "Sandbox:RequireConfinement";
    public const string AgentMemoryCeilingMbKey = "Sandbox:AgentMemoryCeilingMb";

    /// <summary>Root directory for agent-run spool files (stdout/stderr capture, pid files). Null ⇒ a path under the system temp dir, which is fine for development but is NOT durable across a pod restart — a deployment that wants re-attach to survive one points this at a volume.</summary>
    public string? AgentRunSpoolDirectory { get; init; }

    /// <summary>Root directory for offloaded artifact bytes. Null ⇒ a path under the system temp dir. Same durability caveat as the spool: point it at a persistent volume in any deployment whose artifacts must outlive the pod.</summary>
    public string? ArtifactStoreDirectory { get; init; }

    /// <summary>
    /// Explicit deployment qualification that the local-rwx artifact root is one shared namespace visible to every API
    /// and worker instance that may use it. False by default: a durable-looking or existing path does not prove two
    /// hosts see the same bytes, so it cannot authorize an automatically active team route.
    /// </summary>
    public bool ArtifactLocalRwxShared { get; init; }

    /// <summary>
    /// Graceful-shutdown drain budget in seconds — how long the host waits on SIGTERM for in-flight background work
    /// before exiting. The orchestrator's own grace period MUST be at least this (k8s
    /// <c>terminationGracePeriodSeconds</c>), or the process is SIGKILLed before it can drain. The default matches
    /// k8s's own default for exactly that reason.
    /// </summary>
    public int ShutdownDrainSeconds { get; init; } = DefaultShutdownDrainSeconds;

    /// <summary>
    /// Base64-encoded 32-byte AES-256 master key for the variable subsystem. A SECRET: it belongs in a k8s Secret or
    /// the equivalent, never in appsettings, and it is read here only so it arrives through the SAME configuration
    /// pipeline as everything else instead of a bespoke environment read. Null outside Development is fatal.
    /// </summary>
    public string? VariableMasterKey { get; init; }

    /// <summary>Operator-global Anthropic key — the single-tenant LAST RESORT when no team credential matches. A SECRET. Null (the strict posture) means every team must configure its own credential.</summary>
    public string? AnthropicOperatorApiKey { get; init; }

    /// <summary>Operator-global OpenAI key — same single-tenant last-resort role as <see cref="AnthropicOperatorApiKey"/>. A SECRET.</summary>
    public string? OpenAIOperatorApiKey { get; init; }

    /// <summary>EXTRA https hosts a skill/agent pack may be cloned from, comma-separated, ADDED to the built-in github.com / gitlab.com — a self-hosted GitLab or an enterprise GitHub. Anything not on the resulting list is refused, which is what keeps pack import from becoming an SSRF surface.</summary>
    public string? PackAllowedHosts { get; init; }

    /// <summary>
    /// This deployment's FALLBACK cost cap in USD, over the rolling window a team cap uses — the ceiling applied to
    /// any team that has no <c>budget_team_cap</c> row of its own. It is the operator's answer to "a team nobody has
    /// configured must still not be able to spend without limit", which before P15-5b-ii nothing anywhere provided:
    /// the ledger's only sum was per RUN, so any number of individually modest runs added up to no ceiling at all.
    ///
    /// <para>A team's OWN row wins outright rather than being clamped by this — an operator who raises one team
    /// above the deployment floor has said so explicitly, and silently clamping it would make the management
    /// endpoint a lie. Null (the default) ⇒ a team with no row keeps the per-run ceiling only, so an unset value
    /// leaves behaviour exactly as it was.</para>
    ///
    /// <para>Read through the key <see cref="DeploymentCostCapUsdKey"/>, whose literal value is pinned by a unit
    /// test (Rule 8). A configured-but-unparseable or non-positive value FAILS STARTUP: for a ceiling, landing on
    /// the default silently means no ceiling, and a typo must never be the thing that lifts one.</para>
    /// </summary>
    public decimal? DeploymentCostCapUsd { get; init; }

    /// <summary>The configuration key <see cref="DeploymentCostCapUsd"/> is read from. Pinned by a unit test — see <see cref="DeploymentCostCapUsd"/> for why a rename is not a harmless refactor.</summary>
    public const string DeploymentCostCapUsdKey = "Budget:DeploymentCostCapUsd";

    public const int DefaultShutdownDrainSeconds = 30;

    private static readonly AsyncLocal<RuntimeSettings?> ScopedOverride = new();
    private static RuntimeSettings _bound = new();

    /// <summary>The bound settings, or the current execution context's test override. Reads before <see cref="Bind"/> get the defaults, which are the same values the pre-configuration code fell back to.</summary>
    public static RuntimeSettings Current => ScopedOverride.Value ?? _bound;

    /// <summary>Validate and bind the effective configuration before startup. Both Program.Main and CodeSpaceModule call this; invalid security settings never replace a previously bound configuration.</summary>
    public static void Bind(IConfiguration configuration) => _bound = Read(configuration);

    /// <summary>Pure read (no static mutation) so the mapping from configuration keys to values is unit-testable directly.</summary>
    public static RuntimeSettings Read(IConfiguration configuration) => new()
    {
        RequireSandboxConfinement = SandboxConfiguration.ParseRequireConfinement(configuration[RequireConfinementKey]),
        AgentCgroupRoot = Trimmed(configuration["Sandbox:CgroupRoot"]),
        AgentMemoryCeilingMb = SandboxConfiguration.ParseMemoryCeilingMb(configuration[AgentMemoryCeilingMbKey]),
        MaxAutonomy = SandboxConfiguration.ParseMaxAutonomy(configuration[MaxAutonomyKey])?.ToString(),
        AgentRunSpoolDirectory = Trimmed(configuration["Agents:RunSpoolDirectory"]),
        ArtifactStoreDirectory = Trimmed(configuration["Artifacts:StoreDirectory"]),
        ArtifactLocalRwxShared = configuration.GetValue("Artifacts:LocalRwxShared", false),
        ShutdownDrainSeconds = Positive(configuration["Shutdown:DrainSeconds"], DefaultShutdownDrainSeconds),
        PackAllowedHosts = Trimmed(configuration["Agents:PackAllowedHosts"]),
        DeploymentCostCapUsd = PositiveUsd(configuration[DeploymentCostCapUsdKey], DeploymentCostCapUsdKey),
        // Secrets. The LEGACY flat keys are still honoured, and that is load-bearing rather than tidy: every
        // deployment that exists today sets CODESPACE_VARIABLE_MASTER_KEY, and a rename that quietly stopped reading
        // it would fail those pods closed at startup with a message about a key they had in fact set.
        VariableMasterKey = FirstSet(configuration, "Variables:MasterKey", "CODESPACE_VARIABLE_MASTER_KEY", "CODESPACE_TEAM_SECRET_MASTER_KEY"),
        AnthropicOperatorApiKey = FirstSet(configuration, "ModelCredentials:OperatorKeys:Anthropic", "CODESPACE_ANTHROPIC_API_KEY"),
        OpenAIOperatorApiKey = FirstSet(configuration, "ModelCredentials:OperatorKeys:OpenAI", "CODESPACE_OPENAI_API_KEY"),
    };

    /// <summary>The first key with a non-blank value, in preference order — the canonical section key first, then any legacy flat name a deployed pod may still be setting.</summary>
    private static string? FirstSet(IConfiguration configuration, params string[] keys) =>
        keys.Select(k => Trimmed(configuration[k])).FirstOrDefault(v => v is not null);

    /// <summary>Swap the bound settings for the duration of a test, restoring the previous value on dispose. Internal — production binds once at startup and never mutates.</summary>
    internal static IDisposable Override(RuntimeSettings settings) => new Scope(settings);

    /// <summary>Swap ONE value for the duration of a test — <c>Override(s =&gt; s with { AgentRunSpoolDirectory = dir })</c> — so a test states the single thing it varies and inherits the rest.</summary>
    internal static IDisposable Override(Func<RuntimeSettings, RuntimeSettings> mutate) => new Scope(mutate(Current));

    /// <summary>A blank configured value means "not set", not "set to empty" — an operator clearing a ConfigMap entry must land on the default, not on an empty path.</summary>
    private static string? Trimmed(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>
    /// Read as a raw string and parsed here rather than through <c>GetValue&lt;int&gt;</c>, which THROWS on an
    /// unparseable value — a typo in a ConfigMap would take the process down at boot. Zero or negative would mean
    /// "kill in-flight work immediately", which nobody configures on purpose, so both land on the default too.
    /// </summary>
    private static int Positive(string? raw, int fallback) => int.TryParse(raw, out var value) && value > 0 ? value : fallback;

    /// <summary>
    /// A configured USD amount, or null when the setting is absent or blank. Unlike <see cref="Positive"/> this
    /// REFUSES to fall back on a malformed value: the fallback for a spending ceiling is "no ceiling", so a typo
    /// that landed on the default would quietly remove the limit the operator was configuring.
    /// </summary>
    private static decimal? PositiveUsd(string? raw, string key)
    {
        if (Trimmed(raw) is not { } value) return null;
        // Float, not Number: Number allows a thousands separator, so a European operator's "1,5" (meaning 1.5)
        // would silently parse as 15 — a 10x cost cap error with no failure to catch it.
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount > 0m) return amount;

        throw new InvalidOperationException($"Refusing to start: '{key}' must be a positive amount in USD when configured. Omit the setting for no deployment cost cap.");
    }

    private sealed class Scope : IDisposable
    {
        private readonly RuntimeSettings? _previous = ScopedOverride.Value;

        public Scope(RuntimeSettings settings) => ScopedOverride.Value = settings;

        public void Dispose() => ScopedOverride.Value = _previous;
    }
}
