using System.Text.Json;
using CodeSpace.Core.Services.Issues;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// CREATES an issue on a repository via <see cref="IIssueService.CreateAsync"/> — the issue half of the
/// Git write surface. Inputs: <c>repositoryId</c>, <c>title</c> (required), optional <c>body</c> /
/// <c>labels</c> / <c>actAsUserId</c>. Outputs the created <c>number</c>, <c>url</c>, and <c>state</c>.
///
/// Typical use: an agent triages a failure and files a tracking issue, or a scheduled workflow opens a
/// recurring chore. The provider translates the neutral input to its own API (GitHub issue; GitLab issue).
/// </summary>
public sealed class GitCreateIssueNode : INodeRuntime
{
    private readonly IIssueService _issueService;

    public GitCreateIssueNode(IIssueService issueService)
    {
        _issueService = issueService;
    }

    public string TypeKey => "git.create_issue";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Create issue",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "circle-dot",
        Description = "Creates an issue on a repository (title + optional body / labels).",
        // Creating an issue is a permanent externally-visible side effect — the engine refuses auto-resume
        // on abandoned runs so we never file a duplicate.
        IsSideEffecting = true,
        // Acts AS the actor's own identity (Model B), same generic gating as git.open_pr.
        ActsAsUser = new ActsAsUserSpec { ActorInputKey = "actAsUserId", ProviderInputKey = "repositoryId", ProviderSource = ActorProviderSource.Repository, CapabilityType = typeof(IIssueWriteCapability) },
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository. Pick one, or switch to Expression to bind it from the trigger (e.g. {{trigger.repositoryId}})." },
                "title": { "type": "string", "description": "The issue title." },
                "body": { "type": "string", "x-long": true, "description": "Optional markdown body. Supports {{ }} references." },
                "labels": { "type": "array", "items": { "type": "string" }, "description": "Optional label names to attach (comma-separated)." },
                "actAsUserId": { "type": "string", "format": "uuid", "description": "Create the issue AS this CodeSpace user's own linked GitHub/GitLab identity. Omit to use the repository's connection credential." }
              },
              "required": ["repositoryId","title"]
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

        var body = TryReadNonEmpty(context, "body", out var b) ? b : null;
        var labels = TryReadStringArray(context, "labels");
        var actAsUserId = TryReadActAsUserId(context, out var a) ? a : (Guid?)null;

        var input = new CreateIssueInput { Title = title, Body = body, Labels = labels };

        RemoteIssue issue;
        try
        {
            issue = await context.Observability.TraceExternalCallAsync(
                target: $"git.create_issue:{repoId}",
                method: "create_issue",
                requestPayload: JsonSerializer.SerializeToElement(new { repository_id = repoId, title, label_count = labels.Count, body_chars = body?.Length ?? 0, act_as_user_id = actAsUserId }),
                action: ct => _issueService.CreateAsync(repoId, input, actAsUserId, ct),
                completionExtractor: result => new ExternalCallCompletion
                {
                    ResponsePayload = JsonSerializer.SerializeToElement(new { number = result.Number, url = result.WebUrl, state = result.State.ToString() })
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        // The service throws InvalidOperationException for bad input (blank title) — surface it as a clean
        // node failure. Provider scope / permission / validation failures map to actionable text.
        catch (InvalidOperationException ex) { return NodeResult.Fail(ex.Message); }
        catch (ProviderInsufficientScopeException ex) { return NodeResult.Fail(DescribeWriteFailure(ex)); }
        catch (ProviderApiException ex) { return NodeResult.Fail(DescribeWriteFailure(ex)); }

        context.Logger.LogInformation("Created issue #{Num} on repo {RepoId}", issue.Number, repoId);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["number"] = JsonSerializer.SerializeToElement(issue.Number),
            ["url"] = JsonSerializer.SerializeToElement(issue.WebUrl),
            ["state"] = JsonSerializer.SerializeToElement(issue.State.ToString())
        };

        return NodeResult.Ok(outputs);
    }

    private static string DescribeWriteFailure(Exception ex) => ex switch
    {
        ProviderInsufficientScopeException scope =>
            $"Couldn't create the issue: your {scope.ProviderKind} token is missing the {string.Join(", ", scope.MissingScopes)} scope. Re-link your identity with that scope, then try again.",
        ProviderApiException { StatusCode: 403 } api =>
            $"Couldn't create the issue: {api.ProviderKind} refused it — your identity may not have permission to open issues on this repository.",
        ProviderApiException { StatusCode: 404 } api =>
            $"Couldn't create the issue: {api.ProviderKind} couldn't find the repository, or your identity can't access it.",
        ProviderApiException { StatusCode: 410 } api =>
            $"Couldn't create the issue: {api.ProviderKind} reports issues are disabled on this repository.",
        ProviderApiException { StatusCode: 422 } api =>
            $"Couldn't create the issue: {api.ProviderKind} rejected it — a label may not exist on this repository.",
        ProviderApiException api =>
            $"Couldn't create the issue: {api.ProviderKind} returned HTTP {api.StatusCode}.",
        _ => $"Couldn't create the issue: {ex.Message}",
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

    /// <summary>Optional string array (e.g. labels): non-empty trimmed strings from a JSON array; absent / non-array / all-blank ⇒ empty list.</summary>
    private static IReadOnlyList<string> TryReadStringArray(NodeRunContext context, string key)
    {
        if (!context.Inputs.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        var items = new List<string>(value.GetArrayLength());
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) continue;
            var s = (entry.GetString() ?? "").Trim();
            if (s.Length > 0) items.Add(s);
        }
        return items;
    }

    private static bool TryReadActAsUserId(NodeRunContext context, out Guid actAsUserId)
    {
        actAsUserId = Guid.Empty;
        if (!context.Inputs.TryGetValue("actAsUserId", out var value) || value.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(value.GetString(), out actAsUserId);
    }
}
