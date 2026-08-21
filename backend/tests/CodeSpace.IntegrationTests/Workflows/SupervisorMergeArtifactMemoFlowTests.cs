using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Request-local required-patch memoization through the real merge executor + real Postgres contributor query. The
/// spy is only the generic artifact seam: carrier selection, result validation, ordering and outcome serialization
/// remain production code.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorMergeArtifactMemoFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorMergeArtifactMemoFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Top_level_and_primary_repo_same_artifact_read_once_and_preserve_exact_outcome()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();
        var primaryArtifactId = Guid.NewGuid();
        var secondaryArtifactId = Guid.NewGuid();
        var offloader = new RecordingOffloader(new Dictionary<Guid, string>
        {
            [primaryArtifactId] = "primary full patch",
            [secondaryArtifactId] = "secondary full patch",
        });
        await SeedAgentRunAsync(teamId, agentRunId, Result(primaryArtifactId,
            Repo("repo", primaryArtifactId), Repo("api", secondaryArtifactId)));

        var raw = await ExecuteMergeAsync(teamId, offloader, agentRunId);

        offloader.ArtifactCalls.SequenceEqual(new[] { (teamId, primaryArtifactId), (teamId, secondaryArtifactId) }).ShouldBeTrue(
            "the top-level compatibility carrier and exact primary per-repo carrier name the same immutable bytes; the old path read [A,A,B]");
        raw.ShouldBe(ExpectedOutcome(agentRunId, primaryArtifactId, "primary full patch"),
            "memoization is an I/O optimization only — the deterministic merge outcome stays byte-for-byte identical");
    }

    [Fact]
    public async Task Same_artifact_on_two_agent_runs_keeps_the_producer_authority_boundary()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var offloader = new RecordingOffloader(new Dictionary<Guid, string> { [artifactId] = "shared bytes" });
        await SeedAgentRunAsync(teamId, first, Result(artifactId));
        await SeedAgentRunAsync(teamId, second, Result(artifactId));

        await ExecuteMergeAsync(teamId, offloader, first, second);

        offloader.ArtifactCalls.SequenceEqual(new[] { (teamId, artifactId), (teamId, artifactId) }).ShouldBeTrue(
            "the memo key includes AgentRunId; identical content references from distinct producers remain independent required reads");
    }

    [Fact]
    public async Task Different_artifacts_are_each_read_in_carrier_order()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();
        var top = Guid.NewGuid();
        var primary = Guid.NewGuid();
        var secondary = Guid.NewGuid();
        var offloader = new RecordingOffloader(new Dictionary<Guid, string> { [top] = "top", [primary] = "primary", [secondary] = "secondary" });
        await SeedAgentRunAsync(teamId, agentRunId, Result(top, Repo("repo", primary), Repo("api", secondary)));

        await ExecuteMergeAsync(teamId, offloader, agentRunId);

        offloader.ArtifactCalls.SequenceEqual(new[] { (teamId, top), (teamId, primary), (teamId, secondary) }).ShouldBeTrue();
    }

    [Fact]
    public async Task Inline_single_repo_result_is_byte_identical_and_never_touches_artifact_resolution()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();
        var offloader = new RecordingOffloader(new Dictionary<Guid, string>());
        await SeedAgentRunAsync(teamId, agentRunId, new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Summary = "did it",
            ChangedFiles = new[] { "primary.cs" },
            ProducedBranch = "codespace/agent/branch",
            Patch = "inline patch",
        });

        var raw = await ExecuteMergeAsync(teamId, offloader, agentRunId);

        offloader.ArtifactCalls.ShouldBeEmpty();
        raw.ShouldBe(ExpectedOutcome(agentRunId, null, "inline patch"));
    }

    [Fact]
    public async Task Malformed_dual_carrier_preserves_inline_precedence_and_never_enters_the_memo()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var offloader = new RecordingOffloader(new Dictionary<Guid, string>());
        await SeedAgentRunAsync(teamId, agentRunId, new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Summary = "did it",
            ChangedFiles = new[] { "primary.cs" },
            ProducedBranch = "codespace/agent/branch",
            Patch = "inline wins",
            PatchArtifactId = artifactId,
        });

        var raw = await ExecuteMergeAsync(teamId, offloader, agentRunId);

        offloader.ArtifactCalls.ShouldBeEmpty();
        raw.ShouldBe(ExpectedOutcome(agentRunId, artifactId, "inline wins"));
    }

    [Fact]
    public async Task Legacy_result_without_a_patch_carrier_stays_empty_and_never_enters_the_memo()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();
        var offloader = new RecordingOffloader(new Dictionary<Guid, string>());
        await SeedAgentRunAsync(teamId, agentRunId, new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Summary = "did it",
            ChangedFiles = new[] { "primary.cs" },
            ProducedBranch = "codespace/agent/branch",
        });

        var raw = await ExecuteMergeAsync(teamId, offloader, agentRunId);

        offloader.ArtifactCalls.ShouldBeEmpty();
        raw.ShouldBe(ExpectedOutcome(agentRunId, null, ""));
    }

    [Fact]
    public async Task Typed_failure_is_not_cached_and_a_new_merge_request_retries_the_required_read()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var offloader = new RecordingOffloader(new Dictionary<Guid, string> { [artifactId] = "recovered bytes" }, failFirstArtifactId: artifactId);
        await SeedAgentRunAsync(teamId, agentRunId, Result(artifactId, Repo("repo", artifactId)));

        var failure = await Should.ThrowAsync<ArtifactContentUnavailableException>(() => ExecuteMergeAsync(teamId, offloader, agentRunId));
        failure.ArtifactId.ShouldBe(artifactId);
        failure.Kind.ShouldBe(ArtifactContentUnavailableKind.BackendUnavailable);

        (await ExecuteMergeAsync(teamId, offloader, agentRunId)).ShouldBe(ExpectedOutcome(agentRunId, artifactId, "recovered bytes"));
        offloader.ArtifactCalls.SequenceEqual(new[] { (teamId, artifactId), (teamId, artifactId) }).ShouldBeTrue(
            "the failed first request inserts no memo entry; an independent merge request reaches the required reader again, then reuses only that successful result");
    }

    [Fact]
    public async Task Cancellation_is_not_cached_and_a_new_merge_request_retries_the_required_read()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var offloader = new RecordingOffloader(new Dictionary<Guid, string> { [artifactId] = "recovered bytes" }, cancelFirstArtifactId: artifactId);
        await SeedAgentRunAsync(teamId, agentRunId, Result(artifactId, Repo("repo", artifactId)));

        await Should.ThrowAsync<OperationCanceledException>(() => ExecuteMergeAsync(teamId, offloader, agentRunId));

        (await ExecuteMergeAsync(teamId, offloader, agentRunId)).ShouldBe(ExpectedOutcome(agentRunId, artifactId, "recovered bytes"));
        offloader.ArtifactCalls.SequenceEqual(new[] { (teamId, artifactId), (teamId, artifactId) }).ShouldBeTrue(
            "a cancelled required read inserts no memo entry; an independent merge request retries and memoizes only its successful result");
    }

    private async Task<string> ExecuteMergeAsync(Guid teamId, RecordingOffloader offloader, params Guid[] agentRunIds)
    {
        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(offloader).As<IArtifactOffloader>());
        var executor = scope.Resolve<ISupervisorActionExecutor>();
        var context = new SupervisorTurnContext
        {
            Goal = "ship",
            SupervisorRunId = Guid.NewGuid(),
            TeamId = teamId,
            NodeId = "sup",
            TurnNumber = 1,
            PriorDecisions = new[]
            {
                new SupervisorPriorDecision
                {
                    Id = Guid.NewGuid(),
                    Sequence = 1,
                    DecisionKind = SupervisorDecisionKinds.Spawn,
                    Status = SupervisorDecisionStatus.Succeeded,
                    PayloadJson = "{}",
                    OutcomeJson = JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Length }, AgentJson.Options),
                },
            },
            AgentProfile = new SupervisorAgentProfile { IntegrateBranches = false },
        };
        var decision = new SupervisorDecision
        {
            Kind = SupervisorDecisionKinds.Merge,
            PayloadJson = JsonSerializer.Serialize(new SupervisorMergePayload { SynthesisInstruction = "combine" }, AgentJson.Options),
        };

        return (await executor.ExecuteAsync(decision, context, CancellationToken.None)).OutcomeJson;
    }

    private async Task SeedAgentRunAsync(Guid teamId, Guid agentRunId, AgentRunResult result)
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<CodeSpaceDbContext>().AgentRun.Add(new AgentRun
        {
            Id = agentRunId,
            TeamId = teamId,
            NodeId = "sup",
            Harness = "codex-cli",
            Status = result.Status,
            TaskJson = "{}",
            ResultJson = JsonSerializer.Serialize(result, AgentJson.Options),
        });
        await scope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
    }

    private static AgentRunResult Result(Guid patchArtifactId, params RepositoryRunResult[] repositories) => new()
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        Summary = "did it",
        ChangedFiles = new[] { "primary.cs" },
        ProducedBranch = "codespace/agent/branch",
        PatchArtifactId = patchArtifactId,
        RepositoryResults = repositories,
    };

    private static RepositoryRunResult Repo(string alias, Guid patchArtifactId) => new()
    {
        Alias = alias,
        RepositoryId = Guid.NewGuid(),
        PatchArtifactId = patchArtifactId,
        BaseSha = "base",
        ChangedFiles = new[] { $"{alias}.cs" },
        Access = WorkspaceAccess.Write,
    };

    private static string ExpectedOutcome(Guid agentRunId, Guid? patchArtifactId, string patch) => JsonSerializer.Serialize(new
    {
        merged = new[]
        {
            new
            {
                agentRunId,
                status = "Succeeded",
                summary = "did it",
                changedFiles = new[] { "primary.cs" },
                producedBranch = "codespace/agent/branch",
                patch,
                patchArtifactId,
                error = (string?)null,
            },
        },
        count = 1,
        synthesisInstruction = "combine",
    }, AgentJson.Options);

    private sealed class RecordingOffloader(IReadOnlyDictionary<Guid, string> artifacts, Guid? failFirstArtifactId = null, Guid? cancelFirstArtifactId = null) : IArtifactOffloader
    {
        private bool _failed;
        private bool _cancelled;

        public List<(Guid TeamId, Guid ArtifactId)> ArtifactCalls { get; } = new();

        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(inline)) return Task.FromResult(inline);
            if (artifactId is not { } id) return Task.FromResult("");

            ArtifactCalls.Add((teamId, id));
            if (!_failed && failFirstArtifactId == id)
            {
                _failed = true;
                throw new ArtifactContentUnavailableException(id, ArtifactContentUnavailableKind.BackendUnavailable);
            }

            if (!_cancelled && cancelFirstArtifactId == id)
            {
                _cancelled = true;
                throw new OperationCanceledException("Simulated required-read cancellation.");
            }

            return Task.FromResult(artifacts.GetValueOrDefault(id, ""));
        }
    }
}
