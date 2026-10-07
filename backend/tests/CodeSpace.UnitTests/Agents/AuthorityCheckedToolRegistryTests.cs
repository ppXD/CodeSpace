using System.Reflection;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Authority;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The production MCP endpoint serves every tool through <see cref="AuthorityCheckedToolRegistry"/>'s wrapper. A member
/// <see cref="IAgentTool"/> gives a default body is the trap: a wrapper that does not forward it answers with the default
/// instead of the wrapped tool — for <c>PreviewAsync</c>, a card showing raw arguments and a merge pinned to nothing.
/// </summary>
[Trait("Category", "Unit")]
public class AuthorityCheckedToolRegistryTests
{
    [Fact]
    public void The_checked_tool_forwards_every_member_the_interface_gives_a_default_body()
    {
        var checkedTool = typeof(AuthorityCheckedToolRegistry).GetNestedType("CheckedTool", BindingFlags.NonPublic).ShouldNotBeNull();
        var map = checkedTool.GetInterfaceMap(typeof(IAgentTool));

        var fallingThrough = map.TargetMethods.Where(target => target.DeclaringType != checkedTool).Select(target => target.Name).ToList();

        fallingThrough.ShouldBeEmpty("these members answer with the interface default instead of the wrapped tool");
    }

    [Fact]
    public async Task A_preview_through_the_checked_registry_is_the_wrapped_tools_own_resolved_for_the_endpoints_run()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var inner = new PreviewingTool();
        var registry = new AuthorityCheckedToolRegistry(new OneTool(inner), new McpAuthorityContext(runId, teamId, new NoGuard()));

        var preview = await registry.Resolve("git.merge_pr").ShouldNotBeNull().PreviewAsync(new AgentToolCall { Input = JsonDocument.Parse("{}").RootElement }, CancellationToken.None);

        preview.ShouldBeSameAs(PreviewingTool.Preview);
        (inner.Seen!.RunId, inner.Seen.TeamId).ShouldBe((runId, teamId), "the endpoint's own run and team, as every other call through the wrapper");
    }

    private sealed class PreviewingTool : IAgentTool
    {
        public static readonly ToolCallPreview Preview = new() { Lines = [new ToolCallPreviewLine { Label = "head", Value = "outsider/api:release", OutsideRun = true }] };

        public AgentToolCall? Seen { get; private set; }
        public string Kind => "git.merge_pr";
        public string Description => "merge";
        public JsonElement InputSchema => JsonDocument.Parse("{}").RootElement;
        public JsonElement OutputSchema => JsonDocument.Parse("{}").RootElement;
        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;
        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ToolCallPreview> PreviewAsync(AgentToolCall call, CancellationToken cancellationToken)
        {
            Seen = call;
            return Task.FromResult(Preview);
        }
    }

    private sealed class OneTool(IAgentTool tool) : IAgentToolRegistry
    {
        public IReadOnlyList<IAgentTool> All => [tool];
        public IAgentTool? Resolve(string kind) => kind == tool.Kind ? tool : null;
    }

    private sealed class NoGuard : IAgentAuthorityCallGuard
    {
        public Task<AuthorityCallFailure?> CheckAsync(Guid runId, Guid teamId, string toolKind, CancellationToken cancellationToken) => Task.FromResult<AuthorityCallFailure?>(null);
    }
}
