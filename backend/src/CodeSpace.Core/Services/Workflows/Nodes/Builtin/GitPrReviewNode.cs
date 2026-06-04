using System.Text.Json;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Submits a REVIEW VERDICT (approve / request-changes / comment) back to a PR/MR via
/// <see cref="IPullRequestService.SubmitReviewAsync"/> — the write-back that closes the review loop.
/// Inputs: <c>repositoryId</c>, <c>number</c>, <c>verdict</c>, optional <c>body</c>. Outputs the
/// submitted <c>verdict</c> + the review <c>url</c>.
///
/// Wire <c>verdict</c> from an upstream decision — e.g. a chat card click surfaced as
/// <c>{{nodes.&lt;wait&gt;.outputs.action}}</c> — and <c>repositoryId</c> / <c>number</c> from the
/// trigger payload. The provider translates the neutral verdict to its own API (native review on
/// GitHub; a labeled note on GitLab).
/// </summary>
public sealed class GitPrReviewNode : INodeRuntime
{
    private readonly IPullRequestService _prService;

    public GitPrReviewNode(IPullRequestService prService)
    {
        _prService = prService;
    }

    public string TypeKey => "git.pr_review";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Submit PR review",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "git-pull-request",
        Description = "Submits a review verdict (approve / request-changes / comment) back to a pull/merge request.",
        // Submitting a review is a permanent externally-visible side effect — the engine refuses
        // auto-resume on abandoned runs so we don't double-submit.
        IsSideEffecting = true,
        // Acts AS the actor's own identity (Model B). Declaring this lets the engine generically gate
        // the responder's linked identity when this node sits downstream of an interactive wait whose
        // responder feeds actAsUserId — no chat/engine changes needed for future act-as-user nodes.
        ActsAsUser = new ActsAsUserSpec { ActorInputKey = "actAsUserId", ProviderInputKey = "repositoryId", ProviderSource = ActorProviderSource.Repository, CapabilityType = typeof(IPullRequestReviewCapability) },
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository. Pick one, or switch to Expression to bind it from the trigger (e.g. {{trigger.repositoryId}})." },
                "number": { "type": "integer", "description": "The pull/merge request number." },
                "verdict": { "type": "string", "enum": ["approve", "request_changes", "comment"], "x-enumLabels": { "approve": "Approve", "request_changes": "Request changes", "comment": "Comment" }, "description": "The verdict to submit. Wire {{nodes.<wait>.outputs.action}} from a chat card click." },
                "body": { "type": "string", "description": "Review body — required for request_changes / comment, optional for approve. Supports {{ }} references." },
                "actAsUserId": { "type": "string", "format": "uuid", "description": "Submit AS this CodeSpace user's own linked provider identity (Model B). Wire {{nodes.<wait>.outputs.by}} so the review is authored by the person who clicked. Omit to use the repository's connection credential." }
              },
              "required": ["repositoryId","number","verdict"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "verdict": { "type": "string" },
                "url": { "type": ["string","null"] }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadRepositoryId(context, out var repoId)) return NodeResult.Fail("Input 'repositoryId' missing or not a uuid.");
        if (!TryReadNumber(context, out var number)) return NodeResult.Fail("Input 'number' missing or not an integer.");
        if (!TryReadVerdict(context, out var verdict)) return NodeResult.Fail("Input 'verdict' must be one of: approve, request_changes, comment.");

        var body = TryReadBody(context, out var b) ? b : null;
        var actAsUserId = TryReadActAsUserId(context, out var a) ? a : (Guid?)null;

        // Trace the side-effecting Git API call. The body is summarised (length only) to keep the
        // ledger small; the service enforces the body-required-for-comment/request-changes rule and
        // throws on failure — the engine records the node failure with its message. actAsUserId, when
        // wired, makes the service authenticate as that user's own linked identity (Model B).
        RemotePullRequestReview review;
        try
        {
            review = await context.Observability.TraceExternalCallAsync(
                target: $"git.submit_review:{repoId}:{number}",
                method: "submit_review",
                requestPayload: JsonSerializer.SerializeToElement(new { repository_id = repoId, pull_request_number = number, verdict = verdict.ToString(), body_chars = body?.Length ?? 0, act_as_user_id = actAsUserId }),
                action: ct => _prService.SubmitReviewAsync(repoId, number, verdict, body, actAsUserId, ct),
                completionExtractor: result => new ExternalCallCompletion
                {
                    ResponsePayload = JsonSerializer.SerializeToElement(new { verdict = result.Verdict.ToString(), url = result.WebUrl })
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        // A provider scope / permission / not-found failure isn't an engine bug — turn the typed
        // exception into a clean, actionable node failure so the run trace AND a chat.post_message
        // wired to this node's `error` handle tell the clicker WHY the review didn't land, instead
        // of leaking a raw SDK string. (Identity existence is gated up front at respond time → 428;
        // repo-level permission is only knowable here, at write time.)
        catch (ProviderInsufficientScopeException ex) { return NodeResult.Fail(DescribeWriteFailure(ex, number)); }
        catch (ProviderApiException ex) { return NodeResult.Fail(DescribeWriteFailure(ex, number)); }

        context.Logger.LogInformation("Submitted {Verdict} review for repo {RepoId} PR #{Num}", verdict, repoId, number);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["verdict"] = JsonSerializer.SerializeToElement(review.Verdict.ToString()),
            ["url"] = JsonSerializer.SerializeToElement(review.WebUrl)
        };

        return NodeResult.Ok(outputs);
    }

    /// <summary>
    /// A clean, actionable message for a typed provider write failure — surfaced as the node's
    /// failure (and on its <c>error</c> handle), so a chat.post_message can tell the clicker WHY
    /// their review didn't land instead of leaking a raw SDK string. Scope gap vs no-permission
    /// (403) vs not-found (404) each get their own remediation.
    /// </summary>
    private static string DescribeWriteFailure(Exception ex, int number) => ex switch
    {
        ProviderInsufficientScopeException scope =>
            $"Couldn't submit the review: your {scope.ProviderKind} token is missing the {string.Join(", ", scope.MissingScopes)} scope. Re-link your identity with that scope, then try again.",
        ProviderApiException { StatusCode: 403 } api =>
            $"Couldn't submit the review to PR #{number}: {api.ProviderKind} refused it — your identity may not have review/write permission on this repository. Get access, then try again.",
        ProviderApiException { StatusCode: 404 } api =>
            $"Couldn't submit the review: {api.ProviderKind} couldn't find PR #{number}, or your identity can't access this repository.",
        ProviderApiException api =>
            $"Couldn't submit the review to PR #{number}: {api.ProviderKind} returned HTTP {api.StatusCode}.",
        _ => $"Couldn't submit the review to PR #{number}: {ex.Message}",
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

    /// <summary>Parse the verdict string, tolerant of the snake_case wire value (request_changes) and casing.</summary>
    private static bool TryReadVerdict(NodeRunContext context, out PullRequestReviewVerdict verdict)
    {
        verdict = default;
        if (!context.Inputs.TryGetValue("verdict", out var value) || value.ValueKind != JsonValueKind.String) return false;

        var raw = (value.GetString() ?? "").Replace("_", "");
        return Enum.TryParse(raw, ignoreCase: true, out verdict) && Enum.IsDefined(verdict);
    }

    private static bool TryReadBody(NodeRunContext context, out string body)
    {
        body = "";
        if (!context.Inputs.TryGetValue("body", out var value) || value.ValueKind != JsonValueKind.String) return false;
        body = value.GetString() ?? "";
        return body.Length > 0;
    }

    /// <summary>Optional actor: a uuid string → submit AS that user's linked identity (Model B). Absent / blank ⇒ null ⇒ connection credential.</summary>
    private static bool TryReadActAsUserId(NodeRunContext context, out Guid actAsUserId)
    {
        actAsUserId = Guid.Empty;
        if (!context.Inputs.TryGetValue("actAsUserId", out var value) || value.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(value.GetString(), out actAsUserId);
    }
}
