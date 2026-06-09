namespace CodeSpace.Core.Settings;

/// <summary>
/// Graceful-shutdown drain budget — how long the host waits on SIGTERM (rolling update / scale-down)
/// for in-flight background work before the process exits. Hangfire's server is an IHostedService that,
/// within this window, stops fetching new jobs and lets running ones finish. Short jobs (workflow
/// nodes, agent dispatch, webhooks) drain cleanly inside it; a long agent run that exceeds it is killed
/// and recovered by the reconciler, NOT drained — you can't drain a multi-minute run on every deploy
/// (that's what a decoupled out-of-process runner is for).
///
/// <para>Operator-tunable via env (Rule 8). The deployment's grace period MUST be ≥ this, or the
/// orchestrator SIGKILLs the process before it drains (k8s: <c>terminationGracePeriodSeconds</c> ≥
/// <see cref="DrainSecondsEnvVar"/>; the 30s default matches k8s's own default).</para>
/// </summary>
public static class ShutdownSettings
{
    public const string DrainSecondsEnvVar = "CODESPACE_SHUTDOWN_DRAIN_SECONDS";

    public const int DefaultDrainSeconds = 30;

    /// <summary>The host's <c>HostOptions.ShutdownTimeout</c> — a positive-integer-seconds env override, or the default.</summary>
    public static TimeSpan ResolveDrainTimeout()
    {
        var raw = System.Environment.GetEnvironmentVariable(DrainSecondsEnvVar);

        return int.TryParse(raw, out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(DefaultDrainSeconds);
    }
}
