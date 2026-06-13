using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pin the {{ref}} + $ref contract. These are the two ways every node receives upstream
/// data — breaking either silently mangles every workflow in production.
/// </summary>
[Trait("Category", "Unit")]
public class VariableResolverTests
{
    private static NodeRunScope MakeScope() => new()
    {
        Trigger = ParseDict("""{ "title": "Fix bug", "number": 42, "open": true }"""),
        Team = ParseDict("""{ "anthropic_model": "claude-sonnet-4-5" }""")
    };

    private static IReadOnlyDictionary<string, JsonElement> ParseDict(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    [Fact]
    public void Inline_template_substitutes_string_value()
    {
        var scope = MakeScope();
        var source = ParseElement("\"Title: {{trigger.title}}\"");

        var resolved = VariableResolver.Resolve(source, scope);

        resolved.GetString().ShouldBe("Title: Fix bug");
    }

    [Fact]
    public void Sole_template_preserves_native_type()
    {
        var scope = MakeScope();
        var source = ParseElement("\"{{trigger.number}}\"");

        var resolved = VariableResolver.Resolve(source, scope);

        // Sole-placeholder string returns the RAW value — number stays a number, not a stringified number.
        resolved.ValueKind.ShouldBe(JsonValueKind.Number);
        resolved.GetInt32().ShouldBe(42);
    }

    [Fact]
    public void Multiple_templates_concatenate_into_string()
    {
        var scope = MakeScope();
        var source = ParseElement("\"#{{trigger.number}}: {{trigger.title}}\"");

        var resolved = VariableResolver.Resolve(source, scope);

        resolved.GetString().ShouldBe("#42: Fix bug");
    }

    [Fact]
    public void JsonRef_object_resolves_to_full_value()
    {
        var scope = new NodeRunScope { Trigger = MakeScope().Trigger };
        scope.Nodes["fetch"] = ParseDict("""{ "files": [ { "fileName": "a.cs" }, { "fileName": "b.cs" } ] }""");

        var source = ParseElement("""{ "$ref": "nodes.fetch.outputs.files" }""");

        var resolved = VariableResolver.Resolve(source, scope);

        resolved.ValueKind.ShouldBe(JsonValueKind.Array);
        resolved.GetArrayLength().ShouldBe(2);
        resolved[0].GetProperty("fileName").GetString().ShouldBe("a.cs");
    }

    [Fact]
    public void Unknown_path_resolves_to_empty()
    {
        var scope = MakeScope();
        var source = ParseElement("\"prefix {{trigger.missing}} suffix\"");

        var resolved = VariableResolver.Resolve(source, scope);

        resolved.GetString().ShouldBe("prefix  suffix");
    }

    [Fact]
    public void Team_path_walks_team_dictionary()
    {
        var scope = MakeScope();
        var source = ParseElement("\"{{team.anthropic_model}}\"");

        var resolved = VariableResolver.Resolve(source, scope);

        resolved.GetString().ShouldBe("claude-sonnet-4-5");
    }

    [Fact]
    public void Nested_object_preserves_non_template_keys()
    {
        var scope = MakeScope();
        var source = ParseElement("""{ "wrapper": { "title": "{{trigger.title}}", "literal": "stays" } }""");

        var resolved = VariableResolver.Resolve(source, scope);

        resolved.GetProperty("wrapper").GetProperty("title").GetString().ShouldBe("Fix bug");
        resolved.GetProperty("wrapper").GetProperty("literal").GetString().ShouldBe("stays");
    }

    [Theory]
    [InlineData("trigger.title", JsonValueKind.String)]
    [InlineData("trigger.number", JsonValueKind.Number)]
    [InlineData("trigger.open", JsonValueKind.True)]
    [InlineData("trigger.absent", JsonValueKind.Null)]
    public void WalkPath_returns_expected_value_kind(string path, JsonValueKind expected)
    {
        var scope = MakeScope();
        var resolved = VariableResolver.WalkPath(path, scope);

        if (expected == JsonValueKind.Null)
        {
            resolved.ShouldBeNull();
            return;
        }

        resolved.HasValue.ShouldBeTrue();
        resolved!.Value.ValueKind.ShouldBe(expected);
    }

    private static JsonElement ParseElement(string json) => JsonDocument.Parse(json).RootElement;

    // ─── sys.* scope ───────────────────────────────────────────────────────────

    [Fact]
    public void Sys_scope_resolves_engine_injected_context_values()
    {
        var workflowRunId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var scope = new NodeRunScope
        {
            Trigger = ParseDict("{}"),
            Sys = new Dictionary<string, JsonElement>
            {
                [SystemScopeKeys.WorkflowRunId] = JsonSerializer.SerializeToElement(workflowRunId),
                [SystemScopeKeys.SourceType]    = JsonSerializer.SerializeToElement("manual"),
                [SystemScopeKeys.UserId]        = JsonDocument.Parse("null").RootElement.Clone(),
            },
        };

        // Single-placeholder string of a Guid resolves to native string (Guid serializes as string)
        var runId = VariableResolver.Resolve(ParseElement($"\"{{{{sys.{SystemScopeKeys.WorkflowRunId}}}}}\""), scope);
        runId.GetString().ShouldBe(workflowRunId.ToString());

        // sys.source_type surfaces the request's source_type string.
        var src = VariableResolver.Resolve(ParseElement($"\"src={{{{sys.{SystemScopeKeys.SourceType}}}}}\""), scope);
        src.GetString().ShouldBe("src=manual");

        // Null sys values resolve to empty string in interpolation, matching every other scope.
        var nullable = VariableResolver.Resolve(ParseElement($"\"by={{{{sys.{SystemScopeKeys.UserId}}}}}.\""), scope);
        nullable.GetString().ShouldBe("by=.");
    }

    [Fact]
    public void Sys_scope_unknown_key_resolves_to_null()
    {
        var scope = new NodeRunScope
        {
            Trigger = ParseDict("{}"),
            Sys = new Dictionary<string, JsonElement>
            {
                [SystemScopeKeys.WorkflowId] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
            },
        };

        var resolved = VariableResolver.WalkPath("sys.no_such_key", scope);
        resolved.ShouldBeNull();
    }

    [Fact]
    public void Loop_scope_resolves_prefixed_variables_and_index()
    {
        var scope = new NodeRunScope
        {
            Trigger = ParseDict("{}"),
            Loop = ParseDict("""{ "answer": "ship it", "index": 3 }"""),
        };

        // {{loop.<name>}} resolves a loop variable; native types survive a sole placeholder.
        VariableResolver.Resolve(ParseElement("\"{{loop.answer}}\""), scope).GetString().ShouldBe("ship it");
        VariableResolver.Resolve(ParseElement("\"{{loop.index}}\""), scope).GetInt32().ShouldBe(3);

        // An unknown loop var resolves to null (→ empty string in interpolation).
        VariableResolver.WalkPath("loop.missing", scope).ShouldBeNull();
    }

    [Fact]
    public void Loop_and_iteration_scopes_coexist_without_collision()
    {
        // A flow.loop body nested inside a flow.iterate sees BOTH heads: bare {{item}} (iterate)
        // and prefixed {{loop.x}} (loop). The loop. prefix is what keeps them from colliding.
        var scope = new NodeRunScope
        {
            Trigger = ParseDict("{}"),
            Iteration = ParseDict("""{ "item": "PR-7" }"""),
            Loop = ParseDict("""{ "item": "loop-item" }"""),
        };

        VariableResolver.Resolve(ParseElement("\"{{item}}\""), scope).GetString().ShouldBe("PR-7");
        VariableResolver.Resolve(ParseElement("\"{{loop.item}}\""), scope).GetString().ShouldBe("loop-item");
    }

    [Fact]
    public void Loop_scope_supports_nested_access_dollar_ref_and_interpolation()
    {
        var scope = new NodeRunScope
        {
            Trigger = ParseDict("{}"),
            Loop = ParseDict("""{ "state": { "count": 2, "label": "two" }, "items": [10, 20], "index": 1 }"""),
        };

        // Dotted descent into a loop var that holds an object.
        VariableResolver.Resolve(ParseElement("\"{{loop.state.count}}\""), scope).GetInt32().ShouldBe(2);

        // $ref form returns the WHOLE structured value (array), not a stringified copy.
        var arr = VariableResolver.Resolve(ParseElement("""{ "$ref": "loop.items" }"""), scope);
        arr.ValueKind.ShouldBe(JsonValueKind.Array);
        arr.GetArrayLength().ShouldBe(2);

        // Multi-placeholder interpolation mixes loop refs with surrounding text.
        VariableResolver.Resolve(ParseElement("\"#{{loop.index}} = {{loop.state.label}}\""), scope)
            .GetString().ShouldBe("#1 = two");
    }

    [Fact]
    public void Loop_refs_resolve_to_null_when_not_inside_a_loop()
    {
        // Outside a loop body the Loop slot is null — {{loop.x}} must resolve to null (→ "" in
        // interpolation) and NEVER throw, so a stray reference degrades gracefully.
        var scope = new NodeRunScope { Trigger = ParseDict("{}") };   // Loop left null

        VariableResolver.WalkPath("loop.anything", scope).ShouldBeNull();
        VariableResolver.Resolve(ParseElement("\"x={{loop.anything}}\""), scope).GetString().ShouldBe("x=");
    }

    // ─── array indexing + .length ───────────────────────────────────────────────

    private static NodeRunScope ScopeWithFetchOutputs(string outputsJson)
    {
        var scope = new NodeRunScope { Trigger = ParseDict("{}") };
        scope.Nodes["fetch"] = ParseDict(outputsJson);
        return scope;
    }

    [Fact]
    public void Array_index_sole_placeholder_returns_the_native_element()
    {
        var scope = ScopeWithFetchOutputs("""{ "files": [ { "n": "a" }, { "n": "b" } ] }""");

        var resolved = VariableResolver.Resolve(ParseElement("\"{{nodes.fetch.outputs.files[0]}}\""), scope);

        resolved.ValueKind.ShouldBe(JsonValueKind.Object, "a sole index placeholder preserves the element's type");
        resolved.GetProperty("n").GetString().ShouldBe("a");
    }

    [Fact]
    public void Array_index_then_property_descends_into_the_element()
    {
        var scope = ScopeWithFetchOutputs("""{ "files": [ { "n": "a" }, { "n": "b" } ] }""");

        VariableResolver.Resolve(ParseElement("\"{{nodes.fetch.outputs.files[1].n}}\""), scope).GetString().ShouldBe("b");
    }

    [Fact]
    public void Chained_indices_walk_a_matrix()
    {
        var scope = ScopeWithFetchOutputs("""{ "matrix": [ [1, 2], [3, 4] ] }""");

        VariableResolver.WalkPath("nodes.fetch.outputs.matrix[1][0]", scope)!.Value.GetInt32().ShouldBe(3);
    }

    [Fact]
    public void Array_length_sole_placeholder_is_the_count_as_a_number()
    {
        // HEADLINE for flow.map: {{...subtasks.length}} drives the fan-out count.
        var scope = ScopeWithFetchOutputs("""{ "subtasks": [ {}, {}, {} ] }""");

        var resolved = VariableResolver.Resolve(ParseElement("\"{{nodes.fetch.outputs.subtasks.length}}\""), scope);

        resolved.ValueKind.ShouldBe(JsonValueKind.Number);
        resolved.GetInt32().ShouldBe(3);
    }

    [Fact]
    public void Array_length_in_interpolation_stringifies_the_count()
    {
        var scope = ScopeWithFetchOutputs("""{ "subtasks": [ {}, {} ] }""");

        VariableResolver.Resolve(ParseElement("\"count={{nodes.fetch.outputs.subtasks.length}}\""), scope)
            .GetString().ShouldBe("count=2");
    }

    [Fact]
    public void Empty_array_length_is_zero_not_null()
    {
        var scope = ScopeWithFetchOutputs("""{ "items": [] }""");

        var resolved = VariableResolver.WalkPath("nodes.fetch.outputs.items.length", scope);

        resolved.HasValue.ShouldBeTrue("an empty array still has a length of 0 — not a miss");
        resolved!.Value.GetInt32().ShouldBe(0);
    }

    [Fact]
    public void A_real_object_property_named_length_wins_over_the_pseudo()
    {
        // The decisive non-breaking rule: a real 'length' KEY on an object always resolves to its value;
        // the count pseudo only fires on arrays (which can't carry a real member).
        var scope = ScopeWithFetchOutputs("""{ "dims": { "length": 5, "width": 3 } }""");

        VariableResolver.WalkPath("nodes.fetch.outputs.dims.length", scope)!.Value.GetInt32().ShouldBe(5);
        VariableResolver.WalkPath("nodes.fetch.outputs.dims.width", scope)!.Value.GetInt32().ShouldBe(3);
    }

    [Theory]
    [InlineData("nodes.fetch.outputs.files[5]", "out-of-bounds index")]
    [InlineData("nodes.fetch.outputs.files[0].missing", "missing property after a valid index")]
    [InlineData("nodes.fetch.outputs.scalar[0]", "index on a non-array")]
    [InlineData("nodes.fetch.outputs.scalar.length", "length on a non-array/string")]
    [InlineData("nodes.fetch.outputs.files.length.foo", "length is not the terminal segment")]
    [InlineData("nodes.fetch.outputs.files.length[0]", "length segment carries an index")]
    public void Index_and_length_misses_resolve_to_null(string path, string why)
    {
        var scope = ScopeWithFetchOutputs("""{ "files": [ { "n": "a" } ], "scalar": 42 }""");

        VariableResolver.WalkPath(path, scope).ShouldBeNull(why);
    }

    [Fact]
    public void Negative_index_via_ref_resolves_to_null()
    {
        // The inline regex only admits [digits]; a negative index is only reachable via a raw $ref string,
        // and there is no Python-style wraparound — it's a clean miss.
        var scope = ScopeWithFetchOutputs("""{ "files": [ { "n": "a" } ] }""");

        var resolved = VariableResolver.Resolve(ParseElement("""{ "$ref": "nodes.fetch.outputs.files[-1]" }"""), scope);

        resolved.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Ref_form_supports_array_index_and_length_with_no_regex_involvement()
    {
        var scope = ScopeWithFetchOutputs("""{ "files": [ { "n": "a" }, { "n": "b" } ] }""");

        VariableResolver.Resolve(ParseElement("""{ "$ref": "nodes.fetch.outputs.files[1]" }"""), scope)
            .GetProperty("n").GetString().ShouldBe("b");

        VariableResolver.Resolve(ParseElement("""{ "$ref": "nodes.fetch.outputs.files.length" }"""), scope)
            .GetInt32().ShouldBe(2);
    }

    [Fact]
    public void Bare_iteration_head_supports_index_and_nested_index()
    {
        var indexHead = new NodeRunScope { Trigger = ParseDict("{}"), Iteration = ParseDict("""{ "items": [10, 20, 30] }""") };
        VariableResolver.Resolve(ParseElement("\"{{items[1]}}\""), indexHead).GetInt32().ShouldBe(20);

        var nestedHead = new NodeRunScope { Trigger = ParseDict("{}"), Iteration = ParseDict("""{ "item": { "tags": ["x", "y"] } }""") };
        VariableResolver.Resolve(ParseElement("\"{{item.tags[0]}}\""), nestedHead).GetString().ShouldBe("x");
    }

    [Theory]
    [InlineData("\"{{a[0}}\"", "unbalanced opening bracket")]
    [InlineData("\"{{a]0}}\"", "stray closing bracket")]
    public void Malformed_bracket_templates_stay_literal_text(string sourceJson, string why)
    {
        // The tightened regex requires balanced [digits]; a malformed token never matches, so it is left
        // verbatim rather than silently captured as a (failing) reference.
        var scope = ScopeWithFetchOutputs("""{ "a": [1, 2] }""");

        var resolved = VariableResolver.Resolve(ParseElement(sourceJson), scope);

        resolved.GetString().ShouldBe(JsonDocument.Parse(sourceJson).RootElement.GetString(), why);
    }

    // ─── secret-leak normalization (ExtractReferencedPaths) ──────────────────────

    [Fact]
    public void Extract_normalizes_indexed_and_length_refs_to_their_taintable_base()
    {
        // The secret guard compares extracted paths against SecretPaths by exact equality. An index or
        // .length on a secret must normalize back to the base path or element-0 / the count leaks.
        VariableResolver.ExtractReferencedPaths(ParseElement("""{ "x": "{{team.SECRET[0]}}" }"""))
            .ShouldContain("team.SECRET", customMessage: "an indexed secret ref must still match the secret base");

        VariableResolver.ExtractReferencedPaths(ParseElement("""{ "y": "{{team.SECRET.length}}" }"""))
            .ShouldContain("team.SECRET", customMessage: "a .length of a secret must still match the secret base");

        VariableResolver.ExtractReferencedPaths(ParseElement("""{ "z": { "$ref": "team.SECRET[0]" } }"""))
            .ShouldContain("team.SECRET", customMessage: "the $ref emit point must normalize too");
    }

    [Fact]
    public void Extract_preserves_a_mid_path_length_object_key_and_plain_paths()
    {
        // Only a TRAILING 'length' pseudo is stripped — a real object key named 'length' mid-path stays.
        VariableResolver.ExtractReferencedPaths(ParseElement("""{ "z": "{{nodes.x.outputs.length.value}}" }"""))
            .ShouldContain("nodes.x.outputs.length.value");

        // Plain dotted paths (the overwhelming majority) are returned byte-identical — strip is a no-op.
        VariableResolver.ExtractReferencedPaths(ParseElement("""{ "a": "{{team.API_KEY}}" }"""))
            .ShouldContain("team.API_KEY");
    }
}
