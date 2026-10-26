using System.Text.Json;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class ExternalTriggerJsonEqualityTests
{
    [Theory]
    [InlineData("{\"a\":1,\"b\":\"x\"}", "{\"b\":\"\\u0078\",\"a\":1.0}", true)]
    [InlineData("1", "1e0", true)]
    [InlineData("1e-330", "0", false)]
    [InlineData("9007199254740992.1", "9007199254740992", false)]
    [InlineData("12345678901234567890.01", "1234567890123456789001e-2", true)]
    [InlineData("[1,2]", "[2,1]", false)]
    [InlineData("\"1\"", "1", false)]
    [InlineData("\"A\"", "\"a\"", false)]
    public void Json_content_is_compared_without_numeric_rounding(string left, string right, bool equivalent)
    {
        using var a = JsonDocument.Parse(left);
        using var b = JsonDocument.Parse(right);
        ExternalTriggerJsonEquality.AreEquivalent(a.RootElement, b.RootElement).ShouldBe(equivalent);
    }

    [Fact]
    public void Wide_object_member_scans_consume_the_comparison_budget()
    {
        var properties = string.Join(',', Enumerable.Range(0, 50).Select(i => $"\"p{i}\":{i}"));
        using var left = JsonDocument.Parse("{" + properties + "}");
        using var right = JsonDocument.Parse("{" + properties + "}");

        ExternalTriggerJsonEquality.AreEquivalent(left.RootElement, right.RootElement, 100, out var visited, out var exhausted)
            .ShouldBeFalse();
        exhausted.ShouldBeTrue();
        visited.ShouldBe(100);
    }
}
