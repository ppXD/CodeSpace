using System.Text.Json;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// OPENS a pull/merge request between two existing branches via
/// <see cref="IPullRequestService.OpenPullRequestAsync"/> — the creation half of the Git write surface.
/// Inputs: <c>repositoryId</c>, <c>title</c>, <c>sourceBranch</c>, <c>targetBranch</c>, optional
/// <c>body</c> / <c>draft</c> / <c>actAsUserId</c>. Outputs the created <c>number</c>, <c>url</c>, and
/// <c>state</c>.
///
/// Wire the branches from upstream (e.g. a release workflow opening a PR from <c>release/x</c> into
/// <c>main</c>). The branches must already exist on the remote — this opens the request, it does not push
/// code. The provider translates the neutral input to its own API (GitHub PR; GitLab MR).
/// </summary>
public sealed class GitOpenPullRequestNode : INodeRuntime
{
    private readonly IPullRequestService _prService;

    public GitOpenPullRequestNode(IPullRequestService prService)
    {
        _prService = prService;
    }

    public string TypeKey => "git.open_pr";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Open pull request",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "git-pull-request",
        Description = "Opens a pull/merge request between two existing branches on a repository.",
        // Opening a PR is a permanent externally-visible side effect — the engine refuses auto-resume on
        // abandoned runs so we don't open a duplicate.
        IsSideEffecting = true,
        // Synchronous + standalone → exposable as an agent tool (a destructive, approval-gated one): it flows
        // through AgentToolGate exactly like agent.run_command, which can already open a PR via the shell — this
        // is the safer typed alternative, not a wider attack surface. INERT until the MCP endpoint is enabled.
        // Attribution: the ActsAsUser per-user attribution below is an engine-respond-path feature, gated by
        // ActorIdentityRequirementGate (the responder must PROVE they are that user). No such gate runs on the
        // synthetic tool path, so NodeAgentTool STRIPS the actAsUserId actor key from model input there — a
        // tool-invoked open acts as the repo CONNECTION credential, never a specific user. The ledger's
        // agent_run_id provides traceability.
        IsAgentToolEligible = true,
        // Acts AS the actor's own identity (Model B), same generic gating as git.pr_review: when this node
        // sits downstream of an interactive wait feeding actAsUserId, the engine gates the responder's
        // linked identity — no chat/engine changes for future act-as-user nodes.
        ActsAsUser = new ActsAsUserSpec { ActorInputKey = "actAsUserId", ProviderInputKey = "repositoryId", ProviderSource = ActorProviderSource.Repository, CapabilityType = typeof(IPullRequestWriteCapability) },
        // x-intent: the always-first plain-language summary the inspector composes from the live inputs
        // (repositoryId resolves to the repo NAME; a bound {{ref}} shows as a chip; unset fields show the
        // x-intentPlaceholders prompt). Display-only metadata — the engine never reads it. The fields live
        // in InputSchema; the composer reads the merged {config, inputs} scope.
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {},
              "x-intent": "Open a {draft?draft }pull request titled \"{title}\" from {sourceBranch} into {targetBranch} on {repositoryId}.",
              "x-intentPlaceholders": {
                "title": "an untitled PR",
                "sourceBranch": "a source branch",
                "targetBranch": "a target branch",
                "repositoryId": "a repository"
              }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository. Pick one, or switch to Expression to bind it from the trigger (e.g. {{trigger.repositoryId}})." },
                "title": { "type": "string", "description": "The pull/merge request title." },
                "sourceBranch": { "type": "string", "description": "The branch with the changes (head / source). Must already exist on the remote." },
                "targetBranch": { "type": "string", "description": "The branch to merge into (base / target). Must already exist on the remote." },
                "body": { "type": "string", "x-long": true, "description": "Optional markdown description. Supports {{ }} references." },
                "draft": { "type": "boolean", "description": "Open as a draft / work-in-progress when the provider supports it." },
                "actAsUserId": { "type": "string", "format": "uuid", "x-selector": "user", "description": "Open the PR AS this CodeSpace user's own linked GitHub/GitLab identity, so it's authored by that person. Omit to use the repository's connection credential." }
              },
              "required": ["repositoryId","title","sourceBranch","targetBranch"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "number": { "type": "integer" },
                "url": { "type": ["string","null"] },
                "state": { "type": "string" }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadRepositoryId(context, out var repoId)) return NodeResult.Fail("Input 'repositoryId' missing or not a uuid.");
        if (!TryReadNonEmpty(context, "title", out var title)) return NodeResult.Fail("Input 'title' is required.");
        if (!TryReadNonEmpty(context, "sourceBranch", out var sourceBranch)) return NodeResult.Fail("Input 'sourceBranch' is required.");
        if (!TryReadNonEmpty(context, "targetBranch", out var targetBranch)) return NodeResult.Fail("Input 'targetBranch' is required.");
        if (!NodeScopeReader.TryReadTeamId(context, out var teamId)) return NodeResult.Fail("This run has no team context, so a repository can't be resolved.");

        var body = TryReadNonEmpty(context, "body", out var b) ? b : null;
        var draft = TryReadBool(context, "draft");
        var actAsUserId = TryReadActAsUserId(context, out var a) ? a : (Guid?)null;

        var input = new OpenPullRequestInput { Title = title, SourceBranch = sourceBranch, TargetBranch = targetBranch, Body = body, Draft = draft };

        RemotePullRequest pr;
        try
        {
            pr = await context.Observability.TraceExternalCallAsync(
                target: $"git.open_pr:{repoId}",
                method: "open_pull_request",
                requestPayload: JsonSerializer.SerializeToElement(new { repository_id = repoId, title, source_branch = sourceBranch, target_branch = targetBranch, draft, body_chars = body?.Length ?? 0, act_as_user_id = actAsUserId }),
                action: ct => _prService.OpenPullRequestAsync(repoId, teamId, input, actAsUserId, ct),
                completionExtractor: result => new ExternalCallCompletion
                {
                    ResponsePayload = JsonSerializer.SerializeToElement(new { number = result.Number, url = result.WebUrl, state = result.State.ToString() })
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        // The service throws InvalidOperationException for bad input (missing title / same branch) — surface
        // it as a clean node failure. Provider scope / permission / validation failures map to actionable text.
        catch (InvalidOperationException ex) { return NodeResult.Fail(ex.Message); }
        catch (ProviderInsufficientScopeException ex) { return NodeResult.Fail(DescribeWriteFailure(ex)); }
        catch (ProviderApiException ex) { return NodeResult.Fail(DescribeWriteFailure(ex)); }

        context.Logger.LogInformation("Opened PR #{Num} on repo {RepoId} ({Source} -> {Target})", pr.Number, repoId, sourceBranch, targetBranch);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["number"] = JsonSerializer.SerializeToElement(pr.Number),
            ["url"] = JsonSerializer.SerializeToElement(pr.WebUrl),
            ["state"] = JsonSerializer.SerializeToElement(pr.State.ToString())
        };

        return NodeResult.Ok(outputs);
    }

    /// <summary>A clean, actionable message for a typed provider write failure — surfaced as the node's failure (and on its <c>error</c> handle). Scope gap vs no-permission (403) vs not-found (404) vs validation (422) each get their own remediation.</summary>
    private static string DescribeWriteFailure(Exception ex) => ex switch
    {
        ProviderInsufficientScopeException scope =>
            $"Couldn't open the pull request: your {scope.ProviderKind} token is missing the {string.Join(", ", scope.MissingScopes)} scope. Re-link your identity with that scope, then try again.",
        ProviderApiException { StatusCode: 403 } api =>
            $"Couldn't open the pull request: {api.ProviderKind} refused it — your identity may not have write permission on this repository. Get access, then try again.",
        ProviderApiException { StatusCode: 404 } api =>
            $"Couldn't open the pull request: {api.ProviderKind} couldn't find the repository or one of the branches.",
        ProviderApiException { StatusCode: 422 } api =>
            $"Couldn't open the pull request: {api.ProviderKind} rejected it — the branches may be identical, a PR may already exist for them, or a branch may not exist on the remote.",
        ProviderApiException api =>
            $"Couldn't open the pull request: {api.ProviderKind} returned HTTP {api.StatusCode}.",
        _ => $"Couldn't open the pull request: {ex.Message}",
    };

    private static bool TryReadRepositoryId(NodeRunContext context, out Guid repoId)
    {
        repoId = Guid.Empty;
        if (!context.Inputs.TryGetValue("repositoryId", out var value)) return false;
        if (value.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(value.GetString(), out repoId);
    }

    private static bool TryReadNonEmpty(NodeRunContext context, string key, out string text)
    {
        text = "";
        if (!context.Inputs.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String) return false;
        text = (value.GetString() ?? "").Trim();
        return text.Length > 0;
    }

    private static bool TryReadBool(NodeRunContext context, string key) =>
        context.Inputs.TryGetValue(key, out var value) && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed));

    /// <summary>Optional actor: a uuid string → open AS that user's linked identity (Model B). Absent / blank ⇒ null ⇒ connection credential.</summary>
    private static bool TryReadActAsUserId(NodeRunContext context, out Guid actAsUserId)
    {
        actAsUserId = Guid.Empty;
        if (!context.Inputs.TryGetValue("actAsUserId", out var value) || value.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(value.GetString(), out actAsUserId);
    }
}
