using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the <see cref="IAgentTool"/> Tool Fabric contract — above all that its risk declarations are FAIL-CLOSED
/// by default (a tool that forgets to declare itself read-only must not be runnable concurrently or skip the
/// approval gate), plus the two-stage shape (pure validate → structured result/typed error) and idempotency key.
/// </summary>
[Trait("Category", "Unit")]
public class AgentToolContractTests
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    /// <summary>Implements ONLY the required members → inherits every fail-closed default.</summary>
    private sealed class MinimalTool : IAgentTool
    {
        public string Kind => "test.minimal";
        public string Description => "A minimal tool.";
        public JsonElement InputSchema => EmptyObject;
        public JsonElement OutputSchema => EmptyObject;
        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;
        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct) =>
            Task.FromResult(AgentToolResult.Ok(JsonSerializer.SerializeToElement(new { ok = true }), outputBytes: 9));
    }

    /// <summary>A genuinely safe tool relaxes the defaults.</summary>
    private sealed class ReadOnlyTool : IAgentTool
    {
        public string Kind => "test.read";
        public string Description => "A read-only tool.";
        public IReadOnlyList<string> Aliases => new[] { "test.search" };
        public string? SearchHint => "find grep search";
        public JsonElement InputSchema => EmptyObject;
        public JsonElement OutputSchema => EmptyObject;
        public bool IsReadOnly => true;
        public bool IsConcurrencySafe => true;
        public bool IsDestructive => false;
        public AgentToolValidation ValidateInput(JsonElement input) =>
            input.TryGetProperty("query", out _) ? AgentToolValidation.Valid : AgentToolValidation.Invalid("Input 'query' is required.");
        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct) =>
            Task.FromResult(AgentToolResult.Fail("not implemented in the fake"));
    }

    [Fact]
    public void A_minimal_tool_is_fail_closed_by_default()
    {
        IAgentTool tool = new MinimalTool();

        tool.IsReadOnly.ShouldBeFalse("a tool that doesn't declare read-only is assumed to have side effects");
        tool.IsConcurrencySafe.ShouldBeFalse("…and is not safe to run in a parallel batch");
        tool.IsDestructive.ShouldBeTrue();
        tool.RequiresApproval.ShouldBeTrue("destructive-by-default → gated by default");
        tool.Aliases.ShouldBeEmpty();
        tool.SearchHint.ShouldBeNull();
    }

    [Fact]
    public void A_read_only_tool_relaxes_the_defaults_and_skips_approval()
    {
        IAgentTool tool = new ReadOnlyTool();

        tool.IsReadOnly.ShouldBeTrue();
        tool.IsConcurrencySafe.ShouldBeTrue();
        tool.IsDestructive.ShouldBeFalse();
        tool.RequiresApproval.ShouldBeFalse("not destructive → no approval needed by default");
        tool.Aliases.ShouldBe(new[] { "test.search" });
        tool.SearchHint.ShouldBe("find grep search");
    }

    [Fact]
    public void Validate_input_is_the_pure_first_gate_with_a_teachable_error()
    {
        IAgentTool tool = new ReadOnlyTool();

        tool.ValidateInput(JsonDocument.Parse("""{"query":"x"}""").RootElement).IsValid.ShouldBeTrue();

        var bad = tool.ValidateInput(EmptyObject);
        bad.IsValid.ShouldBeFalse();
        bad.Error.ShouldContain("query");
    }

    [Fact]
    public async Task Call_returns_a_structured_ok_result_carrying_the_output_and_size()
    {
        var result = await new MinimalTool().CallAsync(new AgentToolCall { Input = EmptyObject }, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Output.GetProperty("ok").GetBoolean().ShouldBeTrue();
        result.OutputBytes.ShouldBe(9);
    }

    [Fact]
    public async Task Call_returns_a_typed_error_rather_than_throwing()
    {
        var result = await new ReadOnlyTool().CallAsync(new AgentToolCall { Input = EmptyObject }, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Idempotency_key_rides_on_the_call_for_at_most_once_side_effects()
    {
        var call = new AgentToolCall { Input = EmptyObject, IdempotencyKey = "open-pr-run-42" };

        call.IdempotencyKey.ShouldBe("open-pr-run-42");
        new AgentToolCall { Input = EmptyObject }.IdempotencyKey.ShouldBeNull("a read-only call carries no key");
    }

    [Fact]
    public void TeamId_round_trips_under_agent_json_when_present()
    {
        var teamId = Guid.NewGuid();
        var call = new AgentToolCall { Input = EmptyObject, TeamId = teamId };

        var json = JsonSerializer.Serialize(call, AgentJson.Options);
        var back = JsonSerializer.Deserialize<AgentToolCall>(json, AgentJson.Options)!;

        back.TeamId.ShouldBe(teamId);
    }

    [Fact]
    public void TeamId_defaults_to_null_when_omitted_forward_compat()
    {
        var call = new AgentToolCall { Input = EmptyObject };
        call.TeamId.ShouldBeNull("no team → fail-closed default");

        var back = JsonSerializer.Deserialize<AgentToolCall>(JsonSerializer.Serialize(call, AgentJson.Options), AgentJson.Options)!;
        back.TeamId.ShouldBeNull();
    }
}
