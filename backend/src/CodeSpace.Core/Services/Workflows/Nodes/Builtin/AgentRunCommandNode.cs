using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Runs ONE shell command in a sandbox as a workflow step — the build / test / lint primitive that doesn't
/// need a full AI agent. With a <c>repositoryId</c> the command runs inside a freshly-cloned, per-run
/// workspace (so <c>npm test</c> / <c>make lint</c> see real code); without one it runs ephemerally. The
/// command itself never touches the network unless <c>network</c> is set — secure by default — and runs
/// under the runner's process / file-size rlimits (a fork-bomb + runaway-write cap).
///
/// A non-zero exit is a NORMAL outcome: the node SUCCEEDS with <c>status</c>=Failed/TimedOut + the exit code,
/// so a workflow branches on the result (e.g. tests-passed? → open a PR). The node only FAILS on an
/// infrastructure error (clone failure, runner crash) — composing with retry + the error branch.
///
/// Generic across backends: the run executes on the sandbox runner named by <c>runnerKind</c>, or on the
/// deployment default (<see cref="CodeSpace.Core.Settings.AgentDefaultRunnerSetting"/>, itself defaulting to
/// "local") when the input names none; a future docker / k8s runner + workspace provider plug in behind the same
/// registries unchanged.
///
/// <para>ADMISSION SCOPE (D4a): this node runs the command SYNCHRONOUSLY via <see cref="IRunCommandService"/>
/// with no durable <c>AgentRun</c> row, so it is intentionally NOT bounded by the per-team / global in-flight
/// agent-run cap (<c>IAdmissionController</c>) — that gate counts <c>AgentRun</c> rows, which this surface never
/// creates. Its concurrency is instead bounded by the engine's per-frontier <c>maxParallelism</c> (a run_command
/// node holds a wave slot for its whole wall-clock duration), the node's <c>timeoutSeconds</c>, and the runner's
/// process / file-size rlimits. A dedicated synchronous-sandbox admission notion is a follow-up (PR-D4b), not
/// part of this in-flight agent-run cap.</para>
///
/// Inputs: repositoryId? · command (required) · args · branch? · network? · timeoutSeconds? · runnerKind? · maxOutputChars?
/// Outputs: command status and exit code; captured stdout/stderr; observed source-byte counts with explicit lower-bound
/// flags; captured UTF-8 sizes and completeness. Full-output artifact IDs are reserved for complete captures; partial
/// content uses separate CapturedArtifactId outputs and records a completeness gap even when excerpt storage succeeds.
/// </summary>
public sealed class AgentRunCommandNode : INodeRuntime
{
    private readonly IRunCommandService _runCommand;
    private readonly IArtifactStore _artifacts;

    public AgentRunCommandNode(IRunCommandService runCommand, IArtifactStore artifacts)
    {
        _runCommand = runCommand;
        _artifacts = artifacts;
    }

    /// <summary>The node's stable type key — also the MCP tool kind the gate special-cases for command-risk escalation (Slice B3a). A const so the gate references it without drift.</summary>
    public const string NodeTypeKey = "agent.run_command";

