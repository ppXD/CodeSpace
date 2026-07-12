using System.Text.Json;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// INTEGRATES K parallel agent contributions into ONE reviewable branch on a repository via
/// <see cref="IBranchIntegrator"/> — the "integrate, not narrate" half of multi-agent fan-out (SOTA #3). Inputs:
/// <c>repositoryId</c>, the shared <c>baseSha</c> the agents branched from, and a <c>contributions</c> array (each a
/// label + the base it recorded + its unified-diff patch / artifact id / produced branch). Outputs the whole-set
/// <c>status</c>, the <c>integratedBranch</c> (on a clean integration), <c>appliedCount</c>, and per-contribution
/// <c>conflicts</c> detail.
///
/// <para>Fail-SAFE by construction (the integrator's contract): it auto-integrates ONLY a fully-clean set and pushes a
/// run-id-derived REVIEWABLE branch (never the base / a protected branch — a human reviews it via a downstream
/// <c>git.open_pr</c>); a conflict / base-mismatch / multi-repo set is a routable <c>Conflicted</c> OUTCOME (not a node
/// failure — branch on <c>status</c>), and only a genuine infrastructure failure fails the node. <see cref="NodeManifest.IsSideEffecting"/>
/// is true so a re-run on an abandoned run routes through the side-effect approval gate, exactly like <c>git.open_pr</c>.</para>
/// </summary>
public sealed class GitIntegrateNode : INodeRuntime
{
    private readonly IBranchIntegrator _integrator;
    private readonly IAgentWorkspaceResolver _workspaces;

    public GitIntegrateNode(IBranchIntegrator integrator, IAgentWorkspaceResolver workspaces)
    {
        _integrator = integrator;
        _workspaces = workspaces;
    }

    public string TypeKey => "git.integrate";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Integrate agent branches",
        Category = "Git",
        Kind = NodeKind.Regular,
        IconKey = "git-merge",
        Description = "Integrates K parallel agent contributions into one reviewable branch, or fails safe (keeps them separate + reports the conflict).",
        // A clean integration pushes a branch — a permanent externally-visible side effect — so the engine refuses
        // auto-resume on abandoned runs / gates a re-run through the side-effect approval card (mirrors git.open_pr).
        IsSideEffecting = true,
        // x-intent: always-first plain-language summary composed from the live inputs (repositoryId → repo
        // NAME; a bound {{ref}} → chip; unset → the x-intentPlaceholders prompt). Display-only metadata.
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {},
              "x-intent": "Integrate agent branches into {repositoryId}.",
              "x-intentPlaceholders": { "repositoryId": "a repository" }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository to integrate into. Pick one, or bind it from the trigger." },
                "baseSha": { "type": "string", "description": "The shared base revision all contributions branched from. Each contribution is anchored to this exact commit before applying." },
                "contributions": {
                  "type": "array",
                  "description": "The agent contributions to integrate, in order. Each carries its diff (or an artifact reference) plus the base it recorded.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "label": { "type": "string" },
                      "baseSha": { "type": "string" },
                      "patch": { "type": "string" },
                      "patchArtifactId": { "type": "string", "format": "uuid" },
                      "producedBranch": { "type": "string" },
                      "sourceRepositoryId": { "type": "string", "format": "uuid" }
                    },
                    "required": ["label"]
                  }
                }
              },
              "required": ["repositoryId","baseSha","contributions"]
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
        if (!TryReadNonEmpty(context, "baseSha", out var baseSha)) return NodeResult.Fail("Input 'baseSha' is required.");
        if (!NodeScopeReader.TryReadTeamId(context, out var teamId)) return NodeResult.Fail("This run has no team context, so a repository can't be resolved.");

        var contributions = ReadContributions(context);

        var workspace = await _workspaces.ResolveByRepositoryIdAsync(repoId, teamId, cancellationToken).ConfigureAwait(false);

        if (workspace is null) return NodeResult.Fail($"Repository {repoId} could not be resolved to a clone target.");

        var request = new IntegrationRequest
        {
            TeamId = teamId,
            RepositoryUrl = workspace.RepositoryUrl,
            BaseRef = workspace.Ref,
            BaseSha = baseSha,
            Token = workspace.Token,
            TokenUsername = workspace.TokenUsername,
            IntegrationBranch = BuildIntegrationBranch(context),
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
            // A genuine infrastructure failure (auth / network / git unavailable) — token already redacted by the
            // integrator. A CONFLICT is NOT this path — it is a routable Conflicted result below.
            return NodeResult.Fail($"Branch integration failed: {ex.Message}");
        }

        context.Logger.LogInformation("git.integrate on repo {RepoId}: {Status} ({Applied}/{Total} applied)", repoId, result.Status, result.AppliedCount, contributions.Count);

        return NodeResult.Ok(ProjectOutputs(result));
    }

    /// <summary>The run-id-derived integration branch — deterministic so a re-run overwrites the same branch and never forks.</summary>
    private static string BuildIntegrationBranch(NodeRunContext context) =>
        context.Scope.Sys.TryGetValue(SystemScopeKeys.WorkflowRunId, out var v) && v.ValueKind == JsonValueKind.String
            ? $"codespace/integration/{v.GetString()}"
            : $"codespace/integration/{context.NodeId}";

    private static Dictionary<string, JsonElement> ProjectOutputs(IntegrationResult result) => new()
    {
        ["status"] = JsonSerializer.SerializeToElement(result.Status.ToString()),
        ["integratedBranch"] = JsonSerializer.SerializeToElement(result.IntegratedBranch),
        ["appliedCount"] = JsonSerializer.SerializeToElement(result.AppliedCount),
        ["reason"] = JsonSerializer.SerializeToElement(result.Reason),
        ["conflicts"] = JsonSerializer.SerializeToElement(
            result.Outcomes
                .Where(o => o.Disposition != ContributionDisposition.Applied)
                .Select(o => new { label = o.Label, disposition = o.Disposition.ToString(), reason = o.Reason, conflictedFiles = o.ConflictedFiles, fallbackBranch = o.FallbackBranch })),
    };

    private static IReadOnlyList<BranchContribution> ReadContributions(NodeRunContext context)
    {
        if (!context.Inputs.TryGetValue("contributions", out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<BranchContribution>();

        return value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(ReadContribution).ToList();
    }

    private static BranchContribution ReadContribution(JsonElement e) => new()
    {
        Label = ReadString(e, "label") ?? "contribution",
        BaseSha = ReadString(e, "baseSha"),
        Patch = ReadString(e, "patch") ?? "",
        PatchArtifactId = ReadGuid(e, "patchArtifactId"),
        ProducedBranch = ReadString(e, "producedBranch"),
        SourceRepositoryId = ReadGuid(e, "sourceRepositoryId") ?? Guid.Empty,
    };

    private static string? ReadString(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Guid? ReadGuid(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null;

    private static bool TryReadGuid(NodeRunContext context, string key, out Guid id)
    {
        id = Guid.Empty;
        return context.Inputs.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out id);
    }

    private static bool TryReadNonEmpty(NodeRunContext context, string key, out string text)
    {
        text = "";
        if (!context.Inputs.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String) return false;
        text = (value.GetString() ?? "").Trim();
        return text.Length > 0;
    }
}
