using CodeSpace.Core.Services.Workflows.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="SystemScopeKeys.Descriptors"/> is the on-the-wire source of truth for
/// <c>GET /api/workflows/system-variables</c>: the editor's read-only System tab and
/// the {{}} autocomplete picker render directly from it, the descriptor key is the
/// dotted-path segment after <c>sys.</c>, and the engine reads the same constants to
/// populate scope values per run. Drift between <see cref="SystemScopeKeys.All"/> and
/// <see cref="SystemScopeKeys.Descriptors"/> would mean either a key shows up in
/// autocomplete that the engine never populates, OR a key the engine populates never
/// shows in autocomplete. Both are silent bugs; this test makes them compile-once
/// failures.
/// </summary>
[Trait("Category", "Unit")]
public class SystemScopeDescriptorsTests
{
    [Fact]
    public void Descriptors_match_All_one_to_one_in_order()
    {
        // One descriptor per key, same sequence — so the editor renders the keys in the
        // engine's canonical order without a separate sort step.
        SystemScopeKeys.Descriptors.Count.ShouldBe(SystemScopeKeys.All.Count,
            "Descriptors count drifted from All — add/remove an entry in BOTH lists together.");

        for (var i = 0; i < SystemScopeKeys.All.Count; i++)
        {
            SystemScopeKeys.Descriptors[i].Key.ShouldBe(SystemScopeKeys.All[i],
                $"Descriptor at index {i} (key={SystemScopeKeys.Descriptors[i].Key}) doesn't match SystemScopeKeys.All[{i}]={SystemScopeKeys.All[i]}.");
        }
    }

    [Fact]
    public void Every_descriptor_has_non_empty_type_and_description()
    {
        // Empty strings would render as blank rows in the editor — useless. The wire
        // contract treats Type + Description as required (DTO uses `required`); enforce here.
        foreach (var d in SystemScopeKeys.Descriptors)
        {
            d.Type.ShouldNotBeNullOrWhiteSpace($"{d.Key} has empty Type");
            d.Description.ShouldNotBeNullOrWhiteSpace($"{d.Key} has empty Description");
        }
    }

    [Theory]
    [InlineData("workflow_id",      "string")]
    [InlineData("workflow_run_id",  "string")]
    [InlineData("workflow_version", "integer")]
    [InlineData("source_type",      "string")]
    [InlineData("started_at",       "string")]
    [InlineData("team_id",          "string")]
    [InlineData("user_id",          "string")]
    public void Key_and_type_pinned(string expectedKey, string expectedType)
    {
        // Rule 8 — Type strings ("string", "integer") flow to the editor where they drive
        // type-hint chips next to each variable. Renaming "integer" to "int" would silently
        // break the chip rendering until someone notices. Hard-pin here.
        var descriptor = SystemScopeKeys.Descriptors.SingleOrDefault(d => d.Key == expectedKey);
        descriptor.ShouldNotBeNull($"descriptor for key '{expectedKey}' missing");
        descriptor!.Type.ShouldBe(expectedType, $"sys.{expectedKey} type drifted — frontend type chip will mismatch.");
    }
}
