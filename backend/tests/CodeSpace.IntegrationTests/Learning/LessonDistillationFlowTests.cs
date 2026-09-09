using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
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

    [Fact]
    public async Task Runtime_restrictions_are_grounded_in_cited_agent_envelopes_before_they_are_persisted()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        var runId = await SeedFailedRunAsync(teamId, userId, "tool-specific failure");

        using (var seed = _fixture.BeginScope())
        {
            seed.Resolve<CodeSpaceDbContext>().AgentRun.Add(new AgentRun
            {
                Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = runId, Harness = "Claude-Code", Status = AgentRunStatus.Failed,
                TaskJson = JsonSerializer.Serialize(new CodeSpace.Messages.Agents.AgentTask { Goal = "inspect", Harness = "Claude-Code", Model = "configured-alias", Tools = ["Git.Status", "git.diff"] }, CodeSpace.Core.Services.Agents.AgentJson.Options),
                ResultJson = JsonSerializer.Serialize(new CodeSpace.Messages.Agents.AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "failed", Model = "Claude-Opus-4-8" }, CodeSpace.Core.Services.Agents.AgentJson.Options),
            });
            await seed.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
        }

        var canned = new CannedClient(Proposals(runId, ["claude-opus-4-8"], ["claude-code"], ["git.status"]));
        await DistillTeamAsync(teamId, canned);

        canned.LastRequest.UserPrompt.ShouldContain("runtime models=[claude-opus-4-8]; harnesses=[claude-code]; tools=[git.diff, git.status]", customMessage: "the CLI-observed model outranks its configured alias");
        canned.LastRequest.UserPrompt.ShouldNotContain("configured-alias");
        using var scope = _fixture.BeginScope();
        var lesson = await scope.Resolve<CodeSpaceDbContext>().Lesson.AsNoTracking().SingleAsync(row => row.TeamId == teamId);
        lesson.ApplicableModels.ShouldBe(["claude-opus-4-8"]);
        lesson.ApplicableHarnesses.ShouldBe(["claude-code"]);
        lesson.RequiredTools.ShouldBe(["git.status"]);
    }

    [Fact]
    public async Task Qualification_runs_never_become_lesson_candidates()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        var runId = await SeedFailedRunAsync(teamId, userId, "hidden oracle detail", WorkflowRunPurposes.Qualification);
        var canned = new CannedClient(Proposals(runId));

        await DistillTeamAsync(teamId, canned);

        canned.Calls.ShouldBe(0, "an evaluation run is measurement data, never training input for a later evaluation");
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().Lesson.AsNoTracking().CountAsync(lesson => lesson.TeamId == teamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Expired_lessons_remain_auditable_but_never_reenter_the_distillation_prompt()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        var runId = await SeedFailedRunAsync(teamId, userId, "fresh failure");
        var expiredId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using (var seed = _fixture.BeginScope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            db.Lesson.Add(new Lesson
            {
                Id = expiredId, TeamId = teamId, Mode = "generic", FailureClass = "expired-marker", WhatFailed = "expired-marker",
                Why = "old evidence", HowToApply = "expired-marker", SourceRunIds = [Guid.NewGuid()], DistilledByModel = "old-model",
                ValidFrom = now.AddDays(-31), ExpiresAt = now.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        var canned = new CannedClient(Proposals(runId));
        await DistillTeamAsync(teamId, canned);

        canned.LastRequest.ShouldNotBeNull().UserPrompt.ShouldNotContain(expiredId.ToString());
        canned.LastRequest.UserPrompt.ShouldNotContain("expired-marker");
        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().Lesson.AsNoTracking().CountAsync(lesson => lesson.Id == expiredId)).ShouldBe(1);
    }

    [Fact]
    public async Task Different_modes_and_repositories_are_distilled_in_separate_bounded_prompts()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        var repositoryA = Guid.NewGuid();
        var repositoryB = Guid.NewGuid();
        var supervisorRun = await SeedFailedRunAsync(teamId, userId, new FailedRunSeed("supervisor-failure") { ProjectionKind = TaskProjectionKinds.Supervisor, RepositoryId = repositoryA });
        var otherRepositoryRun = await SeedFailedRunAsync(teamId, userId, new FailedRunSeed("other-repository-failure") { ProjectionKind = TaskProjectionKinds.Supervisor, RepositoryId = repositoryB });
        var mapRun = await SeedFailedRunAsync(teamId, userId, new FailedRunSeed("map-failure") { ProjectionKind = TaskProjectionKinds.PlanMapDynamic, RepositoryId = repositoryA });
        var now = DateTimeOffset.UtcNow;

        using (var seed = _fixture.BeginScope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            db.Lesson.AddRange(Enumerable.Range(0, LessonReader.MaxTake + 5).Select(index => new Lesson
            {
                Id = Guid.NewGuid(), TeamId = teamId, Mode = RunModeKeys.Supervisor, RepositoryId = repositoryA,
                FailureClass = $"scope-marker-{index}", WhatFailed = "old", Why = "old", HowToApply = "old",
                SourceRunIds = [Guid.NewGuid()], DistilledByModel = "old-model", ValidFrom = now.AddMinutes(-index - 1), ExpiresAt = now.AddDays(1),
            }));
            await db.SaveChangesAsync();
        }

        var canned = new CannedClient(JsonSerializer.SerializeToElement(new { lessons = Array.Empty<object>() }));
        await DistillTeamAsync(teamId, canned);

        canned.Calls.ShouldBe(3, "each structural mode/repository scope gets its own model call");
        var supervisorPrompt = canned.Requests.Single(request => request.UserPrompt.Contains(supervisorRun.ToString(), StringComparison.Ordinal)).UserPrompt;
        var otherRepositoryPrompt = canned.Requests.Single(request => request.UserPrompt.Contains(otherRepositoryRun.ToString(), StringComparison.Ordinal)).UserPrompt;
        var mapPrompt = canned.Requests.Single(request => request.UserPrompt.Contains(mapRun.ToString(), StringComparison.Ordinal)).UserPrompt;
        supervisorPrompt.ShouldNotContain(otherRepositoryRun.ToString());
        supervisorPrompt.ShouldNotContain(mapRun.ToString());
        mapPrompt.ShouldNotContain(supervisorRun.ToString());
        otherRepositoryPrompt.ShouldNotContain("scope-marker-");
        supervisorPrompt.Split("id=", StringSplitOptions.None).Length.ShouldBe(LessonReader.MaxTake + 1, "current memory is bounded before entering the model context");
        mapPrompt.ShouldNotContain("scope-marker-");
    }

    [Fact]
    public async Task A_later_scope_failure_cannot_leak_an_earlier_scope_fold_into_a_future_save()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        await SeedFailedRunAsync(teamId, userId, new FailedRunSeed("map-failure") { ProjectionKind = TaskProjectionKinds.PlanMapDynamic });
        await SeedFailedRunAsync(teamId, userId, new FailedRunSeed("supervisor-failure") { ProjectionKind = TaskProjectionKinds.Supervisor });

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var distiller = Distiller(scope, new AddThenThrowClient());

        await Should.ThrowAsync<InvalidOperationException>(() => distiller.DistillTeamAsync(teamId, CancellationToken.None));
        await db.SaveChangesAsync();

        (await db.Lesson.AsNoTracking().CountAsync(lesson => lesson.TeamId == teamId)).ShouldBe(0, "a caller's later SaveChanges cannot commit an earlier scope from the failed advisory round");
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private async Task DistillTeamAsync(Guid teamId, IStructuredLLMClient client)
    {
        using var scope = _fixture.BeginScope();
        await Distiller(scope, client).DistillTeamAsync(teamId, CancellationToken.None);
    }

    [Fact]
    public async Task Distillation_runs_on_a_ceilinged_model_and_records_THAT_model_as_the_author()
    {
        // 🟢 High-fidelity: the REAL ModelPoolSelector + real Postgres decide the model; only the LLM transport is faked.
        // The nightly distiller is one of D2's four CHEAP callers, so an unstarred Frontier + unstarred Strong pool must
        // run it on the Strong row — and the ledger's distilled_by_model must name THAT model, not the pick the
        // unceilinged ladder would have made. ('aaa-frontier' sorts first too, so alphabetical luck can't fake a pass.)
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, frontierRow) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "aaa-frontier");
        var (_, strongRow) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "zzz-strong");
        await SetTierAsync(frontierRow, ModelCapabilityTier.Frontier);
        await SetTierAsync(strongRow, ModelCapabilityTier.Strong);

        var runId = await SeedFailedRunAsync(teamId, userId, "acceptance: ./check.sh exited 2");
        var canned = new CannedClient(Proposals(runId));

        await DistillTeamAsync(teamId, canned);

        using var scope = _fixture.BeginScope();
        var lesson = await scope.Resolve<CodeSpaceDbContext>().Lesson.AsNoTracking().SingleAsync(l => l.TeamId == teamId);
        lesson.DistilledByModel.ShouldBe("zzz-strong", "the distiller ran under InProcessStructuredModel.CheapBrainCeiling AND the provenance names the model that actually answered");
    }

    private async Task SetTierAsync(Guid rowId, ModelCapabilityTier tier)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.ModelCredentialModel.SingleAsync(m => m.Id == rowId)).CapabilityTier = tier;
        await db.SaveChangesAsync();
    }

    private static LessonDistiller Distiller(ILifetimeScope scope, IStructuredLLMClient client) =>
        new(new FakeClients(client), scope.Resolve<IModelPoolSelector>(), scope.Resolve<ISupervisorDecisionLog>(), scope.Resolve<CodeSpaceDbContext>(), NullLogger<LessonDistiller>.Instance);

    /// <summary>A real run row through the real snapshot starter, then stamped into the distiller's window shape (Failure + error + terminal stamp).</summary>
    private async Task<Guid> SeedFailedRunAsync(Guid teamId, Guid userId, string error, string? purpose = null)
        => await SeedFailedRunAsync(teamId, userId, new FailedRunSeed(error) { Purpose = purpose });

    private async Task<Guid> SeedFailedRunAsync(Guid teamId, Guid userId, FailedRunSeed seed)
    {
        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(
                WorkflowsTestSeed.MinimalDefinition(), teamId, userId,
                launchPayloadJson: null, scopeRepositoryIds: seed.RepositoryId is { } repositoryId ? [repositoryId] : null, projectionKind: seed.ProjectionKind, session: null, CancellationToken.None);

        using (var stamp = _fixture.BeginScope())
        {
            var db = stamp.Resolve<CodeSpaceDbContext>();
            var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
            run.Status = WorkflowRunStatus.Failure;
            run.Error = seed.Error;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.Purpose = seed.Purpose;
            await db.SaveChangesAsync();
        }

        return runId;
    }

    private static JsonElement Proposals(Guid runId, IReadOnlyList<string>? models = null, IReadOnlyList<string>? harnesses = null, IReadOnlyList<string>? tools = null) => JsonSerializer.SerializeToElement(new
    {
        lessons = new[] { new { action = "add", existingLessonId = (string?)null, failureClass = "broken-acceptance-command", whatFailed = "the acceptance command exits 2 on a clean tree", why = "check.sh assumes a restored solution", howToApply = "run restore before check.sh in this repo", applicableModels = models ?? [], applicableHarnesses = harnesses ?? [], requiredTools = tools ?? [], sourceRunIds = new[] { runId.ToString() } } },
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
        public List<StructuredLLMCompletionRequest> Requests { get; } = [];
        public StructuredLLMCompletionRequest? LastRequest => Requests.LastOrDefault();
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct)
        {
            Calls++;
            Requests.Add(request);
            return Task.FromResult(new StructuredLLMCompletion { Json = _json, Model = request.Model });
        }
    }

    private sealed class ThrowingClient : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "Anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private sealed class AddThenThrowClient : ILLMClient, IStructuredLLMClient
    {
        private int _calls;
        public string Provider => "Anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct)
        {
            if (++_calls > 1) throw new InvalidOperationException("second scope failed");
            var prefix = "### run ";
            var start = request.UserPrompt.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
            var runId = Guid.Parse(request.UserPrompt.Substring(start, 36));
            return Task.FromResult(new StructuredLLMCompletion { Json = Proposals(runId), Model = request.Model });
        }
    }

    private sealed record FailedRunSeed(string Error)
    {
        public string? Purpose { get; init; }
        public string? ProjectionKind { get; init; }
        public Guid? RepositoryId { get; init; }
    }
}
