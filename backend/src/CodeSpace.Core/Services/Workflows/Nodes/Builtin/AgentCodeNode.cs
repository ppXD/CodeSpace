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
                "harness":        { "type": "string", "x-selector": "harness", "description": "Which coding-agent CLI runs the task (e.g. Codex, Claude Code). Pick from the available harnesses.", "x-spotlight": 1 },
                "model":          { "type": "string", "description": "Model id within the harness's catalog. Leave empty to use the persona's model, or the harness default.", "x-spotlight": 2 },
                "modelCredentialId": { "type": "string", "format": "uuid", "x-selector": "modelCredential", "description": "Model credential the agent authenticates with. Leave empty to use the persona's default, or the team/operator default." },
                "modelCredentialModelId": { "type": "string", "format": "uuid", "x-selector": "credentialedModel", "description": "Pick a specific model from a credential's maintained list — sets BOTH the model and its backing credential from one choice. Takes precedence over 'model' / 'modelCredentialId' above. Leave empty to use those loose fields." },
                "approvalConversationId": { "type": "string", "format": "uuid", "x-selector": "conversation", "description": "Conversation the run posts its tool-approval cards into. Leave empty for no approval surface." },
                "tools":          { "type": "array", "items": { "type": "string" }, "description": "Tool allow-list the agent is restricted to (e.g. Read, Grep, Bash). Empty = the harness default. Added to (not replacing) the persona's tools; enforced by harnesses that support an allow-list (Claude Code), carried otherwise (Codex restricts via sandbox)." },
                "runnerKind":     { "type": "string", "description": "Sandbox runner (e.g. \"local\"). Empty → the deployment default, set by the Agents:DefaultRunnerKind configuration key (Agents__DefaultRunnerKind in the environment); \"local\" when that is unset." },
                "cwdMode":        { "type": "string", "enum": ["Auto", "WorkspaceRoot", "PrimaryRepo"], "title": "Working directory", "x-control": "radioCards", "x-enumLabels": { "Auto": "Automatic (default)", "WorkspaceRoot": "Shared workspace root", "PrimaryRepo": "Primary repo root" }, "x-optionConsequence": { "Auto": "A one-repo run starts at the repo root; a many-repo run starts at the shared workspace root.", "WorkspaceRoot": "A multi-repo run starts at the shared root with every repo as a sibling folder (no effect on single-repo runs).", "PrimaryRepo": "A multi-repo run starts inside the primary repo, reaching the others by relative path (no effect on single-repo runs)." }, "description": "MULTI-repo only: where the agent's working directory points. Ignored for a single-repo run, which always runs at the repo root." },
                "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "Wall-clock cap for the run." },
                "autonomyLevel":  { "type": "string", "enum": ["Confined", "Standard", "Trusted", "Unleashed"], "title": "Autonomy", "x-control": "radioCards", "x-enumLabels": { "Confined": "Read-only, no network", "Standard": "Workspace write, no network", "Trusted": "Adds network access", "Unleashed": "Unattended, no approvals" }, "x-optionConsequence": { "Confined": "The agent reads and analyzes only — no file changes, no network, and destructive tools are refused.", "Standard": "The agent edits files in its workspace with no network; risky tool calls pause for human approval.", "Trusted": "Same workspace writes as Standard plus outbound network; risky tool calls still pause for approval.", "Unleashed": "Same writes and network as Trusted, but risky tool calls run without asking — except irreversible or dangerous ones (a PR merge, rm -rf, sudo…), which still require approval." }, "description": "How much the agent may do — one dial for write scope + network. The network/readOnly fields below are advanced per-field overrides of this tier.", "x-spotlight": 3 },
                "network":        { "type": "boolean", "description": "Advanced override of the tier's network posture. Leave unset to inherit the autonomy level." },
                "readOnly":       { "type": "boolean", "description": "Advanced override: force analysis-only (no writes), regardless of the autonomy level. Leave unset to inherit the tier." },
                "pushBranch":     { "type": "boolean", "description": "Per-run opt-in: publish the agent's diff as its own branch (codespace/agent/<runId>) even when the deployment-wide push flag is off — the knob a one-agent-one-branch fan-out sets so each agent's work lands on its own branch. Leave unset to defer to the deployment flag." },
                "enableMcp":      { "type": "boolean", "description": "Per-run opt-in: open the FULL MCP tool-fabric (the side-effecting catalog) for this agent, even when the deployment-wide flag is off. Leave unset to defer to the deployment flag (the read-only catalog). Cannot turn the fabric OFF when the deployment forces it on." },
                "outputReviewMode": { "type": "integer", "enum": [0, 1, 2], "title": "Review the change", "x-control": "radioCards", "x-enumLabels": { "0": "Off", "1": "Gate", "2": "Improve" }, "x-optionConsequence": { "0": "No review — the agent's produced change is used as-is.", "1": "A critic reviews the change; if it disapproves, the run is flagged Needs review so a person looks before it's used.", "2": "A critic reviews the change; if it disapproves, the agent gets one bounded revise round to fix it before flagging." }, "description": "Review the agent's produced change with an independent critic at completion. Leave unset for no review." },
                "reviewerModelId": { "type": "string", "format": "uuid", "x-selector": "credentialedModel", "description": "The credentialed model the output critic runs on. Leave empty to auto-pick the team's strongest structured-eligible model. Only used when outputReviewMode is not None." },
                "reviseRounds": { "type": "integer", "minimum": 0, "maximum": 3, "description": "How many bounded revise rounds the executor may run when the acceptance check fails or the Improve-mode critic flags the output — each round feeds the failure back to the same agent (same conversation, same workspace) and re-verifies. Leave unset for the default: 1 when outputReviewMode is Improve, else 0." },
                "reviewerAgent": { "type": "boolean", "description": "S8: run the output review as a REAL independent agent (read-only, clones the produced branch, prefers a different harness) instead of only the in-process model critic; falls back to the model critic when the agent cannot produce a verdict. Only used when outputReviewMode is not None." },
                "acceptance": { "type": "object", "description": "This task's OBJECTIVE definition-of-done: { command: [argv-or-deliverable-paths...], kind?: TestsPass|ArtifactPresent|LlmJudge|CitationsResolve|ArtifactSchema, description?, rubric? (LlmJudge: { criteria: [{id, requirement, weight?}], threshold? }), schema? (ArtifactSchema: a JSON schema object) }. The executor grades it against the produced branch at completion, fail-closed — a failing oracle re-grades the run to Failed. In a fan-out, bind {{item.acceptance}} to carry each plan item's authored contract." },
                "mode":           { "type": "string", "enum": ["research", "code"], "title": "Mode", "x-control": "radioCards", "x-enumLabels": { "research": "Research (no publish)", "code": "Code (edit & branch)" }, "x-optionConsequence": { "research": "Runs for analysis with no network and publishes nothing — it may write its deliverables (a report, notes) into its own workspace so a deliverable contract can be graded, but no branch is published.", "code": "Edits files up to the autonomy tier's write limit and opts into publishing its diff as its own branch." }, "description": "The model-authored intent — the base a fan-out planner picks per subtask. The autonomyLevel tier + the network/readOnly/pushBranch overrides still layer ON TOP, so the autonomy ceiling always bounds it. Leave unset for today's tier-derived behaviour." }
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

        var workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(repositoryId, related, ReadBaseRef(context), ReadBaseRefFromSession(context), cwdMode, ReadPinnedSha(context), ReadBaseRefRecoverySha(context));

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
            // change at completion. Absent ⇒ None ⇒ no review ⇒ byte-identical. Read as the enum int
            // (0=None / 1=Gate re-grades a disapproved change to NeedsReview / 2=Improve grants one bounded revise round).
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
            AcceptanceAuthority = ReadAcceptanceAuthority(context.Config),
        };

        task = ApplyRespawnEscalation(ApplyRespawnResumeHint(task, context.PriorAttemptPayload), context.PriorAttemptPayload);

        return Task.FromResult(NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.AgentRun,
            Payload = JsonSerializer.SerializeToElement(task, AgentJson.Options),
        }));
    }

    /// <summary>
    /// P5-4 (staking provenance): the acceptance spec's AUTHOR, read from the SIBLING config key the projection
    /// builder writes — deliberately never a field inside the acceptance object, so a model-authored item spec
    /// can't mint its own authority. Allowlist parse in the C2 posture: EXACTLY "Operator" reads Operator;
    /// anything else — missing, garbage, even "ServerPolicy" (no builder writes it) — reads null, which stakes as
    /// ModelProposal. Authority is only ever under-claimed, never inflated by a config typo.
    /// </summary>
    internal static Messages.Contracts.ContractAuthority? ReadAcceptanceAuthority(IReadOnlyDictionary<string, JsonElement> config) =>
        config.TryGetValue("acceptanceAuthority", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == nameof(Messages.Contracts.ContractAuthority.Operator)
            ? Messages.Contracts.ContractAuthority.Operator
            : null;

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

            // NeedsReview parked human-owed work, Cancelled recorded the user's own stop, a fail-closed
            // acceptance re-grade is a VERDICT (same code + same check would fail again — in-run improvement is the
            // revise loop's job, plan-level revision the supervisor's), and a resource-ceiling kill (below) is a fact
            // about the ceiling — respawning the agent can change none of them, so all four fail non-retryable.
            // Everything else (a crashed / timed-out / abandoned run) is a
            // candidate transient death a fresh agent may survive; the node's retry policy decides whether one is bought.
            //
            // P3.1: an acceptance re-grade is deterministic ONLY when the check itself genuinely ran and failed —
            // a grader INFRA fault (e.g. "tests-timed-out", the grader's OWN wall-clock firing on a legitimately
            // slow suite) is an environment/workload fact, not a code defect, so it gets the SAME fresh-respawn
            // chance a crash/timeout does (mirrors AgentAcceptanceContract.IsInfraFailure, the same classification
            // the executor's revise loop / supervisor decider / recitation already apply elsewhere).
            var exitReason = ReadString(payload, "exitReason");
            var acceptanceFailed = exitReason == AgentAcceptanceContract.FailClosedExitReason;
            var acceptanceInfraFault = acceptanceFailed && AgentAcceptanceContract.IsInfraFailure(ReadOptionalString(payload, "acceptanceDetail"), WorkPresent(payload));

            // The SAME carve-out, one status over: NeedsReview is not one fact. The critic flagging an output is a
            // verdict a respawn cannot change, but the IDLE watchdog killing a silent process is an environment
            // fact — and its sibling the wall-clock watchdog ("timed-out") has always been retryable. Neither
            // watchdog can tell an agent stuck at a prompt from one working quietly through a long build, so
            // treating the idle one's guess as terminal turns a false positive into total loss: the quick lane has
            // no error edge, and a map branch's default terminate mode discards every sibling's finished work too.
            // Retries are still bounded by the node's own policy, so a genuinely stuck agent still lands here.
            var stalled = exitReason == AgentAcceptanceContract.StalledExitReason;

            // The stall carve-out run the OTHER way, and for the opposite reason — the watchdogs retry because neither
            // can see WHY a process went quiet, while this one does not retry because it knows exactly why it died:
            // a cgroup memory ceiling killed this agent's process tree, and a fresh respawn runs at the SAME committed
            // ceiling on the same task, so it dies identically. Retrying only re-bills the kill and buries the one fact
            // the operator needs (raise the ceiling) under N identical deaths. Unlike the watchdogs, this is not a
            // guess: the runner classified it from the cgroup's own oom_kill counter (SandboxStatus.ResourceExhausted).
            var resourceExhausted = exitReason == AgentRunExecutor.ResourceExhaustedExitReason;

            // D3: the third carve-out, and the only one that is not about the FAILURE's nature but about whether
            // anything can be done differently. A non-infra acceptance failure is a verdict the SAME model will
            // reproduce — which is why it is deterministic — but the finished attempt may have left a resolved
            // proposal naming a STRONGER credentialed model. Then a respawn is not a re-run: it is the same task on
            // a better model, which is exactly the escalation the supervisor lane's `retry` has always had. A null
            // proposal (nothing stronger is credentialed) stays terminal, so a one-model team never pays for an
            // identical second attempt.
            var escalationAvailable = ReadOptionalString(payload, "proposedEscalation", "to") is { Length: > 0 };

            var deterministic = (status is nameof(AgentRunStatus.NeedsReview) or nameof(AgentRunStatus.Cancelled) || acceptanceFailed || resourceExhausted)
                                && !acceptanceInfraFault
                                && !stalled
                                && !escalationAvailable;

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

    /// <summary>
    /// D3: stamp a model-ESCALATION request when the retiring attempt's own evidence says the MODEL was the limit —
    /// it claimed success against a check that failed, or it produced real work the check still rejected. The node
    /// has no database, so it only names WHY and the tier FLOOR (the prior attempt's own model, carried on the
    /// payload so a chain of respawns escalates monotonically instead of re-deriving the same first step); the
    /// EXECUTOR resolves the pick against the team's credentialed pool at launch. Infra faults (a grader that never
    /// ran, a broken environment, a mangled gateway wire) are excluded by the shared trigger — a pricier model
    /// cannot move any of those verdicts. Returns the task unchanged when this isn't a respawn, or the prior
    /// attempt proved nothing about its model (the common case, byte-identical to before).
    /// </summary>
    private static AgentTask ApplyRespawnEscalation(AgentTask task, JsonElement? priorAttemptPayload)
    {
        if (priorAttemptPayload is not { } payload) return task;

        if (ReadOptionalString(payload, "proposedEscalation", "to") is not { Length: > 0 }) return task;

        if (ReadOptionalString(payload, "proposedEscalation", "reason") is not { Length: > 0 } reason) return task;

        // Carried as a REQUEST (no `to`) on purpose: the pool can change between attempts — a model disabled, a
        // credential revoked, a stronger one added — so the executor re-resolves against the pool as it is at
        // launch. The FLOOR travels, not the answer.
        return task with { Escalation = new AgentModelEscalation { Reason = reason, From = ReadOptionalString(payload, "proposedEscalation", "from") } };
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

    /// <summary>Read the optional <c>baseRefRecoverySha</c> input — the confirmed commit the SESSION baseRef pointed at when recorded (P4 session branch recovery): the clone's detach anchor when the prior branch has vanished. Absent / blank / non-string → null (recovery unavailable — the soft fallback degrades to the default branch).</summary>
    private static string? ReadBaseRefRecoverySha(NodeRunContext context) =>
        context.Inputs.TryGetValue("baseRefRecoverySha", out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

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

    /// <summary>One optional string inside a nested object of the payload (e.g. <c>proposedEscalation.to</c>) — null when either level is absent, null, or not the expected kind. Tolerant by design: this reads an informational hint, and a malformed one must degrade to "no hint", never fail the node.</summary>
    private static string? ReadOptionalString(JsonElement bag, string objectKey, string key) =>
        bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(objectKey, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadOptionalString(nested, key)
            : null;

    private static string? ReadOptionalString(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        var s = ReadString(bag, key);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>Read an optional uuid config field. Absent / empty / unparseable → null — this is optional config, not a safety-critical input, so a malformed value degrades to null rather than failing the node.</summary>
    private static Guid? ReadOptionalGuid(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        Guid.TryParse(ReadOptionalString(bag, key), out var id) ? id : null;

    /// <summary>
    /// Read an optional integer config value — tolerant of a STRING-encoded number ("1") as well as a JSON number.
    /// The editor stores every enum as a string (SchemaForm's {{ref}} unification), so an integer enum like
    /// outputReviewMode arrives as "1"; a Number-only read would drop it and silently revert the field to its default.
    /// </summary>
    internal static int? ReadInt(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        if (!bag.TryGetValue(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    /// <summary>Reads the autonomy tier (case-insensitive); absent / unrecognized → the safe <see cref="AgentAutonomyLevel.Standard"/> default.</summary>
    private static AgentAutonomyLevel ReadAutonomyLevel(IReadOnlyDictionary<string, JsonElement> bag) =>
        Enum.TryParse<AgentAutonomyLevel>(ReadString(bag, "autonomyLevel"), ignoreCase: true, out var level) ? level : AgentAutonomyLevel.Standard;

    /// <summary>Reads the model-authored <c>mode</c> (case-insensitive); absent / unrecognized → <see cref="AgentMode.Unset"/> (today's behaviour, never a throw — mirrors <see cref="ReadAutonomyLevel"/>).</summary>
    private static AgentMode ReadMode(IReadOnlyDictionary<string, JsonElement> bag) =>
        Enum.TryParse<AgentMode>(ReadString(bag, "mode"), ignoreCase: true, out var mode) ? mode : AgentMode.Unset;

    /// <summary>
    /// Resolves permissions over THREE layers, low→high: the mode BASE (the model's intent), the autonomy TIER, then
    /// explicit per-field overrides. <see cref="AgentMode.Research"/> forces the network OFF (analysis reads the
    /// tree it was given, never the internet) and, via <see cref="ResolvePushBranch"/>, publishes nothing;
    /// <see cref="AgentMode.Code"/> / <see cref="AgentMode.Unset"/> use the tier-derived baseline (byte-identical to
    /// before this knob existed). An override applies ONLY when the field is explicitly present, so a tier-only
    /// config inherits cleanly and a legacy network/readOnly config keeps its prior meaning.
    ///
    /// <para><b>Research WRITES to its workspace</b> (the tier's write scope, not a forced ReadOnly). A research /
    /// analysis subtask's objective oracle is a DELIVERABLE-FILE contract — <c>PlannerSchema</c> pairs those kinds
    /// with ArtifactPresent / LlmJudge / CitationsResolve / ArtifactSchema over "the repo-relative deliverable file
    /// paths" — so an agent that cannot write cannot produce the report it is graded on, and every such item flunked
    /// a contract it was never able to satisfy. Read-only was a plausible-sounding default, not a safety boundary:
    /// the boundary is that nothing research writes is PUBLISHED (push stays false) and it has no network.</para>
    ///
    /// <para>Clamp-safe, unchanged: every mode's base is <see cref="AgentAutonomyPolicy.Derive"/> of the
    /// (already-clamped) tier, so a Standard/Confined ceiling still caps the write scope — a mode never raises the
    /// tier, and Research can only ever LOWER what the tier granted (network off, no publish).</para>
    /// </summary>
    private static AgentPermissions ResolvePermissions(IReadOnlyDictionary<string, JsonElement> bag, AgentAutonomyLevel autonomy, AgentMode mode)
    {
        var permissions = AgentAutonomyPolicy.Derive(autonomy);

        if (mode == AgentMode.Research) permissions = permissions with { Network = AgentNetworkAccess.Off };

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
    /// <summary>Analysis: no network and no produced branch, but WRITES its deliverables into its own workspace (the tier's write scope) — a research subtask's oracle grades the report files it was asked to write, so a forced read-only made every deliverable contract unsatisfiable. Nothing it writes is published.</summary>
    Research,

    /// <summary>Edits the codebase: workspace write (the tier-derived posture) and publishes its own branch.</summary>
    Code,

    /// <summary>No mode authored — the tier-derived baseline + defer-to-the-flag push (byte-identical to before this knob existed).</summary>
    Unset,
}
