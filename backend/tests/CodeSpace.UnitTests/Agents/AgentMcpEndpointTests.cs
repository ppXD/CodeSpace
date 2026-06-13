using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the per-run MCP endpoint shell (<see cref="AgentMcpEndpoint"/>): the enabling env-var literal (Rule 8), and
/// that <see cref="AgentMcpEndpoint.DisposeAsync"/> is IDEMPOTENT and NEVER throws — asserted BOTH after the pump
/// ended on a clean EOF AND after a cancel-driven fault. Also confirms dispose drops the run from the connect registry
/// and disposes the dedicated scope it was handed (so the executor never resolves a torn-down run).
/// </summary>
[Trait("Category", "Unit")]
public class AgentMcpEndpointTests
{
    [Fact]
    public void Enabling_env_var_literal_is_pinned()
    {
        // Renaming this silently turns the feature off for an operator who enabled it via env (Rule 8).
        AgentRunExecutor.McpEndpointEnabledEnvVar.ShouldBe("CODESPACE_AGENT_MCP_ENDPOINT_ENABLED");
    }

    [Fact]
    public async Task DisposeAsync_after_clean_eof_is_idempotent_and_never_throws()
    {
        var connects = new AgentMcpConnectRegistry();
        var scope = new TrackingScope();
        var runId = Guid.NewGuid();
        var channel = new AgentMcpChannel();

        var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Confined, Guid.NewGuid(), channel, connects, scope, CancellationToken.None);

        connects.TryConnect(runId, out _).ShouldBeTrue(customMessage: "open endpoint must be reachable through the connect registry");

        // Close the client write end → the server reader sees EOF → the pump returns cleanly (no cancel fault). The
        // brief delay lets the pump observe EOF BEFORE dispose cancels the CTS, so this genuinely drives the EOF path.
        channel.ClientWriter.Dispose();
        await Task.Delay(50);

        await Should.NotThrowAsync(async () => await endpoint.DisposeAsync());
        await Should.NotThrowAsync(async () => await endpoint.DisposeAsync());   // idempotent: a second dispose is a no-op

        connects.TryConnect(runId, out _).ShouldBeFalse(customMessage: "dispose must drop the run from the connect registry");
        scope.Disposed.ShouldBeTrue(customMessage: "dispose must release the dedicated DI scope");
    }

    [Fact]
    public async Task DisposeAsync_after_cancel_driven_fault_is_idempotent_and_never_throws()
    {
        var connects = new AgentMcpConnectRegistry();
        var scope = new TrackingScope();
        var runId = Guid.NewGuid();

        // No EOF: the pump is blocked in ReadLineAsync; DisposeAsync cancels the linked CTS, unwinding it via OCE.
        var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Standard, Guid.NewGuid(), new AgentMcpChannel(), connects, scope, CancellationToken.None);

        await Should.NotThrowAsync(async () => await endpoint.DisposeAsync());
        await Should.NotThrowAsync(async () => await endpoint.DisposeAsync());

        connects.TryConnect(runId, out _).ShouldBeFalse();
        scope.Disposed.ShouldBeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class EmptyRegistry : IAgentToolRegistry
    {
        public IReadOnlyList<IAgentTool> All { get; } = Array.Empty<IAgentTool>();
        public IAgentTool? Resolve(string kind) => null;
    }

    private sealed class TrackingScope : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
