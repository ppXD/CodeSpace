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

    /// <summary>Root directory for agent-run spool files (stdout/stderr capture, pid files). Null ⇒ a path under the system temp dir, which is fine for development but is NOT durable across a pod restart — a deployment that wants re-attach to survive one points this at a volume.</summary>
    public string? AgentRunSpoolDirectory { get; init; }

    /// <summary>Root directory for offloaded artifact bytes. Null ⇒ a path under the system temp dir. Same durability caveat as the spool: point it at a persistent volume in any deployment whose artifacts must outlive the pod.</summary>
    public string? ArtifactStoreDirectory { get; init; }

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

    public const int DefaultShutdownDrainSeconds = 30;

    /// <summary>The bound settings. Reads before <see cref="Bind"/> (a unit test constructing a service directly) get the defaults, which are the same values the pre-configuration code fell back to.</summary>
    public static RuntimeSettings Current { get; private set; } = new();

    /// <summary>Bind from the application's configuration. Idempotent — called from both startup paths on purpose, since neither covers every way this assembly is hosted.</summary>
    public static void Bind(IConfiguration configuration) => Current = Read(configuration);

    /// <summary>Pure read (no static mutation) so the mapping from configuration keys to values is unit-testable directly.</summary>
    public static RuntimeSettings Read(IConfiguration configuration) => new()
    {
        RequireSandboxConfinement = configuration.GetValue("Sandbox:RequireConfinement", false),
        AgentCgroupRoot = Trimmed(configuration["Sandbox:CgroupRoot"]),
        AgentRunSpoolDirectory = Trimmed(configuration["Agents:RunSpoolDirectory"]),
        ArtifactStoreDirectory = Trimmed(configuration["Artifacts:StoreDirectory"]),
        ShutdownDrainSeconds = Positive(configuration["Shutdown:DrainSeconds"], DefaultShutdownDrainSeconds),
        PackAllowedHosts = Trimmed(configuration["Agents:PackAllowedHosts"]),
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

    private sealed class Scope : IDisposable
    {
        private readonly RuntimeSettings _previous = Current;

        public Scope(RuntimeSettings settings) => Current = settings;

        public void Dispose() => Current = _previous;
    }
}
