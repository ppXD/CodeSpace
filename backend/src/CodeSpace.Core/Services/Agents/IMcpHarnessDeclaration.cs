using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// OPTIONAL sibling capability of <see cref="IAgentHarness"/> (Rule 7 — opt-in, not a widening of the core interface):
/// a harness whose CLI can speak MCP over a spawned stdio server implements this to RENDER the run-scoped declaration its
/// config-home expects — Claude Code's <c>.mcp.json</c> (a JSON <c>mcpServers</c> map), Codex's <c>config.toml</c> (an
/// <c>[mcp_servers.&lt;name&gt;]</c> table). The harness owns its file FORMAT: it returns the relative file name + the
/// fully-rendered content (the run socket + token baked in). The executor, once it has opened the run's MCP endpoint,
/// hands the harness the run-scoped <see cref="McpDeclarationContext"/> (socket + token + proxy command), and — only if
/// implemented — hands the runner the resulting <see cref="McpServerWiring"/> so it writes the declaration (0600, since
/// it carries the run token) into the per-run config-home and binds the socket into the sandbox.
///
/// <para>The harness renders the content because IT owns the format; the runner just writes dumb bytes. A harness that
/// can't host an MCP server (a future analysis-only one) simply doesn't implement it → the run gets no tool fabric.</para>
/// </summary>
public interface IMcpHarnessDeclaration
{
    /// <summary>
    /// Render the run's MCP-server declaration: the file name relative to the per-run config-home plus the fully-rendered
    /// content for THIS harness's format, with the context's run-scoped socket + token + proxy command baked in.
    /// </summary>
    McpHarnessDeclaration BuildMcpDeclaration(McpDeclarationContext context);
}
