using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Callbacks;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Engine v2 Phase 1.2c — external callback. <c>flow.wait_callback</c> parks the run on a
/// Callback wait (the engine mints a token); an external system POSTs to the tokened URL (the
/// anonymous <c>ResumeWorkflowCallbackCommand</c> the controller dispatches), which resolves the
/// wait with the posted body + resumes. The node surfaces the body as its <c>body</c> output.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CallbackFlowTests
{
    private readonly PostgresFixture _fixture;

    public CallbackFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Callback_token_resumes_the_run_and_surfaces_the_posted_body()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await ParkOnCallbackAsync(teamId, userId);

        WorkflowRunWait wait;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.Callback);
            wait.WakeAt.ShouldBeNull("a callback wait has no timer — it wakes only on the external POST");
        }

        // What an operator with runs.control reads off the run page.
        var token = TokenOf(await RevealAsync(runId, wait.Id, teamId, userId));

        token.Length.ShouldBe(32, "the callback route admits exactly {token:length(32)}");
        wait.TokenDigest.ShouldBe(WorkflowCallbackTokenSealer.Digest(token), "the digest a resume looks the wait up by");
        wait.TokenCiphertext.ShouldNotBeNull("the reveal reads the token back from its ciphertext");
        wait.TokenCiphertext.ShouldNotContain(token);
        // Expand step: the token column keeps the token so a pod on the previous build, which compares it verbatim, still
        // resumes this URL during the rollout and after a rollback. The follow-up release blanks it — flip this then.
        wait.Token.ShouldBe(token);

        // The external POST — the command the anonymous callbacks controller dispatches.
        bool resumed;
        using (var scope = _fixture.BeginScope())
            resumed = await scope.Resolve<IMediator>().Send(new ResumeWorkflowCallbackCommand
            {
                Token = token,
                Body = """{"status":"done","score":7}""",
            });
        resumed.ShouldBeTrue();

        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "callback");
            var body = JsonDocument.Parse(node.OutputsJson).RootElement.GetProperty("body");
            body.GetProperty("status").GetString().ShouldBe("done", "the posted body is surfaced as the node's `body` output");
            body.GetProperty("score").GetInt32().ShouldBe(7);
        }
    }

    [Fact]
    public async Task The_stored_digest_presented_as_a_token_does_not_resume()
    {
        // Whoever reads the row (a backup, a replica) holds the digest; it must not be a key to the wait it belongs to.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await ParkOnCallbackAsync(teamId, userId);
        var wait = await LoadWaitAsync(runId);

        using var scope = _fixture.BeginScope();
        var resumed = await scope.Resolve<IMediator>().Send(new ResumeWorkflowCallbackCommand { Token = wait.TokenDigest!, Body = "{}" });

        resumed.ShouldBeFalse();
    }

    [Fact]
    public async Task Reveal_answers_404_for_a_run_outside_the_callers_team()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await ParkOnCallbackAsync(teamId, userId);
        var wait = await LoadWaitAsync(runId);

        await Should.ThrowAsync<KeyNotFoundException>(() => RevealAsync(runId, wait.Id, otherTeamId, userId));
    }

    [Fact]
    public async Task Reveal_answers_404_once_the_wait_is_no_longer_pending()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await ParkOnCallbackAsync(teamId, userId);
        var wait = await LoadWaitAsync(runId);
        var token = TokenOf(await RevealAsync(runId, wait.Id, teamId, userId));

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IMediator>().Send(new ResumeWorkflowCallbackCommand { Token = token, Body = "{}" })).ShouldBeTrue();

        await Should.ThrowAsync<KeyNotFoundException>(() => RevealAsync(runId, wait.Id, teamId, userId));
    }

    [Fact]
    public async Task Unknown_callback_token_does_not_resume()
    {
        using var scope = _fixture.BeginScope();
        var resumed = await scope.Resolve<IMediator>().Send(new ResumeWorkflowCallbackCommand
        {
            Token = "deadbeefdeadbeefdeadbeefdeadbeef",
            Body = "{}",
        });
        resumed.ShouldBeFalse("an unknown / already-used token matches no pending callback wait → 404 at the controller");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> ParkOnCallbackAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId, CallbackDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        return runId;
    }

    private async Task<WorkflowRunWait> LoadWaitAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
    }

    private async Task<WorkflowCallbackUrl> RevealAsync(Guid runId, Guid waitId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new RevealWorkflowCallbackUrlCommand { RunId = runId, WaitId = waitId });
    }

    private static string TokenOf(WorkflowCallbackUrl url)
    {
        url.Path.ShouldStartWith(WorkflowCallbackUrlService.RoutePrefix);
        return url.Path[WorkflowCallbackUrlService.RoutePrefix.Length..];
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "callback-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private static WorkflowDefinition CallbackDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "callback", TypeKey = "flow.wait_callback", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "callback" },
            new() { From = "callback", To = "end" },
        },
    };
}
