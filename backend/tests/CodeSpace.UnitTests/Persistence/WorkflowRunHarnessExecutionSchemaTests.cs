using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the durable harness EXECUTION IDENTITY: one execution row per (Agent Run, generation), and one attempt row
/// per physical harness process inside it. The two tables carry the names the data contract already registers, so a
/// later slice attaching native records or semantic events joins the same nouns rather than inventing a second set.
///
/// <para>The execution is keyed to the AGENT RUN, not to a Workflow Run, because an Agent Run is deliberately
/// standalone-capable — <c>AgentRun.WorkflowRunId</c> is nullable — so a NOT NULL workflow run would make a
/// standalone execution unrepresentable. The workflow run rides as the nullable soft correlation it is on
/// <c>AgentRun</c> itself.</para>
///
/// <para>Backend neutrality is structural: the runner KIND and its locator schema version live on the execution, and
/// each attempt's locator is an opaque JSON object no shared code interprets — so a container or remote runner
/// arrives as a new kind, never as a new column.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowRunHarnessExecutionSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Execution_is_one_agent_run_generation_with_a_releasable_lease_and_a_neutral_runner_kind()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunHarnessExecution>(db);

        entity.GetTableName().ShouldBe(WorkflowRunDataNames.HarnessExecution);
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "AgentRunId", "AttemptCount", "CreatedAt", "DeadlineAt", "ErrorCode", "ErrorMessage", "Generation",
            "HarnessTypeKey", "Id", "LastModifiedAt", "LeaseExpiresAt", "LeaseFence", "LeaseOwnerId",
            "NextAttemptOrdinal", "Revision", "RunnerHostAffinity", "RunnerKind", "RunnerLocatorSchemaVersion",
            "State", "TeamId", "TerminalAt", "WorkflowRunId", "Xmin",
        }.Order());
        entity.FindProperty(nameof(WorkflowRunHarnessExecution.State))!.GetMaxLength().ShouldBe(24);
        entity.FindProperty(nameof(WorkflowRunHarnessExecution.Xmin))!.IsConcurrencyToken.ShouldBeTrue();
        entity.FindProperty(nameof(WorkflowRunHarnessExecution.WorkflowRunId))!.IsNullable.ShouldBeTrue(
            customMessage: "an AgentRun may be standalone, so a harness execution must be representable with no workflow run");

        AlternateKey(entity, "ak_workflow_run_harness_execution_scope").Properties.Select(property => property.Name)
            .ShouldBe(new[] { "TeamId", "Id", "AgentRunId" });
        ForeignKey(entity, typeof(AgentRun)).Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "AgentRunId" });

        var generation = Index(entity, "ux_workflow_run_harness_execution_generation");
        generation.IsUnique.ShouldBeTrue();
        generation.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "AgentRunId", "Generation" });
        Index(entity, "ix_workflow_run_harness_execution_lease_expiry").GetFilter().ShouldBe("state IN ('Pending', 'Running')");
        Index(entity, "ix_workflow_run_harness_execution_workflow_run").GetFilter().ShouldBe("workflow_run_id IS NOT NULL");

        var staleLive = Index(entity, "ix_workflow_run_harness_execution_stale_live");
        staleLive.Properties.Select(property => property.Name).ShouldBe(new[] { "LastModifiedAt", "TeamId", "Id" },
            customMessage: "the reaper age scan must lead on LastModifiedAt: a generation that never held a lease has LeaseExpiresAt null, so the lease-expiry index cannot find it and its Agent Run stays unrelaunchable");
        staleLive.GetFilter().ShouldBe("state IN ('Pending', 'Running')");

        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_harness_execution_error", "ck_workflow_run_harness_execution_head",
            "ck_workflow_run_harness_execution_identity", "ck_workflow_run_harness_execution_lease",
            "ck_workflow_run_harness_execution_state", "ck_workflow_run_harness_execution_terminal",
            "ck_workflow_run_harness_execution_terminal_lease", "ck_workflow_run_harness_execution_time",
        }, ignoreOrder: true);
        Constraint(entity, "ck_workflow_run_harness_execution_head").ShouldContain("next_attempt_ordinal = attempt_count + 1");
        Constraint(entity, "ck_workflow_run_harness_execution_terminal_lease").ShouldContain("lease_owner_id IS NULL");
        Constraint(entity, "ck_workflow_run_harness_execution_identity").ShouldContain("runner_kind");
    }

    [Fact]
    public void Attempt_is_one_physical_process_with_an_opaque_locator_and_a_claim_it_loses_at_terminal()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunHarnessProcessAttempt>(db);

        entity.GetTableName().ShouldBe(WorkflowRunDataNames.HarnessProcessAttempt);
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "AgentRunId", "AttemptOrdinal", "CheckpointRef", "ClaimExpiresAt", "ClaimFence", "ClaimOwnerId",
            "CreatedAt", "ErrorCode", "ErrorMessage", "ExecutionId", "ExitCode", "ExitedAt", "Id", "LastModifiedAt",
            "LastObservedAt", "RemoteExecutionId", "Revision", "RunnerLocatorJson", "StartedAt", "State", "TeamId",
            "WorkerFenceEpoch", "Xmin",
        }.Order());
        entity.FindProperty(nameof(WorkflowRunHarnessProcessAttempt.RunnerLocatorJson))!.GetColumnName().ShouldBe("runner_locator_jsonb");
        entity.FindProperty(nameof(WorkflowRunHarnessProcessAttempt.Xmin))!.IsConcurrencyToken.ShouldBeTrue();

        ForeignKey(entity, typeof(WorkflowRunHarnessExecution)).Properties.Select(property => property.Name)
            .ShouldBe(new[] { "TeamId", "ExecutionId", "AgentRunId" });

        var ordinal = Index(entity, "ux_workflow_run_harness_process_attempt_ordinal");
        ordinal.IsUnique.ShouldBeTrue();
        ordinal.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "ExecutionId", "AttemptOrdinal" });
        Index(entity, "ix_workflow_run_harness_process_attempt_live_claim").GetFilter().ShouldBe("state = 'Running'");

        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_harness_process_attempt_bounds", "ck_workflow_run_harness_process_attempt_claim",
            "ck_workflow_run_harness_process_attempt_error", "ck_workflow_run_harness_process_attempt_locator",
            "ck_workflow_run_harness_process_attempt_state", "ck_workflow_run_harness_process_attempt_terminal",
            "ck_workflow_run_harness_process_attempt_terminal_claim", "ck_workflow_run_harness_process_attempt_time",
        }, ignoreOrder: true);
        Constraint(entity, "ck_workflow_run_harness_process_attempt_locator").ShouldContain("jsonb_typeof(runner_locator_jsonb) = 'object'");
        Constraint(entity, "ck_workflow_run_harness_process_attempt_terminal_claim").ShouldContain("claim_owner_id IS NULL");
        Constraint(entity, "ck_workflow_run_harness_process_attempt_bounds").ShouldContain("attempt_ordinal > 0");
    }

    /// <summary>
    /// The schema only exists where DbUp can see it. A file that never ships is indistinguishable from a file that
    /// was never written: <c>PerformUpgrade</c> reports success, and the first row a later slice writes is the only
    /// evidence the tables were never created.
    /// </summary>
    [Fact]
    public void Its_migration_travels_with_the_build()
    {
        DbUpRunner.DiscoverScriptNames().ShouldContain(
            name => name.EndsWith("0137_workflow_run_harness_execution.sql", StringComparison.OrdinalIgnoreCase),
            customMessage: "0137_workflow_run_harness_execution.sql must be discoverable by DbUp. Migrations are copied " +
                           "next to the assembly by the Content item in CodeSpace.Core.csproj; if this one is not there, " +
                           "a deployed image creates neither table and still reports a successful upgrade.");
    }

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options;
        return new CodeSpaceDbContext(options);
    }

    private static IEntityType Entity<TEntity>(CodeSpaceDbContext db) => db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity)).ShouldNotBeNull();
    private static IIndex Index(IEntityType entity, string name) => entity.GetIndexes().Single(index => index.GetDatabaseName() == name);
    private static IKey AlternateKey(IEntityType entity, string name) => entity.GetKeys().Single(key => key.GetName() == name);
    private static IForeignKey ForeignKey(IEntityType entity, Type principal) => entity.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == principal);
    private static string Constraint(IEntityType entity, string name) => entity.GetCheckConstraints().Single(constraint => constraint.Name == name).Sql;
}
