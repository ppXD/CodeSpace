using Autofac;
using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// 🟢 Integration — the CUTOVER PARITY GATE (post-P6). Seeds a golden-run corpus (<see cref="SessionCorpusSeed"/>) ONCE
/// and projects each run through BOTH the real <see cref="IRoomProjector"/> and <see cref="IJournalProjector"/> over real
/// Postgres, asserting the permanent-HYBRID invariant: the journal ③ IS the run's chronological narrative; the room FRAME
/// + its rich/live block projectors are reused by the journal FE. The room NARRATIVE stack (<c>narrative_step</c>) was
/// DELETED in P6 — the journal ③ owns that surface, so the room no longer emits it.
///
/// <list type="number">
/// <item><b>Structural tripwire</b> — every room block kind the focused turn emits is in the FE's <c>JOURNAL_HANDLED</c>
/// set (the retained rich/live blocks the hybrid FE reuses). A room block kind outside it would silently vanish in journal mode.</item>
/// <item><b>Journal coverage</b> — the journal ③ carries the same agents + the retry/respawn beats (the events the room
/// used to narrate before P6), so nothing the room narrated is lost.</item>
/// <item><b>Retained-block liveness</b> — the room still projects each rich/live block the hybrid FE reuses
/// (plan_checklist, final_answer, diagnostic, decision, live_activity) + the attempt ladder is journal-native.</item>
/// </list>
///
/// <para>The room's two SYNTHETIC connecting narrative lines (the crash beat + the between-phase "reviewed N" label) were
/// retired with the rest of the narrative stack: the crash is covered by the RETAINED diagnostic card (witnessed below),
/// the transitions by the chronological ③ beats themselves. If this gate goes red, a room surface would disappear in
/// journal mode — classify it. Pure per-emitter richness stays in the RoomNarrative/RoomRounds unit tier.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RoomJournalParityFlowTests
{
    private readonly PostgresFixture _fixture;

    public RoomJournalParityFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>
    /// The FE's <c>JOURNAL_HANDLED</c> set (SessionRoomView.tsx) mirrored on the backend: the retained room inner-block
    /// kinds the journal mode reuses from the room projection (the permanent-hybrid retained set). Keep in lockstep with
    /// the FE constant. (<c>narrative_step</c> was DELETED in P6 — the journal ③ owns that surface, the room no longer emits it.)
    /// </summary>
    private static readonly IReadOnlySet<string> JournalHandled = new HashSet<string>
    {
        "execution_map", "agent_group", "plan_checklist", "stat", "live_activity",
        "delivery", "final_answer", "diagnostic", "decision",
    };

    // ─── Tier 1: the structural tripwire ────────────────────────────────────────────

    // ReranTurn is intentionally NOT in this Theory: a bare reran-winner turn emits NO inner blocks + no map, so the
    // tripwire's ShouldAllBe would pass VACUOUSLY (zero blocks to check). Its parity is proven by the attempt-ladder test
    // below. The ShouldNotBeEmpty guard makes any FUTURE shape that stops emitting blocks fail loudly rather than hide.
    [Theory]
    [InlineData(SessionCorpusSeed.Shape.SupervisorPlanSpawn)]
    [InlineData(SessionCorpusSeed.Shape.SingleAgent)]
    [InlineData(SessionCorpusSeed.Shape.Retry)]
    [InlineData(SessionCorpusSeed.Shape.RespawnCrash)]
    [InlineData(SessionCorpusSeed.Shape.PendingDecision)]
    public async Task Every_room_block_the_focused_turn_emits_is_handled_by_the_journal_mode(SessionCorpusSeed.Shape shape)
    {
        var seeded = await SessionCorpusSeed.SeedAsync(_fixture, shape);
        var focused = FocusedRoomTurn(await ProjectRoomAsync(seeded), seeded.RunId);

        var kinds = focused.Blocks.Select(KindOf).ToList();
        if (focused.Map is not null) kinds.Add("execution_map");

        kinds.ShouldNotBeEmpty($"[{shape}] this shape must emit inner blocks for the tripwire to guard anything — an empty focused turn would pass the ShouldAllBe below vacuously");
        kinds.ShouldAllBe(k => JournalHandled.Contains(k),
            customMessage: $"[{shape}] a room block kind outside the FE JOURNAL_HANDLED set would silently vanish in journal mode at P6 cutover — classify it (retain its room projector, or cover it natively in the journal ③)");
    }

    [Fact]
    public async Task The_corpus_collectively_exercises_every_retained_room_surface_the_gate_guards()
    {
        var observed = new HashSet<string>();

        foreach (var shape in Enum.GetValues<SessionCorpusSeed.Shape>())
        {
            var seeded = await SessionCorpusSeed.SeedAsync(_fixture, shape);
            var focused = FocusedRoomTurn(await ProjectRoomAsync(seeded), seeded.RunId);

            foreach (var block in focused.Blocks) observed.Add(KindOf(block));
            if (focused.Map is not null) observed.Add("execution_map");
        }

        // The gate isn't hollow: the corpus lights up every surface the tripwire guards (delivery aside — it needs an
        // open-PR node the corpus doesn't seed; it stays guarded by the tripwire, just not exercised here).
        foreach (var kind in new[] { "agent_group", "plan_checklist", "stat", "execution_map", "final_answer", "diagnostic", "decision", "live_activity" })
            observed.ShouldContain(kind, $"the corpus should exercise the {kind} room surface so the parity gate runs over real data, not a vacuous fixture");
    }

    // ─── Tier 2: journal coverage (the journal ③ carries the events the room used to narrate) ──

    [Theory]
    [InlineData(SessionCorpusSeed.Shape.SupervisorPlanSpawn)]
    [InlineData(SessionCorpusSeed.Shape.Retry)]
    [InlineData(SessionCorpusSeed.Shape.RespawnCrash)]
    public async Task The_journal_surfaces_the_same_agents_the_room_groups(SessionCorpusSeed.Shape shape)
    {
        var seeded = await SessionCorpusSeed.SeedAsync(_fixture, shape);

        var roomAgents = RoomAgentIds(FocusedRoomTurn(await ProjectRoomAsync(seeded), seeded.RunId));
        var journalAgents = JournalAgentIds(FocusedJournalTurn(await ProjectJournalAsync(seeded)));

        // Both id sets are sorted (RoomAgentIds/JournalAgentIds OrderBy), so plain equality is order-insensitive.
        journalAgents.ShouldBe(roomAgents,
            customMessage: $"[{shape}] every agent the room groups must also carry a journal card — neither view may drop an agent the other shows");
    }

    [Fact]
    public async Task The_retry_is_carried_as_a_journal_beat_with_its_rationale()
    {
        // The retry the room USED to narrate (its narrative_step was deleted in P6) lives on the journal ③ as a beat with
        // the supervisor's WHY — richer than the old room line.
        var seeded = await SessionCorpusSeed.SeedAsync(_fixture, SessionCorpusSeed.Shape.Retry);

        var journal = FocusedJournalTurn(await ProjectJournalAsync(seeded));
        var retryBeat = journal.Steps.SingleOrDefault(s => s.Beat && s.Title == "Supervisor retried a subtask");

        retryBeat.ShouldNotBeNull("the retry surfaces as a journal ③ beat");
        retryBeat!.Rationale.ShouldNotBeNull("and the journal beat carries the supervisor's WHY");
        retryBeat.Rationale!.ShouldContain("missed the edge cases", customMessage: "the retry rationale reads off the decision tape");
    }

    [Fact]
    public async Task The_respawn_wave_is_carried_as_journal_spawn_beats()
    {
        // The re-spawn wave the room USED to narrate (deleted in P6) becomes two real chronological dispatch beats on ③.
        var seeded = await SessionCorpusSeed.SeedAsync(_fixture, SessionCorpusSeed.Shape.RespawnCrash);

        var journal = FocusedJournalTurn(await ProjectJournalAsync(seeded));
        journal.Steps.Count(s => s.Beat && s.Verb == "spawn").ShouldBeGreaterThanOrEqualTo(2,
            "both spawn waves survive as chronological journal ③ beats");
    }

    [Fact]
    public async Task The_deep_failure_the_room_narrates_survives_on_the_retained_diagnostic()
    {
        // The room narrates a crash as a SYNTHETIC narrative_step ("its next step failed before it could decide") that the
        // journal ③ does not reproduce as a beat — the one narrative line the ③ supersedes rather than carries. It is NOT
        // lost: the deep cause rides the RETAINED diagnostic block (the hybrid FE reuses it), so the failure the operator
        // needs still surfaces after the room narrative stack is deleted. This witnesses that supersession is safe.
        var seeded = await SessionCorpusSeed.SeedAsync(_fixture, SessionCorpusSeed.Shape.RespawnCrash);
        var room = FocusedRoomTurn(await ProjectRoomAsync(seeded), seeded.RunId);

        room.Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem()
            .Text.ShouldContain("the request timed out before the gateway responded",
                customMessage: "the deep failure survives on the retained diagnostic — the journal ③ supersedes the room's synthetic crash narrative with this card, so the failure isn't lost at cutover");
    }

    // ─── Tier 3: retained-block liveness + the journal-native attempt ladder ─────────

    [Theory]
    [InlineData(SessionCorpusSeed.Shape.SupervisorPlanSpawn, "plan_checklist")]
    [InlineData(SessionCorpusSeed.Shape.SingleAgent, "final_answer")]
    [InlineData(SessionCorpusSeed.Shape.RespawnCrash, "diagnostic")]
    [InlineData(SessionCorpusSeed.Shape.PendingDecision, "decision")]
    public async Task The_room_still_projects_each_rich_or_live_block_the_hybrid_fe_reuses(SessionCorpusSeed.Shape shape, string retainedKind)
    {
        var seeded = await SessionCorpusSeed.SeedAsync(_fixture, shape);
        var focused = FocusedRoomTurn(await ProjectRoomAsync(seeded), seeded.RunId);

        focused.Blocks.Select(KindOf).ShouldContain(retainedKind,
            $"[{shape}] the hybrid journal FE reuses the room's '{retainedKind}' block — the room projector must keep emitting it after the narrative stack is deleted");
    }

    [Fact]
    public async Task An_active_turn_still_projects_the_live_activity_ticker_the_journal_leaves_to_the_room()
    {
        var seeded = await SessionCorpusSeed.SeedAsync(_fixture, SessionCorpusSeed.Shape.PendingDecision);
        var focused = FocusedRoomTurn(await ProjectRoomAsync(seeded), seeded.RunId);

        focused.Blocks.OfType<LiveActivityBlock>().ShouldNotBeEmpty(
            "the live 'Working…' ticker is an interactive surface the permanent hybrid keeps in the room frame — it must still project on an active run");
    }

    [Fact]
    public async Task The_attempt_ladder_is_journal_native_and_matches_the_rooms()
    {
        var seeded = await SessionCorpusSeed.SeedAsync(_fixture, SessionCorpusSeed.Shape.ReranTurn);

        var roomLadder = FocusedRoomTurn(await ProjectRoomAsync(seeded), seeded.RunId)
            .Attempts.Select(a => (a.AttemptNumber, a.Status)).ToArray();
        var journalLadder = FocusedJournalTurn(await ProjectJournalAsync(seeded))
            .Attempts.Select(a => (a.AttemptNumber, a.Status)).ToArray();

        roomLadder.ShouldBe(new[] { (1, WorkflowRunStatus.Failure), (2, WorkflowRunStatus.Success) });
        journalLadder.ShouldBe(roomLadder, "the journal reproduces the attempt ladder natively — this surface is NOT a P6 blocker");
    }

    // ─── Projection + extraction helpers ────────────────────────────────────────────

    private async Task<RoomView> ProjectRoomAsync(SessionCorpusSeed.Seeded seeded)
    {
        using var scope = _fixture.BeginScope();
        var room = await scope.Resolve<IRoomProjector>().ProjectByRunAsync(seeded.RunId, seeded.TeamId, CancellationToken.None);
        room.ShouldNotBeNull();
        return room!;
    }

    private async Task<JournalView> ProjectJournalAsync(SessionCorpusSeed.Seeded seeded)
    {
        using var scope = _fixture.BeginScopeAs(seeded.UserId, seeded.TeamId, Roles.Admin);
        var view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(seeded.RunId, seeded.TeamId, CancellationToken.None);
        view.ShouldNotBeNull();
        return view!;
    }

    private static AssistantTurnBlock FocusedRoomTurn(RoomView room, Guid runId) =>
        room.Blocks.OfType<AssistantTurnBlock>().Single(t => t.RunId == runId);

    private static JournalTurn FocusedJournalTurn(JournalView view) => view.Turns.Single(t => t.Focused);

    private static Guid[] RoomAgentIds(AssistantTurnBlock turn) =>
        turn.Blocks.OfType<AgentGroupBlock>().SelectMany(g => g.Agents).Select(a => a.AgentRunId).Distinct().OrderBy(id => id).ToArray();

    private static Guid[] JournalAgentIds(JournalTurn turn) =>
        turn.Steps.SelectMany(s => s.Agents).Select(a => a.AgentRunId).Distinct().OrderBy(id => id).ToArray();

    private static string KindOf(RoomBlock block) => block switch
    {
        UserMessageBlock => "user_message",
        AssistantTurnBlock => "assistant_turn",
        ExecutionMapBlock => "execution_map",
        AgentGroupBlock => "agent_group",
        StatBlock => "stat",
        PlanChecklistBlock => "plan_checklist",
        DeliveryBlock => "delivery",
        DecisionBlock => "decision",
        DiagnosticBlock => "diagnostic",
        FinalAnswerBlock => "final_answer",
        LiveActivityBlock => "live_activity",
        _ => block.GetType().Name,   // an un-mapped RoomBlock subtype fails the tripwire — a new room block kind must be classified
    };
}
