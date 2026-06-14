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
/// Generic across backends: the run executes on the sandbox runner named by <c>runnerKind</c> (default
/// "local"); a future docker / k8s runner + workspace provider plug in behind the same registries unchanged.
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
/// Outputs: exitCode · status · stdout · stderr · stdoutBytes · stderrBytes (the original sizes, even when stdout/stderr are capped)
/// </summary>
public sealed class AgentRunCommandNode : INodeRuntime
{
    private readonly IRunCommandService _runCommand;

    public AgentRunCommandNode(IRunCommandService runCommand)
    {
        _runCommand = runCommand;
    }

    public string TypeKey => "agent.run_command";

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
                "repositoryId":   { "type": "string", "format": "uuid", "x-selector": "repository", "description": "Repository to clone and run inside. Leave empty to run with no checkout. Or switch to Expression to bind it from the trigger (e.g. {{trigger.repositoryId}})." },
                "command":        { "type": "string", "minLength": 1, "description": "Executable to run (resolved on PATH, e.g. \"npm\", \"make\", \"pytest\"). Not shell-interpreted — put each argument in Args." },
                "args":           { "type": "array", "items": { "type": "string" }, "description": "Arguments, one per entry (e.g. [\"test\", \"--silent\"]). No shell splitting or globbing." },
                "branch":         { "type": "string", "description": "Branch / tag / sha to check out (repo runs only). Empty → the repository's default branch." },
                "network":        { "type": "boolean", "description": "Allow the command to reach the network. Off by default — the sandbox severs egress so the command can't call out or exfiltrate." },
                "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "Wall-clock cap. On expiry the command (and its children) are killed and status is TimedOut. Default 600." },
                "runnerKind":     { "type": "string", "description": "Sandbox backend to run on (e.g. \"local\"). Empty → the deployment default." },
                "maxOutputChars": { "type": "integer", "minimum": 1, "description": "Cap stdout/stderr to this many characters (a head+tail preview is kept, the rest dropped). Leave empty for the full output. Use it to keep a noisy build/test log from bloating the run — the exact byte size is always reported on stdoutBytes/stderrBytes." }
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
                "stderrBytes": { "type": "integer" }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadNonEmpty(context, "command", out var command)) return NodeResult.Fail("Input 'command' is required.");

        var request = new RunCommandRequest
        {
            Command = command,
            Args = TryReadStringArray(context, "args"),
            RepositoryId = TryReadGuid(context, "repositoryId", out var repoId) ? repoId : (Guid?)null,
            TeamId = NodeScopeReader.TryReadTeamId(context, out var teamId) ? teamId : (Guid?)null,
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

        // Optional output cap keeps a noisy log from bloating the run state; the full byte size is always reported.
        var maxOutputChars = TryReadPositiveInt(context, "maxOutputChars", out var cap) ? cap : 0;
        var stdout = OutputCap.Apply(result.Stdout, maxOutputChars);
        var stderr = OutputCap.Apply(result.Stderr, maxOutputChars);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["exitCode"] = JsonSerializer.SerializeToElement(result.ExitCode),
            ["status"] = JsonSerializer.SerializeToElement(result.Status.ToString()),
            ["stdout"] = JsonSerializer.SerializeToElement(stdout.Text),
            ["stderr"] = JsonSerializer.SerializeToElement(stderr.Text),
            ["stdoutBytes"] = JsonSerializer.SerializeToElement(stdout.OriginalLength),
            ["stderrBytes"] = JsonSerializer.SerializeToElement(stderr.OriginalLength)
        };

        return NodeResult.Ok(outputs);
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
