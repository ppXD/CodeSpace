using System.Text.Json;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// The MCP protocol core — turns ONE parsed JSON-RPC request into its response (or none, for a notification). This
/// is the seam the future stdio transport pumps newline-delimited messages through; isolating the protocol mapping
/// here keeps it pure + exhaustively unit-testable, with no process / stream / config-file concerns.
/// </summary>
public interface IMcpRequestHandler
{
    /// <summary>
    /// Handle one parsed JSON-RPC request and produce its serialized response, or <c>null</c> for a notification
    /// (a request with no <c>id</c> — JSON-RPC forbids replying to those). Never throws except to propagate
    /// cancellation.
    /// </summary>
    Task<JsonElement?> HandleAsync(JsonElement request, CancellationToken cancellationToken);
}
