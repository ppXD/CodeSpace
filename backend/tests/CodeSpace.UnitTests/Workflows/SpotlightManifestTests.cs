using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Contract tests for the on-card "x-spotlight" ranks. Each of these node types annotates its 1–3 most
/// important config/input properties with <c>"x-spotlight": N</c> (N ∈ {1,2,3}) so the editor renders those
/// properties as ranked chips directly on the node card. The key is a raw JSON-schema extension that flows
/// verbatim to the frontend (SchemaBuilder.Parse clones it; NodeManifestDto embeds the schema as a raw
/// JsonElement), so it needs no DTO/serialization change.
///
/// These assertions pin the annotation SHAPE (present, ranks in [1,3], distinct, ≤3) so a manifest edit that
/// drops a spotlight, doubles a rank, or ranks something out of range is a loud, review-visible test failure
/// rather than a silent editor regression. They intentionally don't pin WHICH property carries which rank —
/// that's an authoring choice free to change — only that every spotlighted type stays well-formed.
/// </summary>
[Trait("Category", "Unit")]
public class SpotlightManifestTests
{
    // The 12 node types that carry x-spotlight ranks. Constructed directly with null! deps where needed —
    // manifest construction happens in the constructor before any service call, so the deps are never touched
    // (same instantiate-with-null idiom as NodeManifestContractTests).
    public static IEnumerable<object[]> SpotlightNodes()
    {
        yield return new object[] { new AgentCodeNode() };
        yield return new object[] { new LlmCompleteNode(null!, null!) };
        yield return new object[] { new GitOpenPullRequestNode(null!) };
        yield return new object[] { new FlowMapNode() };
        yield return new object[] { new FlowSleepNode() };
        yield return new object[] { new TriggerScheduleNode() };
        yield return new object[] { new GitMergePullRequestNode(null!) };
        yield return new object[] { new HttpRequestNode(null!) };
        yield return new object[] { new FlowWaitApprovalNode() };
        yield return new object[] { new AgentRunCommandNode(null!, null!) };
        yield return new object[] { new FlowLoopNode() };
        yield return new object[] { new ChatPostMessageNode(null!, null!, null!) };
    }

    [Theory]
    [MemberData(nameof(SpotlightNodes))]
    public void Spotlighted_node_declares_well_formed_ranks(INodeRuntime node)
    {
        var ranks = CollectSpotlightRanks(node).ToList();

        ranks.ShouldNotBeEmpty(
            $"node '{node.TypeKey}' is a spotlight type but declares no x-spotlight property — the editor would show no on-card chips.");

        ranks.Count.ShouldBeLessThanOrEqualTo(3,
            $"node '{node.TypeKey}' declares {ranks.Count} x-spotlight properties; at most 3 chips fit on a card.");

        foreach (var (prop, rank) in ranks)
            rank.ShouldBeInRange(1, 3,
                $"node '{node.TypeKey}' property '{prop}' has x-spotlight={rank}; the rank must be an integer in [1,3].");

        var rankValues = ranks.Select(r => r.Rank).ToList();
        rankValues.Distinct().Count().ShouldBe(rankValues.Count,
            $"node '{node.TypeKey}' has duplicate x-spotlight ranks ({string.Join(", ", rankValues.OrderBy(r => r))}); ranks must be distinct so chip order is unambiguous.");
    }

    /// <summary>
    /// Walk the ConfigSchema + InputSchema top-level properties and collect every one carrying an
    /// <c>x-spotlight</c> key, as (propertyName, rank). A non-integer x-spotlight value fails loudly here so a
    /// malformed annotation ("2" as a string, 1.5, true) surfaces as a test failure rather than reading as 0.
    /// </summary>
    private static IEnumerable<(string Property, int Rank)> CollectSpotlightRanks(INodeRuntime node)
    {
        foreach (var schema in new[] { node.Manifest.ConfigSchema, node.Manifest.InputSchema })
        {
            if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var prop in props.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object || !prop.Value.TryGetProperty("x-spotlight", out var spotlight))
                    continue;

                spotlight.ValueKind.ShouldBe(JsonValueKind.Number,
                    $"node '{node.TypeKey}' property '{prop.Name}' x-spotlight must be a JSON integer, got {spotlight.ValueKind}.");
                spotlight.TryGetInt32(out var rank).ShouldBeTrue(
                    $"node '{node.TypeKey}' property '{prop.Name}' x-spotlight must be an integer rank.");

                yield return (prop.Name, rank);
            }
        }
    }
}
