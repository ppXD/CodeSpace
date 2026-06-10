using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Runs one already-created (Queued) agent run to a terminal state: claims it, runs the harness in its
/// sandbox while streaming normalized events to the durable log, and lands the result. This is the
/// execution core a worker (the agent.code node's Hangfire job) invokes — substrate-neutral, driving
/// everything through the harness + runner contracts so any harness/runner combination behaves the same.
///
/// <para><b>Exactly-once:</b> the claim is a CAS (<see cref="IAgentRunService.MarkRunningAsync"/>); if the
/// run is already Running or terminal (a re-claimed Hangfire job after a crash, a duplicate dispatch),
/// the executor returns WITHOUT spawning the harness — so an agent never runs twice and tokens aren't
/// re-spent. A worker torn down mid-run (pod shutdown) leaves the run Running for the reconciler / a
/// re-claim; any other failure lands a clean Failed instead of a stuck Running.</para>
/// </summary>
public interface IAgentRunExecutor
{
    Task ExecuteAsync(Guid agentRunId, CancellationToken cancellationToken);
}

public sealed class AgentRunExecutor : IAgentRunExecutor, IScopedDependency
{
    /// <summary>Runner used when the task doesn't pin one. v0 = the in-process local runner.</summary>
    private const string DefaultRunnerKind = "local";

    /// <summary>Cap on the captured diff inlined into the persisted result row (~1 MB). A larger diff is truncated with a marker; the full diff belongs in the artifact layer (a later slice).</summary>
    private const int MaxPatchChars = 1_000_000;

    private readonly IAgentRunService _runs;
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IAgentWorkspaceResolver _workspaceResolver;
    private readonly IWorkspaceProviderRegistry _workspaces;
    private readonly IAgentRunCompletionNotifier _notifier;
    // Mints a fresh DI scope (→ its own DbContext) for the heartbeat loop, which runs concurrently with the event stream.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentRunExecutor> _logger;

