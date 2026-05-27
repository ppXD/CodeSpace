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
}
