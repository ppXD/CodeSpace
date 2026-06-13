using System.IO.Pipelines;
using System.Text;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// An in-process full-duplex, newline-delimited text channel: a SERVER end (the framing loop reads requests, writes
/// responses) and a CLIENT end (a consumer writes requests, reads responses), bridged by two <see cref="Pipe"/>s.
/// This is the in-memory stand-in for the (later) UDS socket — the framing loop is pure over TextReader/TextWriter,
/// so swapping this for a socket-backed pair is a one-line change in the endpoint opener, not a loop rewrite.
///
/// <para>Disposing the channel completes both pipes and the underlying streams; the endpoint disposes it only AFTER
/// the loop has been cancelled + awaited, so the loop never reads from a torn-down pipe.</para>
/// </summary>
public sealed class AgentMcpChannel : IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // requestPipe: client writes → server reads.   responsePipe: server writes → client reads.
    private readonly Pipe _requestPipe = new();
    private readonly Pipe _responsePipe = new();

    private readonly Stream _requestWriteStream;
    private readonly Stream _requestReadStream;
    private readonly Stream _responseWriteStream;
    private readonly Stream _responseReadStream;

    public AgentMcpChannel()
    {
        _requestWriteStream = _requestPipe.Writer.AsStream();
        _requestReadStream = _requestPipe.Reader.AsStream();
        _responseWriteStream = _responsePipe.Writer.AsStream();
        _responseReadStream = _responsePipe.Reader.AsStream();

        ServerReader = NewReader(_requestReadStream);
        ServerWriter = NewWriter(_responseWriteStream);
        ClientReader = NewReader(_responseReadStream);
        ClientWriter = NewWriter(_requestWriteStream);
    }

    /// <summary>Server end — the framing loop reads requests here.</summary>
    public TextReader ServerReader { get; }

    /// <summary>Server end — the framing loop writes responses here.</summary>
    public TextWriter ServerWriter { get; }

    /// <summary>Client end — a consumer (test today, the proxy later) reads responses here.</summary>
    public TextReader ClientReader { get; }

    /// <summary>Client end — a consumer writes requests here.</summary>
    public TextWriter ClientWriter { get; }

    public void Dispose()
    {
        // Best-effort: each disposal is independent so one failure doesn't strand the rest. The endpoint only calls
        // this after the loop is cancelled + awaited, so nothing is mid-read.
        Quietly(ServerReader.Dispose);
        Quietly(ServerWriter.Dispose);
        Quietly(ClientReader.Dispose);
        Quietly(ClientWriter.Dispose);
        Quietly(_requestWriteStream.Dispose);
        Quietly(_requestReadStream.Dispose);
        Quietly(_responseWriteStream.Dispose);
        Quietly(_responseReadStream.Dispose);
    }

    private static StreamReader NewReader(Stream stream) => new(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false);

    private static StreamWriter NewWriter(Stream stream) => new(stream, Utf8NoBom) { AutoFlush = false, NewLine = "\n" };

    private static void Quietly(Action dispose)
    {
        try { dispose(); } catch { /* best-effort teardown */ }
    }
}