    public AgentRunExecutor(IAgentRunService runs, IAgentHarnessRegistry harnesses, ISandboxRunnerRegistry runners, IAgentWorkspaceResolver workspaceResolver, IWorkspaceProviderRegistry workspaces, IAgentRunCompletionNotifier notifier, IServiceScopeFactory scopeFactory, ILogger<AgentRunExecutor> logger)
    {
        _runs = runs;
        _harnesses = harnesses;
        _runners = runners;
        _workspaceResolver = workspaceResolver;
        _workspaces = workspaces;
        _notifier = notifier;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(Guid agentRunId, CancellationToken cancellationToken)
    {
        var run = await _runs.GetAsync(agentRunId, cancellationToken).ConfigureAwait(false);

        if (!await TryClaimAsync(agentRunId, cancellationToken).ConfigureAwait(false)) return;

        try
        {
            var task = JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)
                       ?? throw new InvalidOperationException($"AgentRun {agentRunId} has an empty task envelope.");

            var harness = _harnesses.Resolve(task.Harness);
            var runnerKind = string.IsNullOrWhiteSpace(task.RunnerKind) ? DefaultRunnerKind : task.RunnerKind;
            var runner = _runners.Resolve(runnerKind);

            // Materialise the workspace (clone the bound repo) before the harness runs. Null = no workspace
            // for this run. The handle's lifetime is the run's — DisposeAsync removes the clone afterwards.
            var workspaceRequest = await _workspaceResolver.ResolveAsync(task, run.TeamId, cancellationToken).ConfigureAwait(false);
            await using var workspace = workspaceRequest is null ? null : await _workspaces.Resolve(runnerKind).PrepareAsync(workspaceRequest, cancellationToken).ConfigureAwait(false);

            var effectiveTask = workspace is null ? task : task with { WorkspaceDirectory = workspace.Directory };

            var result = await RunHarnessAsync(agentRunId, harness, runner, harness.BuildInvocation(effectiveTask), cancellationToken).ConfigureAwait(false);

            result = await EnrichWithWorkspaceChangesAsync(agentRunId, result, workspace, cancellationToken).ConfigureAwait(false);

            await CompleteAndNotifyAsync(agentRunId, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Worker torn down (pod shutdown): leave the run Running for the reconciler / a re-claim — do NOT complete.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent run {RunId} failed during execution", agentRunId);
            await CompleteAndNotifyAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "executor-error", Error = ex.Message }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Land the terminal result, then fire the completion notifier (which resumes the agent.code node parked on this run). The notifier is best-effort + swallows its own failures, so completion is never masked by a resume error.</summary>
    private async Task CompleteAndNotifyAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _runs.CompleteAsync(runId, result, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentRunTransitionException ex)
        {
            // The run is already terminal — the reconciler (or another worker) landed it first while this
            // executor was mid-flight. Don't re-complete or throw; still notify below so the parent
            // workflow resumes off whatever terminal state stuck.
            _logger.LogWarning(ex, "Agent run {RunId} was already terminal at completion (likely reconciled); skipping re-complete, still notifying", runId);
        }

        await _notifier.NotifyCompletedAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fold the workspace's git-diff ground truth into the result — the agent's actual changed files +
    /// unified patch, overriding the harness's event-parsed file list (git is authoritative, not the
    /// agent's self-report). No-op when the run had no workspace. Best-effort: a capture failure is logged
    /// and the result kept as-is, never flipping an otherwise-successful run to Failed over a git hiccup.
    /// </summary>
    private async Task<AgentRunResult> EnrichWithWorkspaceChangesAsync(Guid runId, AgentRunResult result, IWorkspaceHandle? workspace, CancellationToken cancellationToken)
    {
        if (workspace is null) return result;

        try
        {
            var changes = await workspace.CaptureChangesAsync(cancellationToken).ConfigureAwait(false);
            return result with { ChangedFiles = changes.ChangedFiles, Patch = TruncatePatch(changes.Patch, MaxPatchChars) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort + defence-in-depth: ANY capture failure (a wrapped WorkspaceException, or a raw
            // infra exception that slipped the provider) is logged and the result kept — a git hiccup must
            // never flip an otherwise-successful run to Failed. Cancellation still propagates (worker torn down).
            _logger.LogWarning(ex, "Agent run {RunId}: failed to capture workspace changes; keeping the harness-reported file list", runId);
            return result;
        }
    }

    /// <summary>
    /// Cap the inlined diff so a runaway / binary diff can't bloat the persisted run row (read on every
    /// resume). The full diff moving to the artifact/observability layer is a later slice. Internal + static
    /// so it's unit-pinned.
    /// </summary>
    internal static string TruncatePatch(string patch, int maxChars)
    {
        if (string.IsNullOrEmpty(patch) || patch.Length <= maxChars) return patch;

        return patch[..maxChars] + $"\n... diff truncated ({patch.Length} chars; capped at {maxChars}) ...\n";
    }

    /// <summary>Claim the run (Queued → Running). Returns false when it's already Running/terminal — the exactly-once guard that stops a re-claim from re-spawning the harness.</summary>
    private async Task<bool> TryClaimAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            await _runs.MarkRunningAsync(runId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AgentRunTransitionException)
        {
            _logger.LogInformation("Agent run {RunId} already claimed or terminal; skipping duplicate execution", runId);
            return false;
        }
    }

    private async Task<AgentRunResult> RunHarnessAsync(Guid runId, IAgentHarness harness, ISandboxRunner runner, SandboxSpec spec, CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();

        async Task PersistLineAsync(string line)
        {
            var normalized = harness.ParseEvent(line);
            if (normalized is null) return;

            await _runs.AppendEventAsync(runId, normalized, cancellationToken).ConfigureAwait(false);
            events.Add(normalized);
        }

        var sandbox = await RunWithHeartbeatAsync(runId, runner, spec, PersistLineAsync, cancellationToken).ConfigureAwait(false);

        return sandbox.Status == SandboxStatus.TimedOut
            ? new AgentRunResult { Status = AgentRunStatus.TimedOut, ExitReason = "timed-out", Error = "The agent run exceeded its time budget and was terminated." }
            : harness.BuildResult(events, sandbox.ExitCode);
    }

    /// <summary>
    /// Run the harness while a background <see cref="HeartbeatLoop"/> keeps the run's liveness fresh, so a
    /// long QUIET step (no event output) isn't mistaken for a crashed worker and abandoned by the reconciler.
    /// The heartbeat pings on a DEDICATED DI scope — its own DbContext — because it runs concurrently with the
    /// event stream (<paramref name="persistLine"/>) and a scoped DbContext is not thread-safe. The loop is
    /// cancelled and awaited the moment the harness returns (or the worker is torn down).
    /// </summary>
    private async Task<SandboxResult> RunWithHeartbeatAsync(Guid runId, ISandboxRunner runner, SandboxSpec spec, Func<string, Task> persistLine, CancellationToken cancellationToken)
    {
        using var heartbeatScope = _scopeFactory.CreateScope();
        var heartbeatRuns = heartbeatScope.ServiceProvider.GetRequiredService<IAgentRunService>();

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatLoop.RunAsync(
            ct => heartbeatRuns.HeartbeatAsync(runId, ct),
            AgentRunLiveness.HeartbeatInterval,
            ex => _logger.LogWarning(ex, "Heartbeat ping failed for agent run {RunId}; will retry next interval", runId),
            heartbeatCts.Token);

        try
        {
            return await RunAndStreamAsync(runner, spec, persistLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat.ConfigureAwait(false);
        }
    }

    /// <summary>Stream the harness live when the runner supports it (events land as emitted); otherwise run batch and replay captured stdout through the same per-line path.</summary>
    private static async Task<SandboxResult> RunAndStreamAsync(ISandboxRunner runner, SandboxSpec spec, Func<string, Task> persistLine, CancellationToken cancellationToken)
    {
        if (runner is ISandboxStreamRunner streamer)
            return await streamer.RunStreamingAsync(spec, (line, _) => persistLine(line), cancellationToken).ConfigureAwait(false);

        var result = await runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);

        foreach (var line in result.Stdout.Split('\n')) await persistLine(line).ConfigureAwait(false);

        return result;
    }
}
