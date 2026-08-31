using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class WorkflowRunCaptureGapConfiguration : IEntityTypeConfiguration<WorkflowRunCaptureGap>
{
    public void Configure(EntityTypeBuilder<WorkflowRunCaptureGap> builder)
    {
        builder.ToTable(WorkflowRunDataNames.CaptureGap, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_capture_gap_bounds", "schema_version > 0");
            table.HasCheckConstraint("ck_workflow_run_capture_gap_channel", "channel IS NULL OR channel IN ('Stdout', 'Stderr', 'Protocol', 'Control', 'SessionState', 'ModelWire', 'ToolWire', 'Hook', 'Metric', 'Debug')");
            table.HasCheckConstraint("ck_workflow_run_capture_gap_attempt_attribution", "(harness_execution_id IS NULL AND harness_process_attempt_id IS NULL AND attempt_worker_fence_epoch IS NULL) OR (agent_run_id IS NOT NULL AND harness_execution_id IS NOT NULL AND harness_process_attempt_id IS NOT NULL AND attempt_worker_fence_epoch IS NOT NULL AND attempt_worker_fence_epoch > 0)");

            // Owner identity, and the reason both keys may be null one at a time but never together: a gap that names
            // no run is a hole nobody can locate, which is no better than the gap a NOT NULL workflow run stopped a
            // standalone Agent Run from recording at all. Kept spelled identically to 0184.
            table.HasCheckConstraint("ck_workflow_run_capture_gap_owner", "workflow_run_id IS NOT NULL OR agent_run_id IS NOT NULL");

            // Four exhaustive, mutually exclusive coordinate systems, so no combination of bounds means nothing. Every
            // comparison on a nullable column carries its own IS NOT NULL: a PostgreSQL CHECK admits a row that
            // evaluates to TRUE *or NULL*, so a bare `range_end >= range_start` over a NULL start would make the whole
            // constraint NULL and ADMIT the malformed span it exists to refuse. Kept spelled identically to 0146.
            table.HasCheckConstraint("ck_workflow_run_capture_gap_range", "(range_kind IN ('Ordinal', 'ByteOffset') AND stream_id IS NOT NULL AND range_start IS NOT NULL AND range_start >= 0 AND (range_end IS NULL OR range_end >= range_start) AND range_started_at IS NULL AND range_ended_at IS NULL) OR (range_kind = 'Time' AND range_started_at IS NOT NULL AND (range_ended_at IS NULL OR range_ended_at >= range_started_at) AND range_start IS NULL AND range_end IS NULL) OR (range_kind = 'Unbounded' AND range_start IS NULL AND range_end IS NULL AND range_started_at IS NULL AND range_ended_at IS NULL)");
            table.HasCheckConstraint("ck_workflow_run_capture_gap_reason", "reason IN ('BoundExceeded', 'WriteRefused', 'ReattachTorn', 'FrameUnreadable') AND (reason_detail IS NULL OR btrim(reason_detail) <> '')");
            table.HasCheckConstraint("ck_workflow_run_capture_gap_resolution", "(resolution = 'Open' AND recovered_at IS NULL AND recovered_by_kind IS NULL AND recovered_by_id IS NULL) OR (resolution = 'Recovered' AND recovered_at IS NOT NULL AND recovered_at >= noticed_at AND recovered_by_kind IS NOT NULL AND recovered_by_kind IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'node-output', 'deliverable', 'capture-gap', 'data-manifest') AND recovered_by_id IS NOT NULL AND btrim(recovered_by_id) <> '')");
            table.HasCheckConstraint("ck_workflow_run_capture_gap_subject", "subject_kind IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'node-output', 'deliverable', 'capture-gap', 'data-manifest') AND (subject_id IS NULL OR btrim(subject_id) <> '') AND btrim(capture_source) <> ''");
            table.HasCheckConstraint("ck_workflow_run_capture_gap_time", "created_at >= noticed_at");
        });
        builder.HasKey(gap => gap.Id);

        builder.Property(gap => gap.SubjectKind).HasMaxLength(48);
        builder.Property(gap => gap.SubjectId).HasMaxLength(512);
        builder.Property(gap => gap.Channel).HasConversion<string>().HasMaxLength(20);
        builder.Property(gap => gap.RangeKind).HasConversion<string>().HasMaxLength(16);
        builder.Property(gap => gap.Reason).HasConversion<string>().HasMaxLength(24);
        builder.Property(gap => gap.ReasonDetail).HasMaxLength(2048);
        builder.Property(gap => gap.CaptureSource).HasMaxLength(64);
        builder.Property(gap => gap.Resolution).HasConversion<string>().HasMaxLength(16);
        builder.Property(gap => gap.RecoveredByKind).HasMaxLength(48);
        builder.Property(gap => gap.RecoveredById).HasMaxLength(512);

        // No concurrency token, unlike the manifest: the only legal UPDATE is the one-way resolution fill, and two
        // writers racing it serialize on the row itself — the second sees a resolution that is no longer Open and the
        // guard refuses it. A token here would add a second answer to a question already settled.
        builder.HasOne<Team>().WithMany().HasForeignKey(gap => gap.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkflowRun>().WithMany().HasForeignKey(gap => new { gap.TeamId, gap.WorkflowRunId })
            .HasPrincipalKey(run => new { run.TeamId, run.Id }).OnDelete(DeleteBehavior.Restrict);

        // The Agent Run key carries the same tenant scope the workflow run key does. The attempt quad used to be what
        // proved a gap's Agent Run belonged to its team, and the gaps that most need naming a run are exactly the ones
        // that cannot carry the quad.
        builder.HasOne<AgentRun>().WithMany().HasForeignKey(gap => new { gap.TeamId, gap.AgentRunId })
            .HasPrincipalKey(run => new { run.TeamId, run.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_workflow_run_capture_gap_agent_run");
        builder.HasOne(gap => gap.HarnessProcessAttempt).WithMany().HasForeignKey(gap => gap.HarnessProcessAttemptId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_workflow_run_capture_gap_harness_process_attempt");

        // The manifest's probe, and the reason a verdict costs a lookup rather than a scan. Partial so it does not grow
        // with recovered spans; the subject suffix makes the per-facet count the same index walk as the run-wide one.
        builder.HasIndex(gap => new { gap.TeamId, gap.WorkflowRunId, gap.SubjectKind }).HasDatabaseName("ix_workflow_run_capture_gap_open").HasFilter("resolution = 'Open'");
        builder.HasIndex(gap => new { gap.WorkflowRunId, gap.NoticedAt, gap.Id }).HasDatabaseName("ix_workflow_run_capture_gap_run_noticed");
        builder.HasIndex(gap => new { gap.TeamId, gap.NoticedAt, gap.Id }).HasDatabaseName("ix_workflow_run_capture_gap_team_noticed");
        builder.HasIndex(gap => new { gap.TeamId, gap.AgentRunId, gap.NoticedAt, gap.Id })
            .HasDatabaseName("ix_workflow_run_capture_gap_agent_run_noticed").HasFilter("agent_run_id IS NOT NULL");
        builder.HasIndex(gap => new { gap.StreamId, gap.RangeStart }).HasDatabaseName("ix_workflow_run_capture_gap_stream").HasFilter("stream_id IS NOT NULL");
    }
}
