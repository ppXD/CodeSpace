using CodeSpace.Core.Persistence.Db;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public sealed class AgentLessonPromptReceiptSchemaTests
{
    [Fact]
    public void Migration_pins_tenancy_bounds_and_immutability()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0215_agent_lesson_prompt_receipt.sql"));
        sql.ShouldContain("PRIMARY KEY (workflow_run_id, prompt_key)");
        sql.ShouldContain("FOREIGN KEY (team_id, workflow_run_id)");
        sql.ShouldContain("cardinality(lesson_ids) <= 10");
        sql.ShouldContain("BEFORE UPDATE OR DELETE");
        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith("0215_agent_lesson_prompt_receipt.sql", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Relevance_migration_pins_candidates_abstention_provenance_and_selection_shape()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0216_agent_lesson_relevance_receipt.sql"));
        sql.ShouldContain("cardinality(candidate_ids) <= 20");
        sql.ShouldContain("'abstained'");
        sql.ShouldContain("lesson_ids <@ candidate_ids");
        sql.ShouldContain("relevance_status = 'no-candidates'");
        sql.ShouldContain("relevance_status NOT IN ('selected', 'abstained') OR assessment_digest IS NOT NULL");
        sql.ShouldContain("assessment_digest ~ '^[0-9a-f]{64}$'");
        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith("0216_agent_lesson_relevance_receipt.sql", StringComparison.OrdinalIgnoreCase));
    }
}
