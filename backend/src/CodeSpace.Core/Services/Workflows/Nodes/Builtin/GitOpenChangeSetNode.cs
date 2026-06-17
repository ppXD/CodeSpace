using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Converges a MULTI-repo Change Set into reviewable output: opens ONE pull/merge request per repository in the set,
/// the cross-repo analogue of <c>git.open_pr</c>. Wire its <c>repositories</c> input from an upstream multi-repo
/// <c>agent.code</c> run's <c>repositoryResults</c> output (each repo's produced branch → that repo's PR).
///
/// <para>Thin over <see cref="IChangeSetService"/> (Rule 16): the per-repo loop, the team-scoped open, and the
/// failure-isolation policy live in the service. Like <c>git.integrate</c>, a per-repo provider rejection is a routable
/// OUTCOME (a Failed disposition in the output), NOT a node crash — the node SUCCEEDS and the workflow branches on
/// <c>failedCount</c>. A repo with no source branch (it produced no changes) is a clean Skip.</para>
///
/// <para>v1 opens each PR as the repository's CONNECTION credential (no act-as-user): per-user attribution on a
/// fan-out of N opens would need the per-node identity-proof gate the single <c>git.open_pr</c> uses, which is a
/// follow-on. The single-PR node remains the attributed, agent-tool-eligible surface.</para>
///
/// <para>Re-run: like <c>git.open_pr</c>, this has no PR-dedup of its own — a from-node re-run is gated by the
/// side-effect approval card (IsSideEffecting), and a re-open of an existing head/base is rejected by the provider
/// (a 422 mapped to a Failed disposition), so a deliberate re-run never creates duplicate PRs but DOES report the
/// already-open repos as Failed. Authoring: bind <c>repositories</c> from an upstream agent.code run's
/// <c>repositoryResults</c> (repositoryId + the produced branch as <c>sourceBranch</c>), supplying each repo's base as
/// <c>targetBranch</c> — auto-resolving the base from the repo's default branch is a follow-on.</para>
/// </summary>
public sealed class GitOpenChangeSetNode : INodeRuntime
{
    private readonly IChangeSetService _changeSets;

    public GitOpenChangeSetNode(IChangeSetService changeSets)
    {
        _changeSets = changeSets;
    }

