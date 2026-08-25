using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public sealed class WorkflowRunModelCallBodyCaptureSchemaTests
{
    [Fact]
    public void Capture_ledger_is_run_owned_retryable_and_queryable_by_pending_work()
    {
        using var db = new CodeSpaceDbContext(new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused")
            .UseSnakeCaseNamingConvention().Options);
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(WorkflowRunModelCallBodyCapture));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe(WorkflowRunDataNames.ModelCallBodyCapture);
        entity.FindProperty(nameof(WorkflowRunModelCallBodyCapture.MaterializationFormat))!.GetMaxLength().ShouldBe(64);
        entity.FindProperty(nameof(WorkflowRunModelCallBodyCapture.Revision))!.IsConcurrencyToken.ShouldBeTrue();
        entity.GetIndexes().Single(index => index.GetDatabaseName() == "ux_workflow_run_model_call_body_capture_identity").IsUnique.ShouldBeTrue();
        entity.GetIndexes().ShouldContain(index => index.GetDatabaseName() == "ix_workflow_run_model_call_body_capture_pending");
        db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(WorkflowRunModelCallAttempt))!.GetIndexes()
            .ShouldContain(index => index.GetDatabaseName() == "ix_workflow_run_model_call_attempt_body_capture"
                && index.GetFilter() == "source_terminal_record_id IS NOT NULL");
        WorkflowRunDataNames.All.ShouldContain(WorkflowRunDataNames.ModelCallBodyCapture);
    }

    [Fact]
    public void Materialization_transition_is_database_fenced_and_available_metadata_is_exact()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles",
            "0152_workflow_run_model_call_body_materialization.sql"));

        migration.ShouldContain("OLD.next_materialization_at > now_at");
        migration.ShouldContain("NEW.lease_fence <> OLD.lease_fence + 1");
        migration.ShouldContain("settlement_owner_id IS DISTINCT FROM OLD.lease_owner_id");
        migration.ShouldContain("settlement_fence IS DISTINCT FROM OLD.lease_fence");
        migration.ShouldContain("OLD.lease_expires_at <= now_at");
        migration.ShouldContain("model-call body reference update cannot rewrite projected attempt facts");
        migration.ShouldContain("artifact_team_id IS DISTINCT FROM NEW.team_id");
        migration.ShouldContain("NEW.source_sha256 IS DISTINCT FROM artifact_sha256");
        migration.ShouldContain("target_artifact_id IS DISTINCT FROM NEW.artifact_id");
        migration.ShouldContain("application/vnd.codespace.workflow-model-call-body");
        migration.ShouldContain("SET materialization_format = 'external-artifact/v1'");
        migration.ShouldContain("IF OLD.state <> 'Pending' THEN",
            customMessage: "the Available rows backfilled as external must make format immutable with their terminal outcome");
    }

    [Fact]
    public void Started_recovery_preserves_the_fenced_body_reference_seam_and_limits_each_source_transition()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles",
            "0169_workflow_run_model_call_started_recovery.sql"));

        migration.ShouldContain("late model-call terminal evidence must advance exactly one revision");
        migration.ShouldContain("orphaned model-call start settlement cannot rewrite projected attempt facts");
        migration.ShouldContain("model-call body reference update cannot rewrite projected attempt facts");
        migration.ShouldContain("NEW.source_evidence_revision <> OLD.source_evidence_revision",
            customMessage: "a body materializer must keep source evidence stable while it attaches artifact refs");
    }
}