    public string TypeKey => NodeTypeKey;

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Run command",
        Category = "Agent",
        IconKey = "terminal",
        Kind = NodeKind.Regular,
        Description = "Runs a shell command in an isolated sandbox (optionally inside a cloned repo). The exit code + output become the node's result; the network is off unless enabled.",
        // A command can mutate state / push / call out — a permanent side effect. The engine refuses
        // auto-resume on abandoned runs so we never run it twice.
        IsSideEffecting = true,
        // Synchronous + standalone → exposable as an agent tool (a destructive, approval-gated one).
        IsAgentToolEligible = true,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId":   { "type": "string", "format": "uuid", "x-selector": "repository", "description": "Repository to clone and run inside. Leave empty to run with no checkout. Or switch to Expression to bind it from the trigger (e.g. {{trigger.repositoryId}}).", "x-spotlight": 2 },
                "command":        { "type": "string", "minLength": 1, "description": "Executable to run (resolved on PATH, e.g. \"npm\", \"make\", \"pytest\"). Not shell-interpreted — put each argument in Args.", "x-spotlight": 1 },
                "args":           { "type": "array", "items": { "type": "string" }, "description": "Arguments, one per entry (e.g. [\"test\", \"--silent\"]). No shell splitting or globbing." },
                "branch":         { "type": "string", "description": "Branch / tag / sha to check out (repo runs only). Empty → the repository's default branch." },
                "network":        { "type": "boolean", "description": "Allow the command to reach the network. Off by default — the sandbox severs egress so the command can't call out or exfiltrate." },
                "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "Wall-clock cap. On expiry the command (and its children) are killed and status is TimedOut. Default 600.", "x-spotlight": 3 },
                "runnerKind":     { "type": "string", "description": "Sandbox backend to run on (e.g. \"local\"). Empty → the deployment default, set by the Agents:DefaultRunnerKind configuration key (Agents__DefaultRunnerKind in the environment); \"local\" when that is unset." },
                "maxOutputChars": { "type": "integer", "minimum": 1, "description": "Cap the captured stdout/stderr to this many characters (a head+tail preview is kept). Leave empty to keep the returned capture. Source byte counts and lower-bound flags report whether the runner reached EOF; capture completeness states whether output was lost before this inline cap." }
              },
              "required": ["command"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "exitCode":    { "type": "integer" },
                "status":      { "type": "string" },
                "stdout":      { "type": "string" },
                "stderr":      { "type": "string" },
                "stdoutBytes": { "type": "integer" },
                "stderrBytes": { "type": "integer" },
                "stdoutBytesIsLowerBound": { "type": "boolean", "description": "True when stdout EOF was not observed; stdoutBytes is not a known total." },
                "stderrBytesIsLowerBound": { "type": "boolean", "description": "True when stderr EOF was not observed; stderrBytes is not a known total." },
                "stdoutCapturedBytes": { "type": "integer", "description": "UTF-8 size of the returned capture before the inline cap; not a durability receipt." },
                "stderrCapturedBytes": { "type": "integer", "description": "UTF-8 size of the returned capture before the inline cap; not a durability receipt." },
                "stdoutCaptureComplete": { "type": "boolean", "description": "The runner retained the full stdout through EOF before any inline cap." },
                "stderrCaptureComplete": { "type": "boolean", "description": "The runner retained the full stderr through EOF before any inline cap." },
                "stdoutCapturedArtifactId": { "type": "string", "format": "uuid", "description": "Artifact holding only the captured stdout excerpt; missing source content is not recoverable from this artifact." },
                "stderrCapturedArtifactId": { "type": "string", "format": "uuid", "description": "Artifact holding only the captured stderr excerpt; missing source content is not recoverable from this artifact." },
                "stdoutArtifactId": { "type": "string", "format": "uuid", "description": "Set only when stdout was capped — the artifact id holding the FULL stdout (fetch via /api/artifacts/{id}). Absent when nothing was dropped." },
                "stderrArtifactId": { "type": "string", "format": "uuid", "description": "Set only when stderr was capped — the artifact id holding the FULL stderr. Absent when nothing was dropped." }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadNonEmpty(context, "command", out var command)) return NodeResult.Fail("Input 'command' is required.");

        var hasTeam = NodeScopeReader.TryReadTeamId(context, out var teamId);

        var request = new RunCommandRequest
        {
            Command = command,
            Args = TryReadStringArray(context, "args"),
            RepositoryId = TryReadGuid(context, "repositoryId", out var repoId) ? repoId : (Guid?)null,
            TeamId = hasTeam ? teamId : (Guid?)null,
            Ref = TryReadNonEmpty(context, "branch", out var branch) ? branch : null,
            AllowNetwork = TryReadBool(context, "network"),
            RunnerKind = TryReadNonEmpty(context, "runnerKind", out var rk) ? rk : null,
        };

        if (TryReadPositiveInt(context, "timeoutSeconds", out var timeout)) request = request with { TimeoutSeconds = timeout };

        SandboxResult result;
        try
        {
            result = await context.Observability.TraceExternalCallAsync(
                target: $"agent.run_command:{(request.RepositoryId is { } id ? id.ToString() : "ephemeral")}",
                method: "run_command",
                requestPayload: JsonSerializer.SerializeToElement(new { repository_id = request.RepositoryId, command, arg_count = request.Args.Count, network = request.AllowNetwork, timeout_seconds = request.TimeoutSeconds, runner_kind = request.RunnerKind }),
                action: ct => _runCommand.RunAsync(request, ct),
                completionExtractor: r => new ExternalCallCompletion
                {
                    ResponsePayload = JsonSerializer.SerializeToElement(new { exit_code = r.ExitCode, status = r.Status.ToString() })
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        // Bad input (blank command) and a workspace/clone failure are clean node failures with actionable text;
        // a non-zero command exit is NOT an exception — it flows through as a successful node below.
        catch (InvalidOperationException ex) { return NodeResult.Fail(ex.Message); }
        catch (WorkspaceException ex) { return NodeResult.Fail($"Couldn't prepare the workspace: {ex.Message}"); }

        context.Logger.LogInformation("Ran command '{Command}' (repo {RepoId}) → status {Status}, exit {Exit}", command, request.RepositoryId, result.Status, result.ExitCode);

        // The inline cap applies to the returned capture, which may already be only an excerpt.
        var maxOutputChars = TryReadPositiveInt(context, "maxOutputChars", out var cap) ? cap : 0;
        var captures = new[]
        {
            new CommandOutputCapture("stdout", result.Stdout, OutputCap.Apply(result.Stdout, maxOutputChars), result.Observation?.Stdout),
            new CommandOutputCapture("stderr", result.Stderr, OutputCap.Apply(result.Stderr, maxOutputChars), result.Observation?.Stderr),
        };
        var outputs = new Dictionary<string, JsonElement>
        {
            ["exitCode"] = JsonSerializer.SerializeToElement(result.ExitCode),
            ["status"] = JsonSerializer.SerializeToElement(result.Status.ToString()),
        };
        foreach (var capture in captures)
        {
            outputs[capture.Name] = JsonSerializer.SerializeToElement(capture.Inline.Text);
            outputs[capture.Name + "Bytes"] = JsonSerializer.SerializeToElement(capture.ObservedBytes);
            outputs[capture.Name + "BytesIsLowerBound"] = JsonSerializer.SerializeToElement(capture.Observation is { ReachedEndOfStream: false });
            outputs[capture.Name + "CapturedBytes"] = JsonSerializer.SerializeToElement(Encoding.UTF8.GetByteCount(capture.Text));
            outputs[capture.Name + "CaptureComplete"] = JsonSerializer.SerializeToElement(capture.Complete);

            if (!capture.Complete)
                await NoticeOutputLossAsync(context, $"Command {capture.Name} capture is incomplete; {capture.ObservedBytes} source bytes were observed{(capture.Observation is { ReachedEndOfStream: false } ? " (a lower bound; EOF was not observed)" : "")}. Only a captured excerpt is available.").ConfigureAwait(false);
            var artifactId = await PreserveOutputAsync(hasTeam ? teamId : null, capture, context, cancellationToken).ConfigureAwait(false);
            if (artifactId is { } id) outputs[capture.Name + (capture.Complete ? "ArtifactId" : "CapturedArtifactId")] = JsonSerializer.SerializeToElement(id);
        }

        return NodeResult.Ok(outputs);
    }

    /// <summary>Preserve the bytes we actually have; a partial capture never receives the full-output artifact key.</summary>
    private async Task<Guid?> PreserveOutputAsync(Guid? teamId, CommandOutputCapture capture, NodeRunContext context, CancellationToken cancellationToken)
    {
        if ((!capture.Inline.Truncated && capture.Complete) || teamId is not { } tid || string.IsNullOrEmpty(capture.Text)) return null;
        try
        {
            return await _artifacts.PutAsync(tid, Encoding.UTF8.GetBytes(capture.Text), "text/plain", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "Failed to preserve captured command {Stream} to the artifact store; keeping the inline preview only", capture.Name);
            await NoticeOutputLossAsync(context, $"Command {capture.Name} capture could not be stored ({ex.GetType().Name}); only the inline preview was kept.").ConfigureAwait(false);
            return null;
        }
    }

    private static async Task NoticeOutputLossAsync(NodeRunContext context, string detail)
    {
        context.Logger.LogWarning("{CommandOutputCaptureGap}", detail);
        if (context.Observability is not INodeLossReporting reporter) return;
        try { await reporter.NoticeContentNotStoredAsync(detail, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { context.Logger.LogWarning(ex, "Command output capture loss could not be recorded; the command outcome is unchanged"); }
    }

    private sealed record CommandOutputCapture(string Name, string Text, OutputCap.Result Inline, SandboxStreamObservation? Observation)
    {
        public bool Complete => Observation is null or { ReachedEndOfStream: true, CaptureComplete: true };
        public long ObservedBytes => Observation?.ObservedBytes ?? Encoding.UTF8.GetByteCount(Text);
    }

    private static bool TryReadNonEmpty(NodeRunContext context, string key, out string text)
    {
        text = "";
        if (!context.Inputs.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String) return false;
        text = (value.GetString() ?? "").Trim();
        return text.Length > 0;
    }

    private static bool TryReadGuid(NodeRunContext context, string key, out Guid id)
    {
        id = Guid.Empty;
        if (!context.Inputs.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(value.GetString(), out id);
    }

    private static bool TryReadBool(NodeRunContext context, string key) =>
        context.Inputs.TryGetValue(key, out var value) && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed));

    private static bool TryReadPositiveInt(NodeRunContext context, string key, out int number)
    {
        number = 0;
        if (!context.Inputs.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Number) return false;
        return value.TryGetInt32(out number) && number > 0;
    }

    /// <summary>Optional string array (e.g. args): non-empty strings from a JSON array; absent / non-array → empty. Blank entries are dropped EXCEPT they're args — keep verbatim non-null entries (an empty arg is valid argv).</summary>
    private static IReadOnlyList<string> TryReadStringArray(NodeRunContext context, string key)
    {
        if (!context.Inputs.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        var items = new List<string>(value.GetArrayLength());
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) continue;
            items.Add(entry.GetString() ?? "");
        }
        return items;
    }
}
