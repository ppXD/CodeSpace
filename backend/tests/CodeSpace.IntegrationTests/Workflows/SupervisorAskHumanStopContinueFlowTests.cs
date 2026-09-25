using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Stop then Continue while a supervisor turn is asking a human. The question — its card, when the run has a
/// conversation — its wait and the ask's decision record commit as one transaction, behind the same share lock on the run
/// at the walk's claimed generation as the spawn wave. A turn the Continue overtook writes none of the three once the
/// revive has moved the run on; a Continue landing while a question is being asked waits for all three, then closes the
/// wait like any other of the stopped attempt's, to be re-opened, same question, by the revived run. Either way the
/// question anyone can see has its token on the decision tape, which is what the run's ask API answers from.
///
/// <para>Fidelity 🟢 high: the REAL turn service, executor, chat bot, message service and ask answer service, the REAL
/// cancel and Continue, the REAL engine for the revived walk, over real Postgres; the scripted decider stands in for the
/// model. The overtaken turn is driven through the turn service directly, under its walk's claim on the run's
/// generation, because the supervisor node resolves its own scope from the root container where a test's hold on the bot
/// or the decision log cannot reach; its node start is recorded the way that walk's engine records it.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorAskHumanStopContinueFlowTests : IDisposable
{
    private const string Goal = "ship the feature";

    private readonly PostgresFixture _fixture;

    public SupervisorAskHumanStopContinueFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().AskHumanStop();   // turn 0 asks, turn 1 stops echoing the answer
    }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task An_ask_a_continue_overtook_before_its_question_posts_no_card_parks_no_wait_and_records_nothing()
    {
        // The overtaken turn had begun its ask before the stop, and reached its card only after the Continue. Card and wait
        // were written unfenced: a person could answer a question the revived run never asked, and the revived supervisor
        // re-parked on that wait as its own.
        var (teamId, userId, conversationId) = await SeedTeamWithConversationAsync();
        var runId = await SeedAskRunAsync(teamId, userId, conversationId);

        using var manual = ResolveJobClient().ManualExecution();

        await RecordSupervisorStartedAsync(runId);

        var begun = new DecisionLogHold(DecisionLogStep.AfterBegin);   // the ask claimed and begun, its question not yet asked
        var overtakenTurn = RunOvertakenTurnInBackground(runId, teamId, conversationId, begun.Decorate);

        try
        {
            await StopContinueSignals.AwaitAsync(begun.Reached.Task, "the overtaken turn beginning its ask");

            await StopAsync(runId, teamId);
            await ContinueAsync(runId, teamId);
        }
        finally
        {
            begun.Release.TrySetResult();
        }

        (await Record.ExceptionAsync(() => StopContinueSignals.AwaitAsync(overtakenTurn, "the overtaken turn returning"))).ShouldBeOfType<RunSupersededException>("the overtaken turn stood down at its question's fence");
        (await CardCountAsync(conversationId)).ShouldBe(0, "it posted no card");
        (await AskWaitsAsync(runId)).ShouldBeEmpty("parked no wait");
        (await AskDecisionAsync(runId, teamId)).Status.ShouldBe(SupervisorDecisionStatus.Running, "and recorded nothing: the ask it began before the stop is still in flight, for the revived walk to finish");

        await RunEngineAsync(runId);   // the revived walk finishes the ask: its own card, its own wait, its own record

        (await CardCountAsync(conversationId)).ShouldBe(1, "exactly one card — the revived run's");
        var wait = (await AskWaitsAsync(runId)).ShouldHaveSingleItem("exactly one wait");
        wait.Status.ShouldBe(WorkflowWaitStatuses.Pending, "and the revived run is parked on it");
        SupervisorOutcome.ReadHumanWaitToken((await AskDecisionAsync(runId, teamId)).OutcomeJson).ShouldBe(wait.Token, "the ask's record names that question");
        (await NodeFailuresAsync(runId)).ShouldBe(0, "standing down is not recorded as the supervisor step failing");

        await AnswerThroughTheAskApiAsync(runId, teamId, userId, "patch it");
    }

    [Fact]
    public async Task A_continue_landing_while_an_ask_posts_its_card_waits_for_it_and_the_revived_run_answers_from_that_card()
    {
        // The Continue lands after the overtaken ask took its fence, while its card is being posted. The ask's record used to
        // be written after the card, on its own: the revive's bump, let through once the card committed, refused it, the
        // decision stayed in flight with no question on it, and the revived run re-parked on the card's re-opened wait —
        // where the run's ask API, reading the question off the tape, found nothing to answer.
        var (teamId, userId, conversationId) = await SeedTeamWithConversationAsync();
        var runId = await SeedAskRunAsync(teamId, userId, conversationId);

        using var manual = ResolveJobClient().ManualExecution();

        await RecordSupervisorStartedAsync(runId);
        await StopAsync(runId, teamId);   // the stop lands first; the overtaken turn, on another host, asks on

        var posting = new HeldBot();   // inside the question's fenced transaction
        var overtakenTurn = RunOvertakenTurnInBackground(runId, teamId, conversationId, b => b.RegisterDecorator<IChatBotService>((_, _, inner) => posting.Wrap(inner)));

        await ContinueWhileTheQuestionIsAskedAsync(runId, teamId, posting.Reached.Task, posting.Release, "the overtaken turn posting its card under its fence");

        (await Record.ExceptionAsync(() => StopContinueSignals.AwaitAsync(overtakenTurn, "the overtaken turn returning"))).ShouldBeNull("its card, wait and record committed together before the bump — nothing of the turn was left to refuse");

        var closed = (await AskWaitsAsync(runId)).ShouldHaveSingleItem("the card's wait committed with it");
        closed.Status.ShouldBe(WorkflowWaitStatuses.Discarded, "and the revive, let through after it, closed it");
        SupervisorOutcome.ReadHumanWaitToken((await AskDecisionAsync(runId, teamId)).OutcomeJson).ShouldBe(closed.Token, "the ask's record committed with its card, naming it");

        await RunEngineAsync(runId);   // the revived walk re-opens the posted question and parks on it

        var reopened = (await AskWaitsAsync(runId)).ShouldHaveSingleItem();
        reopened.Id.ShouldBe(closed.Id, "the same wait");
        reopened.Status.ShouldBe(WorkflowWaitStatuses.Pending, "re-opened, so the card already posted answers it");
        (await CardCountAsync(conversationId)).ShouldBe(1, "no second card was posted");
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the revived run is parked on the question");
        (await NodeFailuresAsync(runId)).ShouldBe(0, "neither walk recorded the supervisor step failing");

        await AnswerThroughTheAskApiAsync(runId, teamId, userId, "patch it");
    }

    [Fact]
    public async Task A_gate_question_asked_with_no_conversation_as_a_continue_lands_stays_answerable_through_the_ask_api()
    {
        // A gate question in a run with no conversation posts no card: its wait is answered only through the run's ask API,
        // which reads the question off the decision tape. The ask's record, written apart from its wait, was refused behind
        // the revive's bump; the revived run re-parked on the re-opened wait with no question on the tape, and nothing could
        // ever answer it — the run stuck, and a stop and Continue asked the same way again.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedAskRunAsync(teamId, userId, conversationId: null);

        using var manual = ResolveJobClient().ManualExecution();

        await RecordSupervisorStartedAsync(runId);
        await StopAsync(runId, teamId);   // the stop lands first; the overtaken turn, on another host, asks on
        ResolveScript().DecideNext(runId, ScriptedSupervisorDecider.GateAskOf(GateQuestion));

        var recording = new DecisionLogHold(DecisionLogStep.BeforeTerminal);   // its wait staged, its record about to be written
        var overtakenTurn = RunOvertakenTurnInBackground(runId, teamId, conversationId: null, recording.Decorate);

        await ContinueWhileTheQuestionIsAskedAsync(runId, teamId, recording.Reached.Task, recording.Release, "the overtaken turn recording its gate question under its fence");

        (await Record.ExceptionAsync(() => StopContinueSignals.AwaitAsync(overtakenTurn, "the overtaken turn returning"))).ShouldBeNull("its wait and record committed together before the bump");

        var closed = (await AskWaitsAsync(runId)).ShouldHaveSingleItem("the question's wait committed with its record");
        closed.Status.ShouldBe(WorkflowWaitStatuses.Discarded, "and the revive closed it");

        await RunEngineAsync(runId);   // the revived walk re-opens the question and parks on it

        (await AskWaitsAsync(runId)).ShouldHaveSingleItem().Status.ShouldBe(WorkflowWaitStatuses.Pending, "the same question, re-opened");

        await AnswerThroughTheAskApiAsync(runId, teamId, userId, "publish it");
    }

    /// <summary>
    /// Land a Continue while an overtaken turn is held inside its question's fenced transaction: it blocks on the share
    /// lock that transaction holds on the run row, and returns once the question committed.
    /// </summary>
    private async Task ContinueWhileTheQuestionIsAskedAsync(Guid runId, Guid teamId, Task held, TaskCompletionSource release, string heldSignal)
    {
        using var continueScope = _fixture.BeginScope();
        Task<bool>? revive = null;

        try
        {
            await StopContinueSignals.AwaitAsync(held, heldSignal);

            var continueDb = continueScope.Resolve<CodeSpaceDbContext>();
            await continueDb.Database.OpenConnectionAsync();
            var continuePid = await continueDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

            revive = continueScope.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None);
            await StopContinueSignals.WaitForLockWaitAsync(_fixture, continuePid, "the Continue's revive to block on the question's share lock on the run row", revive);
        }
        finally
        {
            release.TrySetResult();
        }

        await StopContinueSignals.AwaitAsync(revive, "the Continue returning once the question committed");
        (await revive).ShouldBeTrue("the run continued in place");
    }

    /// <summary>
    /// Answer the run's newest ask through the run's ask API — which reads the question off the decision tape, not the
    /// wait — and walk the revived run on: the answer folds into the ask, and the next turn stops on it.
    /// </summary>
    private async Task AnswerThroughTheAskApiAsync(Guid runId, Guid teamId, Guid userId, string answer)
    {
        SupervisorAskAnswerOutcome? answered;
        using (var scope = _fixture.BeginScope())
            answered = await scope.Resolve<ISupervisorAskAnswerService>().AnswerAsync(runId, teamId, userId, answer, decision: null, CancellationToken.None);

        answered.ShouldNotBeNull("the run's ask API found the question on the decision tape").Resumed.ShouldBeTrue("and its answer resumed the run");

        await RunEngineAsync(runId);   // the answered ask folds, and turn 1 stops on it

        SupervisorOutcome.ReadAskHumanAnswer((await AskDecisionAsync(runId, teamId)).OutcomeJson).ShouldBe(answer, "the answer folded into the ask");
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success, "and the run completed on it");
    }

    /// <summary>The overtaken walk's turn, on another host: the REAL turn service under that walk's claim on the run's current generation, in a scope with <paramref name="hold"/> registered.</summary>
    private Task RunOvertakenTurnInBackground(Guid runId, Guid teamId, Guid? conversationId, Action<ContainerBuilder> hold) => Task.Run(async () =>
    {
        var generation = await GenerationAsync(runId);

        using var scope = _fixture.BeginScope(hold);
        using var claim = RunGenerationFence.Claim(runId, generation);

        await scope.Resolve<ISupervisorTurnService>().RunTurnAsync(runId, teamId, "sup", Goal, conversationId, GoalConfig(conversationId), CancellationToken.None);
    });

    /// <summary>The overtaken walk started the supervisor step before the stop — recorded the way its engine records a node start, so the Continue finds the step to resume.</summary>
    private async Task RecordSupervisorStartedAsync(Guid runId)
    {
        var none = new Dictionary<string, JsonElement>();

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IRunRecordLogger>().NodeStartedAsync(runId, "sup", WorkflowIterationKeys.TopLevel, none, none, CancellationToken.None);
    }

    private async Task<(Guid TeamId, Guid UserId, Guid ConversationId)> SeedTeamWithConversationAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var slug = "sup-ask-stop-" + Guid.NewGuid().ToString("N")[..8];
        var conversationId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, userId, CancellationToken.None);

        return (teamId, userId, conversationId);
    }

    private async Task<Guid> SeedAskRunAsync(Guid teamId, Guid userId, Guid? conversationId)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "sup-ask-stop-" + Guid.NewGuid().ToString("N")[..6],
                Description = null,
                Definition = SupervisorDefinition(conversationId),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task StopAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue("the run was stopped");
    }

    private async Task ContinueAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None)).ShouldBeTrue("the stopped run continues in place");
    }

    private async Task<int> GenerationAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Generation).SingleAsync();
    }

    private async Task<int> CardCountAsync(Guid conversationId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().Message.AsNoTracking().IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationId && m.InteractionJson != null && m.DeletedDate == null);
    }

    private async Task<IReadOnlyList<WorkflowRunWait>> AskWaitsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action).ToListAsync();
    }

    private async Task<SupervisorDecisionRecord> AskDecisionAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.AskHuman);
    }

    private async Task<WorkflowRunStatus> RunStatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
    }

    private async Task<int> NodeFailuresAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId && r.NodeId == "sup" && (r.RecordType == WorkflowRunRecordTypes.NodeFailed || r.RecordType == WorkflowRunRecordTypes.AttemptFailed));
    }

    private SupervisorDecisionScript ResolveScript()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<SupervisorDecisionScript>();
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    /// <summary>A server-authored gate question's shape (the pinned I3 publish-gate prefix): with no conversation it parks on its wait instead of degrading.</summary>
    private const string GateQuestion = "I3 publish gate: the run has accepted work that could not be published — a human must resolve this";

    private static string SupervisorConfig(Guid? conversationId) => conversationId is { } id ? $$"""{"goal":"{{Goal}}","conversationId":"{{id}}"}""" : $$"""{"goal":"{{Goal}}"}""";

    /// <summary>The goal config the supervisor node reads off its own config, for a turn driven through the turn service directly.</summary>
    private static SupervisorGoalConfig? GoalConfig(Guid? conversationId) => Core.Services.Workflows.Nodes.Builtin.AgentSupervisorNode.ReadGoalConfig(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SupervisorConfig(conversationId))!);

    // manual → sup (agent.supervisor, asking into a team conversation) → terminal. Shadow completion, as the ask suites run
    // it: the scripted stop stakes no contract, and completion arbitration is not this suite's subject.
    private static WorkflowDefinition SupervisorDefinition(Guid? conversationId) => new()
    {
        SchemaVersion = 1,
        CompletionMode = WorkflowDefinition.CompletionModeShadow,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(SupervisorConfig(conversationId)), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };

    /// <summary>The real chat bot with its card post held, once — the post runs inside the question's fenced transaction.</summary>
    private sealed class HeldBot
    {
        private int _fired;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IChatBotService Wrap(IChatBotService inner) => new Held(inner, this);

        private async Task HoldAsync()
        {
            if (Interlocked.Exchange(ref _fired, 1) == 1) return;

            Reached.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }

        private sealed class Held(IChatBotService inner, HeldBot hold) : IChatBotService
        {
            public Task<Guid> GetOrCreateTeamBotAsync(Guid teamId, CancellationToken cancellationToken) => inner.GetOrCreateTeamBotAsync(teamId, cancellationToken);

            public async Task<MessageView> PostAsBotAsync(Guid conversationId, string body, MessageInteraction? interaction, CancellationToken cancellationToken)
            {
                await hold.HoldAsync().ConfigureAwait(false);
                return await inner.PostAsBotAsync(conversationId, body, interaction, cancellationToken).ConfigureAwait(false);
            }

            public Task<bool> ConversationBelongsToTeamAsync(Guid conversationId, Guid teamId, CancellationToken cancellationToken) => inner.ConversationBelongsToTeamAsync(conversationId, teamId, cancellationToken);
        }
    }
}
