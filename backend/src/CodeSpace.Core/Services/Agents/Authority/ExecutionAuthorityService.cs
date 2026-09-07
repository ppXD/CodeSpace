using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Authority;

/// <summary>
/// Version 1 preserves the existing team permission matrix: it does not infer a new admin-only Unleashed rule,
/// consent to external actions, or authority from a model/route. A receipt bounds autonomous execution; each tool's
/// own permissions, risk and approval rules still apply. Principals and standing are read afresh, even within one MCP session.
/// </summary>
public sealed partial class ExecutionAuthorityService : IScopedDependency
{
    public const int ReceiptVersion = 1;
    public const string PolicyVersion = "team-permissions-intersection/v1";
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ExecutionAuthorityService(CodeSpaceDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<AgentExecutionAuthority> MintAsync(WorkflowRun run, WorkflowRunRequest request, CancellationToken cancellationToken)
    {
        return await MintCoreAsync(new WorkflowAdmission(run, request, false), new HashSet<Guid>(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentTask> AdmitAgentAsync(AgentAuthorityAdmission admission, CancellationToken cancellationToken)
    {
        AgentExecutionAuthority receipt;
        if (admission.WorkflowRunId is { } workflowRunId)
        {
            var run = await LoadWorkflowAsync(workflowRunId, admission.TeamId, cancellationToken).ConfigureAwait(false);
            receipt = await ReadWorkflowReceiptAsync(run, new HashSet<Guid>(), cancellationToken).ConfigureAwait(false);
            await ValidateWorkflowAsync(run, receipt, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var subject = await ResolveSubjectAsync(new SubjectRequest(admission.TeamId, _currentUser.Id, "launcher", TeamPermissions.RunsLaunch), cancellationToken).ConfigureAwait(false);
            receipt = new AgentExecutionAuthority
            {
                Version = ReceiptVersion, PolicyVersion = PolicyVersion, TeamId = admission.TeamId,
                LogicalRunId = admission.AgentRunId, SourceKind = "standalone", DefinitionHash = "",
                GrantedCeiling = AgentAutonomyPolicy.DeploymentCeiling, IssuedAt = DateTimeOffset.UtcNow, Subjects = [subject],
            };
        }

        if (!Enum.IsDefined(admission.Task.Permissions.Network) || !Enum.IsDefined(admission.Task.Permissions.WriteScope) || !Enum.IsDefined(admission.Task.Permissions.Egress)) throw Denied("invalid-permissions");
        var requested = Enum.IsDefined(admission.Task.Autonomy) ? admission.Task.Autonomy : throw Denied("invalid-autonomy");
        var effective = AgentAutonomyPolicy.Clamp(requested, AgentAutonomyPolicy.Clamp(receipt.GrantedCeiling, AgentAutonomyPolicy.DeploymentCeiling));
        var maximum = AgentAutonomyPolicy.Derive(effective);
        var permissions = admission.Task.Permissions with
        {
            Network = maximum.Network == AgentNetworkAccess.Off ? AgentNetworkAccess.Off : admission.Task.Permissions.Network,
            WriteScope = maximum.WriteScope == AgentWriteScope.ReadOnly ? AgentWriteScope.ReadOnly : admission.Task.Permissions.WriteScope,
        };
        // Never trust an incoming receipt, including when retrying a task supplied by a model or caller.
        return admission.Task with { Autonomy = effective, Permissions = permissions, ExecutionAuthority = receipt };
    }

    public async Task EnsureAgentActionAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken)
    {
        await EnsureAgentActionCoreAsync(agentRunId, teamId, new HashSet<Guid>(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentTask> EnsureAgentActionCoreAsync(Guid agentRunId, Guid teamId, HashSet<Guid> lineage, CancellationToken cancellationToken)
    {
        if (!lineage.Add(agentRunId)) throw Denied("cyclic-agent-delegation");
        var agent = await _db.AgentRun.AsNoTracking().SingleOrDefaultAsync(r => r.Id == agentRunId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false) ?? throw Denied("unknown-agent-run");
        if (agent.Status is not (AgentRunStatus.Queued or AgentRunStatus.Running)) throw Denied("agent-terminal");
        var task = JsonSerializer.Deserialize<AgentTask>(agent.TaskJson, AgentJson.Options) ?? throw Denied("unreadable-task");
        var receipt = task.ExecutionAuthority;

        if (receipt?.SourceKind == ReviewSourceKind)
        {
            await ValidateReviewDelegationAsync(agent, task, receipt, lineage, cancellationToken).ConfigureAwait(false);
        }
        else if (agent.WorkflowRunId is { } workflowRunId)
        {
            var run = await LoadWorkflowAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);
            var canonical = await ReadWorkflowReceiptAsync(run, new HashSet<Guid>(), cancellationToken).ConfigureAwait(false);
            // Pre-receipt agent rows may use their verified workflow source, bounded by their own frozen task.
            if (receipt != null && !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(receipt, AgentJson.Options), JsonSerializer.SerializeToElement(canonical, AgentJson.Options))) throw Denied("receipt-mismatch");
            receipt = canonical;
            await ValidateWorkflowAsync(run, receipt, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // A background worker's current identity cannot establish who admitted a legacy standalone run.
            if (receipt == null || receipt.SourceKind != "standalone" || receipt.LogicalRunId != agentRunId || receipt.TeamId != teamId) throw Denied("legacy-authority-unverifiable");
            await ValidateSubjectsAsync(receipt, cancellationToken).ConfigureAwait(false);
        }

        var ceiling = AgentAutonomyPolicy.Clamp(receipt.GrantedCeiling, AgentAutonomyPolicy.DeploymentCeiling);
        if (!Enum.IsDefined(task.Autonomy) || task.Autonomy > ceiling) throw Denied("ceiling-tightened");
        var maximum = AgentAutonomyPolicy.Derive(ceiling);
        if (maximum.Network == AgentNetworkAccess.Off && task.Permissions.Network != AgentNetworkAccess.Off || maximum.WriteScope == AgentWriteScope.ReadOnly && task.Permissions.WriteScope != AgentWriteScope.ReadOnly) throw Denied("permissions-exceed-ceiling");
        return task with { ExecutionAuthority = receipt };
    }

    private async Task<AgentExecutionAuthority> MintCoreAsync(WorkflowAdmission admission, HashSet<Guid> lineage, CancellationToken cancellationToken)
    {
        var run = admission.Run;
        var request = admission.Request;
        if (!lineage.Add(run.Id)) throw Denied("cyclic-parent-authority");
        if (request.TeamId != run.TeamId || request.Id != run.RunRequestId) throw Denied("source-team-mismatch");
        var subjects = new List<AgentAuthoritySubject>();
        var ceiling = AgentAutonomyPolicy.DeploymentCeiling;
        string definitionJson;
        string definitionHash;
        WorkflowVersion? version = null;
        if (run.WorkflowId is { } workflowId && run.WorkflowVersion is { } versionNumber)
        {
            version = await _db.WorkflowVersion.AsNoTracking().Include(v => v.Workflow).SingleOrDefaultAsync(v => v.WorkflowId == workflowId && v.Version == versionNumber && v.Workflow.TeamId == run.TeamId && v.Workflow.DeletedDate == null, cancellationToken).ConfigureAwait(false) ?? throw Denied("unknown-workflow-version");
            if (request.WorkflowId != workflowId) throw Denied("source-workflow-mismatch");
            definitionJson = version.DefinitionJson;
            definitionHash = version.DefinitionHash;
            if (version.CreatedBy == Guid.Empty || version.CreatedBy == SystemUsers.SeederId) throw Denied("legacy-author-unverifiable-republish-required");
            subjects.Add(await ResolveSubjectAsync(new SubjectRequest(run.TeamId, version.CreatedBy, "author", TeamPermissions.WorkflowsWrite), cancellationToken).ConfigureAwait(false));
        }
        else
        {
            definitionJson = run.DefinitionSnapshotJson ?? throw Denied("unknown-definition");
            definitionHash = run.DefinitionSnapshotHash ?? throw Denied("unknown-definition-hash");
        }
        ceiling = AgentAutonomyPolicy.Clamp(ceiling, DefinitionCeiling(definitionJson));

        Guid? activationRevision = null;
        if (request.ActorType == WorkflowRunActorTypes.User)
        {
            subjects.Add(await ResolveSubjectAsync(new SubjectRequest(run.TeamId, request.ActorId, "launcher", TeamPermissions.RunsLaunch), cancellationToken).ConfigureAwait(false));
            if (request.CausationId is { } causationId)
            {
                var original = await _db.WorkflowRun.AsNoTracking().SingleOrDefaultAsync(r => r.Id == run.ParentRunId && r.RunRequestId == causationId && r.TeamId == run.TeamId, cancellationToken).ConfigureAwait(false) ?? throw Denied("invalid-replay-source");
                // A new actor may replay a cancelled execution. Only the old frozen ceiling travels, never its subjects.
                var originalReceiptJson = await _db.WorkflowRunExecutionAuthority.AsNoTracking().Where(r => r.WorkflowRunId == original.Id && r.TeamId == original.TeamId).Select(r => r.ReceiptJson).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (originalReceiptJson == null) throw Denied("legacy-replay-authority-unverifiable");
                ceiling = AgentAutonomyPolicy.Clamp(ceiling, Deserialize(originalReceiptJson).GrantedCeiling);
                if (original.DefinitionSnapshotHash != run.DefinitionSnapshotHash || original.WorkflowId != run.WorkflowId || original.WorkflowVersion != run.WorkflowVersion) throw Denied("replay-definition-mismatch");
            }
        }
        else if (run.SourceType == WorkflowRunSourceTypes.ChildWorkflow && request.ActorType == WorkflowRunActorTypes.System && run.ParentRunId is { } parentId)
        {
            var parent = await LoadWorkflowAsync(parentId, run.TeamId, cancellationToken).ConfigureAwait(false);
            var parentReceipt = await ReadWorkflowReceiptAsync(parent, lineage, cancellationToken).ConfigureAwait(false);
            await ValidateWorkflowAsync(parent, parentReceipt, cancellationToken).ConfigureAwait(false);
            subjects.AddRange(parentReceipt.Subjects);
            ceiling = AgentAutonomyPolicy.Clamp(ceiling, parentReceipt.GrantedCeiling);
        }
        else if (request.ActorType == WorkflowRunActorTypes.Webhook || request.ActorType == WorkflowRunActorTypes.System && run.SourceType == WorkflowRunSourceTypes.ScheduleCron)
        {
            if (version == null || request.ActivationId is not { } activationId) throw Denied("missing-trigger-delegation");
            var activation = await _db.WorkflowActivation.AsNoTracking().Include(a => a.Workflow).SingleOrDefaultAsync(a => a.Id == activationId && a.WorkflowId == version.WorkflowId && a.Workflow.TeamId == run.TeamId && a.DeletedDate == null, cancellationToken).ConfigureAwait(false) ?? throw Denied("unknown-trigger-delegation");
            VerifyActivation(admission, activation, version);
            activationRevision = activation.AuthorityRevision;
            subjects.Add(await ResolveSubjectAsync(new SubjectRequest(run.TeamId, ActivationAuthoritySnapshot.Publisher(activation), "delegator", TeamPermissions.RunsLaunch), cancellationToken).ConfigureAwait(false));
        }
        else throw Denied("unknown-actor-source");

        return new AgentExecutionAuthority
        {
            Version = ReceiptVersion, PolicyVersion = PolicyVersion, TeamId = run.TeamId, LogicalRunId = run.Id,
            SourceKind = run.SourceType, DefinitionHash = definitionHash, ActivationId = request.ActivationId,
            ActivationRevision = activationRevision, ParentRunId = run.ParentRunId, GrantedCeiling = ceiling,
            IssuedAt = DateTimeOffset.UtcNow, Subjects = subjects.Distinct().ToArray(),
        };
    }

    private static void VerifyActivation(WorkflowAdmission admission, WorkflowActivation activation, WorkflowVersion version)
    {
        // Mutable audit timestamps cannot prove which historical revision delegated execution.
        if (admission.Legacy) throw Denied("legacy-trigger-authority-unverifiable");
        JsonElement snapshot;
        try { snapshot = JsonDocument.Parse(admission.Request.ActivationSnapshotJson ?? "null").RootElement; }
        catch (JsonException) { throw Denied("invalid-trigger-snapshot"); }
        if (snapshot.ValueKind != JsonValueKind.Object || !snapshot.TryGetProperty("id", out var id) || !id.TryGetGuid(out var snapshotId) || snapshotId != activation.Id
            || !snapshot.TryGetProperty("typeKey", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != activation.TypeKey || activation.TypeKey != ActivationTypeFor(admission.Request)
            || !snapshot.TryGetProperty("config", out var config) || !JsonElement.DeepEquals(config, JsonDocument.Parse(activation.ConfigJson).RootElement)
            || !snapshot.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True) throw Denied("trigger-snapshot-mismatch");
        if (!activation.Enabled || version.Version != activation.Workflow.LatestVersion
            || !snapshot.TryGetProperty("authorityRevision", out var revision) || !revision.TryGetGuid(out var snapshotRevision) || snapshotRevision == Guid.Empty || snapshotRevision != activation.AuthorityRevision
            || !snapshot.TryGetProperty("publisherId", out var publisher) || !publisher.TryGetGuid(out var publisherId) || publisherId != ActivationAuthoritySnapshot.Publisher(activation)
            || !snapshot.TryGetProperty("workflowVersion", out var workflowVersion) || !workflowVersion.TryGetInt32(out var snapshotVersion) || snapshotVersion != version.Version) throw Denied("stale-trigger-delegation");
    }

    // The schedule producer has a canonical source distinct from its activation catalog key; webhook matchers use theirs verbatim.
    private static string ActivationTypeFor(WorkflowRunRequest request) => request.ActorType == WorkflowRunActorTypes.System && request.SourceType == WorkflowRunSourceTypes.ScheduleCron ? "trigger.schedule" : request.SourceType;

    private async Task<AgentExecutionAuthority> ReadWorkflowReceiptAsync(WorkflowRun run, HashSet<Guid> lineage, CancellationToken cancellationToken)
    {
        var stored = await _db.WorkflowRunExecutionAuthority.AsNoTracking().Where(r => r.WorkflowRunId == run.Id && r.TeamId == run.TeamId).Select(r => r.ReceiptJson).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (stored != null) return Deserialize(stored);
        var request = await _db.WorkflowRunRequest.AsNoTracking().SingleOrDefaultAsync(r => r.Id == run.RunRequestId && r.TeamId == run.TeamId, cancellationToken).ConfigureAwait(false) ?? throw Denied("unknown-run-request");
        var receipt = await MintCoreAsync(new WorkflowAdmission(run, request, true), lineage, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(receipt, AgentJson.Options);
        var written = await _db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO workflow_run_execution_authority (workflow_run_id, team_id, receipt_json, issued_at) VALUES ({run.Id}, {run.TeamId}, {json}::jsonb, {receipt.IssuedAt}) ON CONFLICT (workflow_run_id) DO NOTHING", cancellationToken).ConfigureAwait(false);
        return written == 1 ? receipt : Deserialize(await _db.WorkflowRunExecutionAuthority.AsNoTracking().Where(r => r.WorkflowRunId == run.Id && r.TeamId == run.TeamId).Select(r => r.ReceiptJson).SingleAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task ValidateWorkflowAsync(WorkflowRun run, AgentExecutionAuthority receipt, CancellationToken cancellationToken)
    {
        if (receipt.TeamId != run.TeamId || receipt.LogicalRunId != run.Id || receipt.SourceKind != run.SourceType) throw Denied("receipt-scope-mismatch");
        if (run.Status is WorkflowRunStatus.Cancelled or WorkflowRunStatus.Failure or WorkflowRunStatus.Success) throw Denied("workflow-terminal");
        await ValidateSubjectsAsync(receipt, cancellationToken).ConfigureAwait(false);
        var lineage = new HashSet<Guid> { run.Id };
        while (run.SourceType == WorkflowRunSourceTypes.ChildWorkflow && run.ParentRunId is { } parentId)
        {
            if (!lineage.Add(parentId)) throw Denied("cyclic-parent-authority");
            run = await LoadWorkflowAsync(parentId, run.TeamId, cancellationToken).ConfigureAwait(false);
            if (run.Status is WorkflowRunStatus.Cancelled or WorkflowRunStatus.Failure or WorkflowRunStatus.Success) throw Denied("parent-terminal");
        }
    }

    private async Task ValidateSubjectsAsync(AgentExecutionAuthority receipt, CancellationToken cancellationToken)
    {
        if (receipt.Version != ReceiptVersion || receipt.PolicyVersion != PolicyVersion || receipt.Subjects.Count == 0 || !Enum.IsDefined(receipt.GrantedCeiling)) throw Denied("unknown-authority-policy");
        foreach (var subject in receipt.Subjects)
        {
            var current = await ResolveSubjectAsync(new SubjectRequest(receipt.TeamId, subject.UserId, subject.Kind, subject.Permission, subject.GlobalAdmin), cancellationToken).ConfigureAwait(false);
            if (subject.SecurityStamp != current.SecurityStamp || subject.GlobalAdmin != current.GlobalAdmin || subject.MembershipId != current.MembershipId) throw Denied("principal-grant-revoked");
            if (!TeamPermissionMatrix.IsGranted(current.IssuedRole, subject.Permission)) throw Denied("permission-revoked");
        }
    }

    private async Task<AgentAuthoritySubject> ResolveSubjectAsync(SubjectRequest subject, CancellationToken cancellationToken)
    {
        var (teamId, userId, kind, permission, requireGlobalAdmin) = subject;
        if (userId is null || userId == Guid.Empty || userId == SystemUsers.SeederId) throw Denied("actor-unverifiable");
        var user = await _db.User.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && !u.IsBot && u.DeletedDate == null && u.DeactivatedAt == null, cancellationToken).ConfigureAwait(false) ?? throw Denied("account-inactive");
        if (!await _db.Team.AsNoTracking().AnyAsync(t => t.Id == teamId && t.DeletedDate == null, cancellationToken).ConfigureAwait(false)) throw Denied("team-inactive");
        var admin = await _db.RoleUser.AsNoTracking().Where(ru => ru.UserId == user.Id).Join(_db.Role.AsNoTracking().Where(r => r.Name == Roles.Admin && r.Status), ru => ru.RoleId, r => r.Id, (ru, r) => r.Id).AnyAsync(cancellationToken).ConfigureAwait(false);
        var membership = await _db.TeamMembership.AsNoTracking().SingleOrDefaultAsync(m => m.TeamId == teamId && m.UserId == user.Id, cancellationToken).ConfigureAwait(false);
        if (requireGlobalAdmin == true && !admin) throw Denied("admin-role-revoked");
        var useAdmin = requireGlobalAdmin ?? (membership == null || !TeamPermissionMatrix.IsGranted(membership.Role, permission));
        if (useAdmin && !admin) throw Denied("membership-revoked");
        var role = useAdmin ? TeamRole.Owner : membership?.Role ?? throw Denied("membership-revoked");
        if (!TeamPermissionMatrix.IsGranted(role, permission)) throw Denied("permission-denied");
        return new AgentAuthoritySubject { Kind = kind, UserId = user.Id, SecurityStamp = user.SecurityStamp, MembershipId = useAdmin ? null : membership!.Id, IssuedRole = role, Permission = permission, GlobalAdmin = useAdmin };
    }

    private async Task<WorkflowRun> LoadWorkflowAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => await _db.WorkflowRun.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false) ?? throw Denied("unknown-workflow-run");

    private static AgentAutonomyLevel DefinitionCeiling(string definitionJson)
    {
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(definitionJson, WorkflowJson.Options) ?? throw Denied("unreadable-definition");
        var contract = definition.LaunchContract;
        if (contract == null) return AgentAutonomyLevel.Unleashed;
        var candidates = new[] { contract.RequestedControls?.Autonomy, contract.ResolvedRoute?.Caps.AutonomyCeiling, contract.ResolvedRoute?.EffectiveAutonomy, contract.ResolvedAgentProfile?.AutonomyLevel };
        var ceiling = contract.ResolvedRoute != null && string.IsNullOrWhiteSpace(contract.ResolvedRoute.Caps.AutonomyCeiling) ? AgentAutonomyPolicy.UnboundedRouteCeiling : AgentAutonomyLevel.Unleashed;
        foreach (var value in candidates.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (!Enum.TryParse<AgentAutonomyLevel>(value, true, out var level) || !Enum.IsDefined(level)) throw Denied("invalid-launch-ceiling");
            ceiling = AgentAutonomyPolicy.Clamp(ceiling, level);
        }
        return ceiling;
    }

    private static AgentExecutionAuthority Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<AgentExecutionAuthority>(json, AgentJson.Options) ?? throw Denied("unreadable-authority"); }
        catch (JsonException) { throw Denied("unreadable-authority"); }
    }

    private static AgentAuthorityDeniedException Denied(string reason) => new(reason);
    private sealed record SubjectRequest(Guid TeamId, Guid? UserId, string Kind, string Permission, bool? RequireGlobalAdmin = null);
    private sealed record WorkflowAdmission(WorkflowRun Run, WorkflowRunRequest Request, bool Legacy);
}
