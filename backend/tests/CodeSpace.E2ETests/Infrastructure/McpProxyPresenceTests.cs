using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using Shouldly;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// 🟢 The cheapest possible guard on the most expensive silent failure in this assembly.
///
/// <para>The benchmark corpus fans out over (task × <c>cli</c> | <c>cli-with-mcp</c>) and its whole claim is that the
/// two arms differ observably. They did not: the proxy binary was in no test bin at all, so
/// <c>BuildMcpWiring</c>'s <c>File.Exists</c> guard fired on every run and both arms executed identically with no
/// tool fabric — while each row recorded the mode it had REQUESTED. A green benchmark proved a real model can solve
/// simple shell tasks tool-lessly, and was read as evidence about the tool fabric.</para>
///
/// <para>Nothing failed, because a fail-closed degrade is not a failure. This test is the thing that fails.</para>
/// </summary>
[Trait("Category", "Infrastructure")]
public class McpProxyPresenceTests
{
    [Fact]
    public void The_mcp_proxy_is_where_the_runner_will_look_for_it()
    {
        var path = LocalProcessRunner.McpProxyBinaryPath();

        File.Exists(path).ShouldBeTrue(
            $"the codespace-mcp proxy is absent at '{path}', so every MCP-mode run in this assembly degrades to a "
          + "TOOL-LESS execution while still recording the mode it asked for. Check the CopyMcpProxyToOutput target "
          + "and the build-only ProjectReference to CodeSpace.Mcp in CodeSpace.E2ETests.csproj.");
    }
}
