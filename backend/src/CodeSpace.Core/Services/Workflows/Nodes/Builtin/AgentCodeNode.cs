using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Runs an AI agent (Codex, Claude Code, …) as a workflow step. On its first pass it builds an
/// <see cref="AgentTask"/> from config and SUSPENDS with an <c>AgentRun</c> token; the engine creates
/// the durable run, dispatches the executor (which streams the harness in its sandbox), and parks this
/// node. When the agent run reaches a terminal state the engine resumes this node with
/// <c>{ status, summary, changedFiles, branch, error }</c>, which it maps: Succeeded → these become the
/// node's outputs; otherwise the node fails, composing with retry + the <c>error</c> branch like any
/// node failure.
///
/// The node is pure — it never touches the DB or spawns a process. The engine + AgentRunService own the
/// run lifecycle, so any failure (unknown harness, sandbox error, timeout) surfaces as a clean node
/// failure.
///
/// It may reference an Agent persona (<c>agentDefinitionId</c>): the node only carries the reference; the
/// dispatch-time resolver merges the persona's system prompt + model into the task (staying pure, no DB).
/// With a persona, <c>goal</c> is the task-specific addition to its prompt (optional); without one, <c>goal</c>
/// is required. <c>harness</c> is always required (a persona is harness-agnostic); <c>model</c> is always
/// optional (blank → the persona's model → the harness default).
///
/// Config: harness (required) · agentDefinitionId? · goal (required unless a persona is set) · model? · runnerKind? · timeoutSeconds? · autonomyLevel? (one dial deriving the sandbox posture) · network?/readOnly? (advanced per-field overrides of the tier)
/// Inputs: repositoryId? (the repo to clone into the workspace — pick or bind from the trigger)
/// Outputs: status · summary · changedFiles · branch
/// </summary>
public sealed class AgentCodeNode : INodeRuntime
{
    public string TypeKey => "agent.run";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Run agent",
        Category = "Agent",
        Kind = NodeKind.Regular,
        CanSuspend = true,
        IsRerunnableWhenSuspendable = true,   // D7-5: the SOLE opt-in — a re-run map branch re-stages a FRESH AgentRun under the branch's iteration key (mechanically identical to the shipped original-run map durable resume). Not side-effecting, so it re-runs with NO human gate ("execute-again").
        IconKey = "agent",
        Description = "Runs an AI agent (Codex, Claude Code, …) as a step. Streams its progress live; the run's result becomes this node's output.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "agentDefinitionId": { "type": "string", "format": "uuid", "x-selector": "agent", "description": "Pick an Agent persona — its system prompt + model become the defaults for this run (its prompt prepends the goal below). Leave empty to configure the run inline." },
                "goal":           { "type": "string", "description": "What the agent should do (the prompt). Required unless a persona is selected, in which case it's the task-specific addition to the persona's prompt." },
                "harness":        { "type": "string", "x-selector": "harness", "description": "Which coding-agent CLI runs the task (e.g. Codex, Claude Code). Pick from the available harnesses." },
                "model":          { "type": "string", "description": "Model id within the harness's catalog. Leave empty to use the persona's model, or the harness default." },
                "modelCredentialId": { "type": "string", "format": "uuid", "x-selector": "modelCredential", "description": "Model credential the agent authenticates with. Leave empty to use the persona's default, or the team/operator default." },
                "modelCredentialModelId": { "type": "string", "format": "uuid", "x-selector": "credentialedModel", "description": "Pick a specific model from a credential's maintained list — sets BOTH the model and its backing credential from one choice. Takes precedence over 'model' / 'modelCredentialId' above. Leave empty to use those loose fields." },
                "approvalConversationId": { "type": "string", "format": "uuid", "x-selector": "conversation", "description": "Conversation the run posts its tool-approval cards into. Leave empty for no approval surface." },
                "tools":          { "type": "array", "items": { "type": "string" }, "description": "Tool allow-list the agent is restricted to (e.g. Read, Grep, Bash). Empty = the harness default. Added to (not replacing) the persona's tools; enforced by harnesses that support an allow-list (Claude Code), carried otherwise (Codex restricts via sandbox)." },
                "runnerKind":     { "type": "string", "description": "Sandbox runner (e.g. \"local\"). Defaults to the deployment default." },
                "cwdMode":        { "type": "string", "enum": ["Auto", "WorkspaceRoot", "PrimaryRepo"], "description": "MULTI-repo only: where the agent's working directory points. Auto (default): repo-root for one repo, workspace-root for many. WorkspaceRoot: always the shared root (every repo a sibling). PrimaryRepo: always the primary repo's root. Ignored for a single-repo run (which always runs at the repo root)." },
                "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "Wall-clock cap for the run." },
                "autonomyLevel":  { "type": "string", "enum": ["Confined", "Standard", "Trusted", "Unleashed"], "description": "How much the agent may do — one dial that sets write scope + network. Confined: read-only, no network · Standard (default): workspace write, no network · Trusted: + network · Unleashed: highest, admin/controlled runners. The network/readOnly fields below are advanced per-field overrides of this tier." },
                "network":        { "type": "boolean", "description": "Advanced override of the tier's network posture. Leave unset to inherit the autonomy level." },
                "readOnly":       { "type": "boolean", "description": "Advanced override: force analysis-only (no writes), regardless of the autonomy level. Leave unset to inherit the tier." },
                "pushBranch":     { "type": "boolean", "description": "Per-run opt-in: publish the agent's diff as its own branch (codespace/agent/<runId>) even when the deployment-wide push flag is off — the knob a one-agent-one-branch fan-out sets so each agent's work lands on its own branch. Leave unset to defer to the deployment flag." },
                "enableMcp":      { "type": "boolean", "description": "Per-run opt-in: open the FULL MCP tool-fabric (the side-effecting catalog) for this agent, even when the deployment-wide flag is off. Leave unset to defer to the deployment flag (the read-only catalog). Cannot turn the fabric OFF when the deployment forces it on." },
                "outputReviewMode": { "type": "integer", "enum": [0, 1, 2], "description": "Review the agent's produced change with an independent critic at completion: 0 = None (default, no review), 1 = Gate (a disapproved change re-grades the run to NeedsReview so a human looks before the downstream PR-open consumes it), 2 = Improve (a disapproved change is fed back to the same agent for a bounded revise round before it can flag). Leave unset for no review." },
                "reviewerModelId": { "type": "string", "format": "uuid", "x-selector": "credentialedModel", "description": "The credentialed model the output critic runs on. Leave empty to auto-pick the team's strongest structured-eligible model. Only used when outputReviewMode is not None." },
                "reviseRounds": { "type": "integer", "minimum": 0, "maximum": 3, "description": "How many bounded revise rounds the executor may run when the acceptance check fails or the Improve-mode critic flags the output — each round feeds the failure back to the same agent (same conversation, same workspace) and re-verifies. Leave unset for the default: 1 when outputReviewMode is Improve, else 0." },
                "reviewerAgent": { "type": "boolean", "description": "S8: run the output review as a REAL independent agent (read-only, clones the produced branch, prefers a different harness) instead of only the in-process model critic; falls back to the model critic when the agent cannot produce a verdict. Only used when outputReviewMode is not None." },
                "acceptance": { "type": "object", "description": "This task's OBJECTIVE definition-of-done: { command: [argv-or-deliverable-paths...], kind?: TestsPass|ArtifactPresent|LlmJudge|CitationsResolve|ArtifactSchema, description?, rubric? (LlmJudge: { criteria: [{id, requirement, weight?}], threshold? }), schema? (ArtifactSchema: a JSON schema object) }. The executor grades it against the produced branch at completion, fail-closed — a failing oracle re-grades the run to Failed. In a fan-out, bind {{item.acceptance}} to carry each plan item's authored contract." },
                "mode":           { "type": "string", "enum": ["research", "code"], "description": "The model-authored intent of this run — the BASE the planner picks per fan-out subtask. research: analysis-only (read-only, no network, no produced branch); code: edits the codebase (workspace write, publishes its own branch). The autonomyLevel tier + the network/readOnly/pushBranch overrides still layer ON TOP, so the autonomy ceiling clamp always bounds it. Leave unset for today's tier-derived behaviour." }
              },
              "required": ["harness"]
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The PRIMARY repository the agent works in — cloned into its workspace before it runs. Pick one, or switch to Expression to bind it from the trigger (e.g. {{trigger.repositoryId}}). Leave empty for an analysis-only run with no repo." },
                "baseRef": { "type": "string", "description": "The branch/ref to clone the PRIMARY repository at. Leave empty for the repo's default branch. A session follow-up sets this to the prior turn's produced branch so the agent builds on earlier work instead of starting from the default branch." },
                "pinnedSha": { "type": "string", "description": "The EXACT commit to materialize the PRIMARY repository at (S1 — the launch's immutable base). Leave empty for the tip of baseRef / the default branch. When set, the clone is full and hard-checks-out this commit; a missing/unreachable pin fails the run loud." },
                "relatedRepositories": {
                  "type": "array",
                  "description": "Multi-repo: ALSO clone these repositories into the workspace (for a coordinated change across e.g. a frontend + backend). The primary is repositoryId; leave empty for a single-repo run.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "repositoryId": { "type": "string", "format": "uuid" },
                      "alias": { "type": "string", "description": "The short name + mount folder for this repo (e.g. 'api'). Defaults to repo-2, repo-3, …" },
                      "access": { "type": "string", "enum": ["read", "write"], "description": "read = context-only (default); write = the agent may edit + branch it." },
                      "ref": { "type": "string", "description": "The branch/ref to clone THIS repo at. Leave empty for its default branch. A session follow-up sets this to the prior turn's produced branch for this repo so the agent builds on earlier work per repo." }
                    },
                    "required": ["repositoryId"]
                  }
                }
              }
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "status":       { "type": "string" },
                "summary":      { "type": "string" },
                "changedFiles": { "type": "array", "items": { "type": "string" } },
                "branch":       { "type": "string" },
                "changeSetId":  { "type": ["string","null"], "description": "Multi-repo run only: a stable id for the SET of branches this run produced. Null for a single-repo run." },
                "repositoryResults": {
                  "type": "array",
                  "description": "Multi-repo run only: one entry per writable repo — bind this whole array straight into git.open_change_set's 'repositories' input (it reads producedBranch + baseBranch) to open a PR per repo. Empty for a single-repo run (use 'branch' instead).",
                  "items": {
                    "type": "object",
                    "properties": {
                      "alias":          { "type": "string" },
                      "repositoryId":   { "type": ["string","null"] },
                      "changedFiles":   { "type": "array", "items": { "type": "string" } },
                      "producedBranch": { "type": ["string","null"] },
                      "baseSha":        { "type": ["string","null"] },
                      "baseBranch":     { "type": ["string","null"] }
                    }
                  }
                }
              }
            }
            """),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Resumed: the agent run finished. ResumePayload = { status, summary, changedFiles, branch, error }.
        if (context.ResumePayload.HasValue) return Task.FromResult(MapResult(context.ResumePayload.Value));

        var goal = ReadString(context.Config, "goal");
        var displayTitle = ReadOptionalString(context.Config, "displayTitle");
        var harness = ReadString(context.Config, "harness");

        if (!TryReadAgentDefinitionId(context, out var agentDefinitionId)) return Fail("Config 'agentDefinitionId' must be an agent persona id (uuid).");

        if (!TryReadModelCredentialId(context, out var modelCredentialId)) return Fail("Config 'modelCredentialId' must be a model credential id (uuid).");

        if (!TryReadModelCredentialModelId(context, out var modelCredentialModelId)) return Fail("Config 'modelCredentialModelId' must be a credentialed-model id (uuid).");

        if (string.IsNullOrWhiteSpace(harness)) return Fail("Config 'harness' is required.");

        // A persona supplies the prompt floor (its system prompt), so 'goal' is only required without one.
        // The dispatch-time resolver composes the persona's prompt + this goal and supplies the model.
        if (agentDefinitionId is null && string.IsNullOrWhiteSpace(goal)) return Fail("Config 'goal' is required when no agent persona is selected.");

        if (!TryReadRepositoryId(context, out var repositoryId)) return Fail("Input 'repositoryId' must be a repository id (uuid).");

        var autonomy = ReadAutonomyLevel(context.Config);
        var mode = ReadMode(context.Config);

        // Multi-repo: authored RELATED repos (the primary is repositoryId) project onto a WorkspaceSpec. No related
        // repos → null Workspace → the resolver derives the single-repo workspace from RepositoryId → BYTE-IDENTICAL.
        var related = ReadRelatedRepositories(context);

        // Fail loud rather than silently drop the authored multi-repo intent: related repos are meaningless without a
        // primary (the workspace has nowhere to anchor + nothing writable to default to).
        if (related.Count > 0 && repositoryId is null) return Fail("Input 'relatedRepositories' requires a primary 'repositoryId' — pick the primary repository, or remove the related ones.");

        var cwdMode = WorkspaceCwdModeWire.FromWire(ReadOptionalString(context.Config, "cwdMode")) ?? WorkspaceCwdMode.Auto;

        var workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(repositoryId, related, ReadBaseRef(context), ReadBaseRefFromSession(context), cwdMode, ReadPinnedSha(context));

        if (!TryReadAcceptance(context.Config, out var acceptance, out var acceptanceError)) return Fail(acceptanceError!);

        var task = new AgentTask
        {
            Goal = goal,
            DisplayTitle = displayTitle,
            Harness = harness,
            Model = ReadOptionalString(context.Config, "model"),
            AgentDefinitionId = agentDefinitionId,
            ModelCredentialId = modelCredentialId,
            ModelCredentialModelId = modelCredentialModelId,
            Tools = ReadStringArray(context.Config, "tools"),
            RepositoryId = repositoryId,
            Workspace = workspace,
            RunnerKind = ReadOptionalString(context.Config, "runnerKind"),
            // A positive timeoutSeconds caps the run; an explicit ≤0 means NO wall-clock (unbounded — bounded only by
            // the stall watchdog + cost cap, the operator's "no timeout" choice); ABSENT → the bounded 1h default. Only
            // an explicit non-positive value is infinite, so an unset config is never accidentally unbounded.
            TimeoutSeconds = ReadInt(context.Config, "timeoutSeconds") is { } t ? (t > 0 ? t : (int?)null) : 3600,
            Autonomy = autonomy,
            Permissions = ResolvePermissions(context.Config, autonomy, mode),
            ApprovalConversationId = ReadOptionalGuid(context.Config, "approvalConversationId"),
            PushProducedBranch = acceptance != null ? true : ResolvePushBranch(context.Config, mode),
            EnableMcpEndpoint = ReadOptionalBool(context.Config, "enableMcp"),
            // The output-review mode + its reviewer model — the executor runs an independent critic over the produced
            // change at completion. Absent ⇒ None ⇒ no review ⇒ byte-identical. Read as the enum int (the schema offers
            // 0=None / 1=Gate; v1 supports Gate only).
            OutputReviewMode = ReadInt(context.Config, "outputReviewMode") is { } rm ? (ReviewMode)rm : ReviewMode.None,
            ReviewerModelId = ReadOptionalGuid(context.Config, "reviewerModelId"),
            // S6: the bounded revise budget — how many times the executor may feed an oracle failure / Improve-critic
            // flag back to the same agent inside this run. Absent ⇒ null ⇒ the executor's default (1 under Improve,
            // else 0). Clamped server-side (the executor), so an authored 99 buys the cap, not a runaway.
            MaxReviseRounds = ReadInt(context.Config, "reviseRounds"),
            // S8: review the output with a REAL independent agent (distinct-harness-first) instead of only the model critic.
            ReviewerAgent = ReadOptionalBool(context.Config, "reviewerAgent") ?? false,
            // F4 (S5 review): a contract implies a GRADABLE branch — force the publish opt-in ON whenever an
            // acceptance is bound (the resolve verb's forcePushBranch precedent; the pushBranch key is an OR-gate,
            // so this can only widen). Without it, a stock deployment (push flag off) would fail every contract
            // "no-branch-or-repo" even when the work and the check are both perfect.
            Acceptance = acceptance,
        };

        task = ApplyRespawnResumeHint(task, context.PriorAttemptPayload);

        return Task.FromResult(NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.AgentRun,
            Payload = JsonSerializer.SerializeToElement(task, AgentJson.Options),
        }));
    }

    /// <summary>
    /// The task's objective acceptance spec. A MISSING key or a JSON null (an item without a contract in a
    /// fan-out — the {{item.acceptance}} no-contract resolution) reads as "no oracle"; but a PRESENT value that
    /// is not a valid spec (a non-object, a typo'd command key, garbage kind) FAILS the node — the operator
    /// authored a contract, and silently dropping it would invert the gate's fail-closed philosophy.
    /// </summary>
    private static bool TryReadAcceptance(IReadOnlyDictionary<string, JsonElement> config, out SupervisorAcceptanceSpec? acceptance, out string? error)
    {
        acceptance = null;
        error = null;

        if (!config.TryGetValue("acceptance", out var v) || v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Undefined) return true;

        if (v.ValueKind != JsonValueKind.Object)
        {
            error = "Config 'acceptance' must be an object: { command: [argv...], kind?, description? }.";
            return false;
        }

        try
        {
            var spec = v.Deserialize<SupervisorAcceptanceSpec>(AgentJson.Options);

            if (spec is not { Command.Count: > 0 } || spec.Command.All(string.IsNullOrWhiteSpace))
            {
                error = "Config 'acceptance' needs a non-empty 'command' argv (e.g. [\"sh\", \"check.sh\"]).";
                return false;
            }

            // Kind-specific completeness (triad S7 — a judge with no rubric / a schema check with no schema): the
            // SHARED authoring rule, fail-loud at staging so a half-authored contract never reaches a billed agent.
            if (AgentAcceptanceContract.ValidateAuthored(spec) is { } specError)
            {
                error = $"Config 'acceptance' is incomplete: {specError}";
                return false;
            }

            acceptance = spec;
            return true;
        }
        catch (JsonException)
        {
            error = "Config 'acceptance' is not a valid spec: { command: [argv...], kind?: TestsPass|ArtifactPresent|LlmJudge|CitationsResolve|ArtifactSchema, description?, rubric?, schema? }.";
            return false;
        }
    }

    /// <summary>Map the resumed agent-run outcome onto this node's result. Succeeded → outputs; anything else → a clean node failure, marked retryable only when a fresh respawn could change the outcome.</summary>
    private static NodeResult MapResult(JsonElement payload)
    {
        var status = ReadString(payload, "status");

        if (status != nameof(AgentRunStatus.Succeeded))
        {
            var error = ReadString(payload, "error");

            // NeedsReview parked human-owed work, Cancelled recorded the user's own stop, and a fail-closed
            // acceptance re-grade is a VERDICT (same code + same check would fail again — in-run improvement is the
            // revise loop's job, plan-level revision the supervisor's) — respawning the agent can change none of
            // them, so all three fail non-retryable. Everything else (a crashed / timed-out / abandoned run) is a
            // candidate transient death a fresh agent may survive; the node's retry policy decides whether one is bought.
            //
            // P3.1: an acceptance re-grade is deterministic ONLY when the check itself genuinely ran and failed —
            // a grader INFRA fault (e.g. "tests-timed-out", the grader's OWN wall-clock firing on a legitimately
            // slow suite) is an environment/workload fact, not a code defect, so it gets the SAME fresh-respawn
            // chance a crash/timeout does (mirrors AgentAcceptanceContract.IsInfraFailure, the same classification
            // the executor's revise loop / supervisor decider / recitation already apply elsewhere).
            var acceptanceFailed = ReadString(payload, "exitReason") == AgentAcceptanceContract.FailClosedExitReason;
            var acceptanceInfraFault = acceptanceFailed && AgentAcceptanceContract.IsInfraFailure(ReadOptionalString(payload, "acceptanceDetail"), WorkPresent(payload));

            var deterministic = (status is nameof(AgentRunStatus.NeedsReview) or nameof(AgentRunStatus.Cancelled) || acceptanceFailed) && !acceptanceInfraFault;

            return NodeResult.Fail($"Agent run did not succeed: {(string.IsNullOrEmpty(error) ? status : error)}", retryable: !deterministic);
        }

        var outputs = new Dictionary<string, JsonElement> { ["status"] = JsonSerializer.SerializeToElement(nameof(AgentRunStatus.Succeeded)) };
        CopyIfPresent(payload, "summary", outputs);
        CopyIfPresent(payload, "changedFiles", outputs);
        CopyIfPresent(payload, "branch", outputs);
        // Multi-repo ONLY: the per-repo change set (each writable repo's branch) + its id, so a downstream
        // git.open_change_set can open a PR per repo. A single-repo run's payload carries an EMPTY array + a null id
        // (the resume payload serializes them), so we copy them ONLY when meaningful — keeping the single-repo output
        // bag byte-identical (no repositoryResults/changeSetId keys added).
        CopyIfNonEmptyArray(payload, "repositoryResults", outputs);
        CopyIfNonNull(payload, "changeSetId", outputs);

        return NodeResult.Ok(outputs);
    }

    /// <summary>
    /// P2.3: stamp the retry-resume hint from the RETIRING prior attempt's own resume payload (the same
    /// sessionId/transcript triple <c>RealSupervisorActionExecutor.ApplyRetryResumeHintAsync</c> reads from a DB
    /// query for a supervisor-orchestrated subtask retry) — or return the task unchanged when this isn't a
    /// respawn, or the retiring attempt captured no resumable session (cold-start, byte-identical to before).
    /// </summary>
    private static AgentTask ApplyRespawnResumeHint(AgentTask task, JsonElement? priorAttemptPayload)
    {
        if (priorAttemptPayload is not { } payload) return task;

        if (ReadOptionalString(payload, "sessionId") is not { } sessionId) return task;

        return task with
        {
            ResumeFromSessionId = sessionId,
            RestoredTranscript = ReadOptionalString(payload, "sessionTranscript"),
            RestoredTranscriptArtifactId = ReadOptionalGuid(payload, "sessionTranscriptArtifactId"),
        };
    }

    /// <summary>Whether the resumed payload shows produced WORK (git ground truth: changed files or a branch, single- or multi-repo) — mirrors <c>SupervisorOutcome.ResultShowsWork</c>'s definition (the one "work exists" read every infra classification shares) over the flat resume payload's own fields.</summary>
    private static bool WorkPresent(JsonElement payload) =>
        (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("changedFiles", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0)
        || ReadOptionalString(payload, "branch") is not null
        || (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("repositoryResults", out var repos) && repos.ValueKind == JsonValueKind.Array
            && repos.EnumerateArray().Any(r => ReadOptionalString(r, "producedBranch") is not null || (r.TryGetProperty("changedFiles", out var rf) && rf.ValueKind == JsonValueKind.Array && rf.GetArrayLength() > 0)));

    private static Task<NodeResult> Fail(string message) => Task.FromResult(NodeResult.Fail(message));

    /// <summary>Read the optional <c>agentDefinitionId</c> config. Absent / empty → no persona (null, a pure-inline run). Present-but-malformed → false (a clean node failure).</summary>
    private static bool TryReadAgentDefinitionId(NodeRunContext context, out Guid? agentDefinitionId)
    {
        agentDefinitionId = null;

        var raw = ReadString(context.Config, "agentDefinitionId");

        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!Guid.TryParse(raw, out var id)) return false;

        agentDefinitionId = id;
        return true;
    }

    /// <summary>Read the optional <c>modelCredentialId</c> config (a node-level override of the persona/team default). Absent / empty → null. Present-but-malformed → false (a clean node failure).</summary>
    private static bool TryReadModelCredentialId(NodeRunContext context, out Guid? modelCredentialId)
    {
        modelCredentialId = null;

        var raw = ReadString(context.Config, "modelCredentialId");

        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!Guid.TryParse(raw, out var id)) return false;

        modelCredentialId = id;
        return true;
    }

    /// <summary>Read the optional <c>modelCredentialModelId</c> config (a picked credentialed model). Absent / empty → null. Present-but-malformed → false (a clean node failure). The dispatch-time resolver expands it into model + credential.</summary>
    private static bool TryReadModelCredentialModelId(NodeRunContext context, out Guid? modelCredentialModelId)
    {
        modelCredentialModelId = null;

        var raw = ReadString(context.Config, "modelCredentialModelId");

        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!Guid.TryParse(raw, out var id)) return false;

        modelCredentialModelId = id;
        return true;
    }

    /// <summary>Read the optional <c>baseRef</c> input — the branch/ref to clone the primary repo at (session branch continuity). Absent / blank / non-string → null (the repo default).</summary>
    private static string? ReadBaseRef(NodeRunContext context) =>
        context.Inputs.TryGetValue("baseRef", out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

    /// <summary>Read the optional <c>baseRefFromSession</c> input — true ONLY when the launch projection set <c>baseRef</c> from a SESSION-inherited prior branch (a transient branch a merged PR can delete). Marks the primary ref SOFT so the clone falls back to the default branch if it was pruned. An author-pinned baseRef never carries this ⇒ stays HARD (fail loud if gone). Absent / non-true → false.</summary>
    private static bool ReadBaseRefFromSession(NodeRunContext context) =>
        context.Inputs.TryGetValue("baseRefFromSession", out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Read the optional <c>pinnedSha</c> input — the primary repo's launch-resolved base pin (S1): the EXACT commit the workspace materializes. Absent / blank / non-string → null (tip-of-ref, byte-identical).</summary>
    private static string? ReadPinnedSha(NodeRunContext context) =>
        context.Inputs.TryGetValue("pinnedSha", out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

    /// <summary>Read the optional <c>repositoryId</c> input. Absent / empty → no repo (null, an analysis-only run). Present-but-malformed → false (a clean node failure).</summary>
    private static bool TryReadRepositoryId(NodeRunContext context, out Guid? repositoryId)
    {
        repositoryId = null;

        if (!context.Inputs.TryGetValue("repositoryId", out var value) || value.ValueKind == JsonValueKind.Null) return true;

        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!Guid.TryParse(raw, out var id)) return false;

        repositoryId = id;
        return true;
    }

    /// <summary>
    /// Parse the authored <c>relatedRepositories</c> input — an array of {repositoryId, alias?, access?} — into
    /// related <see cref="WorkspaceRepositorySpec"/>s (access defaults to read-only context). A malformed/idless entry
    /// is skipped (lenient — the editor validates). Absent / empty → no related repos → a single-repo run.
    /// </summary>
    private static IReadOnlyList<WorkspaceRepositorySpec> ReadRelatedRepositories(NodeRunContext context) =>
        context.Inputs.TryGetValue("relatedRepositories", out var value)
            ? AgentWorkspaceAuthoring.ParseRelatedRepositories(value)
            : Array.Empty<WorkspaceRepositorySpec>();

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string ReadString(JsonElement bag, string key) =>
        bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? ReadOptionalString(JsonElement bag, string key)
    {
        var s = ReadString(bag, key);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static Guid? ReadOptionalGuid(JsonElement bag, string key) =>
        Guid.TryParse(ReadOptionalString(bag, key), out var id) ? id : null;

    private static string? ReadOptionalString(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        var s = ReadString(bag, key);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>Read an optional uuid config field. Absent / empty / unparseable → null — this is optional config, not a safety-critical input, so a malformed value degrades to null rather than failing the node.</summary>
    private static Guid? ReadOptionalGuid(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        Guid.TryParse(ReadOptionalString(bag, key), out var id) ? id : null;

    private static int? ReadInt(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>Reads the autonomy tier (case-insensitive); absent / unrecognized → the safe <see cref="AgentAutonomyLevel.Standard"/> default.</summary>
    private static AgentAutonomyLevel ReadAutonomyLevel(IReadOnlyDictionary<string, JsonElement> bag) =>
        Enum.TryParse<AgentAutonomyLevel>(ReadString(bag, "autonomyLevel"), ignoreCase: true, out var level) ? level : AgentAutonomyLevel.Standard;

    /// <summary>Reads the model-authored <c>mode</c> (case-insensitive); absent / unrecognized → <see cref="AgentMode.Unset"/> (today's behaviour, never a throw — mirrors <see cref="ReadAutonomyLevel"/>).</summary>
    private static AgentMode ReadMode(IReadOnlyDictionary<string, JsonElement> bag) =>
        Enum.TryParse<AgentMode>(ReadString(bag, "mode"), ignoreCase: true, out var mode) ? mode : AgentMode.Unset;

    /// <summary>
    /// Resolves permissions over THREE layers, low→high: the mode BASE (the model's intent), the autonomy TIER, then
    /// explicit per-field overrides. <see cref="AgentMode.Research"/> is the most restrictive base (ReadOnly + no
    /// network — always safe); <see cref="AgentMode.Code"/> / <see cref="AgentMode.Unset"/> use the tier-derived
    /// baseline (byte-identical to before this knob existed). An override applies ONLY when the field is explicitly
    /// present, so a tier-only config inherits cleanly and a legacy network/readOnly config keeps its prior meaning.
    /// Clamp-safe: Code's base is still <see cref="AgentAutonomyPolicy.Derive"/> of the (already-clamped) tier, so a
    /// Standard/Confined ceiling still caps the write scope — mode never raises the tier or turns network on by itself.
    /// </summary>
    private static AgentPermissions ResolvePermissions(IReadOnlyDictionary<string, JsonElement> bag, AgentAutonomyLevel autonomy, AgentMode mode)
    {
        var permissions = mode == AgentMode.Research
            ? new AgentPermissions { Network = AgentNetworkAccess.Off, WriteScope = AgentWriteScope.ReadOnly }
            : AgentAutonomyPolicy.Derive(autonomy);

        if (ReadOptionalBool(bag, "network") is { } network)
            permissions = permissions with { Network = network ? AgentNetworkAccess.On : AgentNetworkAccess.Off };

        if (ReadOptionalBool(bag, "readOnly") is { } readOnly)
            permissions = permissions with { WriteScope = readOnly ? AgentWriteScope.ReadOnly : AgentWriteScope.Workspace };

        return permissions;
    }

    /// <summary>
    /// Resolves the per-run push opt-in over the mode base: an explicit <c>pushBranch</c> always wins; else
    /// <see cref="AgentMode.Code"/> → true (the branch a coding agent produces), <see cref="AgentMode.Research"/> →
    /// false (analysis produces no branch), <see cref="AgentMode.Unset"/> → null (defer to the deployment flag —
    /// byte-identical to before this knob existed). Mirrors the precedence shape of <see cref="ResolvePermissions"/>.
    /// </summary>
    private static bool? ResolvePushBranch(IReadOnlyDictionary<string, JsonElement> bag, AgentMode mode) =>
        ReadOptionalBool(bag, "pushBranch") ?? mode switch { AgentMode.Code => true, AgentMode.Research => false, _ => (bool?)null };

    /// <summary>A tri-state bool read: present-true / present-false / absent (null) — so an override only fires when explicitly set.</summary>
    private static bool? ReadOptionalBool(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null } : null;

    /// <summary>Read an optional string-array config field. Absent → null (inherit the harness default); present → the string elements (blanks skipped), preserving "[]" = no tools.</summary>
    private static IReadOnlyList<string>? ReadStringArray(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        if (!bag.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;

        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static void CopyIfPresent(JsonElement payload, string key, Dictionary<string, JsonElement> outputs)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var v)) outputs[key] = v.Clone();
    }

    /// <summary>Copy an array output ONLY when it is non-empty — a single-repo run's empty change set must not add a <c>repositoryResults: []</c> key (byte-identical).</summary>
    private static void CopyIfNonEmptyArray(JsonElement payload, string key, Dictionary<string, JsonElement> outputs)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
            outputs[key] = v.Clone();
    }

    /// <summary>Copy a scalar output ONLY when it is non-null — a single-repo run's null change-set id must not add a <c>changeSetId: null</c> key (byte-identical).</summary>
    private static void CopyIfNonNull(JsonElement payload, string key, Dictionary<string, JsonElement> outputs)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null)
            outputs[key] = v.Clone();
    }
}

/// <summary>
/// The model-authored INTENT of an agent run — a BOUNDED vocabulary the planner picks per fan-out subtask, mapped
/// to a permission/push BASE by <see cref="AgentCodeNode"/>. It is NODE-PRIVATE: it never reaches the
/// <see cref="AgentTask"/> envelope — the node resolves it into concrete <c>Permissions</c> + <c>PushProducedBranch</c>
/// at suspend time, so the agent layer's wire contract is unchanged. Unrecognized / absent → <see cref="Unset"/>,
/// which is today's tier-derived behaviour (never a throw).
/// </summary>
internal enum AgentMode
{
    /// <summary>Analysis-only: read-only, no network, no produced branch — the most restrictive base, always safe.</summary>
    Research,

    /// <summary>Edits the codebase: workspace write (the tier-derived posture) and publishes its own branch.</summary>
    Code,

    /// <summary>No mode authored — the tier-derived baseline + defer-to-the-flag push (byte-identical to before this knob existed).</summary>
    Unset,
}
