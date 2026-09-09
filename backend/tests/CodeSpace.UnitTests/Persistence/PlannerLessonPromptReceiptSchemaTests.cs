using CodeSpace.Core.Persistence.Db;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public sealed class PlannerLessonPromptReceiptSchemaTests
{
    [Fact]
    public void Migration_pins_tenancy_bounds_semantic_shape_and_immutability()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0217_planner_lesson_prompt_receipt.sql"));
        sql.ShouldContain("PRIMARY KEY (workflow_run_id, prompt_key)");
        sql.ShouldContain("FOREIGN KEY (team_id, workflow_run_id)");
        sql.ShouldContain("cardinality(lesson_ids) <= 5");
        sql.ShouldContain("cardinality(candidate_ids) <= 20");
        sql.ShouldContain("lesson_ids <@ candidate_ids");
        sql.ShouldContain("lesson_arm = 'withheld' AND relevance_status = 'withheld'");
        sql.ShouldContain("lesson_arm = 'none' AND relevance_status = 'no-candidates'");
        sql.ShouldContain("(lesson_arm = 'none') = (cardinality(candidate_ids) = 0)");
        sql.ShouldContain("BEFORE UPDATE OR DELETE");
        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith("0217_planner_lesson_prompt_receipt.sql", StringComparison.OrdinalIgnoreCase));
    }
}
