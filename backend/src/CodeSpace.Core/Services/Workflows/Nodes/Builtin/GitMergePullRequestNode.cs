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
/// MERGES an open pull/merge request via <see cref="IPullRequestService.MergePullRequestAsync"/> — the
/// completion half of the Git write surface (open → review → merge). Inputs: <c>repositoryId</c>,
/// <c>number</c>, optional <c>method</c> (merge / squash / rebase) / <c>commitTitle</c> /
/// <c>commitMessage</c> / <c>deleteSourceBranch</c> / <c>actAsUserId</c>. Outputs <c>merged</c>, <c>sha</c>,
/// <c>message</c>.
///
/// Wire <c>number</c> from upstream (e.g. an auto-merge-after-approval workflow). The provider translates
/// the neutral input to its own API (GitHub merge; GitLab accept).
/// </summary>
public sealed class GitMergePullRequestNode : INodeRuntime
{
    private readonly IPullRequestService _prService;

    public GitMergePullRequestNode(IPullRequestService prService)
    {
        _prService = prService;
    }

    public string TypeKey => "git.merge_pr";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Merge pull request",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "git-merge",
        Description = "Merges an open pull/merge request (merge, squash, or rebase).",
        // Merging integrates code — a permanent side effect. The engine refuses auto-resume on abandoned runs
        // so we never merge twice.
        IsSideEffecting = true,
        // Synchronous + standalone → exposable as an agent tool. But unlike the reversible writes (open_pr /
        // post_pr_comment / pr_review) this is IRREVERSIBLE, so it can NEVER auto-run: AlwaysRequiresApproval forces
        // AgentToolGate to escalate even Unleashed's Allow → RequireApproval. Every merge, at every tier, goes through
        // the D2 human-approval card + the C exactly-once ledger. INERT until the MCP endpoint is enabled.
        // Attribution: the ActsAsUser per-user attribution below is an engine-respond-path feature, gated by
        // ActorIdentityRequirementGate (the responder must PROVE they are that user). No such gate runs on the
        // synthetic tool path, so NodeAgentTool STRIPS the actAsUserId actor key from model input there — a
        // tool-invoked merge acts as the repo CONNECTION credential, never a specific user. The ledger's
        // agent_run_id provides traceability.
        IsAgentToolEligible = true,
        AlwaysRequiresApproval = true,
        ActsAsUser = new ActsAsUserSpec { ActorInputKey = "actAsUserId", ProviderInputKey = "repositoryId", ProviderSource = ActorProviderSource.Repository, CapabilityType = typeof(IPullRequestWriteCapability) },
        // x-intent: always-first plain-language summary composed from the live inputs (repositoryId → repo
        // NAME; a bound {{ref}} → chip; unset → the x-intentPlaceholders prompt). Display-only metadata.
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {},
              "x-intent": "Merge pull request #{number} on {repositoryId}.",
              "x-intentPlaceholders": { "number": "a PR number", "repositoryId": "a repository" }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository. Pick one, or switch to Expression to bind it from the trigger (e.g. {{trigger.repositoryId}})." },
                "number": { "type": "integer", "description": "The pull/merge request number to merge." },
                "method": { "type": "string", "enum": ["merge","squash","rebase"], "x-control": "segmented", "x-enumLabels": { "merge": "Merge commit", "squash": "Squash", "rebase": "Rebase" }, "description": "How to integrate the commits. Default: merge commit." },
                "commitTitle": { "type": "string", "description": "Optional merge-commit title (squash/merge). Provider default when empty." },
                "commitMessage": { "type": "string", "x-long": true, "description": "Optional merge-commit message body." },
                "deleteSourceBranch": { "type": "boolean", "description": "Delete the source branch after a successful merge." },
                "actAsUserId": { "type": "string", "format": "uuid", "x-selector": "user", "description": "Merge AS this CodeSpace user's own linked GitHub/GitLab identity. Omit to use the repository's connection credential." }
              },
              "required": ["repositoryId","number"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "merged": { "type": "boolean" },
                "sha": { "type": ["string","null"] },
                "message": { "type": ["string","null"] }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadRepositoryId(context, out var repoId)) return NodeResult.Fail("Input 'repositoryId' missing or not a uuid.");
        if (!TryReadNumber(context, out var number)) return NodeResult.Fail("Input 'number' missing or not an integer.");
        if (!TryReadMethod(context, out var method)) return NodeResult.Fail("Input 'method' must be one of: merge, squash, rebase.");
        if (!NodeScopeReader.TryReadTeamId(context, out var teamId)) return NodeResult.Fail("This run has no team context, so a repository can't be resolved.");

        var input = new MergePullRequestInput
        {
            Method = method,
            CommitTitle = TryReadNonEmpty(context, "commitTitle", out var t) ? t : null,
            CommitMessage = TryReadNonEmpty(context, "commitMessage", out var m) ? m : null,
            DeleteSourceBranch = TryReadBool(context, "deleteSourceBranch"),
        };
        var actAsUserId = TryReadActAsUserId(context, out var a) ? a : (Guid?)null;

        RemotePullRequestMergeResult result;
        try
        {
            result = await context.Observability.TraceExternalCallAsync(
                target: $"git.merge_pr:{repoId}:{number}",
                method: "merge_pull_request",
                requestPayload: JsonSerializer.SerializeToElement(new { repository_id = repoId, pull_request_number = number, merge_method = method.ToString(), delete_source_branch = input.DeleteSourceBranch, act_as_user_id = actAsUserId }),
                action: ct => _prService.MergePullRequestAsync(repoId, teamId, number, input, actAsUserId, ct),
                completionExtractor: r => new ExternalCallCompletion
                {
                    ResponsePayload = JsonSerializer.SerializeToElement(new { merged = r.Merged, sha = r.Sha })
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderInsufficientScopeException ex) { return NodeResult.Fail(DescribeMergeFailure(ex, number)); }
        catch (ProviderApiException ex) { return NodeResult.Fail(DescribeMergeFailure(ex, number)); }

        context.Logger.LogInformation("Merged PR #{Num} on repo {RepoId} (merged={Merged}, method {Method})", number, repoId, result.Merged, method);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["merged"] = JsonSerializer.SerializeToElement(result.Merged),
            ["sha"] = JsonSerializer.SerializeToElement(result.Sha),
            ["message"] = JsonSerializer.SerializeToElement(result.Message)
        };

        return NodeResult.Ok(outputs);
    }

    private static string DescribeMergeFailure(Exception ex, int number) => ex switch
    {
        ProviderInsufficientScopeException scope =>
            $"Couldn't merge PR #{number}: your {scope.ProviderKind} token is missing the {string.Join(", ", scope.MissingScopes)} scope. Re-link your identity with that scope, then try again.",
        ProviderApiException { StatusCode: 403 } api =>
            $"Couldn't merge PR #{number}: {api.ProviderKind} refused it — your identity may not have merge permission on this repository.",
        ProviderApiException { StatusCode: 404 } api =>
            $"Couldn't merge PR #{number}: {api.ProviderKind} couldn't find it, or your identity can't access this repository.",
        ProviderApiException { StatusCode: 405 } api =>
            $"Couldn't merge PR #{number}: {api.ProviderKind} reports it isn't mergeable — it may have conflicts, failing required checks, or be already merged/closed.",
        ProviderApiException { StatusCode: 409 } api =>
            $"Couldn't merge PR #{number}: {api.ProviderKind} reports a conflict — the head branch may have moved. Rebase/update it, then try again.",
        ProviderApiException { StatusCode: 422 } api =>
            $"Couldn't merge PR #{number}: {api.ProviderKind} rejected it — it may not be mergeable (conflicts, failing checks, or already merged).",
        ProviderApiException api =>
            $"Couldn't merge PR #{number}: {api.ProviderKind} returned HTTP {api.StatusCode}.",
        _ => $"Couldn't merge PR #{number}: {ex.Message}",
    };

    private static bool TryReadRepositoryId(NodeRunContext context, out Guid repoId)
    {
        repoId = Guid.Empty;
        if (!context.Inputs.TryGetValue("repositoryId", out var value)) return false;
        if (value.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(value.GetString(), out repoId);
    }

    private static bool TryReadNumber(NodeRunContext context, out int number)
    {
        number = 0;
        if (!context.Inputs.TryGetValue("number", out var value)) return false;
        if (value.ValueKind != JsonValueKind.Number) return false;
        return value.TryGetInt32(out number);
    }

    /// <summary>Read the optional merge method: absent/empty → Merge (true); a recognised name → that method (true); anything else → false (a clear validation failure).</summary>
    private static bool TryReadMethod(NodeRunContext context, out PullRequestMergeMethod method)
    {
        method = PullRequestMergeMethod.Merge;
        if (!context.Inputs.TryGetValue("method", out var value) || value.ValueKind != JsonValueKind.String) return true;

        var raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return true;

        return Enum.TryParse(raw, ignoreCase: true, out method) && Enum.IsDefined(method);
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

    private static bool TryReadActAsUserId(NodeRunContext context, out Guid actAsUserId)
    {
        actAsUserId = Guid.Empty;
        if (!context.Inputs.TryGetValue("actAsUserId", out var value) || value.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(value.GetString(), out actAsUserId);
    }
}
