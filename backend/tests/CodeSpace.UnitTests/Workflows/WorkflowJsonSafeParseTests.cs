using System.Text.Json;
using CodeSpace.Core.Services.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Direct unit coverage of <see cref="WorkflowJsonSafeParse"/>. The integration tier proves
/// the helper integrates correctly with the read endpoints; this tier pins every fallback
/// branch (null, empty string, whitespace, garbage, valid JSON of various shapes) so a
/// future refactor can't silently weaken the defensive contract.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowJsonSafeParseTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Null_or_whitespace_returns_empty_object(string? raw)
    {
        var result = WorkflowJsonSafeParse.SafeParse(raw);

        result.ValueKind.ShouldBe(JsonValueKind.Object);
        result.EnumerateObject().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not-json-at-all")]
    [InlineData("{also-not-json}")]
    [InlineData("{\"unclosed\": ")]
    [InlineData("[1, 2, ")]
    public void Garbage_input_returns_empty_object_instead_of_throwing(string raw)
    {
        // Postgres prevents these shapes from being persisted into jsonb columns, but the
        // helper is the defensive layer for any code path that might one day bypass EF
        // (raw SQL, future column-type swap to text, hand-built DTOs in tests). Must never
        // throw — that would 500 the API endpoint and lock operators out of the broken row.
        var result = WorkflowJsonSafeParse.SafeParse(raw);

        result.ValueKind.ShouldBe(JsonValueKind.Object);
        result.EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public void Valid_object_round_trips_with_every_property_preserved()
    {
        const string raw = """{"a":1,"b":"text","c":[1,2,3],"d":{"nested":true}}""";

        var result = WorkflowJsonSafeParse.SafeParse(raw);

        result.GetProperty("a").GetInt32().ShouldBe(1);
        result.GetProperty("b").GetString().ShouldBe("text");
        result.GetProperty("c").EnumerateArray().Select(e => e.GetInt32()).ShouldBe(new[] { 1, 2, 3 });
        result.GetProperty("d").GetProperty("nested").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Valid_non_object_root_round_trips_with_correct_value_kind()
    {
        // The helper doesn't force "must be object" — that's a contract decision for each
        // caller. Arrays / scalars at the root round-trip as their own ValueKind.
        WorkflowJsonSafeParse.SafeParse("[]").ValueKind.ShouldBe(JsonValueKind.Array);
        WorkflowJsonSafeParse.SafeParse("123").ValueKind.ShouldBe(JsonValueKind.Number);
        WorkflowJsonSafeParse.SafeParse("\"hello\"").ValueKind.ShouldBe(JsonValueKind.String);
        WorkflowJsonSafeParse.SafeParse("true").ValueKind.ShouldBe(JsonValueKind.True);
        WorkflowJsonSafeParse.SafeParse("null").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Returned_element_survives_after_helper_returns()
    {
        // The helper clones the root element before the backing JsonDocument is disposed,
        // so the caller can keep reading the result after this method returns. A bug here
        // (forgetting .Clone()) wouldn't be obvious in the common path — the value reads
        // fine immediately. We force a GC cycle to flush any pinned buffers, then re-read.
        var result = WorkflowJsonSafeParse.SafeParse("""{"persist":"yes"}""");
        GC.Collect();
        GC.WaitForPendingFinalizers();

        result.GetProperty("persist").GetString().ShouldBe("yes",
            "without .Clone() this fails when the underlying JsonDocument is collected");
    }
}
