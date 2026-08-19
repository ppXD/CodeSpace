using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Learning;

/// <summary>
/// 🟢 Integration (real Postgres + real pool resolve + real run rows; a FAKE structured client at the LLM seam,
/// tiering precedent): D1's nightly post-mortem end to end — a failed run distills into a cited lesson row; the
/// SAME window re-run makes NO second model call (a cited run is never re-distilled — idempotence by provenance);
/// a throwing client leaves the ledger unchanged and never crashes the sweep (advisory).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LessonDistillationFlowTests
{
    private readonly PostgresFixture _fixture;

    public LessonDistillationFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_failed_run_distills_into_a_cited_lesson_and_is_never_redistilled()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        var runId = await SeedFailedRunAsync(teamId, userId, "acceptance: ./check.sh exited 2");

        var canned = new CannedClient(Proposals(runId));
        await DistillTeamAsync(teamId, canned);

        canned.Calls.ShouldBe(1);

        using (var scope = _fixture.BeginScope())
        {
            var lesson = await scope.Resolve<CodeSpaceDbContext>().Lesson.AsNoTracking().SingleAsync(l => l.TeamId == teamId);
            lesson.SourceRunIds.ShouldHaveSingleItem().ShouldBe(runId, "the lesson must cite the run that taught it — provenance is the anti-confabulation guard");
            lesson.FailureClass.ShouldBe("broken-acceptance-command");
            lesson.Mode.ShouldBe("generic", "a bare snapshot definition classifies generic — the mode rides the run, not the prompt");
            lesson.DistilledByModel.ShouldNotBeNullOrWhiteSpace();
            lesson.InvalidatedAt.ShouldBeNull();
        }

        await DistillTeamAsync(teamId, canned);

        canned.Calls.ShouldBe(1, "the cited run is excluded from candidates — a window re-run makes NO second model call");

        using (var verify = _fixture.BeginScope())
            (await verify.Resolve<CodeSpaceDbContext>().Lesson.AsNoTracking().CountAsync(l => l.TeamId == teamId)).ShouldBe(1, "…and mints no duplicate");
    }

    [Fact]
    public async Task A_faulty_round_leaves_the_ledger_unchanged_and_never_crashes_the_sweep()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        await SeedFailedRunAsync(teamId, userId, "boom");

        using var scope = _fixture.BeginScope();
        var distiller = Distiller(scope, new ThrowingClient());

        await distiller.DistillAsync(CancellationToken.None);   // the sweep catches per team — no throw escapes

        (await scope.Resolve<CodeSpaceDbContext>().Lesson.AsNoTracking().CountAsync(l => l.TeamId == teamId)).ShouldBe(0, "a faulty round is advisory — the ledger stays unchanged");
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private async Task DistillTeamAsync(Guid teamId, IStructuredLLMClient client)
    {
        using var scope = _fixture.BeginScope();
        await Distiller(scope, client).DistillTeamAsync(teamId, CancellationToken.None);
    }

    private static LessonDistiller Distiller(ILifetimeScope scope, IStructuredLLMClient client) =>
        new(new FakeClients(client), scope.Resolve<IModelPoolSelector>(), scope.Resolve<ISupervisorDecisionLog>(), scope.Resolve<CodeSpaceDbContext>(), NullLogger<LessonDistiller>.Instance);

    /// <summary>A real run row through the real snapshot starter, then stamped into the distiller's window shape (Failure + error + terminal stamp).</summary>
    private async Task<Guid> SeedFailedRunAsync(Guid teamId, Guid userId, string error)
    {
        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(
                WorkflowsTestSeed.MinimalDefinition(), teamId, userId,
                launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);

        using (var stamp = _fixture.BeginScope())
        {
            var db = stamp.Resolve<CodeSpaceDbContext>();
            var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
            run.Status = WorkflowRunStatus.Failure;
            run.Error = error;
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        return runId;
    }

    private static JsonElement Proposals(Guid runId) => JsonSerializer.SerializeToElement(new
    {
        lessons = new[] { new { action = "add", existingLessonId = (string?)null, failureClass = "broken-acceptance-command", whatFailed = "the acceptance command exits 2 on a clean tree", why = "check.sh assumes a restored solution", howToApply = "run restore before check.sh in this repo", sourceRunIds = new[] { runId.ToString() } } },
    });

    private sealed class FakeClients : ILLMClientRegistry
    {
        public FakeClients(IStructuredLLMClient structured) => All = new ILLMClient[] { (ILLMClient)structured };
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All.First();
    }

    private sealed class CannedClient : ILLMClient, IStructuredLLMClient
    {
        private readonly JsonElement _json;
        public CannedClient(JsonElement json) { _json = json; }
        public string Provider => "Anthropic";
        public int Calls { get; private set; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new StructuredLLMCompletion { Json = _json, Model = request.Model });
        }
    }

    private sealed class ThrowingClient : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "Anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => throw new InvalidOperationException("boom");
    }
}
