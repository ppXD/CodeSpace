using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
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

    private readonly IAgentRunService _runs;
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly ILogger<AgentRunExecutor> _logger;

    public AgentRunExecutor(IAgentRunService runs, IAgentHarnessRegistry harnesses, ISandboxRunnerRegistry runners, ILogger<AgentRunExecutor> logger)
    {
        _runs = runs;
        _harnesses = harnesses;
        _runners = runners;
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
            var runner = _runners.Resolve(string.IsNullOrWhiteSpace(task.RunnerKind) ? DefaultRunnerKind : task.RunnerKind);

            var result = await RunHarnessAsync(agentRunId, harness, runner, harness.BuildInvocation(task), cancellationToken).ConfigureAwait(false);

            await _runs.CompleteAsync(agentRunId, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Worker torn down (pod shutdown): leave the run Running for the reconciler / a re-claim — do NOT complete.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent run {RunId} failed during execution", agentRunId);
            await _runs.CompleteAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "executor-error", Error = ex.Message }, cancellationToken).ConfigureAwait(false);
        }
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

        var sandbox = await RunAndStreamAsync(runner, spec, PersistLineAsync, cancellationToken).ConfigureAwait(false);

        return sandbox.Status == SandboxStatus.TimedOut
            ? new AgentRunResult { Status = AgentRunStatus.TimedOut, ExitReason = "timed-out", Error = "The agent run exceeded its time budget and was terminated." }
            : harness.BuildResult(events, sandbox.ExitCode);
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
