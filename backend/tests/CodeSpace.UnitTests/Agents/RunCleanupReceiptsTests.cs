using CodeSpace.Core.Services.Agents.Recovery;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Recovery;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The one piece of JUDGEMENT in cross-host abandon cleanup: what a worker that did not launch a run is entitled to
/// say about that run's resources. Pure, so every claim is pinned without a database, a filesystem, or a kernel.
///
/// <para>Tier: Unit — the real <see cref="RunCleanupReceipts"/> planner over handcrafted handles.</para>
/// </summary>
public class RunCleanupReceiptsTests
{
    private static readonly RunCleanupStamp Stamp = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 7, "reconciling-host", new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_foreign_abandon_orphans_every_host_local_resource_the_handle_names()
    {
        var receipts = RunCleanupReceipts.ForForeignAbandon(FullHandle(), Stamp, logSegmentsTerminalized: 0);

        receipts.Where(receipt => receipt.Outcome == RunResourceOutcome.Orphaned).Select(receipt => receipt.Kind).ShouldBe(
            [RunResourceKind.Spool, RunResourceKind.McpSocket, RunResourceKind.EgressSubnet, RunResourceKind.Cgroup, RunResourceKind.Workspace],
            ignoreOrder: true,
            customMessage: "each of these lives only on the launching host, so a foreign abandon can do nothing but name it");
    }

    [Fact]
    public void No_resource_of_a_foreign_run_is_ever_reported_as_cleaned_up_here()
    {
        var receipts = RunCleanupReceipts.ForForeignAbandon(FullHandle(), Stamp, logSegmentsTerminalized: 0);

        receipts.Where(receipt => receipt.Kind != RunResourceKind.LogSegments).ShouldAllBe(receipt => !receipt.IsSettled,
            customMessage: "the pre-fix abandon RAN the local netns/cgroup teardowns against foreign keys; a settled receipt here would re-assert exactly that false claim");
        receipts.Where(receipt => receipt.Outcome == RunResourceOutcome.Orphaned).ShouldAllBe(receipt => receipt.OwnerHost == "host-a",
            customMessage: "an orphan is addressed TO a host — one that names none addresses nobody");
    }

    [Theory]
    [InlineData(RunResourceKind.EgressSubnet, "netns-key")]
    [InlineData(RunResourceKind.Cgroup, "cgroup-key")]
    [InlineData(RunResourceKind.Spool, "/spool/run")]
    [InlineData(RunResourceKind.McpSocket, "/spool/run/mcp.sock")]
    [InlineData(RunResourceKind.Workspace, "/clones/run")]
    public void Each_orphan_carries_the_teardown_handle_the_owning_host_needs(RunResourceKind kind, string expectedKey)
    {
        var receipts = RunCleanupReceipts.ForForeignAbandon(FullHandle(), Stamp, logSegmentsTerminalized: 0);

        receipts.Single(receipt => receipt.Kind == kind).ResourceKey.ShouldBe(expectedKey,
            customMessage: "without the key the receipt names a resource the owning host's sweep cannot find");
    }

    [Fact]
    public void A_resource_the_run_never_held_earns_no_row()
    {
        var bare = new SandboxHandle { Kind = "local", ProcessId = 4242, LaunchHost = "host-a", SpoolDirectory = "/spool/run", Deadline = DateTimeOffset.UtcNow };

        var receipts = RunCleanupReceipts.ForForeignAbandon(bare, Stamp, logSegmentsTerminalized: 0);

        receipts.Where(receipt => receipt.Outcome == RunResourceOutcome.Orphaned).Select(receipt => receipt.Kind).ShouldBe([RunResourceKind.Spool],
            customMessage: "an orphan row is a claim that something real is standing there; a run with no netns/cgroup/clone/socket left none of them behind");
    }

    [Fact]
    public void An_injected_credential_is_permanently_unknown_never_orphaned()
    {
        var receipt = RunCleanupReceipts.ForForeignAbandon(FullHandle(), Stamp, logSegmentsTerminalized: 0)
            .Single(r => r.Kind == RunResourceKind.ProviderCredentialLease);

        receipt.Outcome.ShouldBe(RunResourceOutcome.Unknown, "the agent may have been mid-call when its host died, and no sweep anywhere can establish otherwise");
        receipt.ErrorCode.ShouldBe(RunCleanupReceipts.CredentialUnknowableCode);
    }

    [Fact]
    public void A_log_capture_receipt_is_completed_only_when_the_abandon_settled_some()
    {
        var receipts = RunCleanupReceipts.ForForeignAbandon(FullHandle(), Stamp, logSegmentsTerminalized: 2);

        receipts.Single(receipt => receipt.Kind == RunResourceKind.LogSegments).Outcome.ShouldBe(RunResourceOutcome.Completed);
    }

    [Fact]
    public void No_log_capture_receipt_is_written_when_the_abandon_settled_none()
    {
        var receipts = RunCleanupReceipts.ForForeignAbandon(FullHandle(), Stamp, logSegmentsTerminalized: 0);

        receipts.ShouldNotContain(receipt => receipt.Kind == RunResourceKind.LogSegments,
            customMessage: "mirrors the same-host branch: a sweep that moved no intent has nothing outstanding to report, not an unknown that lingers forever");
    }

    [Fact]
    public void An_unstamped_legacy_handle_plans_nothing_because_it_names_no_owner()
    {
        var legacy = new SandboxHandle { Kind = "local", ProcessId = 4242, LaunchHost = null, SpoolDirectory = "/spool/run", Deadline = DateTimeOffset.UtcNow, EgressNetnsKey = "netns-key" };

        RunCleanupReceipts.ForForeignAbandon(legacy, Stamp, logSegmentsTerminalized: 1).ShouldBeEmpty(
            "a handle written before the host stamp existed has no owner to address, and inventing one would address nobody");
    }

    [Fact]
    public void Every_receipt_carries_the_runs_fresh_fence_and_the_host_that_wrote_it()
    {
        var receipts = RunCleanupReceipts.ForForeignAbandon(FullHandle(), Stamp, logSegmentsTerminalized: 1);

        receipts.ShouldAllBe(receipt => receipt.AgentRunId == Stamp.AgentRunId && receipt.FenceEpoch == 7);
        receipts.ShouldAllBe(receipt => receipt.RecordedByHost == "reconciling-host" && receipt.RecordedAt == Stamp.RecordedAt);
    }

    private static SandboxHandle FullHandle() => new()
    {
        Kind = "local", ProcessId = 4242, LaunchHost = "host-a", SpoolDirectory = "/spool/run", Deadline = DateTimeOffset.UtcNow,
        McpSocketPath = "/spool/run/mcp.sock", EgressNetnsKey = "netns-key", CgroupRunKey = "cgroup-key",
        WorkspaceDirectory = "/clones/run", InjectedKeyFingerprint = "sha256:abc",
    };
}
