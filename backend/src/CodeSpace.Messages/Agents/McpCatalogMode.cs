namespace CodeSpace.Messages.Agents;

/// <summary>
/// Which slice of the agent-tool catalog a run's per-run MCP endpoint serves. The endpoint now opens for EVERY run;
/// this decides what it exposes.
/// <list type="bullet">
///   <item><see cref="ReadOnly"/> — the DEFAULT. Only read-only tools (e.g. <c>get_context</c> and the git read
///   tools) are listed, allow-listed, and callable; a side-effecting tool is absent from the catalog and refused at
///   call time. The safe baseline every run gets with no opt-in.</item>
///   <item><see cref="Full"/> — the whole registry, exactly as before. Selected only by the existing opt-in
///   (the <c>CODESPACE_AGENT_MCP_ENDPOINT_ENABLED</c> env flag or the per-run <c>AgentTask.EnableMcpEndpoint</c>), so a
///   run that opted into the side-effecting fabric is byte-identical to the pre-default-read-only behavior.</item>
/// </list>
/// The split is purely about WHICH tools the catalog serves; the per-call autonomy gate + governance still apply on
/// top (a side-effecting tool in <see cref="Full"/> mode is still tier-gated and ledger-tracked as before).
/// </summary>
public enum McpCatalogMode
{
    ReadOnly = 0,
    Full = 1,
}
