using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.RunSources.Schedule;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// End-to-end contract for the schedule producer: <see cref="IScheduleTriggerService.FireDueSchedulesAsync"/>
/// reads <c>trigger.schedule</c> activations, computes due cron occurrences in the look-back window,
/// and stages a <c>workflow_run</c> per occurrence — fired against real Postgres with an injected
/// <c>now</c> so the cron math is deterministic.
///
/// <para>Covers: a due schedule fires with the schedule payload + System actor; firing the same tick
/// twice is idempotent (the run-request unique tuple collapses repeats); a disabled activation and an
/// invalid cron each fire nothing (and the latter doesn't throw, so one bad schedule never blocks the
/// sweep).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ScheduleTriggerFlowTests
{
    private readonly PostgresFixture _fixture;

    public ScheduleTriggerFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // now = 10:05:30 UTC; default look-back is 120s → window (10:03:30, 10:05:30].
    // "*/5 * * * *" has exactly ONE boundary in that window (10:05:00), so a due fire yields one run.
    private static readonly DateTimeOffset Now = new(2026, 6, 12, 10, 5, 30, TimeSpan.Zero);
    private const string EveryFiveMinutes = "*/5 * * * *";

    [Fact]
    public async Task Due_schedule_fires_one_run_with_schedule_payload()
    {
        var ctx = await SeedAsync();
        await SeedScheduleActivationAsync(ctx.WorkflowId, EveryFiveMinutes);

        var fired = await FireAsync(Now);

        fired.ShouldBe(1);

        var (run, payload) = await LoadRunAndPayloadAsync(ctx.WorkflowId);
        run.RunRequest.SourceType.ShouldBe(WorkflowRunSourceTypes.ScheduleCron);
        run.RunRequest.ActorType.ShouldBe(WorkflowRunActorTypes.System);
        run.RunRequest.SourceInstanceId.ShouldNotBeNullOrEmpty();

        payload.GetProperty("cron").GetString().ShouldBe(EveryFiveMinutes);
        payload.GetProperty("scheduledFor").GetString().ShouldBe("2026-06-12T10:05:00.0000000+00:00");
    }

    [Fact]
    public async Task Firing_the_same_tick_twice_is_idempotent()
    {
        // Both ticks see the same now → same occurrence → same (SourceInstanceId, ExternalEventId)
        // tuple. The second fire must create zero new runs (the unique index collapses it).
        var ctx = await SeedAsync();
        await SeedScheduleActivationAsync(ctx.WorkflowId, EveryFiveMinutes);

        (await FireAsync(Now)).ShouldBe(1);
        (await FireAsync(Now)).ShouldBe(0, "the same scheduled occurrence must never fire a second run");

        await AssertRunCountAsync(ctx.WorkflowId, expected: 1);
    }

    [Fact]
    public async Task Disabled_activation_fires_nothing()
    {
        var ctx = await SeedAsync();
        await SeedScheduleActivationAsync(ctx.WorkflowId, EveryFiveMinutes, enabled: false);

        (await FireAsync(Now)).ShouldBe(0);
        await AssertRunCountAsync(ctx.WorkflowId, expected: 0);
    }

    [Fact]
    public async Task Invalid_cron_fires_nothing_and_does_not_throw()
    {
        var ctx = await SeedAsync();
        await SeedScheduleActivationAsync(ctx.WorkflowId, "not-a-cron");

        var fired = await FireAsync(Now);   // must not throw — one bad schedule can't break the sweep

        fired.ShouldBe(0);
        await AssertRunCountAsync(ctx.WorkflowId, expected: 0);
    }

    // ─── Infrastructure ─────────────────────────────────────────────────────────

    private sealed record SeedContext(Guid TeamId, Guid UserId, Guid WorkflowId);

    private async Task<int> FireAsync(DateTimeOffset now)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IScheduleTriggerService>().FireDueSchedulesAsync(now, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<SeedContext> SeedAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var workflowId = await mediator.Send(new CreateWorkflowCommand
        {
            Name = "schedule-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        }).ConfigureAwait(false);

        return new SeedContext(teamId, userId, workflowId);
    }

    private async Task SeedScheduleActivationAsync(Guid workflowId, string cron, bool enabled = true)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowActivation.Add(new WorkflowActivation
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            TypeKey = "trigger.schedule",
            ConfigJson = JsonSerializer.Serialize(new { cron }),
            Enabled = enabled,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<(WorkflowRun run, JsonElement payload)> LoadRunAndPayloadAsync(Guid workflowId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking()
            .Include(r => r.RunRequest)
            .SingleOrDefaultAsync(r => r.WorkflowId == workflowId).ConfigureAwait(false);
        run.ShouldNotBeNull("the producer MUST stage exactly one workflow_run for a due schedule occurrence");
        var payload = JsonDocument.Parse(run.RunRequest.NormalizedPayloadJson).RootElement;
        return (run, payload);
    }

    private async Task AssertRunCountAsync(Guid workflowId, int expected)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var actual = await db.WorkflowRun.AsNoTracking().Where(r => r.WorkflowId == workflowId).CountAsync().ConfigureAwait(false);
        actual.ShouldBe(expected, $"expected {expected} scheduled run(s) for workflow {workflowId}; got {actual}");
    }
}
