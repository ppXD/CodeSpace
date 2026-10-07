using System.Text.Json;
using CodeSpace.Core.Services.Agents.Mcp;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins how a structured result too large to carry is cut for a call that already ran: its long strings keep their start
/// and end, everything else is untouched, so the result is still the tool's declared shape — and it fits.
/// </summary>
[Trait("Category", "Unit")]
public class ToolResultCutTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void A_result_that_fits_is_returned_as_it_is()
    {
        var value = Json(new { exitCode = 0, stdout = "ok" });

        ToolResultCut.Shrunk(value, 1_000).ShouldNotBeNull().GetRawText().ShouldBe(value.GetRawText());
    }

    [Fact]
    public void Long_strings_keep_their_start_and_end_and_the_rest_of_the_shape_is_untouched()
    {
        var value = Json(new { exitCode = 3, status = "Failed", stdout = "HEAD" + new string('x', 50_000) + "TAIL", stderr = "short", lines = new[] { "a", new string('y', 20_000) } });

        var cut = ToolResultCut.Shrunk(value, 10_000).ShouldNotBeNull();

        cut.GetRawText().Length.ShouldBeLessThanOrEqualTo(10_000);
        cut.GetProperty("exitCode").GetInt32().ShouldBe(3);
        (cut.GetProperty("status").GetString(), cut.GetProperty("stderr").GetString()).ShouldBe(("Failed", "short"), "short text is never cut");
        cut.GetProperty("stdout").GetString()!.ShouldStartWith("HEAD");
        cut.GetProperty("stdout").GetString()!.ShouldEndWith("TAIL");
        cut.GetProperty("stdout").GetString()!.ShouldContain("characters cut]");
        cut.GetProperty("lines").GetArrayLength().ShouldBe(2, "a list keeps every item");
    }

    [Fact]
    public void A_result_whose_bulk_is_not_text_cannot_be_cut_to_its_shape()
    {
        ToolResultCut.Shrunk(Json(new { ids = Enumerable.Range(0, 50_000).ToArray() }), 10_000).ShouldBeNull();
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var value = Json(new { text = string.Concat(Enumerable.Repeat("😀", 10_000)) });

        var cut = ToolResultCut.Shrunk(value, 2_000).ShouldNotBeNull();

        Should.NotThrow(() => JsonSerializer.Serialize(cut.GetProperty("text").GetString()), "a lone surrogate would make the answer unserialisable");
        cut.GetProperty("text").GetString()!.ShouldStartWith("😀");
        cut.GetProperty("text").GetString()!.ShouldEndWith("😀");
    }
}
