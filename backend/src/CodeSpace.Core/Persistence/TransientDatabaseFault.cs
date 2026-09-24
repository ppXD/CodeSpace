using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Persistence;

/// <summary>
/// Whether a database call failed on the WAY to the server rather than AT it — a fault the very same statement can
/// survive a moment later, as opposed to a verdict about the statement.
///
/// <para>Deliberately small and explicit, because everything it admits gets retried and everything it refuses
/// surfaces. It admits: <see cref="NpgsqlException.IsTransient"/> — Npgsql's own answer, which covers a reset or
/// refused connection, a command or pool timeout, and the server-side SQLSTATEs that mean "not now" (57P01 admin
/// shutdown, 08xxx connection failures, 53xxx insufficient resources, 40001/40P01); a bare
/// <see cref="TimeoutException"/> or <see cref="SocketException"/>; and an <see cref="IOException"/> that IS a socket
/// reset. It sees through the two wrappers a transport fault arrives in: a <see cref="DbUpdateException"/>, and the
/// <see cref="InvalidOperationException"/> the Npgsql EF provider's execution strategy raises around a transient
/// fault on every query it runs. It admits nothing else: a constraint violation, a serialization bug, a lost fence, a
/// cancellation or a full disk on THIS host (a bare <see cref="IOException"/>) would fail again identically, and
/// retrying one only delays the truth.</para>
///
/// <para>Npgsql's classification is taken as it stands rather than second-guessed, and that includes its view that a
/// SERVER out of disk (53100), memory (53200) or I/O (58030) is "not now" rather than "never": such a write is offered
/// again within the caller's bound and, if the server stays unable, given up as the caller decides.</para>
/// </summary>
public static class TransientDatabaseFault
{
    public static bool Is(Exception exception) => exception switch
    {
        NpgsqlException npgsql => npgsql.IsTransient,
        TimeoutException or SocketException or IOException { InnerException: SocketException } => true,
        DbUpdateException or InvalidOperationException => exception.InnerException is { } inner && Is(inner),
        _ => false,
    };
}
