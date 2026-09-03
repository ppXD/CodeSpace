using System.Data.Common;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// The room projector over real Postgres — the DB assembly the pure <see cref="RoomNarrative"/> can't cover: the turn
/// skeleton (goals + latest-attempt + status per turn), the focused-vs-collapsed split, the change-watermark cursor
/// (MAX of the run's append-only ledger), and tenancy (a foreign run / session is an indistinguishable null). The
/// narrative/map richness is proven exhaustively at the unit tier; this proves the wiring + the persistence reads.
///
/// <para>Tier: high-fidelity Integration — the real <see cref="IRoomProjector"/> + its dependencies over real Postgres.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RoomProjectorFlowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _fixture;

    public RoomProjectorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task One_room_request_loads_the_supervisor_observation_tape_once()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "One room tape read");
        var runId = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Observe it", resultSummary: "done");
        await SeedPlanDecisionAsync(teamId, runId, "Inspect");

        var recorder = new SupervisorTapeReadRecorder();
        using var scope = _fixture.BeginScope(builder =>
        {
            var options = new DbContextOptionsBuilder<CodeSpaceDbContext>()
                .UseNpgsql(_fixture.ConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(recorder)
                .Options;
            builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
        });

        var room = await scope.Resolve<IRoomProjector>().ProjectByRunAsync(runId, teamId, CancellationToken.None);

        room.ShouldNotBeNull();
        recorder.Reads.ShouldBe(1, "phase, narrative and publish observations share one exact request-scoped tape");
    }

    [Fact]
    public async Task Projects_every_turn_richly_not_just_the_focused_one()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Build the dashboard");

        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "First task", resultSummary: "First task done");
        var watermark1 = await SeedRecordsAsync(run1, count: 2);
        var run2 = await SeedTurnAsync(teamId, sessionId, turn: 2, goal: "Second task", resultSummary: "Second task done");
        var watermark2 = await SeedRecordsAsync(run2, count: 3);

        var room = await ProjectByRunAsync(run2, teamId);

        room.ShouldNotBeNull();
        room!.SessionId.ShouldBe(sessionId);
        room.Title.ShouldBe("Build the dashboard");
        room.AnchorBlockId.ShouldBe("turn-2", "entering by the latest run focuses its turn (the scroll anchor)");
        room.Cursor.ShouldBe(Math.Max(watermark1, watermark2), "the cursor is the newest change watermark across the turns");

        room.Blocks.OfType<UserMessageBlock>().Select(b => b.Text).ShouldBe(new[] { "First task", "Second task" }, "the user messages are the per-turn goals, oldest first");

        var turns = room.Blocks.OfType<AssistantTurnBlock>().OrderBy(t => t.TurnIndex).ToList();
        turns.Count.ShouldBe(2);

        // Every turn is richly projected now — the prior turn is no longer a Seq-0 light card. Its OWN watermark proves it
        // went through the full projection, so its execution UI (map / inner blocks) is available on expand, not just "Done.".
        var prior = turns[0];
        prior.TurnIndex.ShouldBe(1);
        prior.RunId.ShouldBe(run1);
        prior.Seq.ShouldBe(watermark1, "a past turn carries its OWN change watermark — the rich projection, not a Seq-0 light card");
        prior.Summary.ShouldBe("First task done", "a turn with a result but no execution narrative still shows its result");
        prior.Actions.ShouldContain(a => a.Kind == RoomActionKind.OpenTrace, "a past turn still carries its capability-aware actions");

        var focused = turns[1];
        focused.TurnIndex.ShouldBe(2);
        focused.RunId.ShouldBe(run2);
        focused.Seq.ShouldBe(watermark2);
        focused.Actions.ShouldContain(a => a.Kind == RoomActionKind.RerunTurn && a.Enabled, "a finished focused turn offers a rerun");
    }

    [Fact]
    public async Task A_file_the_run_produced_is_reachable_even_though_it_touched_no_repository()
    {
        // The hole this closes: an agent.run with no repositoryId and an ArtifactPresent acceptance writes its
        // report into a scratch workspace, the capture mints a manifest row plus CAS bytes, the oracle passes, the
        // run reports Succeeded — and the workspace is then deleted. Every file surface in the UI is built from git
        // ground truth, which that run has none of, so the deliverable existed only as a row nobody could reach.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Produced a report");
        var runId = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Write the report", resultSummary: "done");
        var agentRunId = await SeedProducedFileAsync(teamId, runId, "report.md", ArtifactManifestKind.Document, sizeBytes: 4096);

        var block = await DeliverablesOfAsync(runId, teamId);

        var file = block.Files.ShouldHaveSingleItem();
        file.Path.ShouldBe("report.md");
        file.Kind.ShouldBe(nameof(ArtifactManifestKind.Document));
        file.SizeBytes.ShouldBe(4096);
        file.AgentRunId.ShouldBe(agentRunId, "a multi-agent turn has to say which agent produced which file");
        file.ArtifactId.ShouldNotBe(Guid.Empty, "the id is the whole point — it is what GET /api/artifacts/{id} takes");
    }

    [Fact]
    public async Task A_re_captured_file_is_listed_once_as_its_current_copy()
    {
        // The ledger is append-only: a re-capture supersedes rather than rewrites, and the superseded row keeps
        // pointing at its successor so the chain stays auditable. A reader asking "what did this run produce" wants
        // the current copy; listing both would make one file look like two.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Recaptured a report");
        var runId = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Rewrite the report", resultSummary: "done");
        await SeedProducedFileAsync(teamId, runId, "report.md", ArtifactManifestKind.Document, sizeBytes: 10, supersededBy: Guid.NewGuid());
        await SeedProducedFileAsync(teamId, runId, "report.md", ArtifactManifestKind.Document, sizeBytes: 4096);

        var block = await DeliverablesOfAsync(runId, teamId);

        block.Files.ShouldHaveSingleItem().SizeBytes.ShouldBe(4096);
    }

    [Fact]
    public async Task A_turn_that_produced_no_files_carries_no_deliverables_block_at_all()
    {
        // Absent rather than empty: an empty list reads as "it produced nothing", which is a claim about the run
        // rather than about this surface, and every repo-bound turn would then carry a misleading zero.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Produced nothing");
        var runId = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Just think", resultSummary: "done");

        var room = (await ProjectAsync(runId, teamId)).ShouldNotBeNull();

        AllBlocks(room).OfType<DeliverablesBlock>().ShouldBeEmpty();
    }

    [Fact]
    public async Task One_teams_produced_files_never_appear_in_another_teams_room()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeam, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Scoped");
        var runId = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Write", resultSummary: "done");
        await SeedProducedFileAsync(otherTeam, runId, "theirs.md", ArtifactManifestKind.Document, sizeBytes: 8);

        var room = (await ProjectAsync(runId, teamId)).ShouldNotBeNull();

        AllBlocks(room).OfType<DeliverablesBlock>().ShouldBeEmpty();
    }

    private async Task<DeliverablesBlock> DeliverablesOfAsync(Guid runId, Guid teamId)
    {
        var room = (await ProjectAsync(runId, teamId)).ShouldNotBeNull();

        return AllBlocks(room).OfType<DeliverablesBlock>().ShouldHaveSingleItem();
    }

    private async Task<RoomView?> ProjectAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IRoomProjector>().ProjectByRunAsync(runId, teamId, CancellationToken.None);
    }

    private static IEnumerable<RoomBlock> AllBlocks(RoomView room) =>
        room.Blocks.Concat(room.Blocks.OfType<AssistantTurnBlock>().SelectMany(turn => turn.Blocks));

    /// <summary>Mints the manifest row and its CAS content the way a capture does, so the projection reads what production writes.</summary>
    private async Task<Guid> SeedProducedFileAsync(Guid teamId, Guid runId, string path, ArtifactManifestKind kind, long sizeBytes, Guid? supersededBy = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var agentRunId = Guid.NewGuid();
        var payload = System.Text.Encoding.UTF8.GetBytes($"{path} {Guid.NewGuid():N}");
        var artifactId = Guid.NewGuid();

        db.WorkflowArtifact.Add(new WorkflowArtifact
        {
            Id = artifactId, TeamId = teamId, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)),
            ContentType = "text/markdown", SizeBytes = payload.Length, InlineBytes = payload, CreatedAt = now,
        });
        db.ArtifactManifest.Add(new ArtifactManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, AgentRunId = agentRunId, WorkflowRunId = runId, FenceEpoch = 1,
            Kind = kind, LogicalPath = path, ContentArtifactId = artifactId,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)),
            SizeBytes = sizeBytes, ContentType = "text/markdown", SupersededByManifestId = supersededBy,
            CreatedDate = now, LastModifiedDate = now,
        });
        await db.SaveChangesAsync();

        return agentRunId;
    }

    [Fact]
    public async Task A_foreign_run_or_session_projects_to_null_never_leaked()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeam, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Mine");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "x", resultSummary: "y");

        (await ProjectByRunAsync(run, otherTeam)).ShouldBeNull("a cross-team run resolves to no room");

        using var scope = _fixture.BeginScope();
        var foreignSession = await scope.Resolve<IRoomProjector>().ProjectAsync(sessionId, null, otherTeam, CancellationToken.None);
        foreignSession.ShouldBeNull("a cross-team session resolves to no room");
    }

    [Fact]
    public async Task The_focused_turn_surfaces_node_and_agent_grain_decisions_but_not_a_foreign_runs()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Decisions");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Decide things", resultSummary: null);

        var otherSession = await SeedSessionAsync(teamId, "Other");
        var foreignRun = await SeedTurnAsync(teamId, otherSession, turn: 1, goal: "Other run", resultSummary: null);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        await SeedNodeDecisionAsync(teamId, run, "Pick a path", deadline, new[]
        {
            new DecisionOption { Id = "safe", Label = "Stay safe" },
            new DecisionOption { Id = "deploy", Label = "Deploy now", IsSideEffecting = true },
        });
        await SeedAgentDecisionAsync(teamId, run, "Approve the deploy?", deadline);
        await SeedNodeDecisionAsync(teamId, foreignRun, "Foreign decision", deadline, Array.Empty<DecisionOption>());

        var room = await ProjectByRunAsync(run, teamId);

        var decisions = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1).Blocks.OfType<DecisionBlock>().ToList();

        // Both the run-grain (node) and agent-grain decisions surface; the foreign run's does not leak in.
        decisions.Select(d => d.Question).OrderBy(q => q).ToArray().ShouldBe(new[] { "Approve the deploy?", "Pick a path" });

        var node = decisions.Single(d => d.Question == "Pick a path");
        node.Id.ShouldBe($"decision-{node.DecisionId}", "the block id is prefixed off the decision id");
        node.DecisionId.ShouldNotBe(Guid.Empty);
        node.Shape.ShouldBe(DecisionTypes.ChooseOne, "the answer shape is carried verbatim");
        node.Risk.ShouldBe(DecisionRiskLevels.High);
        node.Deadline.ShouldNotBeNull();
        node.Options.ShouldNotBeNull();
        node.Options!.Single(o => o.Id == "deploy").SideEffecting.ShouldBeTrue("the side-effecting flag is carried so the renderer can warn before submit");
        node.Options!.Single(o => o.Id == "safe").SideEffecting.ShouldBeFalse();
    }

    [Fact]
    public async Task A_past_failed_turn_is_richly_projected_and_keeps_its_actions()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Mixed");

        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Failed work", resultSummary: null, status: WorkflowRunStatus.Failure);
        var run2 = await SeedTurnAsync(teamId, sessionId, turn: 2, goal: "Latest", resultSummary: "ok");

        var room = await ProjectByRunAsync(run2, teamId);

        // A past turn is no longer a light card — it carries its own terminal status + actions; its detail (map / blocks /
        // diagnostic) comes from the rich narrative rather than a status-derived fallback copy.
        var prior = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);
        prior.Status.ShouldBe(WorkflowRunStatus.Failure, "a past turn carries its own terminal status");
        prior.Actions.ShouldContain(a => a.Kind == RoomActionKind.OpenTrace, "a past turn still carries its capability-aware actions");
    }

    [Fact]
    public async Task A_supervisor_turn_surfaces_the_canonical_map_and_the_planned_subtasks()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Supervised");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Do the thing", resultSummary: "Shipped it.");

        await SeedPlanDecisionAsync(teamId, run, "Trace DI registration", "Analyze the template store");

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        turn.Map.ShouldNotBeNull();
        turn.Map!.Steps.Select(s => s.Label).ShouldBe(new[] { "Start", "Plan", "Work", "Review", "Deliver" }, "a supervisor turn (decision tape present) gets the canonical lifecycle map");

        var subtasks = turn.Blocks.OfType<StatBlock>().Single(s => s.Kind == "subtasks");
        subtasks.Label.ShouldBe("Plan");
        subtasks.Detail.ShouldBe("2 subtasks");
        subtasks.Items.Select(i => i.Text).ShouldBe(new[] { "Trace DI registration", "Analyze the template store" }, "the plan's subtask titles are surfaced from the decision tape");
    }

    [Fact]
    public async Task A_non_conformant_give_up_stop_renders_a_degraded_result_not_a_green_success()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Gave up");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Delete the invalid usings", resultSummary: null);

        // The supervisor's decision model produced a non-conformant reply → a fail-closed clean stop with a graceful-failure
        // outcome (no work delivered), yet the run status is a clean terminal Success.
        await SeedStopDecisionAsync(teamId, run, outcome: "no-decision", summary: "The supervisor model returned a response that did not conform to the decision schema — stopping cleanly rather than crashing the run.");

        var turn = (await ProjectByRunAsync(run, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        var result = turn.Blocks.OfType<FinalAnswerBlock>().Single();
        result.Degraded.ShouldBeTrue("a non-conformant give-up stop is a graceful FAILURE, not a green success — the card reads degraded");
        result.Text.ShouldContain("did not conform");

        turn.Map!.Steps.Single(s => s.Label == "Review").Detail.ShouldBe("stopped", "the map must not show a green 'passed' for a run that gave up");
    }

    [Fact]
    public async Task A_genuine_completed_stop_renders_a_normal_green_result()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Done well");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Delete the invalid usings", resultSummary: null);

        await SeedStopDecisionAsync(teamId, run, outcome: "completed", summary: "Deleted 12 invalid usings across the project.");

        var result = (await ProjectByRunAsync(run, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1).Blocks.OfType<FinalAnswerBlock>().Single();
        result.Degraded.ShouldBeFalse("a genuine 'completed' stop is a real success — the green Result stays (regression guard)");
    }

    [Fact]
    public async Task A_stop_whose_acceptance_check_FAILED_renders_a_degraded_result_naming_the_reason()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Claimed done, checks disagreed");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Fix the flaky tests", resultSummary: null);

        // The over-claim shape the room used to launder into a green Result: the supervisor stopped ORDERLY with a
        // conformant "completed" outcome and a confident closing line, but the objective acceptance grade FAILED. The
        // engine already stamps the honest word on the run row (Outcome = AcceptanceFailed) while the graph-level
        // Status stays a clean terminal Success — so the card, reading only the stop's classification, painted green.
        await SeedStopDecisionAsync(teamId, run, outcome: "completed", summary: "Fixed the flaky tests across the suite.", acceptancePassed: false);
        await StampRunOutcomeAsync(run, SupervisorOutcome.AcceptanceFailedOutcome);

        var turn = (await ProjectByRunAsync(run, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        var result = turn.Blocks.OfType<FinalAnswerBlock>().Single();
        result.Degraded.ShouldBeTrue("the work missed its own definition of done — the run row already says AcceptanceFailed, so the card cannot say Result");
        result.DegradedReason.ShouldBe("Checks failed", "the card states the ledger's verdict, because the TEXT is the model's own success claim");
        result.Text.ShouldBe("Fixed the flaky tests across the suite.", "the model's claim is preserved verbatim — the card contradicts it, it does not rewrite it");

        turn.Map!.Steps.Single(s => s.Label == "Review").Detail.ShouldBe("failed", "the objective grade outranks the stop's classification — never softened to 'stopped'");
    }

    [Fact]
    public async Task A_stop_whose_acceptance_check_PASSED_keeps_the_green_result()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Checked and done");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Fix the flaky tests", resultSummary: null);

        await SeedStopDecisionAsync(teamId, run, outcome: "completed", summary: "Fixed the flaky tests across the suite.", acceptancePassed: true);

        var result = (await ProjectByRunAsync(run, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1).Blocks.OfType<FinalAnswerBlock>().Single();
        result.Degraded.ShouldBeFalse("a graded PASS is the one case that earns the green Result (regression guard)");
        result.DegradedReason.ShouldBeNull();
    }

    [Fact]
    public async Task A_server_forced_bound_stop_renders_a_degraded_result_and_surfaces_the_reason()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Ran out of runway");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Fix the flaky tests", resultSummary: null);

        // A no-progress/governance/bound trip forces a terminal stop that stamps {reason} on the PAYLOAD (no model outcome).
        // The old degraded check read only the OUTCOME, so this rendered a green success with a BLANK result. It must
        // now render degraded AND surface the bound that stopped it — the SAME classifier the give-up stop uses.
        await SeedForcedStopDecisionAsync(teamId, run, reason: SupervisorStopReasons.NoProgress);

        var turn = (await ProjectByRunAsync(run, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        var result = turn.Blocks.OfType<FinalAnswerBlock>().Single();
        result.Degraded.ShouldBeTrue("a run the server force-stopped on a bound did NOT finish the work — it is not a green success");
        result.Text.ShouldBe("no progress", "the RESULT never renders blank — it names the bound that stopped the run");

        turn.Map!.Steps.Single(s => s.Label == "Review").Detail.ShouldBe("stopped", "the map must not show a green 'passed' for a force-stopped run");
    }

    [Fact]
    public async Task A_supervisor_turn_aggregates_distinct_changed_files_across_its_agents()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Files");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Edit files", resultSummary: "Done.");

        await SeedPlanDecisionAsync(teamId, run, "Sub A");
        await SeedSpawnDecisionAsync(teamId, run, (Guid.NewGuid(), new[] { "b.cs", "a.cs" }), (Guid.NewGuid(), new[] { "a.cs", "c.cs" }));

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);
        var files = turn.Blocks.OfType<StatBlock>().Single(s => s.Kind == "files");

        files.Label.ShouldBe("Files changed");
        files.Detail.ShouldBe("3 files", "no diff line stat captured → just the file count");
        files.Items.Select(i => i.Text).ShouldBe(new[] { "a.cs", "b.cs", "c.cs" }, "the distinct, ordinal-sorted union of the agents' changed files (a.cs shared → counted once)");

        var agents = turn.Blocks.OfType<AgentGroupBlock>().Single();
        agents.Title.ShouldBe("Agents", "a terminal supervisor turn surfaces its spawned agents as one group");
        agents.Agents.Count.ShouldBe(2, "one card per spawned agent");
    }

    [Fact]
    public async Task A_replan_never_presents_the_superseded_generations_files_as_final_delivery()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Replanned files");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Edit the right file", resultSummary: "Done.");

        await SeedPlanDecisionAsync(teamId, run, "Old approach");
        await SeedSpawnDecisionAsync(teamId, run, (Guid.NewGuid(), new[] { "superseded.cs" }));
        await SeedPlanDecisionAsync(teamId, run, "Replacement approach");
        await SeedSpawnDecisionAsync(teamId, run, (Guid.NewGuid(), new[] { "current.cs" }));

        var turn = (await ProjectByRunAsync(run, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);
        var files = turn.Blocks.OfType<StatBlock>().Single(block => block.Kind == "files");
        var attachments = turn.Blocks.OfType<FinalAnswerBlock>().Single().Attachments.Where(attachment => attachment.Kind == AnswerAttachmentKind.FileLink).ToList();

        files.Items.Select(item => item.Text).ShouldBe(new[] { "current.cs" }, "the old generation remains audit history, not the current reviewable file set");
        attachments.Select(attachment => attachment.Label).ShouldBe(new[] { "current.cs" }, "a superseded output must not be repackaged as a final deliverable");
    }

    [Fact]
    public async Task A_multi_repo_turn_keeps_same_path_files_distinct_and_carries_exact_identity_to_every_click_surface()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Multi-repo files");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Edit both READMEs", resultSummary: "Done.");
        var webId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var agentRunId = await SeedMultiRepoSpawnDecisionAsync(teamId, run,
            new RepositoryRunResult { RepositoryId = webId, Alias = "web", ChangedFiles = new[] { "README.md" } },
            new RepositoryRunResult { RepositoryId = apiId, Alias = "api", ChangedFiles = new[] { "README.md" } });

        var turn = (await ProjectByRunAsync(run, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);
        var files = turn.Blocks.OfType<StatBlock>().Single(block => block.Kind == "files");
        var fileJson = JsonSerializer.SerializeToElement(files.Items, AgentJson.Options);

        files.Items.Count.ShouldBe(2, "repo-relative path alone is not a change identity — web/README.md and api/README.md are two files");
        fileJson.EnumerateArray().Select(item => item.GetProperty("file").GetProperty("repositoryAlias").GetString()).ShouldBe(new[] { "api", "web" }, ignoreOrder: true);
        fileJson.EnumerateArray().ShouldAllBe(item => item.GetProperty("file").GetProperty("agentRunId").GetGuid() == agentRunId);

        var card = turn.Blocks.OfType<AgentGroupBlock>().Single().Agents.ShouldHaveSingleItem();
        var cardJson = JsonSerializer.SerializeToElement(card, AgentJson.Options);
        cardJson.GetProperty("changedFileIdentities").GetArrayLength().ShouldBe(2, "the agent terminal's Files tab needs the same repo identity as the global Files row");
        cardJson.GetProperty("changedFileIdentities").EnumerateArray().Select(item => item.GetProperty("repositoryAlias").GetString()).ShouldBe(new[] { "api", "web" }, ignoreOrder: true);

        var attachments = turn.Blocks.OfType<FinalAnswerBlock>().Single().Attachments.Where(attachment => attachment.Kind == AnswerAttachmentKind.FileLink).ToList();
        var attachmentJson = JsonSerializer.SerializeToElement(attachments, AgentJson.Options);
        attachmentJson.GetArrayLength().ShouldBe(2);
        attachmentJson.EnumerateArray().Select(item => item.GetProperty("file").GetProperty("repositoryAlias").GetString()).ShouldBe(new[] { "api", "web" }, ignoreOrder: true);
        attachmentJson.EnumerateArray().ShouldAllBe(item => item.GetProperty("file").GetProperty("agentRunId").GetGuid() == agentRunId);
    }

    [Fact]
    public async Task A_reran_turn_surfaces_its_attempt_timeline_oldest_to_newest()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Flaky");
        var now = DateTimeOffset.UtcNow;

        var original = await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, status: WorkflowRunStatus.Failure, source: WorkflowRunSourceTypes.Snapshot, createdAt: now.AddMinutes(-5));
        var winner = await SeedAttemptAsync(teamId, sessionId, turnIndex: null, rootRunId: original, status: WorkflowRunStatus.Success, source: WorkflowRunSourceTypes.Rerun, createdAt: now);

        var room = await ProjectByRunAsync(winner, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single();

        turn.Attempts.Select(a => a.AttemptNumber).ShouldBe(new[] { 1, 2 }, "the turn's attempts, oldest → newest");
        turn.Attempts.Select(a => a.RunId).ShouldBe(new[] { original, winner });
        turn.Attempts[0].Status.ShouldBe(WorkflowRunStatus.Failure, "attempt 1 failed");
        turn.Attempts[1].Status.ShouldBe(WorkflowRunStatus.Success, "the rerun recovered");
        turn.Attempts.Single(a => a.IsCurrent).RunId.ShouldBe(winner, "the shown attempt is the newest");
    }

    [Fact]
    public async Task A_terminal_cache_hit_overlays_the_fresh_attempt_ladder_without_rebuilding_the_heavy_block()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Flaky then continued");
        var now = DateTimeOffset.UtcNow;

        var original = await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, status: WorkflowRunStatus.Failure, source: WorkflowRunSourceTypes.Snapshot, createdAt: now.AddMinutes(-10));
        var winner = await SeedAttemptAsync(teamId, sessionId, turnIndex: null, rootRunId: original, status: WorkflowRunStatus.Success, source: WorkflowRunSourceTypes.Rerun, createdAt: now.AddMinutes(-9));
        await SeedStopDecisionAsync(teamId, winner, outcome: "completed", summary: "Cached exact answer.");
        var current = await SeedTurnAsync(teamId, sessionId, turn: 2, goal: "Continue", resultSummary: "Current turn.");

        var before = (await ProjectByRunAsync(current, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(turn => turn.TurnIndex == 1);
        before.Attempts.Select(attempt => attempt.RunId).ShouldBe(new[] { original, winner });
        var cachedBody = JsonSerializer.Serialize(before with { Attempts = Array.Empty<RoomTurnAttempt>() }, AgentJson.Options);

        var laterFailure = await SeedAttemptAsync(teamId, sessionId, turnIndex: null, rootRunId: original, status: WorkflowRunStatus.Failure, source: WorkflowRunSourceTypes.Rerun, createdAt: now.AddMinutes(-8));

        var after = (await ProjectByRunAsync(current, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(turn => turn.TurnIndex == 1);

        after.RunId.ShouldBe(winner, "the newest success remains the effective attempt even after a later failure");
        after.Status.ShouldBe(WorkflowRunStatus.Success);
        after.Attempts.Select(attempt => attempt.RunId).ShouldBe(new[] { original, winner, laterFailure }, "the cached terminal body must not freeze the attempt ladder");
        after.Attempts.Select(attempt => attempt.Status).ShouldBe(new[] { WorkflowRunStatus.Failure, WorkflowRunStatus.Success, WorkflowRunStatus.Failure });
        after.Attempts.Single(attempt => attempt.IsCurrent).RunId.ShouldBe(winner);
        after.Blocks.ShouldBeSameAs(before.Blocks, "the expensive narrative remains the object cached for the effective terminal run");
        after.Actions.ShouldBeSameAs(before.Actions, "only the cheap attempt ladder is overlaid on a cache hit");
        JsonSerializer.Serialize(after with { Attempts = Array.Empty<RoomTurnAttempt>() }, AgentJson.Options).ShouldBe(cachedBody, "headline, answer, map, blocks and actions remain the exact cached heavy projection");
    }

    [Fact]
    public async Task Opening_a_prior_attempts_run_focuses_that_attempts_flow_not_the_latest()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Flaky");
        var now = DateTimeOffset.UtcNow;

        var original = await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, status: WorkflowRunStatus.Failure, source: WorkflowRunSourceTypes.Snapshot, createdAt: now.AddMinutes(-5));
        var winner = await SeedAttemptAsync(teamId, sessionId, turnIndex: null, rootRunId: original, status: WorkflowRunStatus.Success, source: WorkflowRunSourceTypes.Rerun, createdAt: now);

        // Anchoring on the PRIOR attempt's run (the switcher navigates there) focuses THAT attempt, not the latest.
        var turn = (await ProjectByRunAsync(original, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single();

        turn.RunId.ShouldBe(original, "the focused run is the requested prior attempt, not the latest");
        turn.Status.ShouldBe(WorkflowRunStatus.Failure, "and it carries that attempt's OWN status");
        turn.Attempts.Single(a => a.IsCurrent).RunId.ShouldBe(original, "attempt 1 reads 'shown' when focused; the winner is just another row");
    }

    [Fact]
    public async Task A_never_reran_turn_has_no_attempt_timeline()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "One shot");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Do it once", resultSummary: "Done.");

        var room = await ProjectByRunAsync(run, teamId);
        room!.Blocks.OfType<AssistantTurnBlock>().Single().Attempts.ShouldBeEmpty("a lone attempt needs no history — the timeline stays empty");
    }

    [Fact]
    public async Task The_latest_attempt_shows_its_own_wall_clock_not_the_whole_lineage_span()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Reran a week later");
        var now = DateTimeOffset.UtcNow;

        // attempt 1 (the lineage root) was created a WEEK ago; attempt 2 (a rerun) was created an hour ago and ran ~1h.
        var original = await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, status: WorkflowRunStatus.Failure, source: WorkflowRunSourceTypes.Snapshot, createdAt: now.AddDays(-7), completedAt: now.AddDays(-7).AddMinutes(30));
        var latest = await SeedAttemptAsync(teamId, sessionId, turnIndex: null, rootRunId: original, status: WorkflowRunStatus.Failure, source: WorkflowRunSourceTypes.Rerun, createdAt: now.AddHours(-1), completedAt: now);

        var turn = (await ProjectByRunAsync(latest, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single();

        turn.At!.Value.ShouldBe(now.AddHours(-1), TimeSpan.FromSeconds(5), "the latest attempt shows its OWN created time, not the lineage root's (a week ago)");
        turn.DurationMs!.Value.ShouldBeInRange(50 * 60_000L, 70 * 60_000L, "its OWN ~1h wall-clock, NOT the ~7-day span from the first attempt's creation to now");
    }

    [Fact]
    public async Task Each_attempt_shows_its_own_content_not_the_latest_lineage_merged()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Full reruns");
        var now = DateTimeOffset.UtcNow;

        // Two FULL reruns of the SAME turn — each re-ran the "agent" cell with its OWN output. The lineage merge keeps the
        // NEWEST attempt per cell, so without a per-run scope BOTH attempts' rooms would show attempt 2's output.
        var attempt1 = await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, status: WorkflowRunStatus.Success, source: WorkflowRunSourceTypes.Snapshot, createdAt: now.AddMinutes(-10), completedAt: now.AddMinutes(-9));
        await SeedAgentNodeAsync(teamId, attempt1, summary: "first attempt output", changedFiles: new[] { "a1.txt" });

        var attempt2 = await SeedAttemptAsync(teamId, sessionId, turnIndex: null, rootRunId: attempt1, status: WorkflowRunStatus.Success, source: WorkflowRunSourceTypes.Rerun, createdAt: now, completedAt: now.AddMinutes(1));
        await SeedAgentNodeAsync(teamId, attempt2, summary: "second attempt output", changedFiles: new[] { "a2.txt" });

        var turn1 = (await ProjectByRunAsync(attempt1, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);
        var turn2 = (await ProjectByRunAsync(attempt2, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        turn1.Blocks.OfType<FinalAnswerBlock>().Single().Text.ShouldBe("first attempt output", "attempt 1 shows its OWN run's content — not the latest attempt's lineage-merged in");
        turn2.Blocks.OfType<FinalAnswerBlock>().Single().Text.ShouldBe("second attempt output", "attempt 2 shows its own");
    }

    /// <summary>Seed one attempt (a top-level turn run when turnIndex is set, else a rerun/replay fork with rootRunId) of a session turn, with an explicit created (and optional completed) time so the attempt ordering + wall-clock are deterministic.</summary>
    private async Task<Guid> SeedAttemptAsync(Guid teamId, Guid sessionId, int? turnIndex, Guid? rootRunId, WorkflowRunStatus status, string source, DateTimeOffset createdAt, DateTimeOffset? completedAt = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = source, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = createdAt, VerifiedAt = createdAt, NormalizedAt = createdAt,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = source,
            Status = status, SessionId = sessionId, SessionTurnIndex = turnIndex, RootRunId = rootRunId,
            DefinitionSnapshotJson = "{\"nodes\":[],\"edges\":[]}", DefinitionSnapshotHash = "sha256:test",
            OutputsJson = "{}", CreatedDate = createdAt, CompletedAt = completedAt, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    [Fact]
    public async Task A_supervisor_turn_with_a_retry_surfaces_the_failed_original_and_a_retry_step()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Retry");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Do the work", resultSummary: "Done.");

        var failedAgent = Guid.NewGuid();
        var retryAgent = Guid.NewGuid();
        await SeedPlanDecisionAsync(teamId, run, "Sub 0");
        await SeedRetryScenarioAsync(teamId, run, failedAgent, retryAgent);

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        var cards = turn.Blocks.OfType<AgentGroupBlock>().SelectMany(g => g.Agents).ToList();
        cards.ShouldContain(c => c.AgentRunId == failedAgent && c.Status == nameof(AgentRunStatus.Failed), "the failed original is a Failed card in the initial group, not hidden behind its retry");
        cards.Count(c => c.AgentRunId == retryAgent).ShouldBe(1, "the retry agent renders EXACTLY once — as its own chronological card, never also lumped into the round group");

        // The retry's card is its OWN 'Retry' group, distinct from the initial-spawn group (chronological, not a lump).
        // (Post-P6 the "Supervisor retried a subtask" narrative_step + its rationale detail live on the Journal ③ beat,
        // not the room — the room keeps the retry's agent card.)
        var retryGroup = turn.Blocks.OfType<AgentGroupBlock>().Single(g => g.Agents.Any(a => a.AgentRunId == retryAgent));
        retryGroup.Agents.ShouldHaveSingleItem().AgentRunId.ShouldBe(retryAgent);
        retryGroup.Title.ShouldBe("Retry", "the retry's fresh agent is its own 'Retry'-titled group");
    }

    /// <summary>Seed a supervisor turn that FAILED a subtask then RETRIED it: a spawn staging one FAILED agent for subtask "s0", then a retry staging a fresh SUCCEEDED agent — plus both AgentRun rows (ground-truth status). Flat plan (the tape path).</summary>
    private async Task SeedRetryScenarioAsync(Guid teamId, Guid runId, Guid failedAgent, Guid retryAgent)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtaskIds = new[] { "s0" } }),
            OutcomeJson = JsonSerializer.Serialize(new { agentCount = 1, agentRunIds = new[] { failedAgent } }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Retry, IdempotencyKey = $"retry:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtaskId = "s0", rationale = new { why = "The first attempt missed the edge cases.", evidence = "attempt 1 failed its acceptance check." } }),
            OutcomeJson = JsonSerializer.Serialize(new { agentCount = 1, agentRunIds = new[] { retryAgent } }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        db.AgentRun.Add(RetryAgentRow(teamId, runId, failedAgent, AgentRunStatus.Failed, now));
        db.AgentRun.Add(RetryAgentRow(teamId, runId, retryAgent, AgentRunStatus.Succeeded, now));

        await db.SaveChangesAsync();
    }

    private static AgentRun RetryAgentRow(Guid teamId, Guid runId, Guid agentId, AgentRunStatus status, DateTimeOffset now) => new()
    {
        Id = agentId, TeamId = teamId, WorkflowRunId = runId, NodeId = "sup", IterationKey = "sup",
        Harness = "codex-cli", Status = status, TaskJson = "{}",
        CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
    };

    [Fact]
    public async Task A_re_spawned_wave_and_the_deep_error_both_surface_in_the_room()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Investigate");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Investigate", resultSummary: null, status: WorkflowRunStatus.Failure);

        var (w1a, w1b, w2a, w2b) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SeedRespawnScenarioAsync(teamId, run, (w1a, w1b), (w2a, w2b));

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        // (a) the SECOND spawn wave renders as its own group — not collapsed into the first (the authored group anchors
        // wave 1). Post-P6 the "spawned N agents again" narrative_step lives on the Journal ③ beat; the room keeps the wave's cards.
        var waveGroup = turn.Blocks.OfType<AgentGroupBlock>().Single(g => g.Agents.Any(a => a.AgentRunId == w2b));
        waveGroup.Agents.Select(a => a.AgentRunId).ShouldBe(new[] { w2a, w2b }, "the second wave shows exactly its own agents");
        waveGroup.Agents.Single(a => a.AgentRunId == w2b).Status.ShouldBe(nameof(AgentRunStatus.Failed), "the wave's FAILED agent is visible, as Activity shows");

        var allCards = turn.Blocks.OfType<AgentGroupBlock>().SelectMany(g => g.Agents).ToList();
        allCards.Count(a => a.AgentRunId == w2a).ShouldBe(1, "each re-spawned agent renders EXACTLY once — never also lumped into the authored group");
        allCards.ShouldContain(a => a.AgentRunId == w1a, "wave 1's agents stay anchored in the authored 'Investigate' group");

        // (b) the diagnostic surfaces the SPECIFIC deep error (node.failed), not the generic "Node 'sup' failed." run message.
        turn.Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem()
            .Text.ShouldBe("OpenAI API error (no-status, Transient): the request timed out before the gateway responded");
    }

    /// <summary>Seed the user's failing scenario: a plan that grouped sa+sb into one authored "Investigate" phase, a FIRST spawn wave (both agents succeed), a SECOND spawn wave re-dispatching the same subtasks (one agent fails), plus the deep failure the engine wrote onto the node.failed ledger record (the run row's Error is only the generic node message).</summary>
    private async Task SeedRespawnScenarioAsync(Guid teamId, Guid runId, (Guid A, Guid B) wave1, (Guid A, Guid B) wave2)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        // Explicit sequence: plan(1) → wave-1 spawn(2) → wave-2 spawn(3) — the room reads the tape in sequence order,
        // and the wave-2 detection scopes to spawns AFTER the latest plan, so the plan must precede both spawns.
        db.SupervisorDecisionRecord.Add(SupDecision(teamId, runId, 1, SupervisorDecisionKinds.Plan, "{}",
            """{"planned":[],"count":2,"phases":[{"id":"inv","title":"Investigate","subtaskIds":["sa","sb"]}]}"""));

        db.SupervisorDecisionRecord.Add(SupDecision(teamId, runId, 2, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["sa","sb"]}""",
            JsonSerializer.Serialize(new { agentCount = 2, agentRunIds = new[] { wave1.A, wave1.B } })));
        db.SupervisorDecisionRecord.Add(SupDecision(teamId, runId, 3, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["sa","sb"]}""",
            JsonSerializer.Serialize(new { agentCount = 2, agentRunIds = new[] { wave2.A, wave2.B } })));

        db.AgentRun.Add(RetryAgentRow(teamId, runId, wave1.A, AgentRunStatus.Succeeded, now));
        db.AgentRun.Add(RetryAgentRow(teamId, runId, wave1.B, AgentRunStatus.Succeeded, now));
        db.AgentRun.Add(RetryAgentRow(teamId, runId, wave2.A, AgentRunStatus.Succeeded, now));
        db.AgentRun.Add(RetryAgentRow(teamId, runId, wave2.B, AgentRunStatus.Failed, now));

        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.NodeFailed, NodeId = "sup", IterationKey = "", OccurredAt = now,
            PayloadJson = JsonSerializer.Serialize(new { error = "OpenAI API error (no-status, Transient): the request timed out before the gateway responded" }),
        });

        await db.SaveChangesAsync();
    }

    private static SupervisorDecisionRecord SupDecision(Guid teamId, Guid runId, long sequence, string kind, string payloadJson, string? outcomeJson) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
        DecisionKind = kind, IdempotencyKey = $"{kind}:{Guid.NewGuid():N}", InputHash = new string('0', 64),
        Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
        CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
    };

    [Fact]
    public async Task A_single_agent_run_surfaces_its_result_from_the_agent_output_even_without_a_supervisor()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Echo");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Print exactly: PONG", resultSummary: null);

        // A plain single-agent run: one agent node (surfaced by the ledger view) linked to its AgentRun, whose persisted
        // AgentRunResult carries the summary + a changed file. NO supervisor decisions — the tape is empty.
        await SeedAgentNodeAsync(teamId, run, summary: "Printed PONG.", changedFiles: new[] { "out.txt" });

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        var answer = turn.Blocks.OfType<FinalAnswerBlock>().Single();
        answer.Text.ShouldBe("Printed PONG.", "a single-agent run's RESULT is its own agent's summary — read from AgentRun.ResultJson, not a supervisor stop");
        answer.Attachments.ShouldContain(x => x.Label == "out.txt", "the agent's changed file rides the RESULT as an attachment");

        turn.Summary.ShouldBe("Printed PONG.", "the turn headline falls back to the sole agent's summary when there is no supervisor tape");
    }

    [Fact]
    public async Task Tool_histogram_hydrates_a_bounded_offloaded_payload_and_tolerates_an_unavailable_one()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Large tools");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Use the tools", resultSummary: null);
        await SeedAgentNodeAsync(teamId, run, summary: "Done.", changedFiles: Array.Empty<string>());

        Guid agentId;
        using (var scope = _fixture.BeginScope())
            agentId = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(a => a.WorkflowRunId == run).Select(a => a.Id).SingleAsync();

        var largePayload = JsonSerializer.SerializeToElement(new { name = "WebSearch", query = "artifact-backed", body = new string('x', ArtifactStoreConfig.DefaultInlineThresholdBytes + 500) });
        Guid corruptArtifactId;
        using (var scope = _fixture.BeginScope())
        {
            var inline = await scope.Resolve<IAgentRunService>().AppendEventAsync(agentId, new AgentEvent { Kind = AgentEventKind.ToolCall, Text = "read", Data = JsonSerializer.SerializeToElement(new { name = "Read", path = "README.md" }) }, CancellationToken.None);
            inline.DataJson.ShouldNotBeNull("the healthy inline path remains on its existing carrier");

            var appended = await scope.Resolve<IAgentRunService>().AppendEventAsync(agentId, new AgentEvent { Kind = AgentEventKind.ToolCall, Text = "searched", Data = largePayload }, CancellationToken.None);
            appended.DataArtifactId.ShouldNotBeNull("the fixture must exercise the offloaded carrier, not the inline parser");

            var corruptPayload = JsonSerializer.SerializeToElement(new { name = "Write", path = "report.md", body = new string('y', ArtifactStoreConfig.DefaultInlineThresholdBytes + 700) });
            var corrupt = await scope.Resolve<IAgentRunService>().AppendEventAsync(agentId, new AgentEvent { Kind = AgentEventKind.ToolCall, Text = "wrote", Data = corruptPayload }, CancellationToken.None);
            corruptArtifactId = corrupt.DataArtifactId!.Value;
        }

        string corruptStorageUrl;
        using (var scope = _fixture.BeginScope())
            corruptStorageUrl = (await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(a => a.Id == corruptArtifactId)).StorageUrl!;
        await File.WriteAllBytesAsync(new Uri(corruptStorageUrl).LocalPath, new byte[100]);

        using (var scope = _fixture.BeginScope())
        {
            scope.Resolve<CodeSpaceDbContext>().AgentRunEvent.Add(new AgentRunEvent
            {
                Id = Guid.NewGuid(), AgentRunId = agentId, Kind = AgentEventKind.ToolCall, Text = "unavailable",
                DataArtifactId = Guid.NewGuid(),
            });
            await scope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
        }

        var turn = (await ProjectByRunAsync(run, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single();
        var tools = turn.Blocks.OfType<StatBlock>().Single(s => s.Kind == "tools");

        tools.Detail.ShouldBe("4 calls");
        tools.Items.Single(i => i.Text == "Read").Detail.ShouldBe("1", "the pre-existing inline tool-name path is byte-compatible");
        tools.Items.Single(i => i.Text == "WebSearch").Detail.ShouldBe("1", "the bounded prefix read recovers data.name from an offloaded payload");
        tools.Items.Single(i => i.Text == "tool (payload corrupt)").Detail.ShouldBe("1", "a failed range-integrity check remains typed display data rather than a false tool name");
        tools.Items.Single(i => i.Text == "tool (payload missing)").Detail.ShouldBe("1", "an unavailable UI artifact remains a typed display bucket; projection does not fail or erase the call");
    }

    [Fact]
    public async Task Tool_histogram_caps_distinct_artifact_hydration_without_dropping_calls()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Many large tools");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Use many tools", resultSummary: null);
        await SeedAgentNodeAsync(teamId, run, summary: "Done.", changedFiles: Array.Empty<string>());

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var agentId = await db.AgentRun.AsNoTracking().Where(a => a.WorkflowRunId == run).Select(a => a.Id).SingleAsync();
            db.AgentRunEvent.AddRange(Enumerable.Range(0, 129).Select(i => new AgentRunEvent
            {
                Id = Guid.NewGuid(), AgentRunId = agentId, Kind = AgentEventKind.ToolCall, Text = $"tool-{i}", DataArtifactId = Guid.NewGuid(),
            }));
            await db.SaveChangesAsync();
        }

        var tools = (await ProjectByRunAsync(run, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single().Blocks.OfType<StatBlock>().Single(s => s.Kind == "tools");

        tools.Detail.ShouldBe("129 calls", "the display total comes from the full event count, never the hydrate budget");
        tools.Items.Single(i => i.Text == "tool (payload missing)").Detail.ShouldBe("128", "at most 128 distinct artifact prefixes are inspected per turn");
        tools.Items.Single(i => i.Text == "tool (payload not inspected)").Detail.ShouldBe("1", "calls beyond the hydrate budget stay visible and explicitly classified");
    }

    /// <summary>Seed a plain single-agent (non-supervisor) run: a node.started/completed ledger pair for the "agent" node (the workflow_run_node view surfaces it), the AgentRun wait that links the node to its run, and the AgentRun row whose persisted AgentRunResult carries the summary + changed files. No supervisor decisions.</summary>
    private async Task SeedAgentNodeAsync(Guid teamId, Guid runId, string summary, string[] changedFiles)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var agentId = Guid.NewGuid();

        db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = "node.started", NodeId = "agent", IterationKey = "", OccurredAt = now.AddSeconds(-5), PayloadJson = "{}" });
        db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = "node.completed", NodeId = "agent", IterationKey = "", OccurredAt = now, PayloadJson = "{}" });

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = runId, NodeId = "agent", IterationKey = "",
            WaitKind = WorkflowWaitKinds.AgentRun, Token = agentId.ToString(), WakeAt = now,
            Status = WorkflowWaitStatuses.Resolved, PayloadJson = "{}", CreatedAt = now,
        });

        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles };
        db.AgentRun.Add(new AgentRun
        {
            Id = agentId, TeamId = teamId, WorkflowRunId = runId, NodeId = "agent", IterationKey = "",
            Harness = "codex-cli", Status = AgentRunStatus.Succeeded, TaskJson = "{}",
            ResultJson = JsonSerializer.Serialize(result, AgentJson.Options),
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Turn_duration_anchors_on_created_date_so_a_resumed_run_reports_the_full_wall_clock()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Long run");

        // A resumed / re-dispatched run (recovered after a restart): StartedAt was reset to the FINAL leg — ~1 min before
        // completion — long after the run was created ~30 min earlier. The wall-clock must be CompletedAt − CreatedDate.
        var created = DateTimeOffset.UtcNow.AddMinutes(-30);
        var runId = await SeedTimedTurnAsync(teamId, sessionId, created, startedAt: created.AddMinutes(29), completedAt: created.AddMinutes(30));

        var room = await ProjectByRunAsync(runId, teamId);

        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single();
        turn.DurationMs.ShouldNotBeNull();
        turn.DurationMs!.Value.ShouldBeInRange(29 * 60_000L, 31 * 60_000L, "the full CompletedAt − CreatedDate (~30m), NOT CompletedAt − the reset StartedAt (~1m)");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Seed one completed turn run with EXPLICIT created / started / completed timestamps (to exercise the resume-safe wall-clock).</summary>
    private async Task<Guid> SeedTimedTurnAsync(Guid teamId, Guid sessionId, DateTimeOffset created, DateTimeOffset startedAt, DateTimeOffset completedAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal = "Long task" }),
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = created, VerifiedAt = created, NormalizedAt = created,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = 1,
            OutputsJson = JsonSerializer.Serialize(new { summary = "done", branch = (string?)null }),
            StartedAt = startedAt, CompletedAt = completedAt,
            CreatedDate = created, CreatedBy = SystemUsers.SeederId, LastModifiedDate = completedAt, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>Stamp a supervisor SPAWN decision whose folded agentResults carry per-agent changed-file paths, plus the matching AgentRun rows so the phase projection surfaces the agents (the Agents group folds them).</summary>
    private async Task SeedSpawnDecisionAsync(Guid teamId, Guid runId, params (Guid AgentRunId, string[] Files)[] agents)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        var outcome = JsonSerializer.Serialize(new
        {
            agentCount = agents.Length,
            agentRunIds = agents.Select(a => a.AgentRunId).ToArray(),
            agentResults = agents.Select(a => new { agentRunId = a.AgentRunId, status = "Succeeded", changedFiles = a.Files, summary = $"Edited {a.Files.Length} files" }).ToArray(),
        });

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        foreach (var (agentRunId, _) in agents)
            db.AgentRun.Add(new AgentRun
            {
                Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, NodeId = "sup", IterationKey = "sup",
                Harness = "codex-cli", Status = AgentRunStatus.Succeeded, TaskJson = "{}",
                CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
            });

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedMultiRepoSpawnDecisionAsync(Guid teamId, Guid runId, params RepositoryRunResult[] repositories)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var agentRunId = Guid.NewGuid();
        var primary = repositories[0];

        var result = new SupervisorAgentResult
        {
            AgentRunId = agentRunId,
            Status = "Succeeded",
            ChangedFiles = primary.ChangedFiles,
            Summary = "Edited both repositories",
            RepositoryResults = repositories,
        };
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = "{}",
            OutcomeJson = JsonSerializer.Serialize(new { agentCount = 1, agentRunIds = new[] { agentRunId }, agentResults = new[] { result } }, AgentJson.Options),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, NodeId = "sup", IterationKey = "sup",
            Harness = "codex-cli", Status = AgentRunStatus.Succeeded, TaskJson = "{}",
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return agentRunId;
    }

    [Fact]
    public async Task A_run_with_a_persisted_work_plan_projects_the_checklist_and_suppresses_the_plan_stat_rows()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Planned");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Do the thing", resultSummary: "Shipped it.");

        await SeedPlanDecisionAsync(teamId, run, "Trace DI registration", "Analyze the template store");

        // The durable plan artifact (what the S1 supervisor writer persists) — the checklist's contract source.
        using (var seed = _fixture.BeginScope())
        {
            await seed.Resolve<IWorkPlanService>().SaveVersionAsync(new WorkPlanDraft
            {
                TeamId = teamId,
                WorkflowRunId = run,
                OriginKind = WorkPlanOrigins.Supervisor,
                OriginKey = "sup#turn0",
                Goal = "Do the thing",
                Items = new[]
                {
                    new WorkPlanItem { Id = "s1", Title = "Trace DI registration", Instruction = "trace it" },
                    new WorkPlanItem { Id = "s2", Title = "Analyze the template store", Instruction = "analyze it", DependsOn = new[] { "s1" } },
                },
            }, CancellationToken.None);
        }

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        var checklist = turn.Blocks.OfType<PlanChecklistBlock>().Single();
        checklist.Version.ShouldBe(1);
        checklist.Items.Select(i => i.Title).ShouldBe(new[] { "Trace DI registration", "Analyze the template store" });
        checklist.Items[1].DependsOn.ShouldBe(new[] { 1 }, "the dependency id resolves to the 1-based ordinal");
        checklist.Items.ShouldAllBe(i => i.State == WorkPlanItemStates.Pending, "the fabricated tape staged no agents — honestly pending");

        turn.Blocks.OfType<StatBlock>().Where(b => b.Kind == "subtasks").ShouldBeEmpty("the checklist subsumes the per-round plan rows");
    }

    /// <summary>Stamp a supervisor PLAN decision (its subtask decomposition) onto a run's tape — enough for the canonical map + the subtasks stat row.</summary>
    private async Task SeedPlanDecisionAsync(Guid teamId, Guid runId, params string[] subtasks)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var payload = JsonSerializer.Serialize(new { subtasks = subtasks.Select((t, i) => new { id = $"s{i}", title = t, instruction = "do it" }).ToArray() });

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Plan, IdempotencyKey = $"plan:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = "{}",
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Stamp a supervisor STOP decision with its { stopped, outcome, summary } outcome — the terminal verb that drives the RESULT card. A non-success outcome (no-decision / no-model) marks a degraded give-up stop. <paramref name="acceptancePassed"/> folds the objective acceptance grade onto the SAME outcome bytes the terminal writer does (<c>SupervisorOutcome.AppendAcceptanceGrade</c>); null leaves the stop ungraded.</summary>
    private async Task SeedStopDecisionAsync(Guid teamId, Guid runId, string outcome, string summary, bool? acceptancePassed = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var outcomeJson = JsonSerializer.Serialize(new { stopped = true, outcome, summary });

        if (acceptancePassed is { } passed)
            outcomeJson = SupervisorOutcome.AppendAcceptanceGrade(outcomeJson, passed, detail: "2 of 7 tests failed");

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Stop, IdempotencyKey = $"stop:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}",
            OutcomeJson = outcomeJson,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Stamp the run row's durable honest <c>Outcome</c> — what the engine derives from the stop decision's own bytes at terminal time. Seeded alongside the graded stop so the fixture is the SHAPE production writes, not just the half the projector happens to read.</summary>
    private async Task StampRunOutcomeAsync(Guid runId, string outcome)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.Outcome = outcome;
        await db.SaveChangesAsync();
    }

    /// <summary>Stamp a SERVER-FORCED stop — a budget/governance/bound trip that puts {reason} on the PAYLOAD, then records an outcome with a NULL outcome label (exactly what ExecuteStop writes for a forced stop). No success outcome, only a reason.</summary>
    private async Task SeedForcedStopDecisionAsync(Guid teamId, Guid runId, string reason)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Stop, IdempotencyKey = $"stop:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = JsonSerializer.Serialize(new { reason }),
            OutcomeJson = JsonSerializer.Serialize(new { stopped = true, outcome = (string?)null, summary = (string?)null }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }

    private async Task<RoomView?> ProjectByRunAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRoomProjector>().ProjectByRunAsync(runId, teamId, CancellationToken.None);
    }

    private async Task<Guid> SeedSessionAsync(Guid teamId, string title)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.WorkSession.Add(new WorkSession { Id = id, TeamId = teamId, Title = title, Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTurnAsync(Guid teamId, Guid sessionId, int turn, string goal, string? resultSummary, WorkflowRunStatus status = WorkflowRunStatus.Success)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var outputs = string.IsNullOrEmpty(resultSummary) ? "{}" : JsonSerializer.Serialize(new { summary = resultSummary, branch = (string?)null });

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal }),
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = status, SessionId = sessionId, SessionTurnIndex = turn,
            DefinitionSnapshotJson = "{\"nodes\":[],\"edges\":[]}", DefinitionSnapshotHash = "sha256:test",
            OutputsJson = outputs,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>Park a node-grain pending decision (a flow.decision wait) on an existing run, with its stashed envelope.</summary>
    private async Task SeedNodeDecisionAsync(Guid teamId, Guid runId, string question, DateTimeOffset deadline, IReadOnlyList<DecisionOption> options)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var envelope = Envelope(question, deadline, DecisionResumeBackends.WorkflowWait, agentRunId: null, workflowRunId: runId, nodeId: "decide", options);

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = runId, NodeId = "decide", IterationKey = string.Empty,
            WaitKind = WorkflowWaitKinds.Decision, Token = Guid.NewGuid().ToString("N"), WakeAt = deadline,
            Status = WorkflowWaitStatuses.Pending, PayloadJson = JsonSerializer.Serialize(envelope, Json),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Park an agent-grain pending decision (a decision.request tool-ledger row) on a real agent run of <paramref name="runId"/>.</summary>
    private async Task SeedAgentDecisionAsync(Guid teamId, Guid runId, string question, DateTimeOffset deadline)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var agentId = Guid.NewGuid();
        db.AgentRun.Add(new AgentRun
        {
            Id = agentId, TeamId = teamId, WorkflowRunId = runId, Harness = "codex-cli",
            Status = AgentRunStatus.Running, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();   // commit the agent before the ledger row

        var ledgerId = Guid.NewGuid();
        var envelope = Envelope(question, deadline, DecisionResumeBackends.ToolLedger, agentRunId: agentId, workflowRunId: null, nodeId: null, Array.Empty<DecisionOption>());

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = ledgerId, TeamId = teamId, AgentRunId = agentId, ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision.request:{ledgerId:N}", InputHash = new string('0', 64),
            Status = ToolCallLedgerStatus.AwaitingApproval, ApprovalDeadlineAt = deadline,
            DecisionEnvelopeJson = JsonSerializer.Serialize(envelope, Json),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }

    private static DecisionRequest Envelope(string question, DateTimeOffset deadline, string grain, Guid? agentRunId, Guid? workflowRunId, string? nodeId, IReadOnlyList<DecisionOption> options) => new()
    {
        Id = Guid.NewGuid(),
        RootTraceId = Guid.NewGuid(),
        AgentRunId = agentRunId,
        WorkflowRunId = workflowRunId,
        NodeId = nodeId,
        Scope = grain == DecisionResumeBackends.ToolLedger ? DecisionScopes.Agent : DecisionScopes.Node,
        RequesterType = grain == DecisionResumeBackends.ToolLedger ? DecisionRequesterTypes.Agent : DecisionRequesterTypes.WorkflowNode,
        DecisionType = DecisionTypes.ChooseOne,
        Question = question,
        Options = options,
        RecommendedOption = options.Count > 0 ? options[0].Id : null,
        BlockingReason = "needs a human",
        RiskLevel = DecisionRiskLevels.High,
        Policy = DecisionPolicies.HumanRequired,
        TimeoutAt = deadline,
        DedupeKey = Guid.NewGuid().ToString("N"),
        ResumeBackend = grain,
    };

    /// <summary>Append N ledger records to a run (Sequence is the DB BIGSERIAL) and return the resulting MAX — the watermark.</summary>
    private async Task<long> SeedRecordsAsync(Guid runId, int count)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        for (var i = 0; i < count; i++)
            db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = "log", PayloadJson = "{}" });

        await db.SaveChangesAsync();

        return await db.WorkflowRunRecord.Where(r => r.RunId == runId).MaxAsync(r => r.Sequence);
    }

    // ─── PR-6: RoomProjector's new PublishStateAsync gating signal ───────────────────

    [Fact]
    public async Task A_terminal_run_with_a_published_branch_offers_the_OpenPullRequest_action()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Ship the feature");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Ship the feature", resultSummary: "shipped it");
        var repoId = await SeedRepositoryAsync(teamId);

        await SeedIntegrationMergeAsync(teamId, run, "codespace/integration/run/turn1");
        await StampOutputsRepositoryIdAsync(run, repoId);

        var room = await ProjectByRunAsync(run, teamId);

        var action = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1).Actions.SingleOrDefault(a => a.Kind == RoomActionKind.OpenPullRequest);

        action.ShouldNotBeNull("a terminal run with a genuinely published branch must offer the Open-PR action");
        action!.Enabled.ShouldBeTrue();
        action.Url.ShouldBeNull("no PR has been opened yet — the button reads 'Open PR', not 'View PR'");
    }

    [Fact]
    public async Task A_run_with_an_already_opened_PR_surfaces_its_link_on_the_action()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Ship the feature");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Ship the feature", resultSummary: "shipped it");
        var repoId = await SeedRepositoryAsync(teamId);

        await SeedIntegrationMergeAsync(teamId, run, "codespace/integration/run/turn1");
        await StampOutputsRepositoryIdAsync(run, repoId);

        using (var scope = _fixture.BeginScope())
        {
            await scope.Resolve<IPublishManifestStore>().UpsertForIntegrationAsync(new PublishManifestUpsert
            {
                TeamId = teamId, WorkflowRunId = run, RepositoryAlias = "primary", RepositoryId = repoId,
                Branch = "codespace/integration/run/turn1", PublishStateValue = PublishState.Pushed,
                PullRequestNumber = 42, PullRequestUrl = "https://example.test/org/repo/pull/42",
            }, CancellationToken.None);
        }

        var room = await ProjectByRunAsync(run, teamId);

        var action = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1).Actions.Single(a => a.Kind == RoomActionKind.OpenPullRequest);

        action.Enabled.ShouldBeTrue();
        action.Url.ShouldBe("https://example.test/org/repo/pull/42", "an already-opened PR's link must surface on the action so the frontend renders 'View PR', not a second Open-PR button");
    }

    [Fact]
    public async Task A_non_terminal_run_never_offers_the_OpenPullRequest_action_even_with_a_published_branch()
    {
        // RoomProjector.PublishStateAsync short-circuits on WorkflowRunState.IsTerminal BEFORE reading the ledger or
        // the manifest — a running/suspended turn must never show the button, matching IRoomPullRequestService's own
        // hard terminal-status gate (a hidden-dependency sweep finding: a mid-run frontier can still move).
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Ship the feature");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Ship the feature", resultSummary: null, status: WorkflowRunStatus.Suspended);
        var repoId = await SeedRepositoryAsync(teamId);

        await SeedIntegrationMergeAsync(teamId, run, "codespace/integration/run/turn1");
        await StampOutputsRepositoryIdAsync(run, repoId);

        var room = await ProjectByRunAsync(run, teamId);

        room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1).Actions.ShouldNotContain(a => a.Kind == RoomActionKind.OpenPullRequest);
    }

    [Fact]
    public async Task A_ledger_direct_published_run_also_offers_the_OpenPullRequest_action()
    {
        // DC-3 (task_8008ae86): run 96695645's own motivating scenario — a single accepted agent already pushed to
        // a manifest row, no merge/integration decision ever ran. Before DC-3, PublishStateAsync read ONLY the
        // merge-derived tape and reported HasPublishedBranch=false — the button stayed disabled for a run that
        // genuinely already published real work.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Ship the feature");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Ship the feature", resultSummary: "shipped it");
        var repoId = await SeedRepositoryAsync(teamId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnDecisionAsync(teamId, run, (agentRunId, new[] { "a.txt" }));

        using (var scope = _fixture.BeginScope())
        {
            await scope.Resolve<IPublishManifestStore>().UpsertForAgentRunAsync(agentRunId, new PublishManifestUpsert
            {
                TeamId = teamId, WorkflowRunId = run, RepositoryAlias = "primary", RepositoryId = repoId,
                Branch = "codespace/agent/fix", ChangedFileCount = 1, PublishStateValue = PublishState.Pushed,
            }, CancellationToken.None);
        }

        var room = await ProjectByRunAsync(run, teamId);

        var action = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1).Actions.SingleOrDefault(a => a.Kind == RoomActionKind.OpenPullRequest);

        action.ShouldNotBeNull("a ledger-direct published run (no merge at all) must still offer the Open-PR action");
        action!.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task A_pr_opened_outside_any_workflow_node_still_surfaces_a_delivery_card()
    {
        // DC-3 (the 4th reader): the Room's terracotta PR card previously read ONLY a git.open_pr WORKFLOW NODE's
        // own output — a PR opened via the Room's own Open-PR button (or a server-authored delivery step) never
        // ran that node, so the card stayed silent even though the run's own "View PR" link worked fine.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Ship the feature");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Ship the feature", resultSummary: "shipped it");
        var repoId = await SeedRepositoryAsync(teamId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnDecisionAsync(teamId, run, (agentRunId, new[] { "a.txt" }));

        using (var scope = _fixture.BeginScope())
        {
            var manifests = scope.Resolve<IPublishManifestStore>();

            await manifests.UpsertForAgentRunAsync(agentRunId, new PublishManifestUpsert
            {
                TeamId = teamId, WorkflowRunId = run, RepositoryAlias = "primary", RepositoryId = repoId,
                Branch = "codespace/agent/fix", ChangedFileCount = 1, PublishStateValue = PublishState.Pushed,
            }, CancellationToken.None);

            await manifests.UpsertForIntegrationAsync(new PublishManifestUpsert
            {
                TeamId = teamId, WorkflowRunId = run, RepositoryAlias = "primary", RepositoryId = repoId,
                Branch = "codespace/agent/fix", PublishStateValue = PublishState.Pushed,
                PullRequestNumber = 42, PullRequestUrl = "https://example.test/org/repo/pull/42",
            }, CancellationToken.None);
        }

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        var delivery = turn.Blocks.OfType<DeliveryBlock>().Single();
        delivery.Reference.ShouldBe("#42");
        delivery.Url.ShouldBe("https://example.test/org/repo/pull/42");
        delivery.BranchHead.ShouldBe("codespace/agent/fix");
        // BranchBase has NO manifest-row fallback (RoomProjector.DeliveryFromManifestAsync reads it ONLY off the
        // resolver's own join) — a non-null value here is the genuine proof the resolver join fired, since
        // BranchHead alone can't distinguish "joined off the resolver" from "read straight off the bare manifest".
        delivery.BranchBase.ShouldBe("main", "the repository's own default branch — only ever populated via the resolver's join, never the manifest row directly");
    }

    private async Task SeedIntegrationMergeAsync(Guid teamId, Guid runId, string integratedBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var outcome = JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch, appliedCount = 1, reason = (string?)null, excludedAgents = Array.Empty<string>() } });

        db.SupervisorDecisionRecord.Add(SupDecision(teamId, runId, 1, SupervisorDecisionKinds.Merge, "{}", outcome));
        await db.SaveChangesAsync();
    }

    private async Task StampOutputsRepositoryIdAsync(Guid runId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.OutputsJson = JsonSerializer.Serialize(new { repositoryId = repositoryId.ToString() });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "local", BaseUrl = $"https://local-{suffix}" });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId,
            ExternalId = $"ext-{suffix}", NamespacePath = "org", Name = "repo", FullPath = $"org/repo-{suffix}",
            DefaultBranch = "main", WebUrl = $"https://local-{suffix}/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private sealed class SupervisorTapeReadRecorder : DbCommandInterceptor
    {
        public int Reads { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            if (command.CommandText.Contains("supervisor_decision", StringComparison.OrdinalIgnoreCase)) Reads++;
        }
    }
}
