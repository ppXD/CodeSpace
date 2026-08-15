using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the additive model-call data contract before any producer consumes it. The logical call owns workflow /
/// node / work-unit / execution-attempt identity; physical provider attempts carry the effective route, wire
/// artifacts, usage, cost and timing. Keeping these as two tables prevents retries and fallbacks from overwriting
/// the logical request that caused them.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowRunModelCallSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void The_logical_call_shape_and_identity_constraints_are_pinned()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(WorkflowRunModelCall));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe("workflow_run_model_call");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "CallOrdinal", "CaptureCompleteness", "CaptureSource", "CreatedBy", "CreatedDate", "ExecutionAttemptId",
            "ExecutionAttemptOrdinal", "ExecutionGeneration", "Id", "IterationKey", "LastModifiedBy", "LastModifiedDate",
            "NodeId", "PlanVersion", "Purpose", "RequestArtifactId", "RequestedModel", "RequestedModelRowId", "RequestedProvider",
            "SchemaVersion", "SelectionPolicy", "TeamId", "WorkflowRunId", "WorkPlanId", "WorkUnitContractHash", "WorkUnitId",
        }.Order());

        entity.FindProperty(nameof(WorkflowRunModelCall.NodeId))!.IsNullable.ShouldBeTrue();
        entity.FindProperty(nameof(WorkflowRunModelCall.WorkPlanId))!.IsNullable.ShouldBeTrue();
        entity.FindProperty(nameof(WorkflowRunModelCall.ExecutionAttemptId))!.IsNullable.ShouldBeTrue();
        entity.FindProperty(nameof(WorkflowRunModelCall.RequestArtifactId))!.IsNullable.ShouldBeTrue();
        entity.FindProperty(nameof(WorkflowRunModelCall.SchemaVersion))!.IsNullable.ShouldBeFalse();
        entity.FindProperty(nameof(WorkflowRunModelCall.CaptureCompleteness))!.ClrType.ShouldBe(typeof(WorkflowRunCaptureCompleteness));
        entity.FindProperty(nameof(WorkflowRunModelCall.Purpose))!.GetMaxLength().ShouldBe(128);
        entity.FindProperty(nameof(WorkflowRunModelCall.SelectionPolicy))!.GetMaxLength().ShouldBe(256);
        new WorkflowRunModelCall().SchemaVersion.ShouldBe(WorkflowRunDataContract.CurrentVersion);

        Index(entity, "ix_workflow_run_model_call_run_created").Properties.Select(p => p.Name)
            .ShouldBe(new[] { "WorkflowRunId", "CreatedDate", "Id" });
        Index(entity, "ix_workflow_run_model_call_execution_attempt").Properties.Select(p => p.Name)
            .ShouldBe(new[] { "ExecutionAttemptId", "CallOrdinal" });
        Index(entity, "ix_workflow_run_model_call_work_unit").Properties.Select(p => p.Name)
            .ShouldBe(new[] { "WorkPlanId", "PlanVersion", "WorkUnitId" });
        Index(entity, "ix_workflow_run_model_call_requested_model_row").Properties.Select(p => p.Name)
            .ShouldBe(new[] { "RequestedModelRowId", "CreatedDate" });

        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_workflow_run_model_call_capture_completeness",
            "ck_workflow_run_model_call_execution_identity",
            "ck_workflow_run_model_call_positive_values",
            "ck_workflow_run_model_call_provenance",
            "ck_workflow_run_model_call_work_unit_identity",
        }, ignoreOrder: true);
    }

    [Fact]
    public void The_physical_attempt_shape_usage_precision_and_parent_scope_are_pinned()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(WorkflowRunModelCallAttempt));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe("workflow_run_model_call_attempt");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "AttemptOrdinal", "CacheReadTokens", "CacheWriteTokens", "CaptureCompleteness", "CaptureSource", "CompletedAt",
            "CostAmount", "CostCurrency", "CreatedBy", "CreatedDate", "EffectiveModel", "EffectiveModelRowId", "EffectiveProvider",
            "EndpointFingerprint", "ErrorArtifactId", "ErrorCode", "FinishReason", "FirstTokenAt", "HttpStatusCode", "Id",
            "InputTokens", "LastModifiedBy", "LastModifiedDate", "ModelCallId", "OutputTokens", "PricingVersion", "ProviderRequestId",
            "ReasoningTokens", "RequestArtifactId", "ResponseArtifactId", "SchemaVersion", "StartedAt", "Status", "TeamId",
            "TransportKind", "WorkflowRunId",
        }.Order());

        entity.FindProperty(nameof(WorkflowRunModelCallAttempt.CostAmount))!.GetPrecision().ShouldBe(18);
        entity.FindProperty(nameof(WorkflowRunModelCallAttempt.CostAmount))!.GetScale().ShouldBe(8);
        entity.FindProperty(nameof(WorkflowRunModelCallAttempt.CostCurrency))!.GetMaxLength().ShouldBe(3);
        entity.FindProperty(nameof(WorkflowRunModelCallAttempt.ProviderRequestId))!.GetMaxLength().ShouldBe(512);
        entity.FindProperty(nameof(WorkflowRunModelCallAttempt.TransportKind))!.GetMaxLength().ShouldBe(64);
        entity.FindProperty(nameof(WorkflowRunModelCallAttempt.EndpointFingerprint))!.GetMaxLength().ShouldBe(256);
        entity.FindProperty(nameof(WorkflowRunModelCallAttempt.FinishReason))!.GetMaxLength().ShouldBe(100);
        entity.FindProperty(nameof(WorkflowRunModelCallAttempt.CaptureCompleteness))!.ClrType.ShouldBe(typeof(WorkflowRunCaptureCompleteness));
        new WorkflowRunModelCallAttempt().SchemaVersion.ShouldBe(WorkflowRunDataContract.CurrentVersion);

        var parent = entity.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(WorkflowRunModelCall));
        parent.Properties.Select(p => p.Name).ShouldBe(new[] { "ModelCallId", "TeamId", "WorkflowRunId" });
        parent.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);

        var ordinal = Index(entity, "ux_workflow_run_model_call_attempt_ordinal");
        ordinal.IsUnique.ShouldBeTrue();
        ordinal.Properties.Select(p => p.Name).ShouldBe(new[] { "ModelCallId", "AttemptOrdinal" });
        Index(entity, "ix_workflow_run_model_call_attempt_run_started").Properties.Select(p => p.Name)
            .ShouldBe(new[] { "WorkflowRunId", "StartedAt", "Id" });
        Index(entity, "ix_workflow_run_model_call_attempt_effective_model_row").Properties.Select(p => p.Name)
            .ShouldBe(new[] { "EffectiveModelRowId", "StartedAt" });

        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_workflow_run_model_call_attempt_capture_completeness",
            "ck_workflow_run_model_call_attempt_cost",
            "ck_workflow_run_model_call_attempt_http_status",
            "ck_workflow_run_model_call_attempt_positive_values",
            "ck_workflow_run_model_call_attempt_status",
            "ck_workflow_run_model_call_attempt_timing",
        }, ignoreOrder: true);
    }

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseNpgsql(UnreachableDatabase)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CodeSpaceDbContext(options);
    }

    private static IIndex Index(IEntityType entity, string name) => entity.GetIndexes().Single(i => i.GetDatabaseName() == name);
}
