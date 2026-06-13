using System.Text.Json;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Lists the pull/merge requests on a repository via the existing <see cref="IPullRequestService"/>,
/// optionally filtered by lifecycle state. Inputs: <c>repositoryId</c> (required) + optional
/// <c>state</c> / <c>page</c> / <c>perPage</c>. Outputs: <c>pullRequests[]</c> and <c>count</c>.
///
/// This is the DISCOVERY node — it's what lets a workflow enumerate PRs and then fan out over them
/// (e.g. "review every open PR": list → loop → fetch diff → agent → post review). Read-only; reuses
/// the same catalog capability the Pulls tab uses, so it's provider-agnostic (GitHub + GitLab).
/// </summary>
public sealed class GitListPullRequestsNode : INodeRuntime
{
    private const int DefaultPage = 1;
    private const int DefaultPerPage = 30;

    private readonly IPullRequestService _prService;

    public GitListPullRequestsNode(IPullRequestService prService)
    {
        _prService = prService;
    }

    public string TypeKey => "git.list_prs";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "List pull requests",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "git-pull-request",
        Description = "Lists the pull/merge requests on a repository, optionally filtered by state.",
        // Synchronous + read-only → exposable as an agent tool (a non-destructive one).
        IsAgentToolEligible = true,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository. Pick one, or switch to Expression to bind from the trigger (e.g. {{trigger.repositoryId}})." },
                "state": { "type": "string", "enum": ["Open","Draft","Merged","Closed"], "description": "Only list requests in this state. Leave empty to list all." },
                "page": { "type": "integer", "minimum": 1, "description": "Page of results (default 1)." },
                "perPage": { "type": "integer", "minimum": 1, "maximum": 100, "description": "Results per page (default 30, max 100)." }
              },
              "required": ["repositoryId"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "pullRequests": { "type": "array" },
                "count": { "type": "integer" }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadRepositoryId(context, out var repoId)) return NodeResult.Fail("Input 'repositoryId' missing or not a uuid.");
        if (!TryReadState(context, out var state)) return NodeResult.Fail("Input 'state' must be one of Open, Draft, Merged, Closed.");
        if (!NodeScopeReader.TryReadTeamId(context, out var teamId)) return NodeResult.Fail("This run has no team context, so a repository can't be resolved.");

        var page = ReadPositiveInt(context, "page", DefaultPage);
        var perPage = ReadPositiveInt(context, "perPage", DefaultPerPage);

        var pullRequests = await context.Observability.TraceExternalCallAsync(
            target: $"git.list_prs:{repoId}",
            method: "list_pull_requests",
            requestPayload: JsonSerializer.SerializeToElement(new { repository_id = repoId, state = state?.ToString(), page, per_page = perPage }),
            action: ct => _prService.ListAsync(repoId, teamId, state, page, perPage, ct),
            completionExtractor: result => new ExternalCallCompletion
            {
                ResponsePayload = JsonSerializer.SerializeToElement(new { count = result.Count })
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["pullRequests"] = JsonSerializer.SerializeToElement(pullRequests),
            ["count"] = JsonSerializer.SerializeToElement(pullRequests.Count)
        };

        context.Logger.LogInformation("Listed {Count} pull request(s) for repo {RepoId} (state {State})", pullRequests.Count, repoId, state?.ToString() ?? "any");

        return NodeResult.Ok(outputs);
    }

    private static bool TryReadRepositoryId(NodeRunContext context, out Guid repoId)
    {
        repoId = Guid.Empty;

        if (!context.Inputs.TryGetValue("repositoryId", out var value)) return false;
        if (value.ValueKind != JsonValueKind.String) return false;

        return Guid.TryParse(value.GetString(), out repoId);
    }

    /// <summary>Read the optional <c>state</c> filter: absent/empty → null (all states, true); a recognised name → that state (true); anything else → false (a clear validation failure, never a silent "all").</summary>
    private static bool TryReadState(NodeRunContext context, out PullRequestState? state)
    {
        state = null;

        if (!context.Inputs.TryGetValue("state", out var value) || value.ValueKind != JsonValueKind.String) return true;

        var raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!Enum.TryParse<PullRequestState>(raw, ignoreCase: true, out var parsed)) return false;

        state = parsed;
        return true;
    }

    /// <summary>Read an optional positive-int input, falling back to <paramref name="fallback"/> when absent or non-positive.</summary>
    private static int ReadPositiveInt(NodeRunContext context, string key, int fallback)
    {
        if (context.Inputs.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n > 0)
            return n;

        return fallback;
    }
}
