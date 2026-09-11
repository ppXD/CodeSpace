using CodeSpace.Core.Services.Agents.Recovery;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents.Recovery;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// A transient ledger write failure on one orphan must not abort the rest of the sweep's batch — the pre-fix
/// <c>SettleAsync</c> called <c>UpsertAsync</c> unguarded, so one bad row skipped every orphan after it until the
/// next tick.
///
/// <para>Tier: Unit — the real <see cref="AgentRunOrphanReaper"/> over a ledger double that throws on its first write.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunOrphanReaperSettleTests
{
    [Fact]
    public async Task A_ledger_write_failure_on_one_orphan_does_not_stop_the_rest_of_the_sweep()
    {
        var host = LocalProcessRunner.CurrentHost;
        var ledger = new ThrowOnFirstWriteLedger([Orphan(host), Orphan(host)]);
        var reaper = new AgentRunOrphanReaper(ledger, NullLogger<AgentRunOrphanReaper>.Instance);

        var compensated = await reaper.ReapAsync(CancellationToken.None);

        compensated.ShouldBe(1, "row 1's ledger write threw and stays orphaned, but the loop must still reach row 2");
        ledger.WriteAttempts.ShouldBe(2, "both orphans in the batch must be attempted even after the first write fails");
    }

    /// <summary>A Spool orphan whose key names a path that does not exist — <c>AgentRunOrphanReaper</c> observes it as reclaimed without touching any kernel resource, so the test is deterministic on every OS.</summary>
    private static RunCleanupReceipt Orphan(string host) => new()
    {
        AgentRunId = Guid.NewGuid(), FenceEpoch = 1, Kind = RunResourceKind.Spool, Outcome = RunResourceOutcome.Orphaned,
        OwnerHost = host, ResourceKey = Path.Combine(Path.GetTempPath(), "codespace-orphan-reaper-" + Guid.NewGuid().ToString("N")),
        RecordedByHost = "some-other-host", RecordedAt = DateTimeOffset.UtcNow,
    };

    private sealed class ThrowOnFirstWriteLedger : IRunCleanupLedger
    {
        private readonly IReadOnlyList<RunCleanupReceipt> _orphans;

        public ThrowOnFirstWriteLedger(IReadOnlyList<RunCleanupReceipt> orphans) { _orphans = orphans; }

        public int WriteAttempts { get; private set; }

        public Task UpsertAsync(RunCleanupReceipt receipt, CancellationToken cancellationToken)
        {
            WriteAttempts++;

            if (WriteAttempts == 1) throw new InvalidOperationException("transient ledger failure");

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunCleanupReceipt>> ForRunsAsync(Guid teamId, IReadOnlyCollection<Guid> agentRunIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RunCleanupReceipt>> OrphanedOnHostAsync(string host, int batch, CancellationToken cancellationToken) => Task.FromResult(_orphans);
    }
}
