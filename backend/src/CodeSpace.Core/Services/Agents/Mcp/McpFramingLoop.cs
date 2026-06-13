using System.Text.Json;
using CodeSpace.Messages.Agents.Mcp;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// The newline-delimited JSON-RPC framing pump that sits between a transport's byte streams and the protocol core
/// (<see cref="IMcpRequestHandler"/>). It reads one line at a time, parses it as a JSON-RPC request, hands the
/// parsed element to the handler, and writes the handler's serialized response back as exactly one newline-terminated
/// line — or NOTHING for a notification (the handler returns null). A line that isn't valid JSON is the ONLY thing
/// that produces a top-level <c>-32700</c> parse-error reply (with a null id), and the pump CONTINUES to the next
/// line afterwards — one malformed message never kills the stream.
///
/// <para>Pure over <see cref="TextReader"/> / <see cref="TextWriter"/> with no process / socket / config concern, so
/// it's exhaustively unit-testable with a StringReader/StringWriter pair and the real UDS transport (a later slice)
/// swaps in by handing this loop a socket-backed reader/writer — the loop itself doesn't change. Sequential by design:
/// each <see cref="IMcpRequestHandler.HandleAsync"/> is awaited before the next read, so there's no concurrent
/// in-flight handling (mirrors the line-by-line stdout pump in <c>LocalProcessRunner</c>). EOF (a null read) ends the
/// loop cleanly; cancellation surfaces as an <see cref="OperationCanceledException"/> so a caller can unwind it.</para>
/// </summary>
public sealed class McpFramingLoop
{
    private readonly IMcpRequestHandler _handler;

    public McpFramingLoop(IMcpRequestHandler handler) { _handler = handler; }

    public async Task RunAsync(TextReader reader, TextWriter writer, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!TryParse(line, out var request))
            {
                await WriteLineAsync(writer, ParseErrorResponse(), ct).ConfigureAwait(false);
                continue;
            }

            var response = await _handler.HandleAsync(request.RootElement, ct).ConfigureAwait(false);

            if (response is null) continue;   // notification → no reply

            await WriteLineAsync(writer, Serialize(response.Value), ct).ConfigureAwait(false);
        }
    }

    private static bool TryParse(string line, out JsonDocument request)
    {
        try { request = JsonDocument.Parse(line); return true; }
        catch (JsonException) { request = null!; return false; }
    }

    private static async Task WriteLineAsync(TextWriter writer, string payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(payload.AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string ParseErrorResponse() => Serialize(JsonSerializer.SerializeToElement(
        JsonRpcResponse.Fail(NullId, new JsonRpcError { Code = JsonRpcError.ParseError, Message = "Parse error: request line is not valid JSON." }), AgentJson.Options));

    private static string Serialize(JsonElement response) => JsonSerializer.Serialize(response, AgentJson.Options);

    private static readonly JsonElement NullId = JsonDocument.Parse("null").RootElement.Clone();
}
