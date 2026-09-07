using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Authority;

public sealed partial class ExecutionAuthorityService
{
    private const string ReviewSourceKind = "agent-review";

    internal async Task<AgentTask> AdmitReviewAsync(AgentAuthorityAdmission admission, AgentRun parent, CancellationToken cancellationToken)
    {
        if (parent.TeamId != admission.TeamId || parent.WorkflowRunId != admission.WorkflowRunId) throw Denied("parent-scope-mismatch");
        var parentTask = await ReadActiveReviewParentAsync(parent.Id, admission.TeamId, new HashSet<Guid>(), cancellationToken).ConfigureAwait(false);
        await ValidateReviewBoundsAsync(admission.Task, parentTask, admission.TeamId, cancellationToken).ConfigureAwait(false);
        var source = parentTask.ExecutionAuthority!;
        var receipt = new AgentExecutionAuthority
        {
            Version = ReceiptVersion, PolicyVersion = PolicyVersion, TeamId = admission.TeamId, LogicalRunId = admission.AgentRunId,
            SourceKind = ReviewSourceKind, DefinitionHash = source.DefinitionHash, ParentAgentRunId = parent.Id, ParentAuthorityHash = AuthorityHash(source),
            GrantedCeiling = AgentAutonomyLevel.Confined, IssuedAt = DateTimeOffset.UtcNow, Subjects = source.Subjects,
        };
        return admission.Task with { ExecutionAuthority = receipt };
    }

    private async Task ValidateReviewDelegationAsync(AgentRun child, AgentTask task, AgentExecutionAuthority receipt, HashSet<Guid> lineage, CancellationToken cancellationToken)
    {
        if (receipt.ParentAgentRunId is not { } parentId || receipt.TeamId != child.TeamId || receipt.LogicalRunId != child.Id || receipt.GrantedCeiling != AgentAutonomyLevel.Confined || receipt.Version != ReceiptVersion || receipt.PolicyVersion != PolicyVersion) throw Denied("review-receipt-mismatch");
        var parent = await _db.AgentRun.AsNoTracking().SingleOrDefaultAsync(r => r.Id == parentId && r.TeamId == child.TeamId, cancellationToken).ConfigureAwait(false) ?? throw Denied("unknown-review-parent");
        if (parent.WorkflowRunId != child.WorkflowRunId) throw Denied("parent-scope-mismatch");
        var parentTask = await ReadActiveReviewParentAsync(parentId, child.TeamId, lineage, cancellationToken).ConfigureAwait(false);
        var source = parentTask.ExecutionAuthority!;
        if (receipt.ParentAuthorityHash != AuthorityHash(source) || receipt.DefinitionHash != source.DefinitionHash || receipt.ActivationId != null || receipt.ActivationRevision != null || receipt.ParentRunId != null
            || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(receipt.Subjects, AgentJson.Options), JsonSerializer.SerializeToElement(source.Subjects, AgentJson.Options))) throw Denied("parent-authority-changed");
        await ValidateReviewBoundsAsync(task, parentTask, child.TeamId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentTask> ReadActiveReviewParentAsync(Guid parentId, Guid teamId, HashSet<Guid> lineage, CancellationToken cancellationToken)
    {
        // A delegated action belongs to the logical parent. A legitimate observer handoff does not change this grant.
        const string sql = "SELECT count(*)::integer AS \"Value\" FROM agent_run WHERE id = {0} AND team_id = {1} AND status = 'Running' AND owner_id IS NOT NULL AND lease_expires_at > clock_timestamp()";
        if ((await _db.Database.SqlQueryRaw<int>(sql, parentId, teamId).ToListAsync(cancellationToken).ConfigureAwait(false)).Single() != 1) throw Denied("review-parent-inactive");
        var parentTask = await EnsureAgentActionCoreAsync(parentId, teamId, lineage, cancellationToken).ConfigureAwait(false);
        if (parentTask.ExecutionAuthority!.SourceKind == ReviewSourceKind) throw Denied("review-delegation-recursion");
        return parentTask;
    }

    private async Task ValidateReviewBoundsAsync(AgentTask child, AgentTask parent, Guid teamId, CancellationToken cancellationToken)
    {
        if (child.Autonomy != AgentAutonomyLevel.Confined || child.Permissions.Network != AgentNetworkAccess.Off || child.Permissions.WriteScope != AgentWriteScope.ReadOnly
            || !Enum.IsDefined(child.Permissions.Egress) || child.PushProducedBranch != false || child.EnableMcpEndpoint != false || child.OutputReviewMode != ReviewMode.None
            || child.ReviewerAgent || child.MaxReviseRounds != 0 || child.Acceptance != null || child.WorkspaceDirectory != null || child.Environment.Count != 0 || child.RunnerKind != parent.RunnerKind) throw Denied("review-permissions-exceed-scope");
        if (parent.Tools != null && (child.Tools == null || child.Tools.Except(parent.Tools, StringComparer.Ordinal).Any())) throw Denied("review-tools-exceed-parent");
        var allowed = parent.Workspace?.Repositories.Select(r => r.RepositoryId).ToHashSet() ?? (parent.RepositoryId is { } repositoryId ? [repositoryId] : new HashSet<Guid>());
        if (child.RepositoryId is not { } primary || !allowed.Contains(primary) || child.Workspace is not { Repositories.Count: > 0 } workspace
            || workspace.Primary?.RepositoryId != primary || workspace.Repositories.Any(r => !allowed.Contains(r.RepositoryId) || r.Access != WorkspaceAccess.Read)) throw Denied("review-repository-exceeds-parent");
        var repositories = workspace.Repositories.Select(r => r.RepositoryId).Distinct().ToArray();
        var available = await _db.Repository.AsNoTracking().CountAsync(r => repositories.Contains(r.Id) && r.TeamId == teamId && r.DeletedDate == null && r.Status == RepositoryStatus.Active && r.ProviderInstance.DeletedDate == null && r.ProviderInstance.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        if (available != repositories.Length) throw Denied("review-repository-unavailable");
    }

    private static string AuthorityHash(AgentExecutionAuthority receipt) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt, AgentJson.Options))));
}
