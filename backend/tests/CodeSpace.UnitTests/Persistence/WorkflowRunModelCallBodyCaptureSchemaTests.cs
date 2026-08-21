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
        entity.FindProperty(nameof(WorkflowRunModelCallBodyCapture.Revision))!.IsConcurrencyToken.ShouldBeTrue();
        entity.GetIndexes().Single(index => index.GetDatabaseName() == "ux_workflow_run_model_call_body_capture_identity").IsUnique.ShouldBeTrue();
        entity.GetIndexes().ShouldContain(index => index.GetDatabaseName() == "ix_workflow_run_model_call_body_capture_pending");
        db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(WorkflowRunModelCallAttempt))!.GetIndexes()
            .ShouldContain(index => index.GetDatabaseName() == "ix_workflow_run_model_call_attempt_body_capture"
                && index.GetFilter() == "source_terminal_record_id IS NOT NULL");
        WorkflowRunDataNames.All.ShouldContain(WorkflowRunDataNames.ModelCallBodyCapture);
    }
}
