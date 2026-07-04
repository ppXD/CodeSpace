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
/// 🟢 Integration — the P5 CUTOVER PARITY GATE. Seeds a golden-run corpus (<see cref="SessionCorpusSeed"/>) ONCE and
/// projects each run through BOTH the real <see cref="IRoomProjector"/> and <see cref="IJournalProjector"/> over real
/// Postgres, then asserts the permanent-HYBRID invariant that makes it safe to delete the room NARRATIVE stack (P6):
///
/// <list type="number">
/// <item><b>Structural tripwire</b> — every room block kind the focused turn emits is in the FE's <c>JOURNAL_HANDLED</c>
/// set (either REPLACED by the journal ③ steps — only <c>narrative_step</c> — or REUSED from the room projector by the
/// hybrid FE — the other 9). A room block kind outside that set would silently vanish in journal mode at cutover.</item>
/// <item><b>Narrative coverage</b> — the journal ③ carries the same agents + the same retry/respawn beats the room's
/// <c>narrative_step</c>s did, so deleting the room narrative loses nothing.</item>
/// <item><b>Retained-block liveness</b> — the room still projects each rich/live block the hybrid FE reuses
/// (plan_checklist, final_answer, diagnostic, decision, live_activity) + the attempt ladder is journal-native.</item>
/// </list>
///
/// <para>If this gate ever goes red, a room surface would disappear at cutover — classify it (retain the room projector
/// for it, or cover it natively in the journal ③) before deleting the room narrative stack. The pure per-emitter richness
/// stays covered by the RoomNarrative/RoomRounds unit tier — this proves only the cross-projection parity + DB wiring.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RoomJournalParityFlowTests
{
    private readonly PostgresFixture _fixture;

    public RoomJournalParityFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>
    /// The FE's <c>JOURNAL_HANDLED</c> set (SessionRoomView.tsx) mirrored on the backend: the room inner-block kinds the
    /// journal mode explicitly handles — <c>narrative_step</c> is REPLACED by the ③ steps; the other nine are REUSED from
    /// the room projection (the permanent-hybrid retained set). Keep in lockstep with the FE constant.
    /// </summary>
    private static readonly IReadOnlySet<string> JournalHandled = new HashSet<string>
    {
        "execution_map", "narrative_step", "agent_group", "plan_checklist", "stat", "live_activity",
        "delivery", "final_answer", "diagnostic", "decision",
    };

    // ─── Tier 1: the structural tripwire ────────────────────────────────────────────

    [Theory]
    [InlineData(SessionCorpusSeed.Shape.SupervisorPlanSpawn)]
    [InlineData(SessionCorpusSeed.Shape.SingleAgent)]
    [InlineData(SessionCorpusSeed.Shape.Retry)]
    [InlineData(SessionCorpusSeed.Shape.RespawnCrash)]
    [InlineData(SessionCorpusSeed.Shape.PendingDecision)]
    [InlineData(SessionCorpusSeed.Shape.ReranTurn)]
    public async Task Every_room_block_the_focused_turn_emits_is_handled_by_the_journal_mode(SessionCorpusSeed.Shape shape)
    {
        var seeded = await SessionCorpusSeed.SeedAsync(_fixture, shape);
        var focused = FocusedRoomTurn(await ProjectRoomAsync(seeded), seeded.RunId);

        var kinds = focused.Blocks.Select(KindOf).ToList();
        if (focused.Map is not null) kinds.Add("execution_map");

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
        foreach (var kind in new[] { "narrative_step", "agent_group", "plan_checklist", "stat", "execution_map", "final_answer", "diagnostic", "decision", "live_activity" })
            observed.ShouldContain(kind, $"the corpus should exercise the {kind} room surface so the parity gate runs over real data, not a vacuous fixture");
    }

    // ─── Tier 2: narrative coverage (the journal ③ carries what the room narrative did) ──

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
    public async Task The_retry_narrative_the_room_shows_is_carried_as_a_journal_beat_with_its_rationale()
    {
        var seeded = await SessionCorpusSeed.SeedAsync(_fixture, SessionCorpusSeed.Shape.Retry);

        var room = FocusedRoomTurn(await ProjectRoomAsync(seeded), seeded.RunId);
        room.Blocks.OfType<NarrativeStepBlock>().Select(s => s.Text).ShouldContain("Supervisor retried a subtask",
            "the room narrates the retry as a narrative_step — the surface P6 deletes");

        var journal = FocusedJournalTurn(await ProjectJournalAsync(seeded));
        var retryBeat = journal.Steps.SingleOrDefault(s => s.Beat && s.Title == "Supervisor retried a subtask");

        retryBeat.ShouldNotBeNull("the retry the room narrated must survive as a journal ③ beat — else deleting the room narrative loses it");
        retryBeat!.Rationale.ShouldNotBeNull("and the journal beat carries the supervisor's WHY (richer than the room narrative)");
        retryBeat.Rationale!.ShouldContain("missed the edge cases", customMessage: "the retry rationale reads off the decision tape");
    }

    [Fact]
    public async Task The_respawn_wave_the_room_narrates_is_carried_as_journal_spawn_beats()
    {
        var seeded = await SessionCorpusSeed.SeedAsync(_fixture, SessionCorpusSeed.Shape.RespawnCrash);

        var room = FocusedRoomTurn(await ProjectRoomAsync(seeded), seeded.RunId);
        room.Blocks.OfType<NarrativeStepBlock>().Select(s => s.Text).ShouldContain("Supervisor spawned 2 agents again",
            "the room narrates the re-spawn wave as a narrative_step");

        var journal = FocusedJournalTurn(await ProjectJournalAsync(seeded));
        journal.Steps.Count(s => s.Beat && s.Verb == "spawn").ShouldBeGreaterThanOrEqualTo(2,
            "both spawn waves survive as chronological journal ③ beats — the room's synthetic 'again' narrative becomes two real dispatch beats");
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
        NarrativeStepBlock => "narrative_step",
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
