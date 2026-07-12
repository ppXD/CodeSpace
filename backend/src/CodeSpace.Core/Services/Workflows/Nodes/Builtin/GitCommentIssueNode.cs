using System.Text.Json;
using CodeSpace.Core.Services.Issues;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Posts a comment on an issue via <see cref="IIssueService.CommentAsync"/>. Inputs: <c>repositoryId</c>,
/// <c>number</c>, <c>body</c> (required), optional <c>actAsUserId</c>. Outputs the persisted <c>commentId</c>
/// + <c>webUrl</c> (GitHub returns a URL; GitLab notes have none → null).
///
/// Distinct from <c>git.post_pr_comment</c>: that targets a pull/merge request; this targets an issue
/// (GitHub: issue comment; GitLab: issue note via a different client).
/// </summary>
public sealed class GitCommentIssueNode : INodeRuntime
{
    private readonly IIssueService _issueService;

    public GitCommentIssueNode(IIssueService issueService)
    {
        _issueService = issueService;
    }

    public string TypeKey => "git.comment_issue";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Comment on issue",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "message-square",
        Description = "Posts a comment on an issue.",
        // Posting a comment is a permanent externally-visible side effect — the engine refuses auto-resume
        // on abandoned runs so we don't double-post.
        IsSideEffecting = true,
        ActsAsUser = new ActsAsUserSpec { ActorInputKey = "actAsUserId", ProviderInputKey = "repositoryId", ProviderSource = ActorProviderSource.Repository, CapabilityType = typeof(IIssueWriteCapability) },
        // x-intent: always-first plain-language summary composed from the live inputs (repositoryId → repo
        // NAME; a bound {{ref}} → chip; unset → the x-intentPlaceholders prompt). Display-only metadata.
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {},
              "x-intent": "Comment on issue #{number} on {repositoryId}.",
              "x-intentPlaceholders": { "number": "an issue number", "repositoryId": "a repository" }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository. Pick one, or switch to Expression to bind it from the trigger (e.g. {{trigger.repositoryId}})." },
                "number": { "type": "integer", "description": "The issue number." },
                "body": { "type": "string", "minLength": 1, "x-long": true, "description": "Markdown comment body. Supports {{ }} references." },
                "actAsUserId": { "type": "string", "format": "uuid", "x-selector": "actorUser", "description": "Comment AS this CodeSpace user's own linked GitHub/GitLab identity. Omit to use the repository's connection credential." }
              },
              "required": ["repositoryId","number","body"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "commentId": { "type": "string" },
                "webUrl": { "type": ["string","null"] }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadRepositoryId(context, out var repoId)) return NodeResult.Fail("Input 'repositoryId' missing or not a uuid.");
        if (!NodeScopeReader.TryReadTeamId(context, out var teamId)) return NodeResult.Fail("This run has no team context, so a repository can't be resolved.");
        if (!TryReadNumber(context, out var number)) return NodeResult.Fail("Input 'number' missing or not an integer.");
        if (!TryReadBody(context, out var body)) return NodeResult.Fail("Input 'body' missing or empty.");

        var actAsUserId = TryReadActAsUserId(context, out var a) ? a : (Guid?)null;

        RemoteIssueComment comment;
        try
        {
            comment = await context.Observability.TraceExternalCallAsync(
                target: $"git.comment_issue:{repoId}:{number}",
                method: "comment_issue",
                requestPayload: JsonSerializer.SerializeToElement(new { repository_id = repoId, issue_number = number, body_chars = body.Length, act_as_user_id = actAsUserId }),
                action: ct => _issueService.CommentAsync(repoId, teamId, number, body, actAsUserId, ct),
                completionExtractor: result => new ExternalCallCompletion
                {
                    ResponsePayload = JsonSerializer.SerializeToElement(new { comment_id = result.ExternalId, web_url = result.WebUrl })
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { return NodeResult.Fail(ex.Message); }
        catch (ProviderInsufficientScopeException ex) { return NodeResult.Fail(DescribeWriteFailure(ex, number)); }
        catch (ProviderApiException ex) { return NodeResult.Fail(DescribeWriteFailure(ex, number)); }

        context.Logger.LogInformation("Posted comment {CommentId} on repo {RepoId} issue #{Num}", comment.ExternalId, repoId, number);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["commentId"] = JsonSerializer.SerializeToElement(comment.ExternalId),
            ["webUrl"] = JsonSerializer.SerializeToElement(comment.WebUrl)
        };

        return NodeResult.Ok(outputs);
    }

    private static string DescribeWriteFailure(Exception ex, int number) => ex switch
    {
        ProviderInsufficientScopeException scope =>
            $"Couldn't comment on issue #{number}: your {scope.ProviderKind} token is missing the {string.Join(", ", scope.MissingScopes)} scope. Re-link your identity with that scope, then try again.",
        ProviderApiException { StatusCode: 403 } api =>
            $"Couldn't comment on issue #{number}: {api.ProviderKind} refused it — your identity may not have permission to comment on this repository.",
        ProviderApiException { StatusCode: 404 } api =>
            $"Couldn't comment on issue #{number}: {api.ProviderKind} couldn't find the issue, or your identity can't access this repository.",
        ProviderApiException { StatusCode: 410 } api =>
            $"Couldn't comment on issue #{number}: {api.ProviderKind} reports issues are disabled on this repository.",
        ProviderApiException api =>
            $"Couldn't comment on issue #{number}: {api.ProviderKind} returned HTTP {api.StatusCode}.",
        _ => $"Couldn't comment on issue #{number}: {ex.Message}",
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

    private static bool TryReadBody(NodeRunContext context, out string body)
    {
        body = "";
        if (!context.Inputs.TryGetValue("body", out var value)) return false;
        if (value.ValueKind != JsonValueKind.String) return false;
        body = value.GetString() ?? "";
        return body.Length > 0;
    }

    private static bool TryReadActAsUserId(NodeRunContext context, out Guid actAsUserId)
    {
        actAsUserId = Guid.Empty;
        if (!context.Inputs.TryGetValue("actAsUserId", out var value) || value.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(value.GetString(), out actAsUserId);
    }
}
