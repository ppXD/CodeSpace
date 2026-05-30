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

        // The optional per-iteration update ref is null unless the author sets it.
        var withVar = JsonSerializer.Deserialize<LoopConfig>("""{ "loopVariables": [ { "name": "n", "value": 0 } ] }""", Options);
        withVar!.LoopVariables[0].Update.ShouldBeNull();
        withVar.LoopVariables[0].Value!.Value.GetInt32().ShouldBe(0);
    }
}
