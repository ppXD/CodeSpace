using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 THE PR-E E4 CROWN JEWEL (high fidelity — REAL engine + REAL <see cref="SupervisorTurnService"/> +
/// <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> + REAL
/// <see cref="IChatBotService"/> card post + REAL <see cref="IWorkflowResumeService.ResumeByActionTokenAsync"/>
/// answer resume over real Postgres; the scripted decider stands in for the LLM). ask_human is wired as the
/// THIRD park path — a mid-loop HUMAN checkpoint:
/// <list type="bullet">
///   <item>a flag-on <c>agent.supervisor</c> run with a conversation: turn 0 = ask_human("which approach?")
///         posts a question CARD into the run's OWN team conversation + parks on a SINGLE <c>Action</c> wait
///         keyed <c>sup#turn0#ask</c> (the run is Suspended); a human's answer via the real resume path resolves
///         the wait → the supervisor re-enters → turn 1 sees the answer FOLDED into its context → (scripted)
///         stop → Success. The decision ledger records ask_human; the stop summary ECHOES the human's answer.</item>
///   <item>RESTART-WHILE-PARKED: drive to the ask park, then re-dispatch the Suspended run (the crash-recovery
///         path) — it re-parks on the SAME Action wait with NO duplicate question card posted, then the answer
///         resumes it.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorAskHumanFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string? _flagBefore;

    public SupervisorAskHumanFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _flagBefore = Environment.GetEnvironmentVariable(SupervisorLane.EnabledEnvVar);
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().AskHumanStop();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, _flagBefore);
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task Supervisor_asks_a_human_parks_then_the_answer_resumes_and_folds_into_the_next_turn()
    {
        var (teamId, userId, conversationId) = await SeedTeamWithConversationAsync();
        var runId = await CreateSupervisorRunAsync(teamId, userId, conversationId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();

        // ── Turn 0: ask_human → post a card + park on the single Action wait. ──
        await RunEngineAsync(runId);

        string token;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the ask_human turn parks on the human's answer — NOT a self-advance, NOT the agent barrier");

            var askWait = await db.WorkflowRunWait.AsNoTracking()
                .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending);
            askWait.IterationKey.ShouldBe("sup#turn0#ask", "the per-turn ask key <nodeId>#turn{N}#ask");
            token = askWait.Token;

            // The decision ledger recorded the ask_human (with the question + token), still awaiting its answer.
            var ask = (await Ledger(db, runId, teamId)).Single(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman);
            SupervisorOutcome.ReadAskHumanQuestion(ask.OutcomeJson).ShouldBe(ScriptedSupervisorDecider.AskQuestion);
            SupervisorOutcome.ReadHumanWaitToken(ask.OutcomeJson).ShouldBe(token, "the outcome records the card token (so a replay re-parks without re-posting)");
            SupervisorOutcome.ReadAskHumanAnswer(ask.OutcomeJson).ShouldBeNull("no answer folded yet — the human hasn't replied");

            // The question CARD was posted into the run's OWN team conversation (interactive, bot-authored).
            var card = await db.Message.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(m => m.ConversationId == conversationId && m.TeamId == teamId && m.InteractionJson != null && m.DeletedDate == null);
            card.Body.ShouldContain(ScriptedSupervisorDecider.AskQuestion, customMessage: "the card body carries the supervisor's question");
        }

        // ── The human answers via the REAL resume path (token-correlated, team-scoped, authenticated). ──
        await AnswerAsync(token, "patch it", userId, teamId);

        // The immediate single-wait resume re-dispatched the run; drive the engine to run turn 1.
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "the human's answer resumed the supervisor → turn 1 stop → the run completes");

            (await LedgerKinds(runId, teamId)).ShouldBe(
                new[] { SupervisorDecisionKinds.AskHuman, SupervisorDecisionKinds.Stop },
                "the ledger has ask_human then stop in Sequence order");

            // The ask_human outcome now carries the human's answer (folded from the resolved wait) — durable.
            var ask = (await Ledger(db, runId, teamId)).Single(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman);
            SupervisorOutcome.ReadAskHumanAnswer(ask.OutcomeJson).ShouldBe("patch it", "the ledger records ask_human WITH the answer folded into its outcome");

            // The stop decision PROVES the answer reached the next turn's context (the decider echoed it).
            var stop = (await Ledger(db, runId, teamId)).Single(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
            JsonDocument.Parse(stop.OutcomeJson!).RootElement.GetProperty("summary").GetString()
                .ShouldBe("human said: patch it", "turn 1's decider saw the folded answer in its context");

            // Exactly one question card was ever posted.
            (await db.Message.AsNoTracking().IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationId && m.InteractionJson != null && m.DeletedDate == null))
                .ShouldBe(1, "exactly one question card — no duplicate ask");
        }
    }

    [Fact]
    public async Task A_restart_while_parked_re_parks_on_the_same_wait_with_no_duplicate_question()
    {
        var (teamId, userId, conversationId) = await SeedTeamWithConversationAsync();
        var runId = await CreateSupervisorRunAsync(teamId, userId, conversationId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();

        // Drive to the ask park: turn 0 ask_human posts a card + parks on the Action wait.
        await RunEngineAsync(runId);

        string token;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            token = (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending)).Token;
            (await db.Message.AsNoTracking().IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationId && m.InteractionJson != null && m.DeletedDate == null)).ShouldBe(1, "one card posted on the ask");
        }

        // SIMULATE A RESTART WHILE PARKED: the human hasn't answered. Re-dispatch the still-Suspended run as a
        // crash recovery would. The node re-enters: the ask_human decision is a SETTLED ledger row, but its Action
        // wait is STILL pending → the human re-entry guard re-parks on the SAME wait, never advances + never
        // re-posts the question. (The executor's own re-park guard backs this up at the turn-service layer.)
        using (var scope = _fixture.BeginScope())
        {
            await scope.Resolve<CodeSpaceDbContext>().WorkflowRun
                .Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Enqueued));
        }
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the run re-suspended on the same ask wait (still pending)");

            var waits = await db.WorkflowRunWait.AsNoTracking()
                .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending).ToListAsync();
            waits.Count.ShouldBe(1, "exactly the original ask wait remains — none leaked, none duplicated");
            waits[0].Token.ShouldBe(token, "the same token — the human answers the SAME card");

            (await db.Message.AsNoTracking().IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationId && m.InteractionJson != null && m.DeletedDate == null))
                .ShouldBe(1, "NO duplicate question — the restart re-parked on the existing wait, never re-posted");

            (await Ledger(db, runId, teamId)).Count(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman)
                .ShouldBe(1, "exactly one ask_human decision row — the claim hop deduped the replay");
        }

        // The answer still resumes the re-parked run end-to-end.
        await AnswerAsync(token, "rewrite", userId, teamId);
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "the answer resumed the re-parked run → stop → Success");

            var stop = (await Ledger(db, runId, teamId)).Single(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
            JsonDocument.Parse(stop.OutcomeJson!).RootElement.GetProperty("summary").GetString().ShouldBe("human said: rewrite");
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private async Task AnswerAsync(string token, string answer, Guid actorUserId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IWorkflowResumeService>()
            .ResumeByActionTokenAsync(token, RealSupervisorActionExecutor.AnswerActionKey, actorUserId, answer, values: null, teamId, CancellationToken.None);

        result.ShouldBe(ActionResumeResult.Resumed, "the human's answer resolves the supervisor's ask wait via the existing token-correlated resume path");
    }

    private async Task<IReadOnlyList<string>> LedgerKinds(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        return (await Ledger(verify.Resolve<CodeSpaceDbContext>(), runId, teamId)).Select(r => r.DecisionKind).ToList();
    }

    private static async Task<IReadOnlyList<Core.Persistence.Entities.SupervisorDecisionRecord>> Ledger(CodeSpaceDbContext db, Guid runId, Guid teamId) =>
        await db.SupervisorDecisionRecord.AsNoTracking().Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderBy(d => d.Sequence).ToListAsync();

    private async Task<(Guid TeamId, Guid UserId, Guid ConversationId)> SeedTeamWithConversationAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var slug = "sup-ask-" + Guid.NewGuid().ToString("N")[..8];
        var conversationId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, userId, CancellationToken.None);

        return (teamId, userId, conversationId);
    }

    private async Task<Guid> CreateSupervisorRunAsync(Guid teamId, Guid userId, Guid conversationId)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId, conversationId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, Guid conversationId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-ask-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(conversationId),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    // manual → sup (agent.supervisor, with an ask_human conversation) → terminal
    private static WorkflowDefinition SupervisorDefinition(Guid conversationId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json($$"""{"goal":"ship the feature","conversationId":"{{conversationId}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };
}
