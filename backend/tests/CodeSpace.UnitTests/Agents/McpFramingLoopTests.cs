using System.Text.Json;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Messages.Agents.Mcp;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the newline-delimited JSON-RPC framing pump (<see cref="McpFramingLoop"/>): one request → one response line
/// (id echoed), a malformed line → exactly one top-level -32700 reply with a null id and the loop CONTINUES, a
/// notification (no id from the handler) → NO line written, sequential ordering preserved (a notification leaves no
/// gap line), clean return on EOF, and OperationCanceledException surfacing on cancellation (no hang). Drives the real
/// loop over StringReader/StringWriter (buffered) or a blocking reader (cancellation/hang cases) with a tiny scripted
/// handler — no process / socket concern.
/// </summary>
[Trait("Category", "Unit")]
public class McpFramingLoopTests
{
    // ── Dispatch / response framing ─────────────────────────────────────────

    [Fact]
    public async Task One_request_yields_one_response_line_echoing_the_id()
    {
        var writer = new StringWriter();
        var loop = new McpFramingLoop(new EchoIdHandler());

        await loop.RunAsync(new StringReader("""{"id":7,"method":"x"}"""), writer, CancellationToken.None);

        var lines = Lines(writer);
        lines.Length.ShouldBe(1);
        Parse(lines[0]).GetProperty("id").GetInt32().ShouldBe(7);
    }

    [Fact]
    public async Task Malformed_line_yields_one_parse_error_with_null_id_and_the_loop_continues()
    {
        var writer = new StringWriter();
        var loop = new McpFramingLoop(new EchoIdHandler());

        // First line is not JSON (→ -32700, null id), the loop must keep going and answer the second.
        await loop.RunAsync(new StringReader("not json\n" + """{"id":2,"method":"x"}"""), writer, CancellationToken.None);

        var lines = Lines(writer);
        lines.Length.ShouldBe(2);

        var err = Parse(lines[0]);
        err.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        err.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(JsonRpcError.ParseError);

        Parse(lines[1]).GetProperty("id").GetInt32().ShouldBe(2);   // proves the pump survived the parse failure
    }

    [Fact]
    public async Task A_notification_writes_nothing_and_the_loop_continues()
    {
        var writer = new StringWriter();
        // The handler returns null (notification) for the first line, a response for the second.
        var loop = new McpFramingLoop(new NotificationThenEchoHandler());

        await loop.RunAsync(new StringReader("""{"method":"notify"}""" + "\n" + """{"id":5,"method":"x"}"""), writer, CancellationToken.None);

        var lines = Lines(writer);
        lines.Length.ShouldBe(1);   // only the second line produced a reply — no gap/blank line for the notification
        Parse(lines[0]).GetProperty("id").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task Multiple_sequential_requests_yield_one_response_each_in_order_with_no_gap_for_a_notification()
    {
        var writer = new StringWriter();
        var loop = new McpFramingLoop(new EchoOrNotifyHandler());

        // id 1 → reply, notification → nothing, id 3 → reply. Responses must be [1,3] with no blank line between.
        var input = string.Join('\n',
            """{"id":1,"method":"x"}""",
            """{"method":"notify"}""",
            """{"id":3,"method":"x"}""");

        await loop.RunAsync(new StringReader(input), writer, CancellationToken.None);

        var lines = Lines(writer);
        lines.Length.ShouldBe(2);
        Parse(lines[0]).GetProperty("id").GetInt32().ShouldBe(1);
        Parse(lines[1]).GetProperty("id").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Eof_ends_the_loop_cleanly_with_no_trailing_write()
    {
        var writer = new StringWriter();
        var loop = new McpFramingLoop(new EchoIdHandler());

        // Empty reader = immediate EOF (null first read) → RunAsync returns, nothing written.
        await loop.RunAsync(new StringReader(""), writer, CancellationToken.None);

        writer.ToString().ShouldBeEmpty();
    }

    // ── Cancellation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_surfaces_OperationCanceledException_without_hanging()
    {
        var writer = new StringWriter();
        var loop = new McpFramingLoop(new EchoIdHandler());
        using var cts = new CancellationTokenSource();

        // A reader whose ReadLineAsync never completes until cancelled — proves the cancel unwinds the read.
        var run = loop.RunAsync(new BlockingReader(), writer, cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Always replies, echoing whatever <c>id</c> the request carried (a happy-path stand-in for the real handler).</summary>
    private sealed class EchoIdHandler : IMcpRequestHandler
    {
        public Task<JsonElement?> HandleAsync(JsonElement request, CancellationToken cancellationToken) =>
            Task.FromResult<JsonElement?>(EchoResponse(request));
    }

    /// <summary>First call → notification (null), every later call → echo reply.</summary>
    private sealed class NotificationThenEchoHandler : IMcpRequestHandler
    {
        private int _calls;

        public Task<JsonElement?> HandleAsync(JsonElement request, CancellationToken cancellationToken) =>
            Task.FromResult(_calls++ == 0 ? (JsonElement?)null : EchoResponse(request));
    }

    /// <summary>A request with an <c>id</c> → echo reply; a request with no <c>id</c> → notification (null).</summary>
    private sealed class EchoOrNotifyHandler : IMcpRequestHandler
    {
        public Task<JsonElement?> HandleAsync(JsonElement request, CancellationToken cancellationToken) =>
            Task.FromResult(request.TryGetProperty("id", out _) ? (JsonElement?)EchoResponse(request) : null);
    }

    private static JsonElement EchoResponse(JsonElement request)
    {
        var id = request.TryGetProperty("id", out var i) ? i.Clone() : Parse("null");
        return JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id, result = new { } });
    }

    /// <summary>A reader whose ReadLineAsync blocks until the token cancels — never returns a line, never EOFs on its own.</summary>
    private sealed class BlockingReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }
}
