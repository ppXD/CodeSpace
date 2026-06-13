namespace CodeSpace.Messages.Agents;

/// <summary>
/// Everything the SANDBOX RUNNER needs to make a CLI harness talk to this run's MCP tool fabric: the file to write into
/// the per-run config-home, its ALREADY-RENDERED content, and the socket it binds into the sandbox. The harness owns the
/// file FORMAT — it renders the content (token + socket baked in) — so the runner stays dumb: it writes these bytes 0600
/// and binds the socket. The executor assembles this once it has opened the run's endpoint (it owns the freshly-minted
/// token + the resolved socket path) and the chosen harness rendered the content; a pure <c>IAgentHarness.BuildInvocation</c>
/// can't, since the values are run-scoped. Null on <see cref="SandboxSpec.Mcp"/> (the default) → no MCP wiring,
/// byte-identical to a run without the tool fabric.
///
/// <para>The secret here is the run token baked into <see cref="Content"/> — it goes ONLY into the 0600 declaration file,
/// never onto argv, never into a log. The socket path is not a secret.</para>
/// </summary>
public sealed record McpServerWiring
{
    /// <summary>Path of the declaration file RELATIVE to the per-run config-home (e.g. <c>.mcp.json</c> for Claude Code, <c>config.toml</c> for Codex). The runner joins it onto the run's config-home.</summary>
    public required string RelativeFileName { get; init; }

    /// <summary>The fully-rendered declaration file content — the proxy command + the run socket + token already baked in by the harness. The runner writes this verbatim, 0600.</summary>
    public required string Content { get; init; }

    /// <summary>The per-run Unix-domain-socket path the <c>codespace-mcp</c> proxy connects to (the executor's listener binds the SAME path) — what the runner needs for the bind. The token rides <see cref="Content"/> + the durable handle, not here.</summary>
    public required string SocketPath { get; init; }
}
