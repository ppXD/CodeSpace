using System.Text.Json;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Posts a comment on a PR/MR via <see cref="IPullRequestService.PostCommentAsync"/>. Inputs:
/// <c>repositoryId</c>, <c>number</c>, <c>body</c>. Outputs: the persisted comment id + url.
///
/// The AI code review workflow uses this as its terminal effect — drop the model's review
/// onto the PR's conversation thread. Body MUST already be markdown (the providers render
/// it natively); the LLM is expected to format the review with `### Summary`, code fences,
/// task lists, etc.
/// </summary>
public sealed class GitPostPrCommentNode : INodeRuntime
{
    private readonly IPullRequestService _prService;

    public GitPostPrCommentNode(IPullRequestService prService)
    {
        _prService = prService;
    }

    public string TypeKey => "git.post_pr_comment";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Post PR comment",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "message-square",
        Description = "Posts a comment on a pull/merge request.",
        // Posting a comment is a permanent externally-visible side effect. Engine refuses
        // auto-resume on abandoned runs so we don't double-post.
        IsSideEffecting = true,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository. Pick one, or switch to Expression to bind from the trigger (e.g. {{trigger.repositoryId}})." },
                "number": { "type": "integer", "description": "The pull/merge request number." },
                "body": { "type": "string", "minLength": 1, "x-long": true, "description": "Markdown comment body. Supports {{ }} references." }
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
        if (!TryReadNumber(context, out var number)) return NodeResult.Fail("Input 'number' missing or not an integer.");
        if (!TryReadBody(context, out var body)) return NodeResult.Fail("Input 'body' missing or empty.");

        // Trace the side-effecting Git API call. Body content is summarised (length only) to
        // keep the ledger small; the full body lives in node inputs payload already (redacted
        // upstream by the engine if it referenced any secret).
        var comment = await context.Observability.TraceExternalCallAsync(
            target: $"git.post_comment:{repoId}:{number}",
            method: "post_comment",
            requestPayload: JsonSerializer.SerializeToElement(new { repository_id = repoId, pull_request_number = number, body_chars = body.Length }),
            action: ct => _prService.PostCommentAsync(repoId, number, body, ct),
            completionExtractor: result => new ExternalCallCompletion
            {
                ResponsePayload = JsonSerializer.SerializeToElement(new
                {
                    comment_id = result.ExternalId,
                    web_url = result.WebUrl,
                })
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        context.Logger.LogInformation("Posted comment {CommentId} on repo {RepoId} PR #{Num}", comment.ExternalId, repoId, number);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["commentId"] = JsonSerializer.SerializeToElement(comment.ExternalId),
            ["webUrl"] = JsonSerializer.SerializeToElement(comment.WebUrl)
        };

        return NodeResult.Ok(outputs);
    }

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
}
