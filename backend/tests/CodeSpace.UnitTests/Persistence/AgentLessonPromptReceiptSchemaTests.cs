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
}
