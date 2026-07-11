using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// agent.supervisor is the operator's densest config surface, so its schema must read in plain language:
/// every enum value carries an x-enumLabels word (no bare 0/1/2 or none/spawns leaking to the form), and the
/// operator-facing fields declare a human title. These are presentation-only hints the engine ignores — the
/// config VALUE shape is untouched — so this is a copy contract, not a behaviour change. The enum-label
/// completeness check seeds the Phase-2 manifest linter: adding an enum value without a label fails here.
/// </summary>
[Trait("Category", "Unit")]
public class AgentSupervisorManifestCopyTests
{
    // Instance initializer parses the ConfigSchema JSON at construction; the ctor body only stores the dep,
    // so null! is safe and a malformed schema throws right here.
    private static JsonElement Config() => new AgentSupervisorNode(null!).Manifest.ConfigSchema;

    [Fact]
    public void ConfigSchema_parses_as_an_object()
    {
        Config().ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void Every_enum_value_has_a_human_label()
    {
        foreach (var (path, prop) in EnumProperties(Config()))
        {
            prop.TryGetProperty("x-enumLabels", out var labels).ShouldBeTrue($"{path} is an enum with no x-enumLabels — a raw token would leak to the form");
            labels.ValueKind.ShouldBe(JsonValueKind.Object, $"{path} x-enumLabels must be a value-to-label object");

            foreach (var value in prop.GetProperty("enum").EnumerateArray())
            {
                var key = value.ToString();
                labels.TryGetProperty(key, out var label).ShouldBeTrue($"{path} enum value '{key}' has no label");
                label.GetString().ShouldNotBeNullOrWhiteSpace($"{path} enum value '{key}' has a blank label");
            }
        }
    }

    [Fact]
    public void Key_operator_fields_read_in_plain_language()
    {
        var props = Config().GetProperty("properties");

        props.GetProperty("supervisorModelId").GetProperty("title").GetString().ShouldBe("Lead model");
        props.GetProperty("decisionReviewMode").GetProperty("x-enumLabels").GetProperty("0").GetString().ShouldBe("Off");
        props.GetProperty("approvalPolicy").GetProperty("x-enumLabels").GetProperty("none").GetString().ShouldBe("Autonomous");
    }

    /// <summary>
    /// Yields every (jsonPath, schema) in the tree that declares an "enum" — recursing into object
    /// "properties" and array "items" so nested fields (agentProfile.autonomyLevel, relatedRepositories'
    /// access) are covered, not just the top level.
    /// </summary>
    private static IEnumerable<(string Path, JsonElement Prop)> EnumProperties(JsonElement schema, string path = "")
    {
        if (schema.ValueKind != JsonValueKind.Object) yield break;

        if (schema.TryGetProperty("enum", out _)) yield return (path, schema);

        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject())
                foreach (var found in EnumProperties(p.Value, $"{path}/{p.Name}"))
                    yield return found;

        if (schema.TryGetProperty("items", out var items))
            foreach (var found in EnumProperties(items, $"{path}/[]"))
                yield return found;
    }
}
