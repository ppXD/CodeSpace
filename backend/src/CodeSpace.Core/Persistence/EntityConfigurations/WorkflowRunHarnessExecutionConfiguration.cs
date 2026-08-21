using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class WorkflowRunHarnessExecutionConfiguration : IEntityTypeConfiguration<WorkflowRunHarnessExecution>
{
    public void Configure(EntityTypeBuilder<WorkflowRunHarnessExecution> builder)
    {
        builder.ToTable(WorkflowRunDataNames.HarnessExecution, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_harness_execution_error", "(error_code IS NULL AND error_message IS NULL) OR (error_code IS NOT NULL AND btrim(error_code) <> '')");
            table.HasCheckConstraint("ck_workflow_run_harness_execution_head", "generation > 0 AND attempt_count >= 0 AND next_attempt_ordinal = attempt_count + 1 AND runner_locator_schema_version > 0 AND revision > 0");
            table.HasCheckConstraint("ck_workflow_run_harness_execution_identity", "harness_type_key ~ '^[a-z0-9][a-z0-9._-]{0,126}/v[1-9][0-9]*$' AND runner_kind ~ '^[a-z0-9][a-z0-9._-]{0,63}$' AND (runner_host_affinity IS NULL OR btrim(runner_host_affinity) <> '')");
            table.HasCheckConstraint("ck_workflow_run_harness_execution_lease", "lease_fence >= 0 AND ((lease_owner_id IS NULL AND lease_expires_at IS NULL) OR (lease_owner_id IS NOT NULL AND lease_fence > 0 AND lease_expires_at IS NOT NULL))");
            table.HasCheckConstraint("ck_workflow_run_harness_execution_model_call_observation", "model_call_observation_coverage IS NULL OR model_call_observation_coverage ~ '^[A-Z][A-Za-z0-9]{0,47}$'");
            table.HasCheckConstraint("ck_workflow_run_harness_execution_state", "state IN ('Pending', 'Running', 'Exited', 'Abandoned')");
            table.HasCheckConstraint("ck_workflow_run_harness_execution_terminal", "(state IN ('Pending', 'Running') AND terminal_at IS NULL AND error_code IS NULL) OR (state = 'Exited' AND terminal_at IS NOT NULL AND attempt_count > 0) OR (state = 'Abandoned' AND terminal_at IS NOT NULL AND error_code IS NOT NULL)");
            table.HasCheckConstraint("ck_workflow_run_harness_execution_terminal_lease", "state IN ('Pending', 'Running') OR (lease_owner_id IS NULL AND lease_expires_at IS NULL)");
            table.HasCheckConstraint("ck_workflow_run_harness_execution_time", "last_modified_at >= created_at AND (terminal_at IS NULL OR (terminal_at >= created_at AND last_modified_at >= terminal_at)) AND (deadline_at IS NULL OR deadline_at > created_at)");
        });
        builder.HasKey(execution => execution.Id);

        // Lets the attempt's composite foreign key prove its denormalized Agent Run scope belongs to THIS execution,
        // rather than trusting a writer to stamp the same run id twice.
        builder.HasAlternateKey(execution => new { execution.TeamId, execution.Id, execution.AgentRunId }).HasName("ak_workflow_run_harness_execution_scope");

        builder.Property(execution => execution.HarnessTypeKey).HasMaxLength(160);
        builder.Property(execution => execution.ModelCallObservationCoverage).HasMaxLength(48);
        builder.Property(execution => execution.RunnerKind).HasMaxLength(64);
        builder.Property(execution => execution.RunnerHostAffinity).HasMaxLength(255);
        builder.Property(execution => execution.State).HasConversion<string>().HasMaxLength(24);
        builder.Property(execution => execution.ErrorCode).HasMaxLength(128);
        builder.Property(execution => execution.ErrorMessage).HasMaxLength(2048);
        builder.Property(execution => execution.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne<Team>().WithMany().HasForeignKey(execution => execution.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(execution => execution.AgentRun).WithMany()
            .HasForeignKey(execution => new { execution.TeamId, execution.AgentRunId })
            .HasPrincipalKey(run => new { run.TeamId, run.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(execution => new { execution.TeamId, execution.AgentRunId, execution.Generation }).IsUnique()
            .HasDatabaseName("ux_workflow_run_harness_execution_generation");
        builder.HasIndex(execution => new { execution.TeamId, execution.State, execution.LastModifiedAt, execution.Id })
            .HasDatabaseName("ix_workflow_run_harness_execution_state_modified");
        builder.HasIndex(execution => new { execution.LeaseExpiresAt, execution.TeamId, execution.Id })
            .HasDatabaseName("ix_workflow_run_harness_execution_lease_expiry").HasFilter("state IN ('Pending', 'Running')");

        // The stale-execution reaper's age scan. It cannot ride ix_..._lease_expiry: a generation whose launch died
        // before its first attempt has lease_expires_at NULL by birth, so an expiry predicate never returns it — and
        // until something Abandons it, its AgentRun can never open another generation.
        builder.HasIndex(execution => new { execution.LastModifiedAt, execution.TeamId, execution.Id })
            .HasDatabaseName("ix_workflow_run_harness_execution_stale_live").HasFilter("state IN ('Pending', 'Running')");
        builder.HasIndex(execution => new { execution.TeamId, execution.WorkflowRunId, execution.CreatedAt, execution.Id })
            .HasDatabaseName("ix_workflow_run_harness_execution_workflow_run").HasFilter("workflow_run_id IS NOT NULL");
    }
}

public sealed class WorkflowRunHarnessProcessAttemptConfiguration : IEntityTypeConfiguration<WorkflowRunHarnessProcessAttempt>
{
    public void Configure(EntityTypeBuilder<WorkflowRunHarnessProcessAttempt> builder)
    {
        builder.ToTable(WorkflowRunDataNames.HarnessProcessAttempt, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_harness_process_attempt_bounds", "attempt_ordinal > 0 AND worker_fence_epoch > 0 AND claim_fence >= 0 AND revision > 0");
            table.HasCheckConstraint("ck_workflow_run_harness_process_attempt_claim", "(claim_owner_id IS NULL AND claim_expires_at IS NULL) OR (claim_owner_id IS NOT NULL AND claim_fence > 0 AND claim_expires_at IS NOT NULL)");
            table.HasCheckConstraint("ck_workflow_run_harness_process_attempt_error", "(error_code IS NULL AND error_message IS NULL) OR (error_code IS NOT NULL AND btrim(error_code) <> '')");
            table.HasCheckConstraint("ck_workflow_run_harness_process_attempt_locator", "jsonb_typeof(runner_locator_jsonb) = 'object' AND (remote_execution_id IS NULL OR btrim(remote_execution_id) <> '') AND (checkpoint_ref IS NULL OR btrim(checkpoint_ref) <> '')");
            table.HasCheckConstraint("ck_workflow_run_harness_process_attempt_state", "state IN ('Running', 'Exited', 'Lost')");
            table.HasCheckConstraint("ck_workflow_run_harness_process_attempt_terminal", "(state = 'Running' AND exited_at IS NULL AND exit_code IS NULL AND error_code IS NULL) OR (state = 'Exited' AND exited_at IS NOT NULL AND exit_code IS NOT NULL) OR (state = 'Lost' AND exited_at IS NOT NULL AND exit_code IS NULL AND error_code IS NOT NULL)");
            table.HasCheckConstraint("ck_workflow_run_harness_process_attempt_terminal_claim", "state = 'Running' OR (claim_owner_id IS NULL AND claim_expires_at IS NULL)");
            table.HasCheckConstraint("ck_workflow_run_harness_process_attempt_time", "created_at >= started_at AND last_observed_at >= started_at AND last_modified_at >= created_at AND (exited_at IS NULL OR (exited_at >= started_at AND last_observed_at >= exited_at))");
        });
        builder.HasKey(attempt => attempt.Id);

        builder.Property(attempt => attempt.RunnerLocatorJson).HasColumnName("runner_locator_jsonb").HasColumnType("jsonb");
        builder.Property(attempt => attempt.RemoteExecutionId).HasMaxLength(512);
        builder.Property(attempt => attempt.CheckpointRef).HasMaxLength(1024);
        builder.Property(attempt => attempt.State).HasConversion<string>().HasMaxLength(24);
        builder.Property(attempt => attempt.ErrorCode).HasMaxLength(128);
        builder.Property(attempt => attempt.ErrorMessage).HasMaxLength(2048);
        builder.Property(attempt => attempt.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne(attempt => attempt.Execution).WithMany(execution => execution.Attempts)
            .HasForeignKey(attempt => new { attempt.TeamId, attempt.ExecutionId, attempt.AgentRunId })
            .HasPrincipalKey(execution => new { execution.TeamId, execution.Id, execution.AgentRunId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(attempt => new { attempt.TeamId, attempt.ExecutionId, attempt.AttemptOrdinal }).IsUnique()
            .HasDatabaseName("ux_workflow_run_harness_process_attempt_ordinal");
        builder.HasIndex(attempt => new { attempt.TeamId, attempt.AgentRunId, attempt.StartedAt, attempt.Id })
            .HasDatabaseName("ix_workflow_run_harness_process_attempt_run_started");
        builder.HasIndex(attempt => new { attempt.ClaimExpiresAt, attempt.TeamId, attempt.Id })
            .HasDatabaseName("ix_workflow_run_harness_process_attempt_live_claim").HasFilter("state = 'Running'");
    }
}
