namespace CodeSpace.Core.Settings;

/// <summary>
/// Graceful-shutdown drain budget — how long the host waits on SIGTERM (rolling update / scale-down)
/// for in-flight background work before the process exits. Hangfire's server is an IHostedService that,
/// within this window, stops fetching new jobs and lets running ones finish. Short jobs (workflow
/// nodes, agent dispatch, webhooks) drain cleanly inside it; a long agent run that exceeds it is killed
/// and recovered by the reconciler, NOT drained — you can't drain a multi-minute run on every deploy
/// (that's what a decoupled out-of-process runner is for).
///
/// <para>Configured as <c>Shutdown:DrainSeconds</c>. The deployment's grace period MUST be at least this,
/// or the orchestrator SIGKILLs the process before it drains (k8s: <c>terminationGracePeriodSeconds</c>);
/// the default matches k8s's own default for exactly that reason.</para>
/// </summary>
public static class ShutdownSettings
{
    /// <summary>The host's <c>HostOptions.ShutdownTimeout</c>, from the bound <see cref="RuntimeSettings"/>.</summary>
    public static TimeSpan ResolveDrainTimeout() => TimeSpan.FromSeconds(RuntimeSettings.Current.ShutdownDrainSeconds);
}
