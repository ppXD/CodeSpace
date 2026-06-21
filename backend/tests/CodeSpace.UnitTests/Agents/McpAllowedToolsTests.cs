using System.Text.Json;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the allow-list augmentation (PR-C): when the per-run MCP endpoint is open, the governed codespace tool
/// names (<c>mcp__codespace__&lt;Kind&gt;</c>) the run's autonomy tier permits are MERGED into a CLI harness's
/// allow-list — additive (author tools preserved), tier-filtered (a Denied tool is never offered a name), and a
/// no-op when the author named no tools (the CLI default already reaches a declared MCP server's tools, so a
/// default-all run must never regress to restricted).
/// </summary>
[Trait("Category", "Unit")]
public class McpAllowedToolsTests
{
    private const string ServerName = "codespace";

    // A read-only tool (no approval → Allow at every tier) and a destructive tool (gated → tier ladder).
    private static readonly IReadOnlyList<IAgentTool> Registry = new IAgentTool[]
    {
        new FakeTool("git.list_prs", readOnly: true),
        new FakeTool("git.open_pr", readOnly: false),
    };

    [Fact]
    public void Qualified_names_are_the_mcp_server_prefixed_kinds()
    {
        McpAllowedTools.QualifiedName(ServerName, "git.open_pr").ShouldBe("mcp__codespace__git.open_pr");
    }

    [Theory]
    // Confined: the destructive git.open_pr is Denied → omitted; only the read-only git.list_prs is offered.
    [InlineData(AgentAutonomyLevel.Confined, new[] { "mcp__codespace__git.list_prs" })]
    // Standard/Trusted: the destructive tool needs approval (NOT Deny) → it IS offered (subject to the endpoint's HITL).
    [InlineData(AgentAutonomyLevel.Standard, new[] { "mcp__codespace__git.list_prs", "mcp__codespace__git.open_pr" })]
    [InlineData(AgentAutonomyLevel.Trusted, new[] { "mcp__codespace__git.list_prs", "mcp__codespace__git.open_pr" })]
    // Unleashed: both run → both offered.
    [InlineData(AgentAutonomyLevel.Unleashed, new[] { "mcp__codespace__git.list_prs", "mcp__codespace__git.open_pr" })]
    public void Qualified_names_are_tier_filtered_a_denied_tool_is_never_offered(AgentAutonomyLevel autonomy, string[] expected)
    {
        McpAllowedTools.QualifiedNames(Registry, autonomy, ServerName).ShouldBe(expected);
    }

    [Theory]
    [InlineData(AgentAutonomyLevel.Confined)]
    [InlineData(AgentAutonomyLevel.Standard)]
    [InlineData(AgentAutonomyLevel.Trusted)]
    [InlineData(AgentAutonomyLevel.Unleashed)]
    public void Decision_request_is_unremovable_from_a_restricted_allow_list_at_every_tier(AgentAutonomyLevel autonomy)
    {
        // Slice B2 — the decide tool is an ASK, not a side effect (RequiresApproval=false), so the gate Allows it at
        // EVERY tier (even Confined). It must therefore ALWAYS be projected into a restricted agent's allow-list,
        // mirroring the endpoint which intercepts decision.request BEFORE the gate — otherwise a node that set a narrow
        // task.Tools could silently strip the agent's only way to ask. If a future change GATED the decide tool it would
        // be Denied at Confined and dropped here, so this pin makes such a change a visible, conscious decision.
        var registry = new IAgentTool[] { new DecisionRequestTool() };
        var decisionName = McpAllowedTools.QualifiedName(ServerName, DecisionRequestTool.ToolKind);

        decisionName.ShouldBe("mcp__codespace__decision.request");

        McpAllowedTools.QualifiedNames(registry, autonomy, ServerName)
            .ShouldContain(decisionName, customMessage: "the decide tool is offered at every tier — an ask is never gated away");

        var qualified = McpAllowedTools.QualifiedNames(registry, autonomy, ServerName).ToArray();
        McpAllowedTools.Augment(new[] { "Read", "Grep" }, qualified)
            .ShouldContain(decisionName, customMessage: "a restricted author tool list still keeps the decide tool — it survives the augmentation");
    }

    [Fact]
    public void Augment_appends_the_qualified_names_to_a_restricted_author_list_deduped_author_first()
    {
        var author = new[] { "Read", "Grep" };
        var qualified = McpAllowedTools.QualifiedNames(Registry, AgentAutonomyLevel.Unleashed, ServerName).ToArray();

        var merged = McpAllowedTools.Augment(author, qualified);

        // Author tools stay first + verbatim; the codespace tools follow.
        merged.ShouldBe(new[] { "Read", "Grep", "mcp__codespace__git.list_prs", "mcp__codespace__git.open_pr" });
    }

    [Fact]
    public void Augment_does_not_duplicate_a_qualified_name_the_author_already_listed()
    {
        var author = new[] { "Read", "mcp__codespace__git.list_prs" };

        var merged = McpAllowedTools.Augment(author, new[] { "mcp__codespace__git.list_prs", "mcp__codespace__git.open_pr" });

        merged.ShouldBe(new[] { "Read", "mcp__codespace__git.list_prs", "mcp__codespace__git.open_pr" }, customMessage: "an already-listed qualified name must not be appended twice");
    }

    [Fact]
    public void Augment_leaves_a_null_author_list_unchanged_so_the_cli_default_reaches_mcp()
    {
        // Default-all run: the harness omits --allowed-tools entirely, and the CLI default already reaches a
        // declared MCP server's tools. Augmenting must NOT narrow that to a restricted list.
        McpAllowedTools.Augment(null, new[] { "mcp__codespace__git.open_pr" })
            .ShouldBeNull(customMessage: "a null (default-all) author list must stay null — no forced allow-list");
    }

    [Fact]
    public void Augment_leaves_an_empty_author_list_unchanged_so_the_cli_default_reaches_mcp()
    {
        var empty = Array.Empty<string>();

        McpAllowedTools.Augment(empty, new[] { "mcp__codespace__git.open_pr" })
            .ShouldBeSameAs(empty, customMessage: "an empty (default-all) author list must not regress to a restricted allow-list");
    }

    private sealed class FakeTool : IAgentTool
    {
        private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

        private readonly bool _readOnly;

        public FakeTool(string kind, bool readOnly) { Kind = kind; _readOnly = readOnly; }

        public string Kind { get; }
        public string Description => Kind;
        public JsonElement InputSchema => Empty;
        public JsonElement OutputSchema => Empty;
        public bool IsReadOnly => _readOnly;
        public bool IsDestructive => !_readOnly;
        public bool RequiresApproval => !_readOnly;   // mirrors NodeAgentTool: read-only → no approval; side-effecting → gated

        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;
        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
