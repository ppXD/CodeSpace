using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>The run canvas metadata plane must never become another spelling of the legacy whole-detail read.</summary>
public class WorkflowRunViewMetadataReaderTests
{
    [Fact]
    public void Metadata_queries_carry_only_bounded_identity_and_topology_fields()
    {
        var sql = new[]
        {
            WorkflowRunViewMetadataReader.CellMetadataSql,
            WorkflowRunViewMetadataReader.LinkMetadataSql,
            WorkflowRunViewMetadataReader.TopologySql,
        };

        foreach (var statement in sql)
        {
            statement.ShouldNotContain("outputs_jsonb", Case.Insensitive);
            statement.ShouldNotContain("inputs_jsonb", Case.Insensitive);
            statement.ShouldNotContain("normalized_payload_json", Case.Insensitive);
            statement.ShouldNotContain("payload_json", Case.Insensitive);
            statement.ShouldNotContain("artifact", Case.Insensitive);
        }

        WorkflowRunViewMetadataReader.CellMetadataSql.ShouldContain("run_id", Case.Sensitive);
        WorkflowRunViewMetadataReader.CellMetadataSql.ShouldContain("node_id", Case.Sensitive);
        WorkflowRunViewMetadataReader.CellMetadataSql.ShouldContain("iteration_key", Case.Sensitive);
        WorkflowRunViewMetadataReader.TopologySql.ShouldNotContain("'config'", Case.Sensitive);
        WorkflowRunViewMetadataReader.TopologySql.ShouldNotContain("'inputs'", Case.Sensitive);
        WorkflowRunViewMetadataReader.TopologySql.ShouldNotContain("'prompt'", Case.Sensitive);
        WorkflowRunViewMetadataReader.TopologySql.ShouldContain("octet_length(definition::text)", Case.Sensitive,
            "one frozen definition is detoasted once and bounded by its logical JSON bytes before graph extraction");
        WorkflowRunViewMetadataReader.LinkMetadataSql.ShouldContain("wait.token ~*", Case.Sensitive,
            "a malformed durable link is Corrupt, never silently presented as a missing link");
    }

    [Fact]
    public void Cell_page_admits_the_engine_map_ceiling_and_names_larger_views_as_truncated()
    {
        WorkflowRunViewMetadataReader.MaximumCells.ShouldBeGreaterThanOrEqualTo(MapPlan.MaxBranchesCeiling * 2,
            "the bounded view admits two complete maximum-size branch waves before returning an explicit truncated prefix");
        Enum.GetNames<WorkflowRunViewAvailability>().ShouldContain(nameof(WorkflowRunViewAvailability.Truncated));
    }
}
