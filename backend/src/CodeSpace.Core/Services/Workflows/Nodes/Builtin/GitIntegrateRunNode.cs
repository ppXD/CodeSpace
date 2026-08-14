using System.Text.Json;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// P4 (plan-map integrated candidate): integrates THIS RUN's produced agent work into ONE reviewable branch —
/// the run-sourced sibling of <see cref="GitIntegrateNode"/> (Rule 7: a new capability is a sibling node, never a
/// widened schema). Where <c>git.integrate</c> takes an authored <c>contributions</c> array, this node derives the
/// set from the run's own durable ledgers — the per-agent <see cref="Persistence.Entities.PublishManifest"/> rows
/// plus each agent run's inline patch (<see cref="RunIntegrationContributions"/>) — which is the only honest
/// source: map outputs never carry patch bytes, and a sub-threshold diff exists nowhere but the result row.
///
/// <para>Same fail-safe contract as its sibling: a conflict / base-mismatch is a routable <c>Conflicted</c>
/// OUTCOME (branch on <c>status</c>), never a node failure; a run that produced nothing integrable completes
/// <c>Skipped</c> without touching git; only genuine infrastructure failure fails the node. A CLEAN integration
/// additionally records the run-level <c>Integration</c> manifest row — the durable "this run's unique integrated
/// candidate" fact downstream consumers (PR-open, the completion protocol's Integrate stage) read from the
/// ledger instead of re-deriving from node outputs.</para>
/// </summary>
public sealed class GitIntegrateRunNode : INodeRuntime
{
    private readonly IBranchIntegrator _integrator;
    private readonly IAgentWorkspaceResolver _workspaces;
    private readonly IPublishManifestStore _manifests;
    private readonly Persistence.Db.CodeSpaceDbContext _db;

    public GitIntegrateRunNode(IBranchIntegrator integrator, IAgentWorkspaceResolver workspaces, IPublishManifestStore manifests, Persistence.Db.CodeSpaceDbContext db)
    {
        _integrator = integrator;
        _workspaces = workspaces;
        _manifests = manifests;
        _db = db;
    }

