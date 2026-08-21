using System.Data.Common;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Publish.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The dependency-staging EMPTY guard needs only one fact per manifest-selected producer: whether its exact inline
/// carrier contains non-whitespace patch bytes. This projection is intentionally narrower than
/// <see cref="IAgentPatchReader.ReadAsync"/>: PostgreSQL selects the alias and returns a compact boolean/resolution,
/// never K whole <c>result_jsonb</c> documents or patch bodies. The later integration remains the required byte read.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentInlinePatchObservationFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentInlinePatchObservationFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Multiple_sources_are_team_scoped_alias_first_deduplicated_and_projected_by_one_compact_query()
    {
        var teamId = (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;
        var foreignTeamId = (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;
        var multi = await SeedRunAsync(teamId, Result(repositoryResults:
        [
            new RepositoryRunResult { Alias = "primary", Patch = "primary-mirror" },
            new RepositoryRunResult { Alias = "web", Patch = "diff --git a/web b/web" },
        ]));
        var legacy = await SeedRunAsync(teamId, Result(patch: "diff --git a/legacy b/legacy"));
        var whitespace = await SeedRunAsync(teamId, Result(patch: " \r\n\t"));
        var malformed = await SeedRunAsync(teamId, """{"patch":"must-not-cross-without-the-required-result-contract"}""");
        var foreign = await SeedRunAsync(foreignTeamId, Result(patch: "foreign-secret"));
        var duplicate = new AgentPatchSource { AgentRunId = legacy, RepositoryAlias = "any-legacy-alias" };
        var sources = new[]
        {
            new AgentPatchSource { AgentRunId = multi, RepositoryAlias = "web" },
            duplicate,
            new AgentPatchSource { AgentRunId = whitespace, RepositoryAlias = "primary" },
            duplicate,
            new AgentPatchSource { AgentRunId = malformed, RepositoryAlias = "primary" },
            new AgentPatchSource { AgentRunId = foreign, RepositoryAlias = "primary" },
            new AgentPatchSource { AgentRunId = null, RepositoryAlias = "primary" },
        };
        var recorder = new ObservationCommandRecorder();
        using var scope = ReadScope(recorder);
        var reader = new AgentPatchReader(scope.Resolve<CodeSpaceDbContext>(), offloader: null!);

        var observed = await reader.HasInlinePatchesAsync(teamId, sources, maxSources: 16, CancellationToken.None);

        observed.ShouldBe(new[] { true, true, false, true, false, false, false },
            "exact secondary selection must beat the primary mirror; sole legacy stays compatible; missing/malformed/foreign are empty without exposing bytes; duplicate inputs retain caller order");
        recorder.Commands.Count.ShouldBe(1, "K sources are one batch observation query, never K AgentRun reads");
        recorder.Commands[0].CommandText.ShouldContain("has_inline_patch");
        var finalProjection = recorder.Commands[0].CommandText[recorder.Commands[0].CommandText.LastIndexOf("SELECT resolved", StringComparison.Ordinal)..];
        finalProjection.ShouldNotContain("result_jsonb",
            customMessage: "the JSON document is inspected inside PostgreSQL but is never projected across the process boundary");
        recorder.Commands[0].RequestedAgentRunCount.ShouldBe(5,
            "the duplicate source is bound once; the null source needs no database row; foreign remains a team-scoped requested coordinate");
    }

    [Fact]
    public async Task Alias_and_carrier_integrity_failures_remain_the_same_typed_required_read_failures()
    {
        var teamId = (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;
        var artifactId = Guid.NewGuid();
        var missingAlias = await SeedRunAsync(teamId, Result(repositoryResults: [new RepositoryRunResult { Alias = "primary", Patch = "primary" }]));
        var ambiguousAlias = await SeedRunAsync(teamId, Result(repositoryResults:
        [
            new RepositoryRunResult { Alias = "web", Patch = "one" },
            new RepositoryRunResult { Alias = "web", Patch = "two" },
        ]));
        var unexpectedArtifact = await SeedRunAsync(teamId, Result(repositoryResults: [new RepositoryRunResult { Alias = "web", PatchArtifactId = artifactId }]));
        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IAgentPatchReader>();

        var missing = await Should.ThrowAsync<AgentInlinePatchResolutionException>(() => reader.HasInlinePatchesAsync(teamId,
            [new AgentPatchSource { AgentRunId = missingAlias, RepositoryAlias = "web" }], 1, CancellationToken.None));
        missing.Kind.ShouldBe(AgentInlinePatchResolutionKind.RepositoryAliasMissing);

        var ambiguous = await Should.ThrowAsync<AgentInlinePatchResolutionException>(() => reader.HasInlinePatchesAsync(teamId,
            [new AgentPatchSource { AgentRunId = ambiguousAlias, RepositoryAlias = "web" }], 1, CancellationToken.None));
        ambiguous.Kind.ShouldBe(AgentInlinePatchResolutionKind.RepositoryAliasAmbiguous);

        var artifact = await Should.ThrowAsync<AgentInlinePatchResolutionException>(() => reader.HasInlinePatchesAsync(teamId,
            [new AgentPatchSource { AgentRunId = unexpectedArtifact, RepositoryAlias = "web" }], 1, CancellationToken.None));
        artifact.Kind.ShouldBe(AgentInlinePatchResolutionKind.UnexpectedArtifactReference);
        artifact.ArtifactId.ShouldBe(artifactId);
    }

    [Fact]
    public async Task Empty_and_over_bound_requests_issue_zero_queries()
    {
        var recorder = new ObservationCommandRecorder();
        using var scope = ReadScope(recorder);
        var reader = scope.Resolve<IAgentPatchReader>();

        (await reader.HasInlinePatchesAsync(Guid.NewGuid(), [], 1, CancellationToken.None)).ShouldBeEmpty();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => reader.HasInlinePatchesAsync(Guid.NewGuid(),
            [new AgentPatchSource { AgentRunId = Guid.NewGuid() }, new AgentPatchSource { AgentRunId = Guid.NewGuid() }], 1, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => reader.HasInlinePatchesAsync(Guid.NewGuid(),
            [new AgentPatchSource { AgentRunId = Guid.NewGuid(), PatchArtifactId = Guid.NewGuid() }], 1, CancellationToken.None));

        recorder.Commands.ShouldBeEmpty("empty input, bound refusal and a non-inline carrier are rejected before PostgreSQL or artifact I/O");
    }

    [Fact]
    public async Task A_batch_database_fault_propagates_without_per_source_fallback()
    {
        var fault = new ThrowingObservationInterceptor();
        using var scope = ReadScope(fault);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => scope.Resolve<IAgentPatchReader>().HasInlinePatchesAsync(Guid.NewGuid(),
            [new AgentPatchSource { AgentRunId = Guid.NewGuid() }], 1, CancellationToken.None));

        exception.Message.ShouldBe(ThrowingObservationInterceptor.Message);
        fault.AttemptCount.ShouldBe(1);
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, string resultJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.AgentRun.Add(new AgentRun { Id = id, TeamId = teamId, Harness = "test", Status = AgentRunStatus.Succeeded, TaskJson = "{}", ResultJson = resultJson });
        await db.SaveChangesAsync();
        return id;
    }

    private static string Result(string patch = "", IReadOnlyList<RepositoryRunResult>? repositoryResults = null) =>
        JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = patch, RepositoryResults = repositoryResults ?? [] }, AgentJson.Options);

    private ILifetimeScope ReadScope(DbCommandInterceptor interceptor) => _fixture.BeginScope(builder =>
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options;
        builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    });

    private sealed class ObservationCommandRecorder : DbCommandInterceptor
    {
        public List<(string CommandText, int RequestedAgentRunCount)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var requested = command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<Guid[]>().SingleOrDefault();
            Commands.Add((command.CommandText, requested?.Length ?? 0));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingObservationInterceptor : DbCommandInterceptor
    {
        public const string Message = "inline-observation-database-fault";
        public int AttemptCount { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            AttemptCount++;
            throw new InvalidOperationException(Message);
        }
    }
}
