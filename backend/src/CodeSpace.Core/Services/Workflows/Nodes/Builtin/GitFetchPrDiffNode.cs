using System.Text.Json;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Fetches the file-by-file diff for a PR/MR via the existing <see cref="IPullRequestService"/>.
/// Inputs: <c>repositoryId</c> + <c>number</c>. Outputs: <c>files[]</c> (filename + patch + +/-
/// counts) and aggregated <c>additions</c> / <c>deletions</c>.
///
/// This is the "give the LLM something to chew on" node. The patches are unified-diff text,
/// the same shape the GitHub/GitLab UIs render.
/// </summary>
public sealed class GitFetchPrDiffNode : INodeRuntime
{
    private readonly IPullRequestService _prService;

    public GitFetchPrDiffNode(IPullRequestService prService)
    {
        _prService = prService;
    }

    public string TypeKey => "git.fetch_pr_diff";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Fetch PR diff",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "file-diff",
        Description = "Fetches the unified diff for a pull/merge request.",
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository. Pick one, or switch to Expression to bind from the trigger (e.g. {{trigger.repositoryId}})." },
                "number": { "type": "integer", "description": "The pull/merge request number." }
              },
              "required": ["repositoryId","number"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "files": { "type": "array" },
                "additions": { "type": "integer" },
                "deletions": { "type": "integer" }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadRepositoryId(context, out var repoId)) return NodeResult.Fail("Input 'repositoryId' missing or not a uuid.");
        if (!TryReadNumber(context, out var number)) return NodeResult.Fail("Input 'number' missing or not an integer.");

        // Trace the Git provider API call. Target is "git.list_files:<repo>:<num>" so the
        // timeline shows what was fetched without dumping the whole diff into the
        // request_payload (the diff itself can be megabytes; we summarise + emit counts).
        var files = await context.Observability.TraceExternalCallAsync(
            target: $"git.list_files:{repoId}:{number}",
            method: "list_files",
            requestPayload: JsonSerializer.SerializeToElement(new { repository_id = repoId, pull_request_number = number }),
            action: ct => _prService.ListFilesAsync(repoId, number, ct),
            completionExtractor: result => new ExternalCallCompletion
            {
                ResponsePayload = JsonSerializer.SerializeToElement(new
                {
                    file_count = result.Count,
                    additions = result.Sum(f => f.Additions),
                    deletions = result.Sum(f => f.Deletions),
                })
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var totalAdditions = files.Sum(f => f.Additions);
        var totalDeletions = files.Sum(f => f.Deletions);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["files"] = JsonSerializer.SerializeToElement(files),
            ["additions"] = JsonSerializer.SerializeToElement(totalAdditions),
            ["deletions"] = JsonSerializer.SerializeToElement(totalDeletions)
        };

        context.Logger.LogInformation("Fetched {FileCount} files (+{Add} / -{Del}) for repo {RepoId} PR #{Num}", files.Count, totalAdditions, totalDeletions, repoId, number);

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
}
