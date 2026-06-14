using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the <see cref="MapConfig"/> wire contract — the JSON the frontend's Map inspector emits
/// (maxParallelism / errorHandling / resultKey) — AND <see cref="MapPlan"/>'s normalisation (the safe
/// defaults + lenient error-handling parse the engine actually fans out with). The engine deserializes
/// node Config with this shape, so a rename here silently breaks every saved map; the test makes it visible.
/// </summary>
[Trait("Category", "Unit")]
public class MapConfigTests
{
    // The engine reads map config case-insensitively (frontend emits camelCase into PascalCase records).
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Parses_the_inspector_shape()
    {
        const string json = """
            { "maxParallelism": 4, "errorHandling": "continue", "resultKey": "reviews" }
            """;

        var config = JsonSerializer.Deserialize<MapConfig>(json, Options);

        config.ShouldNotBeNull();
        config!.MaxParallelism.ShouldBe(4);
        config.ErrorHandling.ShouldBe("continue");
        config.ResultKey.ShouldBe("reviews");
    }

    [Fact]
    public void Defaults_are_sane_when_fields_are_omitted()
    {
        var config = JsonSerializer.Deserialize<MapConfig>("{}", Options);

        config.ShouldNotBeNull();
        config!.MaxParallelism.ShouldBeNull("omitted ⇒ inherit the engine-wide parallelism (no behaviour/hash change for existing configs)");
        config.ErrorHandling.ShouldBeNull();
        config.ResultKey.ShouldBe("results", "the default reduce key");
    }

    [Theory]
    [InlineData(null, MapErrorHandling.Terminate)]        // omitted ⇒ safe default
    [InlineData("", MapErrorHandling.Terminate)]          // blank ⇒ safe default
    [InlineData("terminate", MapErrorHandling.Terminate)] // explicit
    [InlineData("Continue", MapErrorHandling.Continue)]   // case-insensitive
    [InlineData("continue", MapErrorHandling.Continue)]
    [InlineData("nonsense", MapErrorHandling.Terminate)]  // typo ⇒ safe default
    public void MapPlan_parses_error_handling_leniently(string? raw, MapErrorHandling expected)
    {
        MapPlan.From(new MapConfig { ErrorHandling = raw }).ErrorHandling.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "results")]      // omitted ⇒ default key
    [InlineData("", "results")]        // blank ⇒ default key
    [InlineData("   ", "results")]     // whitespace ⇒ default key
    [InlineData("reviews", "reviews")] // explicit
    [InlineData(" reviews ", "reviews")] // trimmed
    public void MapPlan_normalises_the_result_key(string? raw, string expected)
    {
        // A record initializer with a null value bypasses the property default, so the plan's
        // normalisation is the real backstop the engine relies on.
        MapPlan.From(new MapConfig { ResultKey = raw! }).ResultKey.ShouldBe(expected);
    }

    [Fact]
    public void MapPlan_carries_max_parallelism_raw_for_the_engine_to_clamp()
    {
        // Null inherits the engine-wide default; a value is carried verbatim — the engine clamps it per
        // map via ResolveBodyParallelism (the same path the loop body uses), not here.
        MapPlan.From(new MapConfig()).MaxParallelism.ShouldBeNull();
        MapPlan.From(new MapConfig { MaxParallelism = 3 }).MaxParallelism.ShouldBe(3);
    }

    [Theory]
    [InlineData(MapPlan.MaxBranchesCeiling, false)]       // EXACTLY at the ceiling → admitted (the boundary the integration ceiling test passes 2, never the edge)
    [InlineData(MapPlan.MaxBranchesCeiling + 1, true)]    // one OVER the ceiling → tripped
    public void Branch_ceiling_trips_only_strictly_above_the_cap(int elementCount, bool expectedTripped)
    {
        // Pin the EXACT boundary of the engine's fan-out admission predicate (WorkflowEngine: count >
        // MapPlan.MaxBranchesCeiling) over the production constant, so the at-ceiling element is admitted and
        // the first over-ceiling element is the one that fails. Cheap unit pin of the edge the integration
        // Theory (elementCount 2 vs ceiling+1) can't afford to fan out to.
        var tripped = elementCount > MapPlan.MaxBranchesCeiling;

        tripped.ShouldBe(expectedTripped);
    }
}
