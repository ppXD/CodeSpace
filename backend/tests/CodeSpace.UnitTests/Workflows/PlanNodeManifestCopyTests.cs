using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// plan.author and plan.confirm render through the generic schema form, so their config must read in plain
/// language: reviewMode's 0/1/2 shows as Off/Gate/Improve and the model/repo fields carry a human title.
/// These are presentation-only hints the engine ignores — the config VALUE shape is unchanged. The
/// enum-label completeness check mirrors the supervisor's and seeds the manifest-linter invariant.
/// </summary>
[Trait("Category", "Unit")]
public class PlanNodeManifestCopyTests
{
    public static IEnumerable<object[]> PlanNodes()
    {
        yield return new object[] { "plan.author" };
        yield return new object[] { "plan.confirm" };
    }

    // Instance initializer parses the ConfigSchema at construction; the ctor body only stores the dep, so
    // null! is safe and a malformed schema throws here.
    private static JsonElement Config(string node) => node switch
    {
        "plan.author" => new PlanAuthorNode(null!).Manifest.ConfigSchema,
        "plan.confirm" => new PlanConfirmNode(null!).Manifest.ConfigSchema,
        _ => throw new ArgumentOutOfRangeException(nameof(node)),
    };

    [Theory]
    [MemberData(nameof(PlanNodes))]
    public void Every_enum_value_has_a_human_label(string node)
    {
        foreach (var (path, prop) in EnumProperties(Config(node)))
        {
            prop.TryGetProperty("x-enumLabels", out var labels).ShouldBeTrue($"{node}{path} is an enum with no x-enumLabels");
            labels.ValueKind.ShouldBe(JsonValueKind.Object);

            foreach (var value in prop.GetProperty("enum").EnumerateArray())
            {
                labels.TryGetProperty(value.ToString(), out var label).ShouldBeTrue($"{node}{path} value '{value}' has no label");
                label.GetString().ShouldNotBeNullOrWhiteSpace();
            }
        }
    }

    [Theory]
    [MemberData(nameof(PlanNodes))]
    public void ReviewMode_and_model_read_in_plain_language(string node)
    {
        var props = Config(node).GetProperty("properties");

        props.GetProperty("reviewMode").GetProperty("x-enumLabels").GetProperty("0").GetString().ShouldBe("Off");
        props.GetProperty("plannerModelId").GetProperty("title").GetString().ShouldBe("Planner model");
    }

    // The plan nodes' config is sectioned via x-group (Planning/Revisions + Review), mirroring the supervisor —
    // every top-level field must belong to a section declared in x-sections, so the grouped layout has no stray
    // "More" bucket. Presentation-only; the config VALUE shape is unchanged.
    [Theory]
    [MemberData(nameof(PlanNodes))]
    public void Every_top_level_field_is_grouped_into_a_declared_section(string node)
    {
        var config = Config(node);

        var sections = new HashSet<string?>();
        foreach (var s in config.GetProperty("x-sections").EnumerateArray()) sections.Add(s.GetString());
        sections.Count.ShouldBe(2);

        foreach (var prop in config.GetProperty("properties").EnumerateObject())
        {
            prop.Value.TryGetProperty("x-group", out var group).ShouldBeTrue($"{node} field '{prop.Name}' has no x-group");
            sections.ShouldContain(group.GetString(), $"{node} '{prop.Name}' is in a section not listed in x-sections");
        }
    }

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
