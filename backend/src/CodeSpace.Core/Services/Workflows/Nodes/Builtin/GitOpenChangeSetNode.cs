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
/// <c>agent.code</c> run's <c>repositoryResults</c> output (each repo's produced branch → that repo's PR) OR a
/// multi-repo <c>agent.supervisor</c> run's <c>repositoryBranches</c> output (each repo's reconciled head via
/// <c>sourceBranch</c>/<c>targetBranch</c>) — both bind here verbatim, so this is the ONE per-repo PR-open seam.
///
/// <para>Thin over <see cref="IChangeSetService"/> (Rule 16): the per-repo loop, the team-scoped open, and the
/// failure-isolation policy live in the service. Like <c>git.integrate</c>, a per-repo provider rejection is a routable
/// OUTCOME (a Failed disposition in the output), NOT a node crash — the node SUCCEEDS and the workflow branches on
/// <c>failedCount</c>. A repo with no produced (head) branch — it changed nothing — is a clean Skip; a repo with a head
/// but no resolvable base is a per-repo Failed (it has work but no PR target), distinct from the Skip.</para>
///
/// <para>v1 opens each PR as the repository's CONNECTION credential (no act-as-user): per-user attribution on a
/// fan-out of N opens would need the per-node identity-proof gate the single <c>git.open_pr</c> uses, which is a
/// follow-on. The single-PR node remains the attributed, agent-tool-eligible surface.</para>
///
/// <para>Re-run: like <c>git.open_pr</c>, this has no PR-dedup of its own — a from-node re-run is gated by the
/// side-effect approval card (IsSideEffecting), and a re-open of an existing head/base is rejected by the provider
/// (a 422 mapped to a Failed disposition), so a deliberate re-run never creates duplicate PRs but DOES report the
/// already-open repos as Failed. Authoring: bind <c>repositories</c> from an upstream agent.code run's
/// <c>repositoryResults</c> VERBATIM — each entry carries repositoryId + producedBranch (head) + baseBranch (the
/// per-repo PR target, the ref the repo was cloned at), which this node reads directly. A hand-authored entry may
/// still use the <c>sourceBranch</c>/<c>targetBranch</c> aliases; <c>targetBranch</c> is no longer required at the
/// node layer (a head with no base is reported Failed by the service, a no-head entry is Skipped).</para>
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
                  "description": "One entry per repository in the change set. Bind the upstream agent.code run's repositoryResults output here VERBATIM — it carries repositoryId + producedBranch + baseBranch, which this node reads directly. A repo with no produced branch (it changed nothing) is skipped.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "repositoryId": { "type": "string", "format": "uuid" },
                      "producedBranch": { "type": ["string","null"], "description": "The repo's produced (head) branch — from agent.code repositoryResults. Null/empty ⇒ the repo changed nothing ⇒ Skipped. (Alias: sourceBranch, for hand-authoring.)" },
                      "baseBranch": { "type": ["string","null"], "description": "The repo's base branch to open the PR into — from agent.code repositoryResults. Null/empty with a head ⇒ Failed (no PR target). (Alias: targetBranch, for hand-authoring.)" }
                    },
                    "required": ["repositoryId"]
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

    /// <summary>
    /// Parse the <c>repositories</c> array into per-repo requests. Each entry needs a uuid repositoryId; the head is
    /// <c>producedBranch</c> (preferred — matches agent.code repositoryResults) else <c>sourceBranch</c>, the base is
    /// <c>baseBranch</c> (preferred) else <c>targetBranch</c>. Both branches may be blank here — the service classifies
    /// a no-head entry as Skipped and a head-without-base entry as Failed — so binding repositoryResults verbatim never
    /// fails the whole node over one degraded repo. Returns false only on a structurally malformed array.
    /// </summary>
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

            var sourceBranch = ReadBranch(element, "producedBranch", "sourceBranch");
            var targetBranch = ReadBranch(element, "baseBranch", "targetBranch");

            list.Add(new ChangeSetPullRequest { RepositoryId = repoId, SourceBranch = sourceBranch, TargetBranch = targetBranch });
        }

        if (list.Count == 0) return Bad("Input 'repositories' is empty — nothing to open.", out error);

        repositories = list;
        return true;
    }

    private static bool Bad(string message, out string error) { error = message; return false; }

    /// <summary>Read a branch from the entry under its preferred key (the agent.code repositoryResults field name) else its hand-authored alias, returning "" when neither is a non-empty string. Lets repositoryResults bind verbatim while a hand-authored entry can still use source/targetBranch.</summary>
    private static string ReadBranch(JsonElement obj, string preferredKey, string aliasKey)
    {
        if (TryReadStringProperty(obj, preferredKey, out var preferred) && preferred.Length > 0) return preferred;
        return TryReadStringProperty(obj, aliasKey, out var alias) ? alias : "";
    }

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
