using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// Projects the run's GOVERNED codespace tools into the harness allow-list when the per-run MCP endpoint is open.
/// A CLI harness that natively speaks a tool allow-list (Claude Code's <c>--allowed-tools</c>) is handed ONLY the
/// tools the author named in <c>task.Tools</c> — so a run with a RESTRICTED tool list would never see the
/// <c>mcp__codespace__*</c> tools the endpoint serves, even though the endpoint is open and the autonomy tier
/// permits them. This helper closes that gap: it computes the fully-qualified MCP tool names
/// (<c>mcp__&lt;ServerName&gt;__&lt;Kind&gt;</c>) for every registry tool the run's tier can call via
/// <see cref="AgentToolGate.Decide"/>, and MERGES them onto the author's list.
///
/// <para><b>Additive + tier-filtered.</b> The author's own tools are preserved verbatim; the codespace tools are
/// appended (deduped). A tool the tier can never reach (a <see cref="AgentToolGateDecision.Deny"/> at this tier) is
/// NOT projected — the CLI never even sees a name it would be refused at the endpoint, so the allow-list and the
/// endpoint gate agree. A <see cref="AgentToolGateDecision.RequireApproval"/> tool IS projected (the tier can call
/// it, subject to the human-in-the-loop the endpoint enforces) so the model can request it.</para>
///
/// <para><b>No regression when the author named no tools.</b> When <c>task.Tools</c> is null/empty the harness omits
/// <c>--allowed-tools</c> entirely (the CLI's own default toolset, which already includes a declared MCP server's
/// tools) — so this returns the list UNCHANGED in that case. Augmenting only ever WIDENS a restricted list; it never
/// converts a default-all run into a restricted one. Pure + static so it's unit-pinned.</para>
/// </summary>
public static class McpAllowedTools
{
    /// <summary>
    /// The author's tool list with the run's already-tier-filtered <c>mcp__&lt;serverName&gt;__&lt;Kind&gt;</c> names
    /// (from <see cref="QualifiedNames"/>) merged in — deduped, author order first. Returns <paramref name="authorTools"/>
    /// UNCHANGED when it is null/empty (harness default-all → the CLI default already reaches the MCP tools) so a
    /// default-all run never regresses to restricted.
    /// </summary>
    public static IReadOnlyList<string>? Augment(IReadOnlyList<string>? authorTools, IReadOnlyList<string> qualifiedNames)
    {
        if (authorTools is not { Count: > 0 }) return authorTools;   // default-all run — the CLI default reaches the MCP tools; do not narrow it

        var merged = new List<string>(authorTools);
        var seen = new HashSet<string>(authorTools, StringComparer.Ordinal);

        foreach (var qualified in qualifiedNames)
            if (seen.Add(qualified)) merged.Add(qualified);

        return merged;
    }

    /// <summary>The fully-qualified <c>mcp__&lt;serverName&gt;__&lt;Kind&gt;</c> names for every tool the tier can call (Allow or RequireApproval — a Deny is omitted), in registry order. Pure + internal so it's unit-pinned independently of the merge.</summary>
    internal static IEnumerable<string> QualifiedNames(IEnumerable<IAgentTool> tools, AgentAutonomyLevel autonomy, string serverName) =>
        tools.Where(t => AgentToolGate.Decide(autonomy, t.RequiresApproval, t.AlwaysRequiresApproval) != AgentToolGateDecision.Deny)
             .Select(t => QualifiedName(serverName, t.Kind));

    /// <summary>The MCP-qualified name a CLI applies to a server's tool: <c>mcp__&lt;serverName&gt;__&lt;kind&gt;</c> (the prefix <see cref="McpRequestHandler.ServerName"/> advertises). Single source of truth so the allow-list matches the endpoint's advertised names by construction.</summary>
    internal static string QualifiedName(string serverName, string kind) => $"mcp__{serverName}__{kind}";
}
