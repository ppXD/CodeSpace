using System.Net.Sockets;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// What a crashed worker leaves at a per-run socket's path: a socket FILE whose socket is gone. It refuses every
/// connect, and a plain bind at the path fails with <c>EADDRINUSE</c> until someone deletes it — which is exactly what
/// a listener re-opening that path has to do first.
///
/// <para><b>Why it is made beside the path and moved onto it.</b> .NET's <c>Socket.Dispose</c> unlinks the path it
/// bound, which a SIGKILLed or OOM-killed worker never gets to do. A socket bound AT the path and then disposed leaves
/// nothing behind, so a fixture built that way stages a clean exit and proves nothing about a crash. Moving the file
/// onto the path first means the dispose unlinks a name that is already gone, and the file at the path outlives its
/// socket.</para>
/// </summary>
internal static class StaleUnixSocket
{
    public static void LeaveAt(string path)
    {
        var aside = path + ".dead";

        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            dead.Bind(new UnixDomainSocketEndPoint(aside));
            File.Move(aside, path);
        }

        File.Exists(path).ShouldBeTrue("precondition: the stale socket file outlives the socket that made it, as a crashed worker's does");
        Should.Throw<SocketException>(() => BindOnce(path), "precondition: the stale file blocks a plain bind at its path, so only a listener that clears it first can serve there again");
    }

    private static void BindOnce(string path)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        probe.Bind(new UnixDomainSocketEndPoint(path));
    }
}