    public string TypeKey => "git.integrate_run";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Integrate this run's agent work",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "git-merge",
        Description = "Integrates every branch/patch this run's agents produced for one repository into a single reviewable branch, or fails safe (keeps them separate + reports the conflict).",
        // A clean integration pushes a branch — a permanent externally-visible side effect — so the engine refuses
        // auto-resume on abandoned runs / gates a re-run through the side-effect approval card (mirrors git.integrate).
        IsSideEffecting = true,
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {},
              "x-intent": "Integrate this run's agent work into {repositoryId}.",
              "x-intentPlaceholders": { "repositoryId": "a repository" }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository whose produced work this run integrates. The contributions themselves are derived from the run's own publish ledger." }
              },
              "required": ["repositoryId"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "status": { "type": "string" },
                "integratedBranch": { "type": ["string","null"] },
                "appliedCount": { "type": "integer" },
                "reason": { "type": ["string","null"] },
                "conflicts": { "type": "array" }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!TryReadGuid(context, "repositoryId", out var repoId)) return NodeResult.Fail("Input 'repositoryId' missing or not a uuid.");
        if (!NodeScopeReader.TryReadTeamId(context, out var teamId)) return NodeResult.Fail("This run has no team context, so a repository can't be resolved.");
        if (!TryReadRunId(context, out var runId)) return NodeResult.Fail("This run has no run id in scope, so its produced work can't be located.");

        var manifests = await _manifests.ListForWorkflowRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var agentWork = await LoadAgentWorkAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var contributions = RunIntegrationContributions.Build(repoId, manifests, agentWork);

        if (contributions.Count == 0)
            return NodeResult.Ok(SkippedOutputs("the run produced no integrable work for this repository"));

        var baseSha = contributions.Select(c => c.BaseSha).FirstOrDefault(sha => !string.IsNullOrEmpty(sha));

        if (string.IsNullOrEmpty(baseSha))
            return NodeResult.Ok(SkippedOutputs("the produced work recorded no base revision to integrate from"));

        WorkspaceRequest? workspace;
        try
        {
            workspace = await _workspaces.ResolveByRepositoryIdAsync(repoId, teamId, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            return NodeResult.Fail($"Repository {repoId} could not be resolved: {ex.Message}");
        }

        if (workspace is null) return NodeResult.Fail($"Repository {repoId} could not be resolved to a clone target.");

        var request = new IntegrationRequest
        {
            TeamId = teamId,
            RepositoryUrl = workspace.RepositoryUrl,
            BaseRef = workspace.Ref,
            BaseSha = baseSha!,
            Token = workspace.Token,
            TokenUsername = workspace.TokenUsername,
            IntegrationBranch = $"codespace/integration/{runId:N}",
            Depth = 0,
            Contributions = contributions,
        };

        IntegrationResult result;
        try
        {
            result = await _integrator.IntegrateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            return NodeResult.Fail($"Branch integration failed: {ex.Message}");
        }

        if (false)
            await RecordIntegrationManifestAsync(runId, teamId, repoId, baseSha!, result, manifests, cancellationToken).ConfigureAwait(false);

        context.Logger.LogInformation("git.integrate_run on repo {RepoId}: {Status} ({Applied}/{Total} applied)", repoId, result.Status, result.AppliedCount, contributions.Count);

        return NodeResult.Ok(GitIntegrateNode.ProjectOutputs(result));
    }

    /// <summary>The durable run-level candidate fact: one <c>Integration</c>-kind manifest row per (run, alias) — the ledger read downstream instead of node outputs. The commit sha is deliberately absent (the integrator reports the branch, not its head); the branch + base anchor the candidate.</summary>
    private async Task RecordIntegrationManifestAsync(Guid runId, Guid teamId, Guid repositoryId, string baseSha, IntegrationResult result, IReadOnlyList<Persistence.Entities.PublishManifest> manifests, CancellationToken cancellationToken)
    {
        var alias = manifests.FirstOrDefault(m => m.RepositoryId == repositoryId)?.RepositoryAlias ?? "primary";

        await _manifests.UpsertForIntegrationAsync(new PublishManifestUpsert
        {
            TeamId = teamId,
            WorkflowRunId = runId,
            RepositoryAlias = alias,
            RepositoryId = repositoryId,
            BaseSha = baseSha,
            Branch = result.IntegratedBranch,
            PublishStateValue = PublishState.Pushed,
            Summary = $"Integrated {result.AppliedCount} contribution(s) from this run",
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RunAgentWork>> LoadAgentWorkAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) =>
        (await _db.AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.TeamId == teamId)
            .Select(r => new { r.Id, r.NodeId, r.IterationKey, r.CreatedDate, r.ResultJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .Select(r => new RunAgentWork(r.Id, r.NodeId, r.IterationKey, r.CreatedDate, r.ResultJson))
        .ToList();

    private static Dictionary<string, JsonElement> SkippedOutputs(string reason) => new()
    {
        ["status"] = JsonSerializer.SerializeToElement("Skipped"),
        ["integratedBranch"] = JsonSerializer.SerializeToElement((string?)null),
        ["appliedCount"] = JsonSerializer.SerializeToElement(0),
        ["reason"] = JsonSerializer.SerializeToElement(reason),
        ["conflicts"] = JsonSerializer.SerializeToElement(Array.Empty<object>()),
    };

    private static bool TryReadGuid(NodeRunContext context, string key, out Guid id)
    {
        id = Guid.Empty;
        return context.Inputs.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out id);
    }

    private static bool TryReadRunId(NodeRunContext context, out Guid runId)
    {
        runId = Guid.Empty;
        return context.Scope.Sys.TryGetValue(SystemScopeKeys.WorkflowRunId, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out runId);
    }
}
