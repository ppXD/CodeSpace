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
/// CLOSES an issue via <see cref="IIssueService.CloseAsync"/> — the completion half of the issue write
/// surface (create → comment → close). Inputs: <c>repositoryId</c>, <c>number</c> (required), optional
/// <c>actAsUserId</c>. Outputs the issue's <c>number</c>, resulting <c>state</c>, and <c>url</c>.
///
/// Wire <c>number</c> from upstream (e.g. close the issue a workflow opened once its task is done).
/// </summary>
public sealed class GitCloseIssueNode : INodeRuntime
{
    private readonly IIssueService _issueService;

    public GitCloseIssueNode(IIssueService issueService)
    {
        _issueService = issueService;
    }

    public string TypeKey => "git.close_issue";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Close issue",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "circle-dot",
        Description = "Closes an open issue on a repository.",
        // Closing an issue is a permanent externally-visible side effect — the engine refuses auto-resume
        // on abandoned runs. (Closing an already-closed issue is a provider no-op, so re-close is harmless.)
        IsSideEffecting = true,
        ActsAsUser = new ActsAsUserSpec { ActorInputKey = "actAsUserId", ProviderInputKey = "repositoryId", ProviderSource = ActorProviderSource.Repository, CapabilityType = typeof(IIssueWriteCapability) },
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository. Pick one, or switch to Expression to bind it from the trigger (e.g. {{trigger.repositoryId}})." },
                "number": { "type": "integer", "description": "The issue number to close." },
                "actAsUserId": { "type": "string", "format": "uuid", "description": "Close AS this CodeSpace user's own linked GitHub/GitLab identity. Omit to use the repository's connection credential." }
              },
              "required": ["repositoryId","number"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "number": { "type": "integer" },
                "state": { "type": "string" },
                "url": { "type": ["string","null"] }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadRepositoryId(context, out var repoId)) return NodeResult.Fail("Input 'repositoryId' missing or not a uuid.");
        if (!NodeScopeReader.TryReadTeamId(context, out var teamId)) return NodeResult.Fail("This run has no team context, so a repository can't be resolved.");
        if (!TryReadNumber(context, out var number)) return NodeResult.Fail("Input 'number' missing or not an integer.");

        var actAsUserId = TryReadActAsUserId(context, out var a) ? a : (Guid?)null;

        RemoteIssue issue;
        try
        {
            issue = await context.Observability.TraceExternalCallAsync(
                target: $"git.close_issue:{repoId}:{number}",
                method: "close_issue",
                requestPayload: JsonSerializer.SerializeToElement(new { repository_id = repoId, issue_number = number, act_as_user_id = actAsUserId }),
                action: ct => _issueService.CloseAsync(repoId, teamId, number, actAsUserId, ct),
                completionExtractor: result => new ExternalCallCompletion
                {
                    ResponsePayload = JsonSerializer.SerializeToElement(new { number = result.Number, state = result.State.ToString(), url = result.WebUrl })
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { return NodeResult.Fail(ex.Message); }
        catch (ProviderInsufficientScopeException ex) { return NodeResult.Fail(DescribeWriteFailure(ex, number)); }
        catch (ProviderApiException ex) { return NodeResult.Fail(DescribeWriteFailure(ex, number)); }

        context.Logger.LogInformation("Closed issue #{Num} on repo {RepoId} (state={State})", number, repoId, issue.State);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["number"] = JsonSerializer.SerializeToElement(issue.Number),
            ["state"] = JsonSerializer.SerializeToElement(issue.State.ToString()),
            ["url"] = JsonSerializer.SerializeToElement(issue.WebUrl)
        };

        return NodeResult.Ok(outputs);
    }

    private static string DescribeWriteFailure(Exception ex, int number) => ex switch
    {
        ProviderInsufficientScopeException scope =>
            $"Couldn't close issue #{number}: your {scope.ProviderKind} token is missing the {string.Join(", ", scope.MissingScopes)} scope. Re-link your identity with that scope, then try again.",
        ProviderApiException { StatusCode: 403 } api =>
            $"Couldn't close issue #{number}: {api.ProviderKind} refused it — your identity may not have permission to manage issues on this repository.",
        ProviderApiException { StatusCode: 404 } api =>
            $"Couldn't close issue #{number}: {api.ProviderKind} couldn't find it, or your identity can't access this repository.",
        ProviderApiException { StatusCode: 410 } api =>
            $"Couldn't close issue #{number}: {api.ProviderKind} reports issues are disabled on this repository.",
        ProviderApiException api =>
            $"Couldn't close issue #{number}: {api.ProviderKind} returned HTTP {api.StatusCode}.",
        _ => $"Couldn't close issue #{number}: {ex.Message}",
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

    private static bool TryReadActAsUserId(NodeRunContext context, out Guid actAsUserId)
    {
        actAsUserId = Guid.Empty;
        if (!context.Inputs.TryGetValue("actAsUserId", out var value) || value.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(value.GetString(), out actAsUserId);
    }
}
