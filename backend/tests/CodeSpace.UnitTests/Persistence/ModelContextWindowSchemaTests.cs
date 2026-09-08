using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public class ModelContextWindowSchemaTests
{
    [Fact]
    public void Migration_adds_a_nullable_positive_context_capacity_to_the_model_row()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0205_model_credential_model_context_window.sql"));

        sql.ShouldContain("context_window_tokens integer NULL", Case.Insensitive);
        sql.ShouldContain("context_window_tokens > 0", Case.Insensitive);
    }
}
