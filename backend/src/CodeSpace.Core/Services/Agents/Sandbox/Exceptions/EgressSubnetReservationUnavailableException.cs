using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Sandbox.Exceptions;

/// <summary>
/// A filtered-egress launch REFUSED because this host cannot reserve the run's /30 at all — its reservation directory
/// can be neither created nor written. Fail-closed by the same rule the rest of the netns setup already follows: two
/// concurrently-active runs sharing one /30 co-evaluate each other's packets in the host-global nftables forward chain
/// (a cross-run egress WIDEN), so a run that cannot hold a reservation is aborted rather than handed a subnet nothing
/// reserved. The directory lives under the agent-run spool root the run's own <c>out.log</c> / <c>exit</c> marker need
/// too, so a host this refuses was not going to complete the run anyway — refusing names the cause instead.
///
/// <para>It carries the directory and the OS error (<see cref="Exception.InnerException"/>) because the actionable
/// fact is the MOUNT, not the run: before this, a rights error on a reservation file was counted as another worker's
/// contention, every candidate /30 was lost the same way, and the allocator then reported "this host already holds
/// 4096 filtered-egress /30s" — a true sentence about nothing, pointing an operator at concurrency that did not
/// exist.</para>
/// </summary>
public sealed class EgressSubnetReservationUnavailableException : Exception, IFailure
{
    public EgressSubnetReservationUnavailableException(string reservationDirectory, string reason, Exception? cause)
        : base($"EgressSubnetAllocator: {reason} ({reservationDirectory}); refusing this filtered-egress run rather than handing it a /30 no lock reserved.", cause)
    {
        ReservationDirectory = reservationDirectory;
        Reason = reason;
    }

    /// <summary>The directory whose rights an operator has to fix.</summary>
    public string ReservationDirectory { get; }

    /// <summary>Which cause it was, in the allocator's own words, so the message, the Warning and a test all name the same thing.</summary>
    public string Reason { get; }

    // The host's own runtime directory, not the caller's request: nothing about the launch can be changed to make it
    // work, and the same launch succeeds untouched once an operator fixes the mount — which is Unavailable exactly.
    FailureKind IFailure.Kind => FailureKind.Unavailable;
    string IFailure.Code => FailureCodes.SandboxEgressReservationUnavailable;
    string? IFailure.ClientMessage => "This host cannot reserve a private subnet for a network-restricted run right now.";
}
