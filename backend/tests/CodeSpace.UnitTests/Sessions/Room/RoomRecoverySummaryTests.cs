using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Agents.Recovery;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// The Room's fold over a run's cleanup receipts — what an operator is told is still standing, and where. Settled
/// receipts must stay invisible: a resource already reclaimed is not something a reader has to act on, and surfacing
/// it would make every abandoned run look permanently broken.
///
/// <para>Tier: Unit — the real <see cref="RoomProjector.SummarizeRecovery"/> fold over handcrafted receipts.</para>
/// </summary>
public class RoomRecoverySummaryTests
{
    [Fact]
    public void A_run_whose_resources_were_all_reclaimed_says_nothing()
    {
        RoomProjector.SummarizeRecovery([
            Receipt(RunResourceKind.Spool, RunResourceOutcome.Compensated, "host-a"),
            Receipt(RunResourceKind.EgressSubnet, RunResourceOutcome.Completed, "host-a"),
        ]).ShouldBeNull("nothing is outstanding, so the card has nothing to warn about");
    }

    [Fact]
    public void Outstanding_orphans_are_counted_and_their_host_named()
    {
        var summary = RoomProjector.SummarizeRecovery([
            Receipt(RunResourceKind.Spool, RunResourceOutcome.Orphaned, "host-a"),
            Receipt(RunResourceKind.EgressSubnet, RunResourceOutcome.Orphaned, "host-a"),
            Receipt(RunResourceKind.Cgroup, RunResourceOutcome.Orphaned, "host-a"),
            Receipt(RunResourceKind.LogSegments, RunResourceOutcome.Completed, "host-a"),
        ]);

        summary.ShouldNotBeNull();
        summary!.OrphanedCount.ShouldBe(3);
        summary.UnknownCount.ShouldBe(0);
        summary.OrphanHosts.ShouldBe(["host-a"]);
        summary.Detail.ShouldBe("3 resources orphaned on host host-a");
    }

    [Fact]
    public void Resources_stranded_across_two_hosts_name_both()
    {
        var summary = RoomProjector.SummarizeRecovery([
            Receipt(RunResourceKind.Spool, RunResourceOutcome.Orphaned, "host-b"),
            Receipt(RunResourceKind.Cgroup, RunResourceOutcome.Orphaned, "host-a"),
        ]);

        summary!.Detail.ShouldBe("2 resources orphaned on hosts host-a, host-b", "a reader has to know every machine that still has to be reaped");
    }

    [Fact]
    public void An_unsupported_teardown_is_reported_separately_from_an_orphan()
    {
        var both = RoomProjector.SummarizeRecovery([
            Receipt(RunResourceKind.Spool, RunResourceOutcome.Orphaned, "host-a"),
            Receipt(RunResourceKind.Cgroup, RunResourceOutcome.Unknown, "host-a"),
        ]);

        both!.Detail.ShouldBe("1 resource orphaned on host host-a · 1 with an unknown cleanup state",
            "an orphan is addressed to a host and an unknowable teardown is addressed to nobody — collapsing them would claim a sweep can fix the second");
    }

    [Fact]
    public void A_provider_credential_lease_left_unknown_never_counts_towards_unknown_count()
    {
        var summary = RoomProjector.SummarizeRecovery([
            Receipt(RunResourceKind.Spool, RunResourceOutcome.Orphaned, "host-a"),
            Receipt(RunResourceKind.ProviderCredentialLease, RunResourceOutcome.Unknown, "host-a"),
        ]);

        summary!.UnknownCount.ShouldBe(0, "a structurally unknowable credential lease is not outstanding cleanup work");
        summary.Detail.ShouldBe("1 resource orphaned on host host-a");
    }

    [Fact]
    public void A_run_fully_compensated_except_an_unknowable_credential_lease_says_nothing()
    {
        RoomProjector.SummarizeRecovery([
            Receipt(RunResourceKind.Spool, RunResourceOutcome.Compensated, "host-a"),
            Receipt(RunResourceKind.EgressSubnet, RunResourceOutcome.Compensated, "host-a"),
            Receipt(RunResourceKind.Cgroup, RunResourceOutcome.Compensated, "host-a"),
            Receipt(RunResourceKind.ProviderCredentialLease, RunResourceOutcome.Unknown, "host-a"),
        ]).ShouldBeNull("once the owner host reclaims every resource it can, a permanently-unknowable credential lease must not keep the Room's warning alive forever");
    }

    private static RunCleanupReceipt Receipt(RunResourceKind kind, RunResourceOutcome outcome, string? ownerHost) => new()
    {
        AgentRunId = Guid.Parse("22222222-2222-2222-2222-222222222222"), FenceEpoch = 3, Kind = kind, Outcome = outcome,
        OwnerHost = ownerHost, RecordedByHost = "host-b", RecordedAt = DateTimeOffset.UnixEpoch,
    };
}