    public string TypeKey => "git.open_change_set";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Open change-set pull requests",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "git-pull-request",
        Description = "Opens one pull/merge request per repository in a multi-repo change set, isolating each repo's failure.",
        // Opening PRs is a permanent externally-visible side effect — the engine refuses auto-resume on abandoned runs
        // so a re-run routes the side-effect approval gate (it does not duplicate opened PRs automatically).
        IsSideEffecting = true,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositories": {
                  "type": "array",
                  "description": "One entry per repository in the change set — bind from the upstream agent.code run's repositoryResults. A repo with an empty sourceBranch (it produced no changes) is skipped.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "repositoryId": { "type": "string", "format": "uuid" },
                      "sourceBranch": { "type": "string", "description": "The repo's produced (head) branch. Must already exist on the remote." },
                      "targetBranch": { "type": "string", "description": "The repo's base branch to open the PR into. Must already exist on the remote." }
                    },
                    "required": ["repositoryId","sourceBranch","targetBranch"]
                  }
                },
                "title": { "type": "string", "description": "The pull/merge request title, applied to every repo's PR." },
                "body": { "type": "string", "x-long": true, "description": "Optional markdown description, applied to every repo's PR." },
                "draft": { "type": "boolean", "description": "Open every PR as a draft when the provider supports it." }
              },
              "required": ["repositories","title"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "pullRequests": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "repositoryId": { "type": "string" },
                      "disposition": { "type": "string" },
                      "number": { "type": ["integer","null"] },
                      "url": { "type": ["string","null"] },
                      "state": { "type": ["string","null"] },
                      "error": { "type": ["string","null"] }
                    }
                  }
                },
                "openedCount": { "type": "integer" },
                "skippedCount": { "type": "integer" },
                "failedCount": { "type": "integer" }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadNonEmpty(context, "title", out var title)) return NodeResult.Fail("Input 'title' is required.");
        if (!NodeScopeReader.TryReadTeamId(context, out var teamId)) return NodeResult.Fail("This run has no team context, so a repository can't be resolved.");
        if (!TryReadRepositories(context, out var repositories, out var parseError)) return NodeResult.Fail(parseError);

        var body = TryReadNonEmpty(context, "body", out var b) ? b : null;
        var draft = TryReadBool(context, "draft");

        var spec = new ChangeSetPullRequestSpec { Repositories = repositories, Title = title, Body = body, Draft = draft };

        var result = await context.Observability.TraceExternalCallAsync(
            target: "git.open_change_set",
            method: "open_change_set_pull_requests",
            requestPayload: JsonSerializer.SerializeToElement(new { repo_count = repositories.Count, title, draft, body_chars = body?.Length ?? 0 }),
            action: ct => _changeSets.OpenPullRequestsAsync(teamId, spec, actorUserId: null, ct),
            completionExtractor: r => new ExternalCallCompletion
            {
                ResponsePayload = JsonSerializer.SerializeToElement(new { opened = r.OpenedCount, skipped = r.SkippedCount, failed = r.FailedCount })
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        context.Logger.LogInformation("Change set: opened {Opened}, skipped {Skipped}, failed {Failed} of {Total} repos", result.OpenedCount, result.SkippedCount, result.FailedCount, repositories.Count);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["pullRequests"] = JsonSerializer.SerializeToElement(result.PullRequests, AgentJson.Options),
            ["openedCount"] = JsonSerializer.SerializeToElement(result.OpenedCount),
            ["skippedCount"] = JsonSerializer.SerializeToElement(result.SkippedCount),
            ["failedCount"] = JsonSerializer.SerializeToElement(result.FailedCount),
        };

        return NodeResult.Ok(outputs);
    }

    /// <summary>Parse the <c>repositories</c> array into per-repo requests. Each entry needs a uuid repositoryId + a targetBranch; a missing/blank sourceBranch is allowed (the service skips it). Returns false with a clean message on a malformed array.</summary>
    private static bool TryReadRepositories(NodeRunContext context, out IReadOnlyList<ChangeSetPullRequest> repositories, out string error)
    {
        repositories = Array.Empty<ChangeSetPullRequest>();
        error = "";

        if (!context.Inputs.TryGetValue("repositories", out var value) || value.ValueKind != JsonValueKind.Array)
            return Bad("Input 'repositories' is required and must be an array.", out error);

        var list = new List<ChangeSetPullRequest>();

        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) return Bad("Each 'repositories' entry must be an object.", out error);

            if (!TryReadGuidProperty(element, "repositoryId", out var repoId)) return Bad("Each 'repositories' entry needs a 'repositoryId' (uuid).", out error);
            if (!TryReadStringProperty(element, "targetBranch", out var targetBranch) || targetBranch.Length == 0) return Bad("Each 'repositories' entry needs a non-empty 'targetBranch'.", out error);

            var sourceBranch = TryReadStringProperty(element, "sourceBranch", out var src) ? src : "";

            list.Add(new ChangeSetPullRequest { RepositoryId = repoId, SourceBranch = sourceBranch, TargetBranch = targetBranch });
        }

        if (list.Count == 0) return Bad("Input 'repositories' is empty — nothing to open.", out error);

        repositories = list;
        return true;
    }

    private static bool Bad(string message, out string error) { error = message; return false; }

    private static bool TryReadGuidProperty(JsonElement obj, string name, out Guid value)
    {
        value = Guid.Empty;
        return obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out value);
    }

    private static bool TryReadStringProperty(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return false;
        value = (p.GetString() ?? "").Trim();
        return true;
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
}
