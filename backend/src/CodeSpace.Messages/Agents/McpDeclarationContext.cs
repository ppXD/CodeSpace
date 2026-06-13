namespace CodeSpace.Messages.Agents;

/// <summary>
/// The run-scoped inputs the executor hands a harness to render its MCP-server declaration — all owned by the executor
/// (it minted the token + resolved the socket + proxy paths at endpoint open), none persisted. A pure value passed across
/// the executor→harness seam (Rule 18.1): the harness reads it to bake the socket + token + proxy command into its
/// format-specific content, never the other way round.
/// </summary>
public sealed record McpDeclarationContext
{
    /// <summary>The absolute <c>codespace-mcp</c> proxy command the CLI launches as the MCP server (identity-bound, so the host path == the in-sandbox path).</summary>
    public required string ProxyCommand { get; init; }

    /// <summary>The UDS path as seen INSIDE the sandbox (== host path; the bind is identity) — written into the declaration's server env so the proxy connects to this run's listener.</summary>
    public required string SocketPath { get; init; }

    /// <summary>The per-run capability token, baked into the declaration's server env (0600, never argv, never logged).</summary>
    public required string Token { get; init; }

    /// <summary>The MCP server name to key the declaration under — always <c>codespace</c> (the <c>mcp__codespace__*</c> tool prefix the handler advertises).</summary>
    public required string ServerName { get; init; }
}

/// <summary>
/// A harness's rendered MCP-server declaration: which file to write (relative to the per-run config-home) and its
/// fully-rendered content (the run socket + token already baked in for this harness's format). The runner writes the
/// content verbatim 0600. A pure value — the harness produces it from an <see cref="McpDeclarationContext"/>.
/// </summary>
public sealed record McpHarnessDeclaration
{
    /// <summary>Path of the declaration file RELATIVE to the per-run config-home — <c>.mcp.json</c> (Claude Code), <c>config.toml</c> (Codex).</summary>
    public required string RelativeFileName { get; init; }

    /// <summary>The fully-rendered declaration content for this harness's format — the proxy command + run socket + token baked in. Written verbatim, 0600 (it carries the token).</summary>
    public required string Content { get; init; }
}
