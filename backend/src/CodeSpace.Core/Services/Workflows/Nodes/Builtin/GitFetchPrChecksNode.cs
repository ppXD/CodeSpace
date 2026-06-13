using System.Text.Json;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Fetches a PR/MR's CI checks via the existing <see cref="IPullRequestService.ListChecksAsync"/> and
/// folds them into a branch-ready rollup. Inputs: <c>repositoryId</c> + <c>number</c>. Outputs the raw
/// <c>checks[]</c> plus a derived summary — <c>state</c> ("success" / "pending" / "failure"),
/// <c>allPassed</c>, and <c>total</c> / <c>passing</c> / <c>failing</c> / <c>pending</c> counts.
///
/// This is the "gate on CI" node: wire <c>{{nodes.checks.outputs.allPassed}}</c> (or
/// <c>state == "success"</c>) into an If/else so a workflow only merges / proceeds once CI is green.
/// Read-only (not side-effecting). A PR with NO checks reports <c>state = "success"</c> /
/// <c>allPassed = true</c> (vacuously — nothing is pending or failing), mirroring how providers treat a
/// PR with no required checks as mergeable.
/// </summary>
public sealed class GitFetchPrChecksNode : INodeRuntime
{
    private readonly IPullRequestService _prService;

    public GitFetchPrChecksNode(IPullRequestService prService)
    {
        _prService = prService;
    }

    public string TypeKey => "git.fetch_pr_checks";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Fetch PR checks",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "circle-check",
        Description = "Fetches a pull/merge request's CI checks and a green/pending/failed summary — wire allPassed into an If/else to gate on CI.",
        // Synchronous + read-only → exposable as an agent tool (a non-destructive one).
        IsAgentToolEligible = true,
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
                "checks": { "type": "array" },
                "state": { "type": "string" },
                "allPassed": { "type": "boolean" },
                "total": { "type": "integer" },
                "passing": { "type": "integer" },
                "failing": { "type": "integer" },
                "pending": { "type": "integer" }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadRepositoryId(context, out var repoId)) return NodeResult.Fail("Input 'repositoryId' missing or not a uuid.");
        if (!TryReadNumber(context, out var number)) return NodeResult.Fail("Input 'number' missing or not an integer.");

        var checks = await context.Observability.TraceExternalCallAsync(
            target: $"git.list_checks:{repoId}:{number}",
            method: "list_checks",
            requestPayload: JsonSerializer.SerializeToElement(new { repository_id = repoId, pull_request_number = number }),
            action: ct => _prService.ListChecksAsync(repoId, number, ct),
            completionExtractor: result =>
            {
                var s = SummarizeChecks(result);
                return new ExternalCallCompletion
                {
                    ResponsePayload = JsonSerializer.SerializeToElement(new { total = s.Total, passing = s.Passing, failing = s.Failing, pending = s.Pending, state = s.State })
                };
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var summary = SummarizeChecks(checks);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["checks"] = JsonSerializer.SerializeToElement(checks),
            ["state"] = JsonSerializer.SerializeToElement(summary.State),
            ["allPassed"] = JsonSerializer.SerializeToElement(summary.AllPassed),
            ["total"] = JsonSerializer.SerializeToElement(summary.Total),
            ["passing"] = JsonSerializer.SerializeToElement(summary.Passing),
            ["failing"] = JsonSerializer.SerializeToElement(summary.Failing),
            ["pending"] = JsonSerializer.SerializeToElement(summary.Pending)
        };

        context.Logger.LogInformation("Fetched {Total} checks ({State}: {Passing} passing / {Failing} failing / {Pending} pending) for repo {RepoId} PR #{Num}", summary.Total, summary.State, summary.Passing, summary.Failing, summary.Pending, repoId, number);

        return NodeResult.Ok(outputs);
    }

    /// <summary>
    /// Pure: fold a PR's checks into a branch-ready rollup. <c>State</c> is "pending" if any check is still
    /// running, else "failure" if any failed/cancelled, else "success". <c>AllPassed</c> is true when nothing
    /// is pending or failing — so an EMPTY list passes vacuously (no required check blocks the gate), matching
    /// how GitHub/GitLab treat a PR with no checks as mergeable. Skipped / Neutral don't block. Internal +
    /// static so the gate logic is exhaustively unit-pinned without a NodeRunContext.
    /// </summary>
    internal static (int Total, int Passing, int Failing, int Pending, string State, bool AllPassed) SummarizeChecks(IReadOnlyList<RemotePullRequestCheck> checks)
    {
        var passing = checks.Count(c => c.Status == PullRequestCheckStatus.Success);
        var failing = checks.Count(c => c.Status is PullRequestCheckStatus.Failure or PullRequestCheckStatus.Cancelled);
        var pending = checks.Count(c => c.Status == PullRequestCheckStatus.Pending);

        var state = pending > 0 ? "pending" : failing > 0 ? "failure" : "success";

        return (checks.Count, passing, failing, pending, state, pending == 0 && failing == 0);
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
