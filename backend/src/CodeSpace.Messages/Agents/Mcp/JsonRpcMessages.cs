using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents.Mcp;

/// <summary>
/// A JSON-RPC 2.0 response envelope — the wire shape every MCP reply takes. It carries EITHER <c>Result</c>
/// (success) OR <c>Error</c> (failure), never both; the <c>Ok</c> / <c>Fail</c> factories are the only way to build
/// one, so the invariant can't be violated. <c>Id</c> echoes the request's id verbatim (a string or number), and is
/// JSON <c>null</c> for an error whose id couldn't be read. The absent of result/error is OMITTED from the wire (not
/// emitted as null) so the envelope matches the spec exactly.
/// </summary>
public sealed record JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement Id { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; init; }

    public static JsonRpcResponse Ok(JsonElement id, JsonElement result) => new() { Id = id, Result = result };
    public static JsonRpcResponse Fail(JsonElement id, JsonRpcError error) => new() { Id = id, Error = error };
}

/// <summary>
/// A JSON-RPC 2.0 error object — the protocol-level failure channel (malformed envelope, unknown method, bad
/// params). The named constants are the canonical reserved codes; a tool that merely FAILS does NOT use this channel
/// (it returns a normal result with <c>isError</c>), so this is reserved for envelope/routing problems.
/// </summary>
public sealed record JsonRpcError
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    [JsonPropertyName("code")]
    public required int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
