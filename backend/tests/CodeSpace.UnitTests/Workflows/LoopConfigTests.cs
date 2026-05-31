using System.Text.Json;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the <see cref="LoopConfig"/> wire contract — the JSON the frontend's Loop inspector emits
/// (loop variables from a variable ref OR a constant, a termination condition with a Dify-style
/// operator, a max-iterations cap). The engine deserializes node Config with this shape, so a rename
/// here is a silent break of every saved loop; the test makes it visible.
/// </summary>
[Trait("Category", "Unit")]
public class LoopConfigTests
{
    // The engine reads loop config case-insensitively (frontend emits camelCase into PascalCase records).
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Parses_the_inspector_shape_from_the_screenshots()
    {
        // Mirrors the Dify Loop panel: one var from a variable ref, one constant; a termination
        // condition reading a body node's output; the default cap.
        const string json = """
            {
              "loopVariables": [
                { "name": "x", "type": "String", "ref": "{{input.start_time}}" },
                { "name": "y", "type": "String", "value": "test" }
              ],
              "termination": {
                "logic": "and",
                "conditions": [
                  { "ref": "{{nodes.llm.outputs.reasoning_content}}", "op": "contains", "value": "DONE" }
                ]
              },
              "maxIterations": 10
            }
            """;

        var config = JsonSerializer.Deserialize<LoopConfig>(json, Options);

        config.ShouldNotBeNull();
        config!.MaxIterations.ShouldBe(10);

        config.LoopVariables.Count.ShouldBe(2);

        // First var: a Variable source (Ref set, Value absent).
        var x = config.LoopVariables[0];
        x.Name.ShouldBe("x");
        x.Type.ShouldBe("String");
        x.Ref.ShouldBe("{{input.start_time}}");
        x.Value.ShouldBeNull();

        // Second var: a Constant source (Value set, Ref absent).
        var y = config.LoopVariables[1];
        y.Name.ShouldBe("y");
        y.Ref.ShouldBeNull();
        y.Value.ShouldNotBeNull();
        y.Value!.Value.GetString().ShouldBe("test");

        config.Termination.ShouldNotBeNull();
        config.Termination!.Logic.ShouldBe("and");
        config.Termination.Conditions.Count.ShouldBe(1);

        var c = config.Termination.Conditions[0];
        c.Ref.ShouldBe("{{nodes.llm.outputs.reasoning_content}}");
        c.Op.ShouldBe("contains");
        c.Value.ShouldBe("DONE");
    }

    [Fact]
    public void Defaults_are_sane_when_fields_are_omitted()
    {
        // An empty object: no loop vars, no termination, default cap + default logic via the records.
        var config = JsonSerializer.Deserialize<LoopConfig>("{}", Options);

        config.ShouldNotBeNull();
        config!.LoopVariables.ShouldBeEmpty();
        config.Termination.ShouldBeNull();
        config.MaxIterations.ShouldBe(10);
        config.MaxParallelism.ShouldBeNull("omitted ⇒ inherit the engine-wide parallelism (no behaviour/hash change for existing configs)");

        // The optional per-iteration update ref is null unless the author sets it.
        var withVar = JsonSerializer.Deserialize<LoopConfig>("""{ "loopVariables": [ { "name": "n", "value": 0 } ] }""", Options);
        withVar!.LoopVariables[0].Update.ShouldBeNull();
        withVar.LoopVariables[0].Value!.Value.GetInt32().ShouldBe(0);
    }

    [Fact]
    public void Parses_a_complex_config_exercising_every_feature_at_once()
    {
        // A maximal config: a ref var, a numeric constant var, a var with a per-iteration update;
        // OR-logic termination mixing a binary (eq) and a unary (is_not_empty) condition; a
        // non-default cap. This is the "complex combination" the engine must round-trip faithfully.
        const string json = """
            {
              "loopVariables": [
                { "name": "cursor",  "type": "String", "ref": "{{input.start}}" },
                { "name": "count",   "type": "Number", "value": 0 },
                { "name": "history", "type": "String", "value": "", "update": "{{nodes.llm.outputs.text}}" }
              ],
              "termination": {
                "logic": "or",
                "conditions": [
                  { "ref": "{{loop.count}}", "op": "eq", "value": "5" },
                  { "ref": "{{nodes.llm.outputs.done}}", "op": "is_not_empty" }
                ]
              },
              "maxIterations": 50,
              "maxParallelism": 4
            }
            """;

        var config = JsonSerializer.Deserialize<LoopConfig>(json, Options);

        config.ShouldNotBeNull();
        config!.MaxIterations.ShouldBe(50);
        config.MaxParallelism.ShouldBe(4);
        config.LoopVariables.Count.ShouldBe(3);

        config.LoopVariables[0].Ref.ShouldBe("{{input.start}}");
        config.LoopVariables[1].Type.ShouldBe("Number");
        config.LoopVariables[1].Value!.Value.GetInt32().ShouldBe(0);
        config.LoopVariables[2].Update.ShouldBe("{{nodes.llm.outputs.text}}");

        config.Termination!.Logic.ShouldBe("or");
        config.Termination.Conditions.Count.ShouldBe(2);
        config.Termination.Conditions[0].Op.ShouldBe("eq");
        config.Termination.Conditions[0].Value.ShouldBe("5");
        // The unary condition carries no value.
        config.Termination.Conditions[1].Op.ShouldBe("is_not_empty");
        config.Termination.Conditions[1].Value.ShouldBeNull();
    }
}
