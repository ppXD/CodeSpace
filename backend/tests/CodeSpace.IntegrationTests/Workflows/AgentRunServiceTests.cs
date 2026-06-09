using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Drives the REAL AgentRunService (resolved through CodeSpaceModule's DI, proving it's registered)
/// against real Postgres across a full lifecycle — create → running → append events → complete, then
/// reads back run + events via the live cursor — plus the guards: an illegal transition and a
/// non-terminal completion status each throw, and re-running an already-running run is rejected.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunServiceTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunServiceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Full_lifecycle_create_run_append_events_complete()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Queued);
            runId = run.Id;
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            await svc.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.CommandExecuted, Text = "npm test", Data = JsonSerializer.SerializeToElement(new { command = "npm test", exitCode = 0 }) }, CancellationToken.None);
            await svc.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "Fixed the failing tests." }, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "Fixed.", ChangedFiles = new[] { "src/a.ts" } }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();

            var run = await svc.GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded);
            run.StartedAt.ShouldNotBeNull();
            run.CompletedAt.ShouldNotBeNull();
            run.ResultJson.ShouldNotBeNull();
            run.ResultJson!.ShouldContain("completed");

            var events = await svc.GetEventsAsync(runId, 0, CancellationToken.None);
            events.Select(e => e.Kind).ShouldBe(new[] { AgentEventKind.CommandExecuted, AgentEventKind.AssistantMessage });
            events[0].DataJson.ShouldNotBeNull();
            events[0].DataJson!.ShouldContain("npm test");

            // cursor: events strictly after the first
            var tail = await svc.GetEventsAsync(runId, events[0].Sequence, CancellationToken.None);
            tail.Select(e => e.Kind).ShouldBe(new[] { AgentEventKind.AssistantMessage });
        }
    }

    [Fact]
    public async Task Completing_a_queued_run_is_illegal()
    {
        var teamId = await SeedTeamAsync();
        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None);

        // Queued → Succeeded is illegal — a run can't succeed without running.
        await Should.ThrowAsync<AgentRunTransitionException>(() =>
            svc.CompleteAsync(run.Id, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, CancellationToken.None));
    }

    [Fact]
    public async Task Completing_with_a_nonterminal_status_is_rejected()
    {
        var teamId = await SeedTeamAsync();
        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None);
        await svc.MarkRunningAsync(run.Id, CancellationToken.None);

        await Should.ThrowAsync<AgentRunTransitionException>(() =>
            svc.CompleteAsync(run.Id, new AgentRunResult { Status = AgentRunStatus.Running, ExitReason = "still going" }, CancellationToken.None));
    }

    [Fact]
    public async Task Re_running_an_already_running_run_is_rejected()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None);
            runId = run.Id;
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            await Should.ThrowAsync<AgentRunTransitionException>(() =>
                scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None));
    }

    private static AgentTask BuildTask(string goal = "Fix the failing billing tests") =>
        new() { Goal = goal, Harness = "codex-cli", Model = "gpt-5.3-codex" };

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
