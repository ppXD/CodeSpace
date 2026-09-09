using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Learning;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Learning;

/// <summary>Real PostgreSQL coverage for the prompt-facing lesson boundary: only applicable, current, sourced lessons may leave the ledger.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LessonReaderScopeFlowTests
{
    private readonly PostgresFixture _fixture;

    public LessonReaderScopeFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reader_returns_only_mode_and_repository_applicable_current_sourced_lessons()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repositoryId = Guid.NewGuid();
        var otherRepositoryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var exact = await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "exact", now.AddMinutes(-2)) { RepositoryId = repositoryId, QualifiedAt = now.AddMinutes(-1) });
        var general = await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "general", now.AddMinutes(-1)) { QualifiedAt = now.AddMinutes(-1) });
        await SeedAsync(new LessonSeed(teamId, RunModeKeys.Supervisor, "wrong-mode", now.AddMinutes(-3)) { RepositoryId = repositoryId });
        await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "wrong-repository", now.AddMinutes(-3)) { RepositoryId = otherRepositoryId });
        await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "future", now.AddMinutes(1)) { RepositoryId = repositoryId });
        await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "expired", now.AddDays(-31)) { RepositoryId = repositoryId, ExpiresAt = now.AddMinutes(-1) });
        await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "invalidated", now.AddMinutes(-3)) { RepositoryId = repositoryId, InvalidatedAt = now.AddMinutes(-1) });
        await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "no-sources", now.AddMinutes(-3)) { RepositoryId = repositoryId, SourceRunIds = [] });
        await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "no-producer", now.AddMinutes(-3)) { RepositoryId = repositoryId, DistilledByModel = "" });

        using var scope = _fixture.BeginScope();
        var rows = await scope.Resolve<ILessonReader>().ListCurrentAsync(new LessonReadRequest(teamId, RunModeKeys.PlanMap, LessonRuntimeContext.General(repositoryId), now, 10), CancellationToken.None);

        rows.Select(row => row.Id).ShouldBe([exact, general], "an exact repository lesson precedes the general fallback; another mode/repository or untrusted temporal/provenance row cannot reach the model");
    }

    [Fact]
    public async Task Repositoryless_reads_do_not_import_repository_specific_lessons()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        var general = await SeedAsync(new LessonSeed(teamId, RunModeKeys.Supervisor, "general", now.AddMinutes(-1)) { QualifiedAt = now.AddMinutes(-1) });
        await SeedAsync(new LessonSeed(teamId, RunModeKeys.Supervisor, "repository-only", now.AddMinutes(-2)) { RepositoryId = Guid.NewGuid() });

        using var scope = _fixture.BeginScope();
        var rows = await scope.Resolve<ILessonReader>().ListCurrentAsync(new LessonReadRequest(teamId, RunModeKeys.Supervisor, LessonRuntimeContext.General(), now, 10), CancellationToken.None);

        rows.Select(row => row.Id).ShouldBe([general]);
    }

    [Fact]
    public async Task Reader_enforces_its_own_hard_cap_even_when_a_caller_requests_more()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < LessonReader.MaxTake + 5; index++)
            await SeedAsync(new LessonSeed(teamId, RunModeKeys.Supervisor, $"lesson-{index}", now.AddSeconds(-index - 1)) { QualifiedAt = now.AddMinutes(-1) });

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<ILessonReader>();
        var capped = await reader.ListCurrentAsync(new LessonReadRequest(teamId, RunModeKeys.Supervisor, LessonRuntimeContext.General(), now, int.MaxValue), CancellationToken.None);
        var empty = await reader.ListCurrentAsync(new LessonReadRequest(teamId, RunModeKeys.Supervisor, LessonRuntimeContext.General(), now, 0), CancellationToken.None);

        capped.Count.ShouldBe(LessonReader.MaxTake);
        empty.ShouldBeEmpty();
    }

    [Fact]
    public async Task Qualified_rules_precede_one_bounded_candidate_for_controlled_exploration()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        var newestCandidate = await SeedAsync(new LessonSeed(teamId, RunModeKeys.Supervisor, "candidate-new", now.AddMinutes(-1)));
        await SeedAsync(new LessonSeed(teamId, RunModeKeys.Supervisor, "candidate-old", now.AddMinutes(-2)));
        var qualified = await SeedAsync(new LessonSeed(teamId, RunModeKeys.Supervisor, "qualified", now.AddMinutes(-3)) { QualifiedAt = now.AddMinutes(-2) });

        using var scope = _fixture.BeginScope();
        var rows = await scope.Resolve<ILessonReader>().ListCurrentAsync(new LessonReadRequest(teamId, RunModeKeys.Supervisor, LessonRuntimeContext.General(), now, 5), CancellationToken.None);

        rows.Select(row => row.Id).ShouldBe([qualified, newestCandidate], "formal rules rank first and at most one unqualified lesson reaches the treatment prompt");
    }

    [Fact]
    public async Task Runtime_specific_lessons_require_every_declared_capability_and_unknown_context_fails_closed()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        var general = await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "general", now.AddMinutes(-4)) { QualifiedAt = now.AddMinutes(-1) });
        var exact = await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "exact", now.AddMinutes(-3))
        {
            QualifiedAt = now.AddMinutes(-1), ApplicableModels = ["claude-opus-4-8"], ApplicableHarnesses = ["claude-code"], RequiredTools = ["git.status", "git.diff"],
        });
        await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "wrong-model", now.AddMinutes(-2)) { QualifiedAt = now.AddMinutes(-1), ApplicableModels = ["gpt-6"] });
        await SeedAsync(new LessonSeed(teamId, RunModeKeys.PlanMap, "missing-tool", now.AddMinutes(-1)) { QualifiedAt = now.AddMinutes(-1), RequiredTools = ["browser.open"] });

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<ILessonReader>();
        var matched = await reader.ListCurrentAsync(new LessonReadRequest(teamId, RunModeKeys.PlanMap,
            new LessonRuntimeContext(null, "CLAUDE-OPUS-4-8", "Claude-Code", ["git.diff", "git.status", "extra"]), now, 10), CancellationToken.None);
        var unknown = await reader.ListCurrentAsync(new LessonReadRequest(teamId, RunModeKeys.PlanMap,
            new LessonRuntimeContext(null, null, null, null), now, 10), CancellationToken.None);

        matched.Select(row => row.Id).ShouldBe([exact, general], "a specialized rule needs its exact model/harness and every required tool; general rules remain fallbacks");
        unknown.Select(row => row.Id).ShouldBe([general], "unknown runtime facts cannot authorize a specialized lesson");
    }

    private async Task<Guid> SeedAsync(LessonSeed seed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var lesson = new Lesson
        {
            Id = Guid.NewGuid(), TeamId = seed.TeamId, Mode = seed.Mode, RepositoryId = seed.RepositoryId,
            FailureClass = seed.Marker, WhatFailed = seed.Marker, Why = seed.Marker, HowToApply = seed.Marker,
            SourceRunIds = seed.SourceRunIds?.ToList() ?? [Guid.NewGuid()], DistilledByModel = seed.DistilledByModel,
            ValidFrom = seed.ValidFrom, InvalidatedAt = seed.InvalidatedAt, ExpiresAt = seed.ExpiresAt ?? seed.ValidFrom.AddDays(30),
            QualifiedAt = seed.QualifiedAt, SuccessfulExposureRunIds = seed.QualifiedAt == null ? [] : [Guid.NewGuid(), Guid.NewGuid()],
            ApplicableModels = seed.ApplicableModels.ToList(), ApplicableHarnesses = seed.ApplicableHarnesses.ToList(), RequiredTools = seed.RequiredTools.ToList(),
        };
        db.Lesson.Add(lesson);
        await db.SaveChangesAsync();
        return lesson.Id;
    }

    private sealed record LessonSeed(Guid TeamId, string Mode, string Marker, DateTimeOffset ValidFrom)
    {
        public Guid? RepositoryId { get; init; }
        public DateTimeOffset? InvalidatedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public IReadOnlyList<Guid>? SourceRunIds { get; init; }
        public string DistilledByModel { get; init; } = "test-model";
        public DateTimeOffset? QualifiedAt { get; init; }
        public IReadOnlyList<string> ApplicableModels { get; init; } = [];
        public IReadOnlyList<string> ApplicableHarnesses { get; init; } = [];
        public IReadOnlyList<string> RequiredTools { get; init; } = [];
    }
}
