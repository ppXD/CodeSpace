using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// P0-B2: the structured evidence of what the run's MCP fabric ACTUALLY did — seven facts captured off the live
/// endpoint at result time, so "the tools were available" stops being an inference from configuration and becomes
/// an observation. Before this, a run whose fabric silently failed (proxy missing, declaration unread, client
/// never connected) was indistinguishable from a run that simply chose not to call tools — and an MCP-REQUIRED
/// benchmark arm graded such a run as a model failure instead of infrastructure. Compact scalars by design:
/// inline on <see cref="AgentRunResult"/> (result_jsonb), never an artifact ref.
/// </summary>
public sealed record McpFabricEvidence
{
    /// <summary>The catalog width the task requested (<c>Full</c> / <c>ReadOnly</c>) — configuration, recorded beside the observations it should explain.</summary>
    public required string RequestedCatalogMode { get; init; }

    /// <summary>The per-run UDS endpoint bound + listened (the opener fail-softs, so false is a real degraded-host observation).</summary>
    public required bool EndpointBound { get; init; }

    /// <summary>The harness MCP declaration rode the sandbox spec (the runner writes it into the harness config home before exec).</summary>
    public required bool DeclarationWritten { get; init; }

    /// <summary>The <c>codespace-mcp</c> stdio proxy binary existed where the runner resolves it.</summary>
    public required bool ProxyResolved { get; init; }

    /// <summary>An authenticated client actually served the MCP <c>initialize</c> handshake at least once — the one fact that proves the fabric CONNECTED, not merely that it was offered.</summary>
    public required bool HandshakeObserved { get; init; }

    /// <summary>How many <c>tools/call</c> requests the in-process server dispatched for this run — including read-only calls the governance ledger never records.</summary>
    public required int ObservedToolCalls { get; init; }

    /// <summary>SHA-256 (hex) over the sorted qualified tool names this run's endpoint actually served — the effective catalog's identity, comparable across runs and modes. Null when the endpoint never bound.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EffectiveCatalogDigest { get; init; }
}
