using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public sealed class PairedQualificationSelectionSnapshotSchemaTests
{
    [Fact]
    public void Selection_snapshots_are_nullable_json_and_the_database_requires_both_exact_protocol_bindings()
    {
        using var db = Infrastructure.EmptyTestDb.New();
        var entity = db.Model.FindEntityType(typeof(PairedQualificationProtocol)).ShouldNotBeNull();
        foreach (var propertyName in new[] { nameof(PairedQualificationProtocol.ControlSelectionJson), nameof(PairedQualificationProtocol.CandidateSelectionJson) })
        {
            var property = entity.FindProperty(propertyName).ShouldNotBeNull();
            property.IsNullable.ShouldBeTrue("legacy complete campaigns remain recoverable without inventing their historical treatment");
            property.FindAnnotation("Relational:ColumnType").ShouldNotBeNull().Value.ShouldBe("jsonb");
        }

        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0220_paired_qualification_selection_snapshot.sql"));
        sql.ShouldContain("control_selection_json IS NULL AND candidate_selection_json IS NULL");
        sql.ShouldContain("control_selection_json ? 'modelCredentialModelId'");
        sql.ShouldContain("candidate_selection_json ? 'modelCredentialModelId'");
        sql.ShouldContain("control_selection_json ? 'maxCostUsd'");
        sql.ShouldContain("candidate_selection_json ? 'maxCostUsd'");
        sql.ShouldContain("::uuid = control_model_row_id");
        sql.ShouldContain("::uuid = candidate_model_row_id");
        sql.ShouldContain("::numeric = max_cost_usd_per_launch");
        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith("0220_paired_qualification_selection_snapshot.sql", StringComparison.OrdinalIgnoreCase));
    }
}
