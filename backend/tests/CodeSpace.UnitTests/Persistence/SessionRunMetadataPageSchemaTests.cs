using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public sealed class SessionRunMetadataPageSchemaTests
{
    [Fact]
    public void Migration_adds_only_the_session_run_number_membership_index()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0162_workflow_run_session_run_number_index.sql"));

        sql.ShouldContain("idx_workflow_run_session_run_number", Case.Sensitive);
        sql.ShouldContain("ON workflow_run (session_id, run_number)", Case.Sensitive);
        sql.ShouldContain("WHERE session_id IS NOT NULL", Case.Sensitive);
        sql.ShouldNotContain("outputs_jsonb", Case.Insensitive);
        sql.ShouldNotContain("normalized_payload_json", Case.Insensitive);
    }
}
