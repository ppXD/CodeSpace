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

    private static readonly IReadOnlyDictionary<string, string> EmptySecretEnv = new Dictionary<string, string>();

    private readonly IAgentRunService _runs;
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IAgentWorkspaceResolver _workspaceResolver;
    private readonly IModelCredentialResolver _modelCredentials;
    private readonly IWorkspaceProviderRegistry _workspaces;
    private readonly IAgentRunCompletionNotifier _notifier;
    // Mints a fresh DI scope (→ its own DbContext) for the heartbeat loop, which runs concurrently with the event stream.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentRunExecutor> _logger;

    public AgentRunExecutor(IAgentRunService runs, IAgentHarnessRegistry harnesses, ISandboxRunnerRegistry runners, IAgentWorkspaceResolver workspaceResolver, IModelCredentialResolver modelCredentials, IWorkspaceProviderRegistry workspaces, IAgentRunCompletionNotifier notifier, IServiceScopeFactory scopeFactory, ILogger<AgentRunExecutor> logger)
    {
        _runs = runs;
        _harnesses = harnesses;
        _runners = runners;
        _workspaceResolver = workspaceResolver;
        _modelCredentials = modelCredentials;
        _workspaces = workspaces;
        _notifier = notifier;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(Guid agentRunId, CancellationToken cancellationToken)
    {
        var run = await _runs.GetAsync(agentRunId, cancellationToken).ConfigureAwait(false);

        if (!await TryClaimAsync(agentRunId, cancellationToken).ConfigureAwait(false)) return;

        // Holds the run's resolved secret(s) once the credential is resolved (below), so the catch-all can scrub
        // them from a failure message too. None until then — a pre-resolve failure has no secret to leak.
        var redactor = SecretRedactor.None;

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

            // Resolve + decrypt the model credential JUST-IN-TIME (team from the run row, never the envelope) and
            // project it onto the harness's env vars. The secret lives only in this in-memory effectiveTask →
            // SandboxSpec.Environment; it is NEVER re-persisted (CompleteAsync writes only the result). The
            // redactor (keyed on the decrypted key) strips it from any echoed event / error before it persists.
            var (secretEnv, secretRedactor) = await ResolveModelCredentialEnvAsync(task, run.TeamId, harness, cancellationToken).ConfigureAwait(false);
            redactor = secretRedactor;

            var effectiveTask = (workspace is null ? task : task with { WorkspaceDirectory = workspace.Directory }) with { Environment = MergeEnvironment(task.Environment, secretEnv) };

            var result = await RunHarnessAsync(agentRunId, harness, runner, harness.BuildInvocation(effectiveTask), redactor, cancellationToken).ConfigureAwait(false);

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
            await CompleteAndNotifyAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "executor-error", Error = redactor.Redact(ex.Message) }, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Resolve + decrypt the run's model credential (if any) just-in-time and project it onto the harness's env
    /// vars. Empty when the harness can't authenticate (implements no projector) or no credential applies — the
    /// run then relies on whatever env the runner already provides. A PINNED-but-unresolvable credential throws
    /// (the executor's catch lands a clean Failed), never silently using a different key.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string> Env, SecretRedactor Redactor)> ResolveModelCredentialEnvAsync(AgentTask task, Guid teamId, IAgentHarness harness, CancellationToken cancellationToken)
    {
        var projector = harness as IModelCredentialProjector;

        var credential = await _modelCredentials.ResolveAsync(task, teamId, projector, cancellationToken).ConfigureAwait(false);

        var env = projector is not null && credential is not null ? projector.ProjectToEnv(credential) : EmptySecretEnv;
        // Redact only the actual SECRET (the api key / gateway token) — never the non-secret base URL.
        var redactor = credential?.ApiKey is { Length: > 0 } key ? new SecretRedactor(new[] { key }) : SecretRedactor.None;

        return (env, redactor);
    }

    /// <summary>Layer the resolved credential's env onto the task's own non-secret env — the injected value wins for a shared key. In-memory only; the result is never re-persisted (an empty secret env returns the task env unchanged).</summary>
    internal static IReadOnlyDictionary<string, string> MergeEnvironment(IReadOnlyDictionary<string, string> taskEnv, IReadOnlyDictionary<string, string> secretEnv)
    {
        if (secretEnv.Count == 0) return taskEnv;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in taskEnv) merged[key] = value;
        foreach (var (key, value) in secretEnv) merged[key] = value;
        return merged;
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

    private async Task<AgentRunResult> RunHarnessAsync(Guid runId, IAgentHarness harness, ISandboxRunner runner, SandboxSpec spec, SecretRedactor redactor, CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();

        async Task PersistLineAsync(string line)
        {
            var normalized = harness.ParseEvent(line);
            if (normalized is null) return;

            // Strip any secret the CLI echoed BEFORE the append-only log freezes it (the log can't be edited later).
            var redacted = Redact(normalized, redactor);

            await _runs.AppendEventAsync(runId, redacted, cancellationToken).ConfigureAwait(false);
            events.Add(redacted);
        }

        var sandbox = await RunWithHeartbeatAsync(runId, runner, spec, PersistLineAsync, cancellationToken).ConfigureAwait(false);

        // Events are already redacted, so a result the harness folds from them (summary / error) is redacted too.
        return sandbox.Status == SandboxStatus.TimedOut
            ? new AgentRunResult { Status = AgentRunStatus.TimedOut, ExitReason = "timed-out", Error = "The agent run exceeded its time budget and was terminated." }
            : harness.BuildResult(events, sandbox.ExitCode);
    }

    /// <summary>Redact any echoed secret out of a normalized event — its text AND its structured payload — before it reaches the append-only log. No-op when the run has no secret.</summary>
    private static AgentEvent Redact(AgentEvent normalized, SecretRedactor redactor)
    {
        if (redactor.IsEmpty) return normalized;

        return normalized with { Text = redactor.Redact(normalized.Text), Data = RedactData(normalized.Data, redactor) };
    }

    /// <summary>Mask a structured payload via its raw JSON text, then re-parse. If masking somehow broke the JSON, drop the payload rather than persist an unredacted blob.</summary>
    private static JsonElement? RedactData(JsonElement? data, SecretRedactor redactor)
    {
        if (data is null) return null;

        var raw = data.Value.GetRawText();
        var redacted = redactor.Redact(raw);

        if (redacted == raw) return data;

        try { using var doc = JsonDocument.Parse(redacted); return doc.RootElement.Clone(); }
        catch (JsonException) { return null; }
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
