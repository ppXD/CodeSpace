using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Test helpers for workflow integration scenarios. Mirrors the
/// <c>SeedBindablePrerequisitesAsync</c> pattern that the outbox + repository tests use:
/// the helper produces a ready-to-use (teamId, userId) pair plus convenience builders for
/// definitions / runs / drains. Tests focus on the assertion, not the seed plumbing.
/// </summary>
public static class WorkflowsTestSeed
{
    /// <summary>
    /// Seeds a fresh user + team + Owner membership. Returns ids the caller scopes mediator
    /// calls under via <see cref="PostgresFixture.BeginScopeAs"/>. The seeded user has the
    /// Admin role so tenancy enforcement doesn't reject test calls.
    /// </summary>
    public static async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync(PostgresFixture fixture)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User
        {
            Id = userId,
            Email = $"workflow-{userId:N}@test.local",
            Name = $"wf-{userId:N}",
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team
        {
            Id = teamId,
            Slug = $"wf-team-{teamId:N}",
            Name = "Workflow Test Team",
            Kind = TeamKind.Workspace,
            OwnerUserId = userId,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        db.TeamMembership.Add(new TeamMembership
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamRole.Owner,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
        return (teamId, userId);
    }

    /// <summary>Minimal valid definition — trigger → terminal — useful as a baseline for CRUD tests.</summary>
    public static WorkflowDefinition MinimalDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.pr.opened", Config = EmptyJson(), Inputs = EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = EmptyJson(), Inputs = EmptyJson() }
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "end" }
        }
    };

    public static JsonElement EmptyJson() => JsonDocument.Parse("{}").RootElement.Clone();
    public static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>
    /// Insert a <c>workflow_run_request</c> + <c>workflow_run</c> pair simulating a manual
    /// run, return the run id. <paramref name="payloadJson"/> becomes
    /// <c>workflow_run_request.normalized_payload_json</c>; the engine sees it as
    /// <c>{{trigger.*}}</c>.
    /// </summary>
    public static async Task<Guid> SeedManualRunAsync(PostgresFixture fixture, Guid workflowId, Guid teamId, int workflowVersion = 1, string payloadJson = "{}")
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId,
            TeamId = teamId,
            WorkflowId = workflowId,
            SourceType = WorkflowRunSourceTypes.Manual,
            ActorType = "user",
            ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = payloadJson,
            Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now,
            VerifiedAt = now,
            NormalizedAt = now,
        });

        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId,
            WorkflowId = workflowId,
            WorkflowVersion = workflowVersion,
            TeamId = teamId,
            // RunRequestId is NOT NULL; every run traces back through exactly one
            // workflow_run_request. The request row is added a few lines above; wire the FK
            // explicitly so EF Core's INSERT carries the value (rather than the Guid default
            // of all-zeros, which would FK-violate).
            RunRequestId = requestId,
            // Seed directly in Enqueued state to mirror what production produces at engine
            // entry. Production has WorkflowService.RunManuallyAsync calling
            // IWorkflowRunDispatcher.DispatchAsync, which performs the Pending→Enqueued CAS
            // and the Hangfire Enqueue. Tests skip the Hangfire side (they call the engine
            // directly to simulate the worker), so the row lands in Enqueued state and the
            // engine's atomic CAS at entry expects Enqueued → Running.
            Status = WorkflowRunStatus.Enqueued,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        // Mirror WorkflowService.RunManuallyAsync's run.queued emit so tests using this seed
        // see the complete lifecycle ledger that production produces. Without this,
        // RunLifecycleLedgerFlowTests' sequence assertion would have a gap.
        var recordLogger = scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>();
        await recordLogger.RunQueuedAsync(runId, CodeSpace.Messages.Constants.WorkflowRunSourceTypes.Manual, SystemUsers.SeederId, CancellationToken.None).ConfigureAwait(false);

        return runId;
    }
}
