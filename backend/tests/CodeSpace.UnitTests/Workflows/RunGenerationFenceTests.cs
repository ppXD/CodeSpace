using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The walk claim <see cref="RunGenerationFence"/> carries to every node a walk runs: read back only for the run it was
/// taken for, restored when released, flowing with the async call that took it — and a lock that refuses to run where it
/// could not hold. The lock itself, and a Continue meeting it, are pinned against real Postgres by the stop/continue flow
/// suites; nothing here reaches a database.
/// </summary>
[Trait("Category", "Unit")]
public class RunGenerationFenceTests
{
    [Fact]
    public void A_claim_is_read_back_only_for_the_run_it_was_taken_for()
    {
        var runId = Guid.NewGuid();

        using (RunGenerationFence.Claim(runId, 3))
        {
            RunGenerationFence.ClaimedFor(runId).ShouldBe(3, "the walk's own run reads the generation it claimed");
            RunGenerationFence.ClaimedFor(Guid.NewGuid()).ShouldBeNull("any other run — a child the node starts, a run it only reads — is not fenced by this walk's claim");
        }

        RunGenerationFence.ClaimedFor(runId).ShouldBeNull("released, the claim is gone");
    }

    [Fact]
    public void Releasing_a_nested_claim_restores_the_one_it_replaced()
    {
        var outerRun = Guid.NewGuid();
        var innerRun = Guid.NewGuid();

        using (RunGenerationFence.Claim(outerRun, 1))
        {
            using (RunGenerationFence.Claim(innerRun, 7))
            {
                RunGenerationFence.ClaimedFor(innerRun).ShouldBe(7);
                RunGenerationFence.ClaimedFor(outerRun).ShouldBeNull("while a nested walk holds its claim, only its own run is fenced");
            }

            RunGenerationFence.ClaimedFor(outerRun).ShouldBe(1, "releasing the nested claim restores the outer walk's");
        }
    }

    [Fact]
    public async Task A_claim_flows_with_the_async_call_that_took_it_and_never_into_its_caller()
    {
        var runId = Guid.NewGuid();

        async Task<int?> ClaimAcrossAnAwaitAsync()
        {
            using var claim = RunGenerationFence.Claim(runId, 5);
            await Task.Yield();
            return RunGenerationFence.ClaimedFor(runId);
        }

        (await ClaimAcrossAnAwaitAsync()).ShouldBe(5, "the claim survives the awaits of the call that took it");
        RunGenerationFence.ClaimedFor(runId).ShouldBeNull("and never leaks into the caller");
    }

    [Fact]
    public async Task The_lock_refuses_to_run_outside_a_transaction()
    {
        await using var db = UnreachableDatabase();

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => RunGenerationFence.TryLockAsync(db, Guid.NewGuid(), 1, CancellationToken.None));

        refused.Message.ShouldContain("needs the caller's transaction", customMessage: "outside one the share lock would end with its own statement — a check, not a fence");
    }

    [Fact]
    public async Task Outside_a_walk_nothing_is_fenced_and_no_database_is_touched()
    {
        await using var db = UnreachableDatabase();
        var runId = Guid.NewGuid();

        await RunGenerationFence.EnterAsync(db, runId, CancellationToken.None);
        await RunGenerationFence.ThrowIfSupersededAsync(db, runId, CancellationToken.None);

        (await RunGenerationFence.CommitUnderClaimAsync(db, runId, () => Task.FromResult(42), CancellationToken.None)).ShouldBe(42, "a write outside a walk commits as it always did");
    }

    private static CodeSpaceDbContext UnreachableDatabase() =>
        new(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=unused").UseSnakeCaseNamingConvention().Options);
}
