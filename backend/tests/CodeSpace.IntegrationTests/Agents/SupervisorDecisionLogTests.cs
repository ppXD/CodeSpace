using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (high fidelity — REAL <see cref="SupervisorDecisionLog"/> resolved through the DI container against
/// real Postgres, proving it's registered). Pins the PR-E E1 ledger substrate over real PG, not mocked:
/// the INSERT-first exactly-once invariant (a second claim for the same (run, key) dedups — terminal returns the stored
/// outcome, non-terminal returns InFlight; a CONCURRENT race yields exactly one Proceed — the 23505 path), the
/// must-fix-#2 Pending → Running claim hop (two callers race → exactly one Running BEFORE any side effect), the
/// status-guarded terminal CAS (rejects an illegal transition + a foreign team), the ordered-by-Sequence replay read,
/// the frozen-vs-CAS immutability trigger (a journal-field UPDATE is rejected but the status-path CAS proceeds), and the
/// stale-Pending reaper (a stale row expires, a fresh one is left).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorDecisionLogTests
{
    private const string Kind = "spawn";
    private const string Key = "spawn:abc";
    private const string InputHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string Payload = """{"goal":"ship","next":"spawn"}""";

    private readonly PostgresFixture _fixture;

    public SupervisorDecisionLogTests(PostgresFixture fixture) { _fixture = fixture; }

    // ── Exactly-once / dedup (INSERT-first 23505 path) ────────────────────────────

    [Fact]
    public async Task First_claim_proceeds_and_a_second_pending_claim_is_in_flight()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid decisionId;
        using (var scope = _fixture.BeginScope())
        {
            var claim = await Log(scope).TryClaimAsync(runId, teamId, Kind, Key, InputHash, Payload, 0, CancellationToken.None);
            claim.Outcome.ShouldBe(SupervisorDecisionClaimOutcome.Proceed, "the first claim INSERTs the Pending row");
            decisionId = claim.DecisionId;
        }

        using (var scope = _fixture.BeginScope())
        {
            // Still Pending (no terminal) → a second claim for the same key dedups to In-Flight, NOT a second row.
            var second = await Log(scope).TryClaimAsync(runId, teamId, Kind, Key, InputHash, Payload, 0, CancellationToken.None);
            second.Outcome.ShouldBe(SupervisorDecisionClaimOutcome.InFlight, "a Pending row means a concurrent/prior decision owns the key — don't double-execute");
            second.DecisionId.ShouldBe(decisionId, "the in-flight claim points at the existing row");
        }

        using (var scope = _fixture.BeginScope())
            (await Log(scope).GetForRunAsync(runId, teamId, CancellationToken.None)).Count.ShouldBe(1, "the unique index permitted exactly one row — the 23505 path dedups, never inserts a second");
    }

    [Fact]
    public async Task A_claim_after_a_terminal_record_dedups_to_the_prior_outcome()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        const string outcome = """{"spawned":["agent-1","agent-2"]}""";

        Guid decisionId;
        using (var scope = _fixture.BeginScope())
            decisionId = (await Log(scope).TryClaimAsync(runId, teamId, Kind, Key, InputHash, Payload, 0, CancellationToken.None)).DecisionId;

        // Must claim into Running BEFORE recording a terminal (must-fix #2 — no Pending → terminal shortcut).
        using (var scope = _fixture.BeginScope())
            (await Log(scope).TryBeginExecutionAsync(decisionId, teamId, CancellationToken.None)).ShouldBeTrue();

        using (var scope = _fixture.BeginScope())
            await Log(scope).RecordTerminalAsync(decisionId, teamId, SupervisorDecisionStatus.Succeeded, outcome, null, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var dup = await Log(scope).TryClaimAsync(runId, teamId, Kind, Key, InputHash, Payload, 0, CancellationToken.None);
            dup.Outcome.ShouldBe(SupervisorDecisionClaimOutcome.Duplicate, "a terminal row for (run, key) dedups — never re-execute");
            dup.PriorStatus.ShouldBe(SupervisorDecisionStatus.Succeeded);

            dup.PriorOutcomeJson.ShouldNotBeNull("the dedup returns the WINNER's stored outcome");
            var replayed = JsonDocument.Parse(dup.PriorOutcomeJson!).RootElement;
            replayed.GetProperty("spawned")[0].GetString().ShouldBe("agent-1");
        }
    }

    [Fact]
    public async Task Two_concurrent_claims_for_the_same_key_yield_exactly_one_proceed()
    {
        // The TOCTOU proof: identical claims race the unique index in parallel; the DB serializes them so exactly one
        // INSERT wins (Proceed) and every loser reads the winner (InFlight, since none recorded a terminal).
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        async Task<SupervisorDecisionClaim> ClaimAsync()
        {
            using var scope = _fixture.BeginScope();
            return await Log(scope).TryClaimAsync(runId, teamId, Kind, Key, InputHash, Payload, 0, CancellationToken.None);
        }

        var results = await Task.WhenAll(ClaimAsync(), ClaimAsync(), ClaimAsync(), ClaimAsync());

        results.Count(r => r.Outcome == SupervisorDecisionClaimOutcome.Proceed).ShouldBe(1, "exactly one concurrent claim may proceed — the unique index serializes the race");
        results.Where(r => r.Outcome != SupervisorDecisionClaimOutcome.Proceed).ShouldAllBe(r => r.Outcome == SupervisorDecisionClaimOutcome.InFlight);

        using var verify = _fixture.BeginScope();
        (await Log(verify).GetForRunAsync(runId, teamId, CancellationToken.None)).Count.ShouldBe(1, "the unique index permits exactly one row per (run, key)");
    }

    // ── The must-fix-#2 claim hop (single-winner BEFORE the side effect) ──────────

    [Fact]
    public async Task Two_callers_racing_the_claim_hop_yield_exactly_one_running()
    {
        // The single-winner execution gate: of N executors racing the SAME claimed (run, key), exactly one wins the
        // Pending → Running CAS (true → runs the side effect once); every loser gets false → replays. This is the gate
        // the INSERT alone cannot provide for the synchronous path.
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid decisionId;
        using (var scope = _fixture.BeginScope())
            decisionId = (await Log(scope).TryClaimAsync(runId, teamId, Kind, Key, InputHash, Payload, 0, CancellationToken.None)).DecisionId;

        async Task<bool> ClaimExecutionAsync()
        {
            using var scope = _fixture.BeginScope();
            return await Log(scope).TryBeginExecutionAsync(decisionId, teamId, CancellationToken.None);
        }

        var won = await Task.WhenAll(ClaimExecutionAsync(), ClaimExecutionAsync(), ClaimExecutionAsync(), ClaimExecutionAsync());

        won.Count(w => w).ShouldBe(1, "exactly one caller wins the Pending → Running claim — the single-winner gate before the side effect");

        using var verify = _fixture.BeginScope();
        (await Log(verify).GetForRunAsync(runId, teamId, CancellationToken.None)).ShouldHaveSingleItem().Status.ShouldBe(SupervisorDecisionStatus.Running);
    }

    // ── Status-guarded terminal CAS ───────────────────────────────────────────────

    [Fact]
    public async Task Recording_a_terminal_from_pending_is_rejected_no_skip_running()
    {
        // must-fix #2 at the service boundary: a terminal is reachable ONLY from Running — recording one straight from
        // Pending (without the claim hop) is an illegal transition, rejected by the state machine.
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid decisionId;
        using (var scope = _fixture.BeginScope())
            decisionId = (await Log(scope).TryClaimAsync(runId, teamId, Kind, Key, InputHash, Payload, 0, CancellationToken.None)).DecisionId;

        using (var scope = _fixture.BeginScope())
            await Should.ThrowAsync<SupervisorDecisionTransitionException>(() =>
                Log(scope).RecordTerminalAsync(decisionId, teamId, SupervisorDecisionStatus.Succeeded, "{}", null, CancellationToken.None));
    }

    [Fact]
    public async Task Recording_a_terminal_twice_rejects_the_second_as_a_lost_CAS()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid decisionId;
        using (var scope = _fixture.BeginScope())
            decisionId = (await Log(scope).TryClaimAsync(runId, teamId, Kind, Key, InputHash, Payload, 0, CancellationToken.None)).DecisionId;

        using (var scope = _fixture.BeginScope())
            (await Log(scope).TryBeginExecutionAsync(decisionId, teamId, CancellationToken.None)).ShouldBeTrue();

        using (var scope = _fixture.BeginScope())
            await Log(scope).RecordTerminalAsync(decisionId, teamId, SupervisorDecisionStatus.Succeeded, "{}", null, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            await Should.ThrowAsync<SupervisorDecisionTransitionException>(() =>
                Log(scope).RecordTerminalAsync(decisionId, teamId, SupervisorDecisionStatus.Failed, null, "boom", CancellationToken.None));
    }

    [Fact]
    public async Task Reads_and_record_terminal_are_team_scoped()
    {
        var ownerTeam = await SeedTeamAsync();
        var otherTeam = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid decisionId;
        using (var scope = _fixture.BeginScope())
            decisionId = (await Log(scope).TryClaimAsync(runId, ownerTeam, Kind, Key, InputHash, Payload, 0, CancellationToken.None)).DecisionId;

        using (var scope = _fixture.BeginScope())
            (await Log(scope).TryBeginExecutionAsync(decisionId, ownerTeam, CancellationToken.None)).ShouldBeTrue();

        using (var scope = _fixture.BeginScope())
        {
            (await Log(scope).GetForRunAsync(runId, otherTeam, CancellationToken.None)).ShouldBeEmpty("a foreign team sees no decision rows");
            (await Log(scope).GetForRunAsync(runId, ownerTeam, CancellationToken.None)).ShouldHaveSingleItem();
        }

        // A foreign team cannot flip the owner's row — the team-scoped fresh-read returns nothing → not-found → rejected.
        using (var scope = _fixture.BeginScope())
            await Should.ThrowAsync<SupervisorDecisionTransitionException>(() =>
                Log(scope).RecordTerminalAsync(decisionId, otherTeam, SupervisorDecisionStatus.Succeeded, "{}", null, CancellationToken.None));

        using (var scope = _fixture.BeginScope())
            (await Log(scope).GetForRunAsync(runId, ownerTeam, CancellationToken.None)).ShouldHaveSingleItem().Status.ShouldBe(SupervisorDecisionStatus.Running, "a foreign-team record must not flip the owner's row");
    }

    [Fact]
    public async Task Get_for_run_is_ordered_by_sequence_the_replay_tape()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var log = Log(scope);
            await log.TryClaimAsync(runId, teamId, "plan", "plan:1", InputHash, Payload, 0, CancellationToken.None);
            await log.TryClaimAsync(runId, teamId, "spawn", "spawn:2", InputHash, Payload, 0, CancellationToken.None);
            await log.TryClaimAsync(runId, teamId, "merge", "merge:3", InputHash, Payload, 0, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var rows = await Log(verify).GetForRunAsync(runId, teamId, CancellationToken.None);
        rows.Select(r => r.DecisionKind).ShouldBe(new[] { "plan", "spawn", "merge" }, "the replay tape is ordered by the per-run BIGSERIAL sequence");
        rows.Select(r => r.Sequence).ShouldBe(rows.Select(r => r.Sequence).OrderBy(s => s), "sequence is monotonic in insert order");
    }

    // ── The frozen-vs-CAS immutability trigger ────────────────────────────────────

    [Fact]
    public async Task The_immutability_trigger_blocks_a_journal_field_update_but_allows_the_status_path_cas()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid decisionId;
        using (var scope = _fixture.BeginScope())
            decisionId = (await Log(scope).TryClaimAsync(runId, teamId, Kind, Key, InputHash, Payload, 0, CancellationToken.None)).DecisionId;

        // A journal-field UPDATE (payload_jsonb) is rejected by the trigger — the emitted decision is frozen at insert.
        var journalUpdate = await Should.ThrowAsync<PostgresException>(() =>
            ExecRawAsync("UPDATE supervisor_decision SET payload_jsonb = '{\"tampered\":true}'::jsonb WHERE id = @id", decisionId));
        journalUpdate.MessageText.ShouldContain("frozen", Case.Insensitive, "the trigger names the frozen-journal contract it enforced");

        // An IDENTITY-column UPDATE (team_id) is rejected too — a decision can never be re-tenanted by a stray UPDATE
        // (defense-in-depth on the tenancy boundary; team_id/supervisor_run_id/fence_epoch are frozen alongside the journal).
        await Should.ThrowAsync<PostgresException>(() =>
            ExecRawAsync("UPDATE supervisor_decision SET team_id = @id WHERE id = @id", decisionId));

        // A DELETE is rejected too — the ledger is permanent audit.
        await Should.ThrowAsync<PostgresException>(() =>
            ExecRawAsync("DELETE FROM supervisor_decision WHERE id = @id", decisionId));

        // But the status-path CAS proceeds — the deliberately-mutable path is NOT blocked.
        using (var scope = _fixture.BeginScope())
            (await Log(scope).TryBeginExecutionAsync(decisionId, teamId, CancellationToken.None)).ShouldBeTrue("the status path (status) is mutable — the claim CAS succeeds");

        using (var scope = _fixture.BeginScope())
            await Log(scope).RecordTerminalAsync(decisionId, teamId, SupervisorDecisionStatus.Succeeded, """{"ok":true}""", null, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var row = await ReadRowAsync(decisionId);
            row.Status.ShouldBe(SupervisorDecisionStatus.Succeeded, "the status path moved through the trigger untouched");
            JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("goal").GetString().ShouldBe("ship", "the frozen payload survived verbatim — the tamper UPDATE was rejected");
        }
    }

    // ── The stale-Pending reaper ──────────────────────────────────────────────────

    [Fact]
    public async Task Expire_stale_pending_marks_a_stale_row_and_leaves_a_fresh_one()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid staleId, freshId;
        using (var scope = _fixture.BeginScope())
        {
            var log = Log(scope);
            staleId = (await log.TryClaimAsync(runId, teamId, "plan", "plan:stale", InputHash, Payload, 0, CancellationToken.None)).DecisionId;
            freshId = (await log.TryClaimAsync(runId, teamId, "plan", "plan:fresh", InputHash, Payload, 0, CancellationToken.None)).DecisionId;
        }

        // Backdate the stale row's created_date below the cutoff (the status path is mutable; created_date isn't a
        // journal field, so the trigger allows this maintenance UPDATE).
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        await ExecRawAsync("UPDATE supervisor_decision SET created_date = @ts WHERE id = @id", staleId, ("@ts", cutoff.AddMinutes(-5)));

        int expired;
        using (var scope = _fixture.BeginScope())
            expired = await Log(scope).ExpireStalePendingAsync(cutoff, CancellationToken.None);

        expired.ShouldBe(1, "exactly the one stale Pending row is swept");
        (await ReadRowAsync(staleId)).Status.ShouldBe(SupervisorDecisionStatus.Expired, "the stale row is durably Expired");
        (await ReadRowAsync(staleId)).Error.ShouldBe(SupervisorDecisionLog.StalePendingError, "the audit reason records the sweep");
        (await ReadRowAsync(freshId)).Status.ShouldBe(SupervisorDecisionStatus.Pending, "the fresh row (created after the cutoff) is left untouched");
    }

    [Fact]
    public async Task Expire_stale_pending_does_not_touch_a_running_row()
    {
        // The Status == Pending guard: a stale row already claimed into Running belongs to an in-flight executor and is
        // never expired out from under it.
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        Guid decisionId;
        using (var scope = _fixture.BeginScope())
            decisionId = (await Log(scope).TryClaimAsync(runId, teamId, Kind, Key, InputHash, Payload, 0, CancellationToken.None)).DecisionId;

        using (var scope = _fixture.BeginScope())
            (await Log(scope).TryBeginExecutionAsync(decisionId, teamId, CancellationToken.None)).ShouldBeTrue();

        await ExecRawAsync("UPDATE supervisor_decision SET created_date = @ts WHERE id = @id", decisionId, ("@ts", DateTimeOffset.UtcNow.AddHours(-2)));

        int expired;
        using (var scope = _fixture.BeginScope())
            expired = await Log(scope).ExpireStalePendingAsync(DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        expired.ShouldBe(0, "a Running row is not Pending — the reaper's candidate query excludes it");
        (await ReadRowAsync(decisionId)).Status.ShouldBe(SupervisorDecisionStatus.Running, "the in-flight row is untouched");
    }

    // ── A1: a model-authored acceptance spec is CARRIED durably (so A3 can later read it off the replayed tape) ──
    // NOTE: the PayloadJson column is jsonb (Postgres normalizes whitespace), so the durable guarantee is SEMANTIC
    // round-trip — the carried acceptance deserializes back intact. Canonical-byte / idempotency-key stability is a
    // pure-projector concern proven at the unit tier (SupervisorAcceptanceSpecTests); here we prove durable carry.

    [Fact]
    public async Task A_stop_decisions_authored_acceptance_survives_a_real_persist_and_replay_round_trip()
    {
        // A1 is "carried, not consumed": the model authors the acceptance on a stop, the projector freezes it into
        // the canonical PayloadJson, and the ledger must persist it so a later turn (A3) can read it off the durable
        // tape. Prove the full canonical → real Postgres → replay → deserialize path preserves it.
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        var canonical = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Stop,
            Stop = new SupervisorStopPayload
            {
                Outcome = "completed",
                Summary = "done",
                Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "dotnet", "test" }, Description = "suite green" },
            },
        }).PayloadJson;

        using (var scope = _fixture.BeginScope())
            await Log(scope).TryClaimAsync(runId, teamId, "stop", "stop:accept", InputHash, canonical, 0, CancellationToken.None);

        SupervisorDecisionRecord replayed;
        using (var scope = _fixture.BeginScope())
            replayed = (await Log(scope).GetForRunAsync(runId, teamId, CancellationToken.None)).Single();

        var stop = JsonSerializer.Deserialize<SupervisorStopPayload>(replayed.PayloadJson, AgentJson.Options)!;
        stop.Outcome.ShouldBe("completed");
        stop.Summary.ShouldBe("done");
        stop.Acceptance.ShouldNotBeNull("the carried acceptance survived the real persist + replay — the tape A3 reads");
        stop.Acceptance!.Command.ShouldBe(new[] { "dotnet", "test" });
        stop.Acceptance.Description.ShouldBe("suite green");
    }

    [Fact]
    public async Task A_stop_without_acceptance_replays_with_no_acceptance_leaked()
    {
        // Back-compat at the persistence tier: a stop the model authors WITHOUT acceptance canonicalizes to the
        // exact pre-field bytes (the idempotency-key input), and replays with NO acceptance — the optional field
        // never leaks a spurious value onto a plain stop.
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        var canonical = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Stop,
            Stop = new SupervisorStopPayload { Outcome = "completed", Summary = "done" },
        }).PayloadJson;

        canonical.ShouldBe("""{"outcome":"completed","summary":"done"}""", "no acceptance authored → the pre-field idempotency-key bytes");

        using (var scope = _fixture.BeginScope())
            await Log(scope).TryClaimAsync(runId, teamId, "stop", "stop:plain", InputHash, canonical, 0, CancellationToken.None);

        SupervisorDecisionRecord replayed;
        using (var verify = _fixture.BeginScope())
            replayed = (await Log(verify).GetForRunAsync(runId, teamId, CancellationToken.None)).Single();

        JsonSerializer.Deserialize<SupervisorStopPayload>(replayed.PayloadJson, AgentJson.Options)!.Acceptance
            .ShouldBeNull("a plain stop replays with no acceptance — the optional field never leaks");
    }

    // ── B1: a spawn's model-authored per-agent dispatch is CARRIED durably (so the executor reads it off the tape) ──

    [Fact]
    public async Task A_spawn_decisions_per_agent_dispatch_survives_a_real_persist_and_replay()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var repo = Guid.NewGuid();

        var canonical = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Spawn,
            Spawn = new SupervisorSpawnPayload
            {
                SubtaskIds = new[] { "s1", "s2" },
                Agents = new[]
                {
                    new SupervisorAgentDispatch { SubtaskId = "s1", Role = "backend implementer", Harness = "codex-cli", RepositoryId = repo, AutonomyLevel = "trusted" },
                    new SupervisorAgentDispatch { SubtaskId = "s2", Role = "frontend adapter" },
                },
            },
        }).PayloadJson;

        using (var scope = _fixture.BeginScope())
            await Log(scope).TryClaimAsync(runId, teamId, "spawn", "spawn:agents", InputHash, canonical, 0, CancellationToken.None);

        SupervisorDecisionRecord replayed;
        using (var scope = _fixture.BeginScope())
            replayed = (await Log(scope).GetForRunAsync(runId, teamId, CancellationToken.None)).Single();

        var spawn = JsonSerializer.Deserialize<SupervisorSpawnPayload>(replayed.PayloadJson, AgentJson.Options)!;
        spawn.SubtaskIds.ShouldBe(new[] { "s1", "s2" });
        spawn.Agents.ShouldNotBeNull("the per-agent dispatch survived the real persist + replay — the tape the executor reads");
        spawn.Agents!.Count.ShouldBe(2);
        spawn.Agents[0].Role.ShouldBe("backend implementer");
        spawn.Agents[0].RepositoryId.ShouldBe(repo);
        spawn.Agents[0].AutonomyLevel.ShouldBe("trusted");
        spawn.Agents[1].Role.ShouldBe("frontend adapter");
    }

    [Fact]
    public async Task A_spawn_without_per_agent_dispatch_replays_with_none()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        var canonical = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Spawn,
            Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "s1" } },
        }).PayloadJson;

        canonical.ShouldBe("""{"subtaskIds":["s1"]}""", "no per-agent specs → the pre-field idempotency-key bytes");

        using (var scope = _fixture.BeginScope())
            await Log(scope).TryClaimAsync(runId, teamId, "spawn", "spawn:plain", InputHash, canonical, 0, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        JsonSerializer.Deserialize<SupervisorSpawnPayload>((await Log(verify).GetForRunAsync(runId, teamId, CancellationToken.None)).Single().PayloadJson, AgentJson.Options)!.Agents
            .ShouldBeNull("a plain spawn replays with no per-agent dispatch — the optional field never leaks");
    }

    private static ISupervisorDecisionLog Log(ILifetimeScope scope) => scope.Resolve<ISupervisorDecisionLog>();

    private async Task<SupervisorDecisionRecord> ReadRowAsync(Guid decisionId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.Id == decisionId);
    }

    private async Task ExecRawAsync(string sql, Guid id, params (string name, object value)[] extra)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        foreach (var (name, value) in extra) cmd.Parameters.AddWithValue(name, value);

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"sd-{userId:N}@test.local", Name = $"sd-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"sd-{teamId:N}", Name = "Supervisor Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
