using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Drives the REAL ToolCallLedgerService (resolved through CodeSpaceModule's DI, proving it's registered) against real
/// Postgres: the INSERT-first exactly-once invariant (a claim wins; a second claim for the same (run, key) dedups —
/// terminal returns the stored result, non-terminal returns InFlight; a CONCURRENT race yields exactly one Proceed),
/// the status-guarded terminal CAS (legal flip wins; an already-terminal row rejects; a FOREIGN team cannot flip the
/// owner's row), and the team-scoped audit read. The unique (agent_run_id, idempotency_key) index is the proof — driven
/// over real PG, not mocked.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ToolCallLedgerServiceTests
{
    private const string Key = "git.open_pr:abc";
    private const string InputHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly PostgresFixture _fixture;

    public ToolCallLedgerServiceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task First_claim_proceeds_and_a_second_pending_claim_is_in_flight()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid ledgerId;
        using (var scope = _fixture.BeginScope())
        {
            var claim = await Svc(scope).TryClaimAsync(runId, teamId, "git.open_pr", Key, InputHash, 0, CancellationToken.None);
            claim.Outcome.ShouldBe(ToolCallClaimOutcome.Proceed, "the first claim INSERTs the Pending row");
            ledgerId = claim.LedgerId;
        }

        using (var scope = _fixture.BeginScope())
        {
            // The first row is still Pending (no RecordTerminal yet) → a second claim for the same key is In-Flight.
            var second = await Svc(scope).TryClaimAsync(runId, teamId, "git.open_pr", Key, InputHash, 0, CancellationToken.None);
            second.Outcome.ShouldBe(ToolCallClaimOutcome.InFlight, "a Pending row means a concurrent/prior call owns the key — don't double-run");
            second.LedgerId.ShouldBe(ledgerId, "the in-flight claim points at the existing row");
        }
    }

    [Fact]
    public async Task A_claim_after_a_terminal_record_dedups_to_the_prior_result()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        const string storedResult = """{"content":[{"type":"text","text":"opened"}],"isError":false}""";

        Guid ledgerId;
        using (var scope = _fixture.BeginScope())
            ledgerId = (await Svc(scope).TryClaimAsync(runId, teamId, "git.open_pr", Key, InputHash, 0, CancellationToken.None)).LedgerId;

        using (var scope = _fixture.BeginScope())
            await Svc(scope).RecordTerminalAsync(ledgerId, teamId, ToolCallLedgerStatus.Succeeded, storedResult, null, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var dup = await Svc(scope).TryClaimAsync(runId, teamId, "git.open_pr", Key, InputHash, 0, CancellationToken.None);
            dup.Outcome.ShouldBe(ToolCallClaimOutcome.Duplicate, "a terminal row for (run, key) dedups — never re-run");
            dup.PriorStatus.ShouldBe(ToolCallLedgerStatus.Succeeded);

            // Compare SEMANTIC content, not raw bytes — the jsonb column normalizes whitespace/key-order on read, but
            // the stored result's meaningful content (the wire tool-result the model replays) is preserved verbatim.
            dup.PriorResultJson.ShouldNotBeNull("the dedup returns the WINNER's stored (already-redacted) result");
            var replayed = JsonDocument.Parse(dup.PriorResultJson!).RootElement;
            replayed.GetProperty("content")[0].GetProperty("text").GetString().ShouldBe("opened");
            replayed.GetProperty("isError").GetBoolean().ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Two_concurrent_claims_for_the_same_key_yield_exactly_one_proceed()
    {
        // The TOCTOU proof: two identical claims race the unique index in parallel; the DB serializes them so exactly
        // one INSERT wins (Proceed) and the loser reads the winner (InFlight, since neither has recorded a terminal).
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        async Task<ToolCallClaim> ClaimAsync()
        {
            using var scope = _fixture.BeginScope();
            return await Svc(scope).TryClaimAsync(runId, teamId, "git.open_pr", Key, InputHash, 0, CancellationToken.None);
        }

        var results = await Task.WhenAll(ClaimAsync(), ClaimAsync(), ClaimAsync(), ClaimAsync());

        results.Count(r => r.Outcome == ToolCallClaimOutcome.Proceed).ShouldBe(1, "exactly one concurrent claim may proceed — the unique index serializes the race");
        results.Count(r => r.Outcome != ToolCallClaimOutcome.Proceed).ShouldBe(3, "every loser dedups (InFlight here, since none recorded a terminal)");
        results.Where(r => r.Outcome != ToolCallClaimOutcome.Proceed).ShouldAllBe(r => r.Outcome == ToolCallClaimOutcome.InFlight);

        // And only ONE row exists for the (run, key) — the unique index held.
        using var verify = _fixture.BeginScope();
        (await Svc(verify).GetForRunAsync(runId, teamId, CancellationToken.None)).Count.ShouldBe(1, "the unique index permits exactly one row per (run, key)");
    }

    [Fact]
    public async Task Recording_a_terminal_twice_rejects_the_second_as_a_lost_CAS()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid ledgerId;
        using (var scope = _fixture.BeginScope())
            ledgerId = (await Svc(scope).TryClaimAsync(runId, teamId, "git.open_pr", Key, InputHash, 0, CancellationToken.None)).LedgerId;

        using (var scope = _fixture.BeginScope())
            await Svc(scope).RecordTerminalAsync(ledgerId, teamId, ToolCallLedgerStatus.Succeeded, "{}", null, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            // The row is already Succeeded (terminal) → the second terminal record is an illegal transition, rejected.
            await Should.ThrowAsync<ToolCallLedgerTransitionException>(() =>
                Svc(scope).RecordTerminalAsync(ledgerId, teamId, ToolCallLedgerStatus.Succeeded, "{}", null, CancellationToken.None));
    }

    [Fact]
    public async Task Recording_a_non_terminal_status_is_rejected()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid ledgerId;
        using (var scope = _fixture.BeginScope())
            ledgerId = (await Svc(scope).TryClaimAsync(runId, teamId, "git.open_pr", Key, InputHash, 0, CancellationToken.None)).LedgerId;

        using (var scope = _fixture.BeginScope())
            await Should.ThrowAsync<ToolCallLedgerTransitionException>(() =>
                Svc(scope).RecordTerminalAsync(ledgerId, teamId, ToolCallLedgerStatus.Pending, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Reads_are_team_scoped_get_and_record_terminal()
    {
        var ownerTeam = await SeedTeamAsync();
        var otherTeam = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid ledgerId;
        using (var scope = _fixture.BeginScope())
            ledgerId = (await Svc(scope).TryClaimAsync(runId, ownerTeam, "git.open_pr", Key, InputHash, 0, CancellationToken.None)).LedgerId;

        // GetForRunAsync is team-scoped: a foreign team sees nothing; the owner sees its row.
        using (var scope = _fixture.BeginScope())
        {
            (await Svc(scope).GetForRunAsync(runId, otherTeam, CancellationToken.None)).ShouldBeEmpty("a foreign team sees no ledger rows");
            (await Svc(scope).GetForRunAsync(runId, ownerTeam, CancellationToken.None)).ShouldHaveSingleItem();
        }

        // RecordTerminalAsync is team-scoped too (FIX 4 defense-in-depth): a foreign team cannot flip the owner's row —
        // the team-scoped fresh-read returns nothing, so it's treated as not-found and rejected.
        using (var scope = _fixture.BeginScope())
            await Should.ThrowAsync<ToolCallLedgerTransitionException>(() =>
                Svc(scope).RecordTerminalAsync(ledgerId, otherTeam, ToolCallLedgerStatus.Succeeded, "{}", null, CancellationToken.None));

        // The owner's row is still Pending — the foreign-team record never touched it.
        using (var scope = _fixture.BeginScope())
            (await Svc(scope).GetForRunAsync(runId, ownerTeam, CancellationToken.None)).ShouldHaveSingleItem().Status.ShouldBe(ToolCallLedgerStatus.Pending, "a foreign-team terminal record must not flip the owner's row");

        // The owning team CAN flip it.
        using (var scope = _fixture.BeginScope())
            await Svc(scope).RecordTerminalAsync(ledgerId, ownerTeam, ToolCallLedgerStatus.Succeeded, "{}", null, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            (await Svc(scope).GetForRunAsync(runId, ownerTeam, CancellationToken.None)).ShouldHaveSingleItem().Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the owning team flips its own row");
    }

    [Fact]
    public async Task A_different_input_is_a_different_key_and_runs_separately()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var svc = Svc(scope);
            (await svc.TryClaimAsync(runId, teamId, "git.open_pr", "git.open_pr:hash-a", "a".PadRight(64, 'a'), 0, CancellationToken.None)).Outcome.ShouldBe(ToolCallClaimOutcome.Proceed);
            (await svc.TryClaimAsync(runId, teamId, "git.open_pr", "git.open_pr:hash-b", "b".PadRight(64, 'b'), 0, CancellationToken.None)).Outcome.ShouldBe(ToolCallClaimOutcome.Proceed, "a different key (different input) is a separate call — it proceeds");
        }

        using var verify = _fixture.BeginScope();
        (await Svc(verify).GetForRunAsync(runId, teamId, CancellationToken.None)).Count.ShouldBe(2, "two distinct keys → two rows");
    }

    private static IToolCallLedgerService Svc(ILifetimeScope scope) => scope.Resolve<IToolCallLedgerService>();

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"ledger-{userId:N}@test.local", Name = $"ledger-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"ledger-{teamId:N}", Name = "Ledger Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
