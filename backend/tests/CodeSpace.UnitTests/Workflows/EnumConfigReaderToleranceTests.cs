using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the integer / enum config readers tolerate the STRING-encoded values the editor stores (SchemaForm's
/// {{ref}} unification). Regression for the P0.2 remainder: a <c>"1"</c> for outputReviewMode / reviewMode used to
/// read as null and silently revert the field to Off.
/// </summary>
[Trait("Category", "Unit")]
public class EnumConfigReaderToleranceTests
{
    private static Dictionary<string, JsonElement> Bag(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    [Fact]
    public void AgentCode_ReadInt_reads_a_string_encoded_number()
    {
        AgentCodeNode.ReadInt(Bag(new { outputReviewMode = "1" }), "outputReviewMode").ShouldBe(1);   // string, as the editor stores it
        AgentCodeNode.ReadInt(Bag(new { outputReviewMode = 2 }), "outputReviewMode").ShouldBe(2);     // plain number still reads
        AgentCodeNode.ReadInt(Bag(new { }), "outputReviewMode").ShouldBeNull();                       // absent → null
        AgentCodeNode.ReadInt(Bag(new { outputReviewMode = "nope" }), "outputReviewMode").ShouldBeNull(); // non-numeric string → null
    }

    [Fact]
    public void PlanAuthor_ReadReviewMode_reads_a_string_encoded_review_mode()
    {
        PlanAuthorNode.ReadReviewMode(Bag(new { reviewMode = "1" })).ShouldBe(ReviewMode.Gate);   // "1" → Gate, not dropped to None
        PlanAuthorNode.ReadReviewMode(Bag(new { reviewMode = 1 })).ShouldBe(ReviewMode.Gate);     // plain number still reads
        PlanAuthorNode.ReadReviewMode(Bag(new { reviewMode = "nope" })).ShouldBe(ReviewMode.None);
        PlanAuthorNode.ReadReviewMode(Bag(new { })).ShouldBe(ReviewMode.None);
    }
}
