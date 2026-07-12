using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Plans;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit DRIFT-GUARD: plan.author / plan.confirm now declare a TYPED item shape on their <c>items</c> output so the
/// {{ref}} picker can drill <c>items[0].instruction</c> / <c>items[0].acceptance.command</c>. That is only safe if
/// every declared key is a REAL serialized key of <see cref="WorkPlanItem"/> — a declared-but-never-emitted key would
/// be a silent null dead-ref (the exact failure mode the picker is meant to avoid). This pins schema↔runtime parity:
/// serialize a fully-populated item with the SAME options the node uses (<see cref="AgentJson.Options"/>) and assert
/// each schema-declared field actually appears. Rename a <c>WorkPlanItem</c> field and this fails until the schema follows.
/// </summary>
[Trait("Category", "Unit")]
public class PlanItemsOutputSchemaTests
{
    private static readonly WorkPlanItem FullItem = new()
    {
        Id = "t1",
        Title = "Do the thing",
        Instruction = "Implement X",
        Rationale = "because",
        Kind = "code",
        DependsOn = new[] { "t0" },
        Acceptance = new SupervisorAcceptanceSpec
        {
            Command = new[] { "dotnet", "test" },
            Kind = BenchmarkGradingKind.TestsPass,
            Description = "unit tests pass",
            TimeoutSeconds = 600,
            SetupCommand = new[] { "dotnet", "restore" },
        },
        AcceptanceCriteria = new[] { "no regressions" },
        Harness = "claude-code",
        Model = "claude-opus-4-8",
        ExpectsChanges = true,
    };

    public static IEnumerable<object[]> PlanNodes()
    {
        yield return new object[] { "plan.author" };
        yield return new object[] { "plan.confirm" };
    }

    private static JsonElement ItemSchema(string node)
    {
        var output = node switch
        {
            "plan.author" => new PlanAuthorNode(null!).Manifest.OutputSchema,
            "plan.confirm" => new PlanConfirmNode(null!).Manifest.OutputSchema,
            _ => throw new ArgumentOutOfRangeException(nameof(node)),
        };
        return output.GetProperty("properties").GetProperty("items").GetProperty("items");
    }

    [Theory]
    [MemberData(nameof(PlanNodes))]
    public void Every_declared_item_field_is_a_real_serialized_key(string node)
    {
        var emitted = JsonSerializer.SerializeToElement(FullItem, AgentJson.Options);
        var itemProps = ItemSchema(node).GetProperty("properties");

        foreach (var prop in itemProps.EnumerateObject())
            emitted.TryGetProperty(prop.Name, out _).ShouldBeTrue($"{node} declares items[].{prop.Name} but WorkPlanItem never serializes that key → dead ref");
    }

    [Theory]
    [MemberData(nameof(PlanNodes))]
    public void Every_declared_acceptance_field_is_a_real_serialized_key(string node)
    {
        var emitted = JsonSerializer.SerializeToElement(FullItem.Acceptance!, AgentJson.Options);
        var accProps = ItemSchema(node).GetProperty("properties").GetProperty("acceptance").GetProperty("properties");

        foreach (var prop in accProps.EnumerateObject())
            emitted.TryGetProperty(prop.Name, out _).ShouldBeTrue($"{node} declares items[].acceptance.{prop.Name} but SupervisorAcceptanceSpec never serializes that key → dead ref");
    }

    [Theory]
    [MemberData(nameof(PlanNodes))]
    public void Required_item_fields_are_declared(string node)
    {
        var itemProps = ItemSchema(node).GetProperty("properties");

        foreach (var required in new[] { "id", "title", "instruction" })
            itemProps.TryGetProperty(required, out _).ShouldBeTrue($"{node} items[] must declare '{required}'");
    }
}
