using System.Text.Json;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the <c>get_context</c> tool's pure surface — its risk declarations (a safe read), its input/output schema, its
/// input validation, and the no-run short-circuit. The retrieval LOGIC (run→session resolution, source dispatch,
/// tenancy) needs real Postgres and is proven in <c>GetContextFlowTests</c> (Integration). These tests need no DB: the
/// no-run path returns BEFORE the tool ever opens a DI scope, which we prove with a scope factory that throws if used.
/// </summary>
[Trait("Category", "Unit")]
public class GetContextToolTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A scope factory that fails the test if the tool tries to open a scope — the no-run path must never reach the DB.</summary>
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("the no-run path must not open a DI scope");
    }

    private static GetContextTool Tool() => new(new ThrowingScopeFactory());

    [Fact]
    public void Kind_is_the_reserved_name()
    {
        GetContextTool.ToolKind.ShouldBe("get_context");
        Tool().Kind.ShouldBe("get_context");
    }

    [Fact]
    public void Risk_declares_a_safe_read_runnable_at_every_tier()
    {
        IAgentTool tool = Tool();   // via the interface so the defaulted AlwaysRequiresApproval is in scope

        tool.IsReadOnly.ShouldBeTrue("a pure read");
        tool.IsConcurrencySafe.ShouldBeTrue("a read is safe to run concurrently");
        tool.IsDestructive.ShouldBeFalse("it mutates nothing");
        tool.RequiresApproval.ShouldBeFalse("a safe read is never gated — it runs at every autonomy tier incl. Confined");
        tool.AlwaysRequiresApproval.ShouldBeFalse();
    }

    [Fact]
    public void Input_schema_makes_source_and_query_optional()
    {
        var schema = Tool().InputSchema;

        schema.GetProperty("type").GetString().ShouldBe("object");
        schema.GetProperty("properties").TryGetProperty("source", out _).ShouldBeTrue();
        schema.GetProperty("properties").TryGetProperty("query", out _).ShouldBeTrue();
        schema.TryGetProperty("required", out _).ShouldBeFalse("both inputs are optional — calling with no arguments pulls every source");
    }

    [Fact]
    public void Output_schema_requires_found_source_and_text()
    {
        var required = Tool().OutputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();

        required.ShouldBe(new[] { "found", "source", "text" }, ignoreOrder: true);
    }

    [Theory]
    [InlineData("{}", true)]                                   // no args → valid (pull all)
    [InlineData("""{"source":"session.turns"}""", true)]
    [InlineData("""{"query":"auth"}""", true)]
    [InlineData("""{"source":"session.turns","query":"auth"}""", true)]
    [InlineData("[]", false)]                                  // not an object
    [InlineData("""{"source":123}""", false)]                  // wrong type
    [InlineData("""{"query":true}""", false)]                  // wrong type
    public void ValidateInput_accepts_optional_string_fields_only(string json, bool expectedValid)
    {
        Tool().ValidateInput(Parse(json)).IsValid.ShouldBe(expectedValid);
    }

    public static IEnumerable<object?[]> NoScopeCases() => new[]
    {
        new object?[] { null, true,  "RunId null" },                          // no run id
        new object?[] { Guid.Empty.ToString(), true, "RunId == Guid.Empty" }, // default/unset run id
        new object?[] { Guid.NewGuid().ToString(), false, "TeamId null" },    // real run, but no team to scope to
    };

    [Theory]
    [MemberData(nameof(NoScopeCases))]
    public async Task A_call_with_no_run_scope_is_a_clean_miss_without_opening_a_scope(string? runId, bool teamPresent, string because)
    {
        var call = new AgentToolCall
        {
            Input = Parse("{}"),
            RunId = runId is null ? null : Guid.Parse(runId),
            TeamId = teamPresent ? Guid.NewGuid() : null,
        };

        // The ThrowingScopeFactory makes this assertion double as proof the tool never reached the DB.
        var result = await Tool().CallAsync(call, CancellationToken.None);

        result.IsError.ShouldBeFalse($"absent run scope ({because}) is a clean miss, not an error");
        result.Output.GetProperty("found").GetBoolean().ShouldBeFalse(because);
        result.Output.GetProperty("source").GetString().ShouldBe("none");
    }
}
