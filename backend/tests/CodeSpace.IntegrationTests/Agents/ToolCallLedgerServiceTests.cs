using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task FindBlockingDecisionId_returns_the_oldest_unanswered_decision_excluding_answered_approved_and_non_decision_rows()
    {
        // Pins the completion-contract discriminator directly over Postgres (the gate tests only ever exercise it at
        // N=1): with several rows on one run, FindBlockingDecisionIdAsync returns the OLDEST still-unanswered
        // decision.request (by CreatedDate) — both unanswered statuses (Pending + AwaitingApproval) qualify — while an
        // ANSWERED (Succeeded) decision, an ApprovedAt-stamped row, and a non-decision approval (git.open_pr) are all
        // excluded. Distinct explicit CreatedDates make the ordering deterministic (the auditor preserves a set value).
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var oldestUnanswered = Guid.NewGuid();
        var newerUnanswered = Guid.NewGuid();

        await SeedDecisionRowAsync(teamId, runId, Guid.NewGuid(), ToolCallLedgerStatus.Succeeded, t0.AddMinutes(-5));                                  // answered → excluded
        await SeedDecisionRowAsync(teamId, runId, Guid.NewGuid(), ToolCallLedgerStatus.AwaitingApproval, t0.AddMinutes(-4), approvedAt: t0);            // ApprovedAt set → excluded
        await SeedDecisionRowAsync(teamId, runId, Guid.NewGuid(), ToolCallLedgerStatus.AwaitingApproval, t0.AddMinutes(-3), toolKind: "git.open_pr");  // not a decision → excluded
        await SeedDecisionRowAsync(teamId, runId, oldestUnanswered, ToolCallLedgerStatus.Pending, t0.AddMinutes(1));                                   // unanswered (Pending) → the oldest candidate
        await SeedDecisionRowAsync(teamId, runId, newerUnanswered, ToolCallLedgerStatus.AwaitingApproval, t0.AddMinutes(2));                           // unanswered (AwaitingApproval), newer

        using var scope = _fixture.BeginScope();
        (await Svc(scope).FindBlockingDecisionIdAsync(runId, CancellationToken.None))
            .ShouldBe(oldestUnanswered, "the OLDEST unanswered decision.request is returned; answered / approved / non-decision rows are excluded");
    }

    [Fact]
    public async Task FindBlockingDecisionId_returns_null_when_no_decision_is_outstanding()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        await SeedDecisionRowAsync(teamId, runId, Guid.NewGuid(), ToolCallLedgerStatus.Succeeded, DateTimeOffset.UtcNow);                              // answered decision
        await SeedDecisionRowAsync(teamId, runId, Guid.NewGuid(), ToolCallLedgerStatus.AwaitingApproval, DateTimeOffset.UtcNow, toolKind: "git.open_pr");  // a pending side-effecting approval, not a decision

        using var scope = _fixture.BeginScope();
        (await Svc(scope).FindBlockingDecisionIdAsync(runId, CancellationToken.None))
            .ShouldBeNull("no unanswered decision.request → nothing blocks a clean completion");
    }

    [Theory]
    // stranded side-effecting row whose run is TERMINAL + whose worker's lease has EXPIRED (provably dead) → reaped:
    [InlineData(ToolCallLedgerStatus.Pending, AgentRunStatus.Failed, -1, "git.open_pr", true)]
    [InlineData(ToolCallLedgerStatus.Running, AgentRunStatus.Failed, -1, "git.open_pr", true)]       // a Running ledger row (approval path) too
    [InlineData(ToolCallLedgerStatus.Pending, AgentRunStatus.Cancelled, -1, "git.open_pr", true)]
    [InlineData(ToolCallLedgerStatus.Pending, AgentRunStatus.NeedsReview, -1, "git.open_pr", true)]  // every terminal status
    [InlineData(ToolCallLedgerStatus.Pending, AgentRunStatus.TimedOut, -1, "git.open_pr", true)]
    [InlineData(ToolCallLedgerStatus.Pending, AgentRunStatus.Succeeded, -1, "git.open_pr", true)]
    // NOT reaped — each guard in isolation:
    [InlineData(ToolCallLedgerStatus.Pending, AgentRunStatus.Cancelled, +10, "git.open_pr", false)]  // THE FIX: cancelled but the worker's lease is still VALID → live worker (e.g. a long run_command) → never yank
    [InlineData(ToolCallLedgerStatus.Pending, AgentRunStatus.Running, -1, "git.open_pr", false)]      // run not terminal → not yet stranded (the reconciler hasn't ended it)
    [InlineData(ToolCallLedgerStatus.Pending, AgentRunStatus.Failed, -1, "decision.request", false)] // a decision row → owned by ExpireStaleDecisionsAsync, not this reaper
    public async Task ExpireStaleToolCalls_only_terminalizes_a_stranded_row_under_a_terminal_run_whose_worker_lease_expired(
        ToolCallLedgerStatus ledgerStatus, AgentRunStatus runStatus, int leaseOffsetMinutes, string toolKind, bool expectedSwept)
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        // Lease offset is relative to the sweep clock `at`: negative = expired (dead worker), positive = valid (live worker).
        await SeedAgentRunAsync(teamId, runId, runStatus, at + TimeSpan.FromMinutes(leaseOffsetMinutes));
        var ledgerId = await SeedToolCallAsync(teamId, runId, ledgerStatus, toolKind, $"{toolKind}:{runId:N}", at);

        // Assert THIS row's outcome, not the global count — the sweep is team-agnostic, so a shared fixture may carry other
        // tests' eligible rows; the per-row status is the precise, non-flaky proof.
        using (var scope = _fixture.BeginScope())
            await Svc(scope).ExpireStaleToolCallsAsync(at, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var row = await verify.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().SingleAsync(l => l.Id == ledgerId);

        if (expectedSwept)
        {
            row.Status.ShouldBe(ToolCallLedgerStatus.Failed, "a stranded side-effecting row under a terminal run whose worker is provably gone is terminalized so a re-call stops hitting InFlight");
            row.Error.ShouldBe(ToolCallLedgerService.InterruptedError);
        }
        else
        {
            row.Status.ShouldBe(ledgerStatus, "the guard held — a live worker (valid lease) / a non-terminal run / the wrong reaper's row is left alone");
        }
    }

    [Fact]
    public async Task A_reaped_stranded_row_makes_a_re_call_replay_the_failure_never_re_execute()
    {
        // The exactly-once proof: after the reaper terminalizes a stranded Pending row, a re-call of the SAME (run, key)
        // hits the unique index → Duplicate replaying the Failed terminal — it does NOT make a fresh claim, so the
        // interrupted side effect is never re-run. The run is unblocked (a terminal error) instead of hanging InFlight.
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        const string key = "git.open_pr:stranded";

        await SeedAgentRunAsync(teamId, runId, AgentRunStatus.Failed, at - TimeSpan.FromMinutes(1));   // worker lease expired → provably gone
        await SeedToolCallAsync(teamId, runId, ToolCallLedgerStatus.Pending, "git.open_pr", key, at);

        using (var scope = _fixture.BeginScope())
            (await Svc(scope).ExpireStaleToolCallsAsync(at, CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1, "the sweep terminalized at least this run's stranded row");

        using var scope2 = _fixture.BeginScope();
        var reclaim = await Svc(scope2).TryClaimAsync(runId, teamId, "git.open_pr", key, InputHash, 0, CancellationToken.None);

        reclaim.Outcome.ShouldBe(ToolCallClaimOutcome.Duplicate, "the reaped row is terminal → a re-call dedups (replays), never re-runs the side effect");
        reclaim.PriorStatus.ShouldBe(ToolCallLedgerStatus.Failed);
    }

    private async Task SeedAgentRunAsync(Guid teamId, Guid runId, AgentRunStatus status, DateTimeOffset leaseExpiresAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = status, LeaseExpiresAt = leaseExpiresAt, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedToolCallAsync(Guid teamId, Guid runId, ToolCallLedgerStatus status, string toolKind, string key, DateTimeOffset at)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = id, TeamId = teamId, AgentRunId = runId, ToolKind = toolKind, IdempotencyKey = key, InputHash = InputHash, Status = status,
            CreatedDate = at, LastModifiedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedDecisionRowAsync(Guid teamId, Guid runId, Guid id, ToolCallLedgerStatus status, DateTimeOffset createdDate, string toolKind = DecisionToolKinds.DecisionRequest, DateTimeOffset? approvedAt = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = id, TeamId = teamId, AgentRunId = runId, ToolKind = toolKind,
            IdempotencyKey = $"{toolKind}:{id:N}", InputHash = InputHash, Status = status, ApprovedAt = approvedAt,
            CreatedDate = createdDate, LastModifiedDate = createdDate, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
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
