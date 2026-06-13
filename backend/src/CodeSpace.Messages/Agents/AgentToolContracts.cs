using System.Text.Json;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// One invocation of an agent tool: the input the model produced, plus an optional idempotency key. The key is
/// the at-most-once handle for side-effecting tools — the same key on a retry/replay must NOT re-apply the
/// effect (open the PR once, not twice). Null = the call is not idempotent (read-only / inherently safe to repeat).
/// </summary>
public sealed record AgentToolCall
{
    public required JsonElement Input { get; init; }
    public string? IdempotencyKey { get; init; }
}

/// <summary>Result of the pure, I/O-free input-validation stage — the first gate before any permission check or side effect.</summary>
public sealed record AgentToolValidation
{
    public required bool IsValid { get; init; }

    /// <summary>A teachable error the model can act on; null when valid.</summary>
    public string? Error { get; init; }

    public static AgentToolValidation Valid { get; } = new() { IsValid = true };
    public static AgentToolValidation Invalid(string error) => new() { IsValid = false, Error = error };
}

/// <summary>
/// Structured outcome of a tool call. Either a success carrying an <see cref="Output"/> payload or a typed
/// <see cref="Error"/> the model can retry on — never a thrown exception across the tool boundary.
/// <see cref="OutputBytes"/> reports the original size before any cap (pairs with OutputCap) so large results
/// stay navigable.
/// </summary>
public sealed record AgentToolResult
{
    public required bool IsError { get; init; }
    public JsonElement Output { get; init; }
    public string? Error { get; init; }
    public int OutputBytes { get; init; }

    public static AgentToolResult Ok(JsonElement output, int outputBytes) => new() { IsError = false, Output = output, OutputBytes = outputBytes };
    public static AgentToolResult Fail(string error) => new() { IsError = true, Error = error };
}
