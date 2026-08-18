using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the durable reduction checkpoint: one row per (harness execution, reducer kind), carrying the exact prefix a
/// reduction consumed and the bounded state it reduced from it. The table is keyed through the execution's own scope
/// alternate key, so its denormalized Agent Run id is proved rather than trusted.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowRunHarnessReductionCheckpointSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Checkpoint_is_one_reduction_per_execution_with_a_frontier_a_count_and_a_fence()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunHarnessReductionCheckpoint>(db);

        entity.GetTableName().ShouldBe(WorkflowRunDataNames.HarnessReductionCheckpoint);
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "AgentRunId", "ContractVersion", "CreatedAt", "ExecutionId", "Id", "LastModifiedAt",
            "PositionJson", "RecordsConsumed", "ReducedStateJson", "ReducerFence", "ReducerKind",
            "ReducerLeaseExpiresAt", "ReducerOwnerId", "Revision", "TeamId", "Xmin",
        }.Order());
        entity.FindProperty(nameof(WorkflowRunHarnessReductionCheckpoint.PositionJson))!.GetColumnName().ShouldBe("position_jsonb");
        entity.FindProperty(nameof(WorkflowRunHarnessReductionCheckpoint.ReducedStateJson))!.GetColumnName().ShouldBe("reduced_state_jsonb");
        entity.FindProperty(nameof(WorkflowRunHarnessReductionCheckpoint.Xmin))!.IsConcurrencyToken.ShouldBeTrue();

        ForeignKey(entity, typeof(WorkflowRunHarnessExecution)).Properties.Select(property => property.Name)
            .ShouldBe(new[] { "TeamId", "ExecutionId", "AgentRunId" },
                customMessage: "the checkpoint joins the execution through its scope alternate key, so the Agent Run id cannot disagree with the execution's own");

        var reducer = Index(entity, "ux_workflow_run_harness_reduction_checkpoint_reducer");
        reducer.IsUnique.ShouldBeTrue();
        reducer.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "ExecutionId", "ReducerKind" });
        Index(entity, "ix_workflow_run_harness_reduction_checkpoint_lease_expiry").GetFilter().ShouldBe("reducer_owner_id IS NOT NULL",
            customMessage: "only a HELD lease can lapse; filtering on a non-null expiry would return rows nothing owns");
        Index(entity, "ix_workflow_run_harness_reduction_checkpoint_agent_run").Properties.Select(property => property.Name)
            .ShouldBe(new[] { "TeamId", "AgentRunId", "LastModifiedAt", "Id" });

        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_harness_reduction_checkpoint_bounds", "ck_workflow_run_harness_reduction_checkpoint_claim",
            "ck_workflow_run_harness_reduction_checkpoint_kind", "ck_workflow_run_harness_reduction_checkpoint_shape",
            "ck_workflow_run_harness_reduction_checkpoint_time",
        }, ignoreOrder: true);
        Constraint(entity, "ck_workflow_run_harness_reduction_checkpoint_bounds").ShouldContain("records_consumed >= 0");
        Constraint(entity, "ck_workflow_run_harness_reduction_checkpoint_claim").ShouldContain("reducer_fence > 0");
        Constraint(entity, "ck_workflow_run_harness_reduction_checkpoint_shape").ShouldContain("jsonb_typeof(position_jsonb -> 'streams') IS NOT DISTINCT FROM 'array'",
            customMessage: "jsonb_typeof of a MISSING key is SQL NULL and a CHECK that evaluates to NULL is SATISFIED, so an `= 'array'` arm admits a position of '{}'");
    }

    /// <summary>
    /// The table name and the owner noun are the ones the data contract registers, so an artifact this row ever
    /// references — an oversized reduced state spilled to storage — has a registered owner kind to be referenced by.
    /// </summary>
    [Fact]
    public void Its_name_and_owner_noun_are_registered_by_the_data_contract()
    {
        WorkflowRunDataNames.HarnessReductionCheckpoint.ShouldBe("workflow_run_harness_reduction_checkpoint");
        WorkflowRunDataNames.All.ShouldContain(WorkflowRunDataNames.HarnessReductionCheckpoint);
        WorkflowRunDataOwnerKinds.HarnessReductionCheckpoint.ShouldBe("harness-reduction-checkpoint");
        WorkflowRunDataOwnerKinds.IsSupported(WorkflowRunDataOwnerKinds.HarnessReductionCheckpoint).ShouldBeTrue();
    }

    /// <summary>
    /// The schema only exists where DbUp can see it. A file that never ships is indistinguishable from a file that was
    /// never written: <c>PerformUpgrade</c> reports success, and the first checkpoint a later slice writes is the only
    /// evidence the table was never created.
    /// </summary>
    [Fact]
    public void Its_migration_travels_with_the_build()
    {
        DbUpRunner.DiscoverScriptNames().ShouldContain(
            name => name.EndsWith("0140_workflow_run_harness_reduction_checkpoint.sql", StringComparison.OrdinalIgnoreCase),
            customMessage: "0140_workflow_run_harness_reduction_checkpoint.sql must be discoverable by DbUp. Migrations are " +
                           "copied next to the assembly by the Content item in CodeSpace.Core.csproj; if this one is not " +
                           "there, a deployed image creates neither the table nor its guard and still reports a successful upgrade.");
    }

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options;
        return new CodeSpaceDbContext(options);
    }

    private static IEntityType Entity<TEntity>(CodeSpaceDbContext db) => db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity)).ShouldNotBeNull();
    private static IIndex Index(IEntityType entity, string name) => entity.GetIndexes().Single(index => index.GetDatabaseName() == name);
    private static IForeignKey ForeignKey(IEntityType entity, Type principal) => entity.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == principal);
    private static string Constraint(IEntityType entity, string name) => entity.GetCheckConstraints().Single(constraint => constraint.Name == name).Sql;
}
