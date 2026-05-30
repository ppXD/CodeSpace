using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>
/// <c>workflow_run_node</c> is a SQL view aggregating <c>workflow_run_record</c>. EF treats it
/// as keyless / read-only so any accidental <c>_db.WorkflowRunNode.Add()</c> in app code fails
/// at translation time. The (run, node, iter) tuple still uniquely identifies a row at the SQL
/// level (the view's GROUP BY guarantees it), but EF doesn't need a key for read-only queries.
/// </summary>
public class WorkflowRunNodeConfiguration : IEntityTypeConfiguration<WorkflowRunNode>
{
    public void Configure(EntityTypeBuilder<WorkflowRunNode> builder)
    {
        // ToView pins the SQL object name AND tells EF this is read-only — no migration
        // generation, no INSERT/UPDATE/DELETE methods, no shadow tracking changes.
        builder.HasNoKey().ToView("workflow_run_node");

        builder.Property(n => n.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(n => n.InputsJson).HasColumnName("inputs_jsonb").HasColumnType("jsonb");
        builder.Property(n => n.OutputsJson).HasColumnName("outputs_jsonb").HasColumnType("jsonb");
        builder.Property(n => n.RoutingHintsJson).HasColumnName("routing_hints_jsonb").HasColumnType("jsonb");
    }
}
