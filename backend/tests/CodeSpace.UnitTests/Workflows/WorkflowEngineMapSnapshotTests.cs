using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class WorkflowEngineMapSnapshotTests
{
    [Fact]
    public void Plain_snapshot_returns_the_exact_array_when_count_and_hash_match()
    {
        var array = JsonDocument.Parse("""["a", {"n":2}, true]""").RootElement.Clone();

        var elements = WorkflowEngine.ValidatePlainMapSnapshot("map", 3, Hash(array), array);

        JsonSerializer.Serialize(elements).ShouldBe(JsonSerializer.Serialize(array.EnumerateArray().Select(element => element.Clone()).ToList()));
    }

    [Fact]
    public void Plain_snapshot_rejects_a_count_mismatch_instead_of_collapsing_the_branch_space()
    {
        var array = JsonDocument.Parse("""["a"]""").RootElement.Clone();

        var exception = Should.Throw<Exception>(() => WorkflowEngine.ValidatePlainMapSnapshot("map", 2, Hash(array), array));

        exception.Message.ShouldContain("froze 2");
        exception.Message.ShouldContain("contained 1");
    }

    [Fact]
    public void Plain_snapshot_rejects_a_content_hash_mismatch_even_when_the_count_matches()
    {
        var array = JsonDocument.Parse("""["a"]""").RootElement.Clone();
        var other = JsonDocument.Parse("""["b"]""").RootElement.Clone();

        var exception = Should.Throw<Exception>(() => WorkflowEngine.ValidatePlainMapSnapshot("map", 1, Hash(other), array));

        exception.Message.ShouldContain("content hash");
    }

    [Fact]
    public void Plain_snapshot_rejects_a_non_array_value_instead_of_returning_zero_elements()
    {
        var value = JsonDocument.Parse("""{"not":"an array"}""").RootElement.Clone();

        var exception = Should.Throw<Exception>(() => WorkflowEngine.ValidatePlainMapSnapshot("map", 0, Hash(value), value));

        exception.Message.ShouldContain("Object");
    }

    private static string Hash(JsonElement array)
    {
        var elements = array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Select(element => element.Clone()).ToList() : new List<JsonElement> { array };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(elements))));
    }
}
