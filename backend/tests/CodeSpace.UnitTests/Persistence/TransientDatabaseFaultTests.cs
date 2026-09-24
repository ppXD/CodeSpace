using System.Net.Sockets;
using System.Text.Json;
using CodeSpace.Core.Persistence;
using CodeSpace.Core.Services.Agents.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Everything this predicate admits is retried by the agent observer and everything it refuses surfaces, so each row
/// here is a decision about a real fault shape: what Npgsql raises for a dropped or refused connection, what EF's
/// provider strategy wraps a transient query fault in, and the verdicts about a write that would fail identically a
/// moment later.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TransientDatabaseFaultTests
{
    public enum Fault
    {
        AdminShutdown,
        ConnectionFailure,
        TooManyConnections,
        ServerDiskFull,
        StreamReadFailed,
        CommandTimeout,
        ConnectionRefused,
        BareTimeout,
        BareSocket,
        SocketReset,
        EfStrategyWrappedAdminShutdown,
        SaveChangesOverDroppedConnection,
        UniqueViolation,
        CheckViolation,
        CharacterNotInRepertoire,
        SaveChangesOverUniqueViolation,
        NpgsqlWithoutTransportCause,
        PlainInvalidOperation,
        InvalidOperationOverSerializationBug,
        ClientDiskFull,
        SerializationBug,
        LostFence,
        Cancellation,
    }

    [Theory]
    [InlineData(Fault.AdminShutdown, true)]                         // 57P01: pg_terminate_backend, a restart, a failover
    [InlineData(Fault.ConnectionFailure, true)]                     // 08006
    [InlineData(Fault.TooManyConnections, true)]                    // 53300: the server is full now, not forever
    [InlineData(Fault.ServerDiskFull, true)]                        // 53100: Npgsql's own classification — the server's disk, "not now"
    [InlineData(Fault.StreamReadFailed, true)]                      // the socket died under a command
    [InlineData(Fault.CommandTimeout, true)]                        // Npgsql's command / pool timeout
    [InlineData(Fault.ConnectionRefused, true)]                     // the server is not listening yet
    [InlineData(Fault.BareTimeout, true)]
    [InlineData(Fault.BareSocket, true)]
    [InlineData(Fault.SocketReset, true)]                           // an IOException that IS a reset
    [InlineData(Fault.EfStrategyWrappedAdminShutdown, true)]        // how every EF query surfaces a transient fault
    [InlineData(Fault.SaveChangesOverDroppedConnection, true)]
    [InlineData(Fault.UniqueViolation, false)]                      // a verdict: the same insert fails again
    [InlineData(Fault.CheckViolation, false)]
    [InlineData(Fault.CharacterNotInRepertoire, false)]             // 22021: a bad byte stays bad
    [InlineData(Fault.SaveChangesOverUniqueViolation, false)]
    [InlineData(Fault.NpgsqlWithoutTransportCause, false)]
    [InlineData(Fault.PlainInvalidOperation, false)]
    [InlineData(Fault.InvalidOperationOverSerializationBug, false)]
    [InlineData(Fault.ClientDiskFull, false)]                       // this host's disk: an IOException that is not a reset
    [InlineData(Fault.SerializationBug, false)]
    [InlineData(Fault.LostFence, false)]                            // the tear-down arms own it; retrying would hide it
    [InlineData(Fault.Cancellation, false)]                         // a torn-down worker, never a blip
    public void Only_a_fault_on_the_way_to_the_server_is_transient(Fault shape, bool transient)
    {
        TransientDatabaseFault.Is(Build(shape)).ShouldBe(transient, $"{shape} must {(transient ? "" : "NOT ")}be offered again");
    }

    private static Exception Build(Fault shape) => shape switch
    {
        Fault.AdminShutdown => Postgres(PostgresErrorCodes.AdminShutdown),
        Fault.ConnectionFailure => Postgres(PostgresErrorCodes.ConnectionFailure),
        Fault.TooManyConnections => Postgres(PostgresErrorCodes.TooManyConnections),
        Fault.ServerDiskFull => Postgres(PostgresErrorCodes.DiskFull),
        Fault.StreamReadFailed => new NpgsqlException("Exception while reading from stream", new EndOfStreamException("Attempted to read past the end of the stream.")),
        Fault.CommandTimeout => new NpgsqlException("Exception while reading from stream", new TimeoutException("Timeout during reading attempt")),
        Fault.ConnectionRefused => new NpgsqlException("Failed to connect to 127.0.0.1:5432", new SocketException((int)SocketError.ConnectionRefused)),
        Fault.BareTimeout => new TimeoutException("The operation has timed out."),
        Fault.BareSocket => new SocketException((int)SocketError.ConnectionReset),
        Fault.SocketReset => new IOException("Unable to read data from the transport connection.", new SocketException((int)SocketError.ConnectionReset)),
        Fault.EfStrategyWrappedAdminShutdown => new InvalidOperationException("An exception has been raised that is likely due to a transient failure.", Postgres(PostgresErrorCodes.AdminShutdown)),
        Fault.SaveChangesOverDroppedConnection => new DbUpdateException("An error occurred while saving the entity changes.", new NpgsqlException("Exception while writing to stream", new IOException("Broken pipe"))),
        Fault.UniqueViolation => Postgres(PostgresErrorCodes.UniqueViolation),
        Fault.CheckViolation => Postgres(PostgresErrorCodes.CheckViolation),
        Fault.CharacterNotInRepertoire => Postgres(PostgresErrorCodes.CharacterNotInRepertoire),
        Fault.SaveChangesOverUniqueViolation => new DbUpdateException("An error occurred while saving the entity changes.", Postgres(PostgresErrorCodes.UniqueViolation)),
        Fault.NpgsqlWithoutTransportCause => new NpgsqlException("The connection is not open."),
        Fault.PlainInvalidOperation => new InvalidOperationException("injected fault: simulated DB failure flushing a batched agent-event write"),
        Fault.InvalidOperationOverSerializationBug => new InvalidOperationException("could not persist the payload", new JsonException("'}' is invalid after a value.")),
        Fault.ClientDiskFull => new IOException("No space left on device"),
        Fault.SerializationBug => new JsonException("'}' is invalid after a value."),
        Fault.LostFence => new AgentRunOwnershipLostException(Guid.NewGuid()),
        Fault.Cancellation => new OperationCanceledException(),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static PostgresException Postgres(string sqlState) => new($"server raised {sqlState}", "ERROR", "ERROR", sqlState);
}
