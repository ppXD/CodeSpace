using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the capability-tiering structured-output contract — the tier string maps fail-soft to <see cref="ModelCapabilityTier"/>
/// (anything unrecognised → Unknown, never throws), and the schema constrains the reply to the four tiers + the id.
/// </summary>
[Trait("Category", "Unit")]
public class ModelTieringSchemaTests
{
    [Theory]
    [InlineData("frontier", ModelCapabilityTier.Frontier)]
    [InlineData("FRONTIER", ModelCapabilityTier.Frontier)]   // case-insensitive
    [InlineData("  strong  ", ModelCapabilityTier.Strong)]   // trimmed
    [InlineData("basic", ModelCapabilityTier.Basic)]
    [InlineData("unknown", ModelCapabilityTier.Unknown)]
    [InlineData("gibberish", ModelCapabilityTier.Unknown)]   // unrecognised → Unknown (fail-soft)
    [InlineData("", ModelCapabilityTier.Unknown)]
    [InlineData(null, ModelCapabilityTier.Unknown)]
    public void ParseTier_maps_fail_soft_to_unknown(string? raw, ModelCapabilityTier expected) =>
        ModelTieringSchema.ParseTier(raw).ShouldBe(expected);

    [Fact]
    public void A_model_emitted_tier_batch_binds_through_the_options()
    {
        const string json = """{ "models": [ { "id": "claude-opus-4-8", "tier": "frontier" }, { "id": "metis-coder-max", "tier": "unknown" } ] }""";

        var batch = JsonSerializer.Deserialize<ModelTierAssignments>(json, ModelTieringSchema.Options)!;

        batch.Models.Count.ShouldBe(2);
        batch.Models[0].Id.ShouldBe("claude-opus-4-8");
        ModelTieringSchema.ParseTier(batch.Models[0].Tier).ShouldBe(ModelCapabilityTier.Frontier);
        ModelTieringSchema.ParseTier(batch.Models[1].Tier).ShouldBe(ModelCapabilityTier.Unknown);
    }

    [Fact]
    public void The_schema_constrains_the_tier_to_the_four_buckets()
    {
        var tierEnum = ModelTieringSchema.ResponseSchema
            .GetProperty("properties").GetProperty("models").GetProperty("items")
            .GetProperty("properties").GetProperty("tier").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        tierEnum.ShouldBe(new[] { "frontier", "strong", "basic", "unknown" });
    }
}
