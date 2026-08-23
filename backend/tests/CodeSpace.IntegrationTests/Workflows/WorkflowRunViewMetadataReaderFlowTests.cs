using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Nodes;
using CodeSpace.Core.Services.Tasks.Timeline;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Workflows;
using CodeSpace.Messages.Tasks.Phases;
using CodeSpace.Messages.Tasks.Timeline;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowRunViewMetadataReaderFlowTests
{
    private const string Bomb = "metadata-must-not-return-this-body";
    private readonly PostgresFixture _fixture;

    public WorkflowRunViewMetadataReaderFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task View_metadata_is_exactly_scoped_lineage_aware_and_never_reads_execution_or_artifact_bodies()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var definition = Definition(Bomb + new string('p', 64 * 1024));
        var artifactId = await PutBombArtifactAsync(teamId);
        var original = await SeedSnapshotRunAsync(teamId, definition, null, null, DateTimeOffset.UtcNow.AddMinutes(-2));
        var latest = await SeedSnapshotRunAsync(teamId, definition, original, original, DateTimeOffset.UtcNow);
        var originalAgent = Guid.NewGuid();
        var latestAgent = Guid.NewGuid();

        await SeedCellAsync(original, "work", string.Empty, WorkflowRunRecordTypes.NodeFailed, originalAgent, artifactId);
        await SeedCellAsync(latest, "work", string.Empty, WorkflowRunRecordTypes.NodeCompleted, latestAgent, artifactId);
        var child = await SeedChildRunAsync(teamId, definition, latest, DateTimeOffset.UtcNow.AddSeconds(1));
        await SeedCellAsync(child, "work", string.Empty, WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);

        var reads = new BodyReadObservation();
        using var scope = _fixture.BeginScope(builder =>
        {
            builder.RegisterInstance(new TestCurrentUser(userId, "metadata-test", new[] { Roles.Admin })).As<ICurrentUser>().SingleInstance();
            builder.RegisterInstance(new TestCurrentTeam(teamId)).As<ICurrentTeam>().SingleInstance();
            builder.RegisterDecorator<IArtifactStore>((_, _, inner) => new ObservedArtifactStore(inner, reads));
            builder.RegisterDecorator<IRunNodeOutputInflater>((_, _, inner) => new ObservedOutputInflater(inner, reads));
        });

        var mediator = scope.Resolve<IMediator>();
        var merged = await mediator.Send(new GetWorkflowRunViewMetadataQuery { RunId = latest });
        var originalOnly = await mediator.Send(new GetWorkflowRunViewMetadataQuery { RunId = original, Scope = WorkflowRunViewScope.AttemptOnly });
        var foreign = await scope.Resolve<IWorkflowRunViewMetadataReader>().ReadAsync(latest, Guid.NewGuid(), WorkflowRunViewScope.LineageMerged, CancellationToken.None);
        var childView = await scope.Resolve<IWorkflowRunViewMetadataReader>().ReadAsync(child, teamId, WorkflowRunViewScope.LineageMerged, CancellationToken.None);

        merged.ShouldNotBeNull();
        merged!.RunId.ShouldBe(latest, "the requested attempt remains the response identity even when cells are lineage-merged");
        merged.CellsAvailability.ShouldBe(WorkflowRunViewAvailability.Available);
        var latestCell = merged.Cells.Single(cell => cell.NodeId == "work" && cell.IterationKey == string.Empty);
        latestCell.SourceRunId.ShouldBe(latest, "the latest attempt owns the returned cell body coordinate");
        latestCell.AgentRunId.ShouldBe(latestAgent, "links are joined through that same exact source attempt");
        latestCell.Status.ShouldBe(NodeStatus.Success);

        var oldCell = originalOnly!.Cells.Single(cell => cell.NodeId == "work" && cell.IterationKey == string.Empty);
        oldCell.SourceRunId.ShouldBe(original);
        oldCell.AgentRunId.ShouldBe(originalAgent);
        oldCell.Status.ShouldBe(NodeStatus.Failure);
        foreign.ShouldBeNull("foreign and absent run ids are intentionally conflated");
        childView!.Cells.ShouldHaveSingleItem().SourceRunId.ShouldBe(child, "a child execution is self-scoped, not erased by the top-level lineage-index exclusion");

        reads.ArtifactReads.ShouldBe(0);
        reads.InflaterReads.ShouldBe(0);
        var wire = JsonSerializer.Serialize(merged, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        wire.ShouldNotContain(Bomb, Case.Sensitive);
        wire.ShouldNotContain("$artifact_ref", Case.Sensitive);
        merged.TopologyAvailability.ShouldBe(WorkflowRunViewAvailability.Available);
        merged.Topology!.Nodes.Select(node => node.Id).ShouldBe(new[] { "start", "work" });
    }

    [Fact]
    public async Task Ten_thousand_and_fifty_legal_map_cells_use_a_run_scoped_index_without_payload_detoast_or_disk_sort()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSnapshotRunAsync(teamId, Definition("small"), null, null, DateTimeOffset.UtcNow);
        var foreignRunId = await SeedSnapshotRunAsync(teamId, Definition("small"), null, null, DateTimeOffset.UtcNow);
        await BulkSeedCellsAsync(runId, 10_050, includeOneToastedBomb: true);
        await BulkSeedCellsAsync(foreignRunId, 10_050, includeOneToastedBomb: false);

        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IWorkflowRunViewMetadataReader>().ReadAsync(runId, teamId, WorkflowRunViewScope.AttemptOnly, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.CellsAvailability.ShouldBe(WorkflowRunViewAvailability.Available);
        result.Cells.Count.ShouldBe(10_050, "the engine admits 10,000 map branches; the metadata plane must not fail at 5,000");
        result.Cells.ShouldAllBe(cell => cell.SourceRunId == runId);

        var plan = await ExplainCellQueryAsync(scope.Resolve<CodeSpaceDbContext>(), runId);
        var scans = string.Join('\n', plan.Split('\n').Where(line => line.Contains("Bitmap Index", StringComparison.Ordinal)
            || line.Contains("Index Scan", StringComparison.Ordinal) || line.Contains("Seq Scan on workflow_run_record", StringComparison.Ordinal)));
        scans.ShouldContain("idx_wrr_run_", Case.Sensitive, "an existing run-scoped ledger index bounds the scan to the requested run");
        plan.ShouldNotContain("Seq Scan on workflow_run_record", Case.Sensitive);
        plan.ShouldNotContain("Sort Method: external", Case.Sensitive, "the bounded metadata sort must remain in memory");
        plan.ShouldNotContain("pg_toast", Case.Insensitive, "the selected columns never dereference payload_json's toasted body");
    }

    [Fact]
    public async Task Map_dispatch_source_resolved_from_di_projects_the_real_bounded_metadata_without_body_reads()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var artifactId = await PutBombArtifactAsync(teamId);
        var runId = await SeedSnapshotRunAsync(teamId, MapDefinition(), null, null, DateTimeOffset.UtcNow);
        await SeedCellAsync(runId, "fan", string.Empty, WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await SeedCellAsync(runId, "worker", "fan#0", WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        var reads = new BodyReadObservation();

        using var scope = _fixture.BeginScope(builder =>
        {
            builder.RegisterDecorator<IArtifactStore>((_, _, inner) => new ObservedArtifactStore(inner, reads));
            builder.RegisterDecorator<IRunNodeOutputInflater>((_, _, inner) => new ObservedOutputInflater(inner, reads));
        });
        var source = scope.Resolve<IEnumerable<IRunTimelineSource>>().Single(value => value.SourceKey == MapDispatchTimelineMap.Key);
        var cards = scope.Resolve<IEnumerable<IJournalFactsSource>>().Single(value => value is MapAgentCardFactsSource);

        var events = await source.ContributeAsync(new RunTimelineContext { RunId = runId, TeamId = teamId }, CancellationToken.None);
        var facts = await cards.GatherAsync(runId, teamId, CancellationToken.None);

        var item = events.ShouldHaveSingleItem();
        item.Id.ShouldBe("map-dispatch-fan");
        item.Title.ShouldBe("Dispatched 1 agent");
        item.Summary.ShouldBeNull();
        facts.ShouldBeEmpty("the linked agent row is deliberately absent; the bounded source skips it instead of fabricating a card");
        reads.ArtifactReads.ShouldBe(0);
        reads.InflaterReads.ShouldBe(0);
        JsonSerializer.Serialize(events, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldNotContain(Bomb, Case.Sensitive);
    }

    [Fact]
    public async Task Node_phase_source_resolved_from_di_reads_only_bounded_error_and_map_output_leaves()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var artifactId = await PutBombArtifactAsync(teamId);
        var runId = await SeedSnapshotRunAsync(teamId, MapDefinition(), null, null, DateTimeOffset.UtcNow);
        await SeedCellAsync(runId, "fan", string.Empty, WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await SeedCellAsync(runId, "worker", "fan#0", WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await SeedCellAsync(runId, "failed", string.Empty, WorkflowRunRecordTypes.NodeFailed, Guid.NewGuid(), artifactId);
        await SeedCellAsync(runId, "plain", string.Empty, WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await AppendStatePayloadAsync(runId, "fan", string.Empty,
            """{"outputs":{"count":3,"failed":1,"resultsCoverage":{"complete":false,"totalBranches":3,"includedBranches":2,"shortenedBranches":[0]}}}""");
        await AppendStatePayloadAsync(runId, "failed", string.Empty, JsonSerializer.Serialize(new { error = new string('e', 3_000), outputs = new { } }));
        await AppendStatePayloadAsync(runId, "plain", string.Empty, JsonSerializer.Serialize(new { outputs = new { baggage = Bomb + new string('x', 2 * 1024 * 1024) } }));
        var reads = new BodyReadObservation();

        using var scope = _fixture.BeginScope(builder =>
        {
            builder.RegisterDecorator<IArtifactStore>((_, _, inner) => new ObservedArtifactStore(inner, reads));
            builder.RegisterDecorator<IRunNodeOutputInflater>((_, _, inner) => new ObservedOutputInflater(inner, reads));
        });
        var observation = await scope.Resolve<IWorkflowRunNodeObservationReader>()
            .ReadAsync(new WorkflowRunNodeObservationRequest(runId, teamId, WorkflowRunViewScope.LineageMerged), CancellationToken.None);
        var source = scope.Resolve<IEnumerable<IRunPhaseSource>>().Single(value => value.SourceKey == WorkflowNodePhaseSource.Key);
        var phases = await source.ContributeAsync(new RunPhaseContext { RunId = runId, TeamId = teamId }, CancellationToken.None);

        observation.ShouldNotBeNull();
        observation!.Availability.ShouldBe(WorkflowRunViewAvailability.Available);
        observation.TopLevelLeaves["fan"].MapMetrics!.Count.ShouldBe(3);
        observation.TopLevelLeaves["fan"].MapMetrics!.Failed.ShouldBe(1);
        observation.TopLevelLeaves["fan"].MapMetrics!.ResultsCoverageState.ShouldBe(WorkflowRunNodeLeafState.Exact);
        observation.TopLevelLeaves["failed"].ErrorState.ShouldBe(WorkflowRunNodeLeafState.Truncated);
        observation.TopLevelLeaves["failed"].ErrorPrefix!.Length.ShouldBe(WorkflowRunNodeObservationReader.MaximumErrorCharacters);

        var map = phases.Single(value => value.Id == "fan");
        map.Kind.ShouldBe("map");
        map.Metrics.SucceededCount.ShouldBe(2);
        map.Metrics.FailedCount.ShouldBe(1);
        map.Metrics.Extra[WorkflowOutputKeys.MapResultsCoverage].GetProperty("includedBranches").GetInt32().ShouldBe(2);
        phases.Single(value => value.Id == "failed").Summary.ShouldEndWith("[truncated; the full error remains available in Trace.]");
        reads.ArtifactReads.ShouldBe(0);
        reads.InflaterReads.ShouldBe(0);
        JsonSerializer.Serialize(new { observation, phases }, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldNotContain(Bomb, Case.Sensitive);
    }

    [Fact]
    public async Task Oversized_map_coverage_is_explicitly_truncated_and_never_crosses_the_reader_boundary()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var artifactId = await PutBombArtifactAsync(teamId);
        var runId = await SeedSnapshotRunAsync(teamId, MapDefinition(), null, null, DateTimeOffset.UtcNow);
        await SeedCellAsync(runId, "fan", string.Empty, WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await SeedCellAsync(runId, "worker", "fan#0", WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await AppendStatePayloadAsync(runId, "fan", string.Empty, JsonSerializer.Serialize(new
        {
            outputs = new { count = 1, failed = 0, resultsCoverage = Bomb + new string('c', WorkflowRunNodeObservationReader.MaximumCoverageBytes) },
        }));

        using var scope = _fixture.BeginScope();
        var observation = await scope.Resolve<IWorkflowRunNodeObservationReader>()
            .ReadAsync(new WorkflowRunNodeObservationRequest(runId, teamId, WorkflowRunViewScope.LineageMerged), CancellationToken.None);

        observation.ShouldNotBeNull();
        var metrics = observation!.TopLevelLeaves["fan"].MapMetrics!;
        metrics.Count.ShouldBe(1);
        metrics.Failed.ShouldBe(0);
        metrics.ResultsCoverageState.ShouldBe(WorkflowRunNodeLeafState.Truncated);
        metrics.ResultsCoverage.ShouldBeNull();
        JsonSerializer.Serialize(observation).ShouldNotContain(Bomb, Case.Sensitive);
    }

    [Fact]
    public async Task Node_leaf_scope_tracks_the_selected_lineage_attempt_conflates_foreign_and_rejects_a_torn_state()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var artifactId = await PutBombArtifactAsync(teamId);
        var original = await SeedSnapshotRunAsync(teamId, MapDefinition(), null, null, DateTimeOffset.UtcNow.AddMinutes(-1));
        var latest = await SeedSnapshotRunAsync(teamId, MapDefinition(), original, original, DateTimeOffset.UtcNow);
        var foreign = await SeedSnapshotRunAsync(foreignTeamId, MapDefinition(), null, null, DateTimeOffset.UtcNow);
        await SeedCellAsync(original, "fan", string.Empty, WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await SeedCellAsync(original, "worker", "fan#0", WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await SeedCellAsync(latest, "fan", string.Empty, WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await SeedCellAsync(latest, "worker", "fan#0", WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await AppendStatePayloadAsync(original, "fan", string.Empty, """{"outputs":{"count":1,"failed":0}}""");
        await AppendStatePayloadAsync(latest, "fan", string.Empty, """{"outputs":{"count":2,"failed":1}}""");

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IWorkflowRunNodeObservationReader>();
        var merged = await reader.ReadAsync(new WorkflowRunNodeObservationRequest(original, teamId, WorkflowRunViewScope.LineageMerged), CancellationToken.None);
        var attempt = await reader.ReadAsync(new WorkflowRunNodeObservationRequest(original, teamId, WorkflowRunViewScope.AttemptOnly), CancellationToken.None);
        var hidden = await reader.ReadAsync(new WorkflowRunNodeObservationRequest(foreign, teamId, WorkflowRunViewScope.AttemptOnly), CancellationToken.None);

        merged!.Metadata.Cells.Single(value => value.NodeId == "fan").SourceRunId.ShouldBe(latest);
        merged.TopLevelLeaves["fan"].MapMetrics!.Count.ShouldBe(2);
        merged.TopLevelLeaves["fan"].MapMetrics!.Failed.ShouldBe(1);
        attempt!.Metadata.Cells.Single(value => value.NodeId == "fan").SourceRunId.ShouldBe(original);
        attempt.TopLevelLeaves["fan"].MapMetrics!.Count.ShouldBe(1);
        hidden.ShouldBeNull("missing and foreign requested runs remain intentionally conflated");

        var staleMetadata = attempt.Metadata with
        {
            Cells = attempt.Metadata.Cells.Select(value => value.NodeId == "fan"
                ? value with { Status = NodeStatus.Running, CompletedAt = null }
                : value).ToList(),
        };
        var torn = await new WorkflowRunNodeObservationReader(new FixedMetadataReader(staleMetadata), scope.Resolve<CodeSpaceDbContext>())
            .ReadAsync(new WorkflowRunNodeObservationRequest(original, teamId, WorkflowRunViewScope.AttemptOnly), CancellationToken.None);
        torn!.Availability.ShouldBe(WorkflowRunViewAvailability.Unavailable,
            "a state change between metadata and leaf reads is rejected instead of combining the old status with the new output");
        torn.TopLevelLeaves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Map_plan_reader_and_both_consumers_share_exact_bounded_inline_leaves_without_full_detail_or_baggage()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSnapshotRunAsync(teamId, PlannerMapDefinition(), null, null, DateTimeOffset.UtcNow);
        var artifactId = await PutBombArtifactAsync(teamId);
        await SeedCellAsync(runId, "planner", string.Empty, WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await SeedCellAsync(runId, "fan", string.Empty, WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), artifactId);
        await AppendStatePayloadAsync(runId, "planner", string.Empty, JsonSerializer.Serialize(new
        {
            outputs = new
            {
                json = new { subtasks = new[] { new { id = "a", title = "Research" }, new { id = "b", title = "Write" } }, baggage = Bomb + new string('x', 2 * 1024 * 1024) },
                model = "metis-coder-max", inputTokens = 7, outputTokens = 11, costUsd = 0.02m,
            },
        }));

        using var scope = _fixture.BeginScope();
        var bundle = scope.Resolve<IWorkflowMapPlanObservationBundle>();
        var observation = await bundle.GetAsync(runId, teamId, CancellationToken.None);
        var timeline = scope.Resolve<IEnumerable<IRunTimelineSource>>().Single(value => value.SourceKey == MapPlannerTimelineMap.Key);
        var factsSource = scope.Resolve<IEnumerable<IJournalFactsSource>>().Single(value => value is MapPlannerFactsSource);
        var events = await timeline.ContributeAsync(new RunTimelineContext { RunId = runId, TeamId = teamId }, CancellationToken.None);
        var facts = await factsSource.GatherAsync(runId, teamId, CancellationToken.None);

        var planner = observation!.Planners.ShouldHaveSingleItem();
        planner.SubtasksState.ShouldBe(WorkflowMapPlanLeafState.Exact);
        planner.SubtasksTotalCount.ShouldBe(2);
        planner.ModelUsageState.ShouldBe(WorkflowMapPlanLeafState.Exact);
        events.ShouldHaveSingleItem().Title.ShouldBe("Planned 2 subtasks");
        facts["map-plan-planner"].Plan!.Select(value => value.Title).ShouldBe(new[] { "Research", "Write" });
        facts["map-plan-planner"].ModelCall!.Tokens.ShouldBe(18);
        var wire = JsonSerializer.Serialize(new { observation, events, facts });
        wire.ShouldNotContain(Bomb, Case.Sensitive);
        wire.Length.ShouldBeLessThan(20_000, "the unrelated 2 MiB producer baggage never crosses the observation seam");
    }

    [Fact]
    public async Task Map_plan_reader_batch_resolves_verified_artifacts_and_marks_oversized_or_foreign_content_incomplete()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var exactBytes = Encoding.UTF8.GetBytes("{\"subtasks\":[\"One\",\"Two\"]}");
        var oversizedBytes = Encoding.UTF8.GetBytes("{\"subtasks\":[\"" + new string('z', WorkflowMapPlanObservationReader.MaximumLeafBytes) + "\"]}");
        Guid exactId;
        Guid oversizedId;
        Guid foreignId;
        using (var artifactScope = _fixture.BeginScope())
        {
            var store = artifactScope.Resolve<IArtifactStore>();
            exactId = await store.PutAsync(teamId, exactBytes, "application/json", CancellationToken.None);
            oversizedId = await store.PutAsync(teamId, oversizedBytes, "application/json", CancellationToken.None);
            foreignId = await store.PutAsync(foreignTeamId, Encoding.UTF8.GetBytes("{\"subtasks\":[\"secret\"]}"), "application/json", CancellationToken.None);
        }

        var exactRun = await SeedSnapshotRunAsync(teamId, PlannerMapDefinition(), null, null, DateTimeOffset.UtcNow);
        var oversizedRun = await SeedSnapshotRunAsync(teamId, PlannerMapDefinition(), null, null, DateTimeOffset.UtcNow);
        var foreignArtifactRun = await SeedSnapshotRunAsync(teamId, PlannerMapDefinition(), null, null, DateTimeOffset.UtcNow);
        await SeedPlannerArtifactAsync(exactRun, exactId, exactBytes.Length);
        await SeedPlannerArtifactAsync(oversizedRun, oversizedId, oversizedBytes.Length);
        await SeedPlannerArtifactAsync(foreignArtifactRun, foreignId, Encoding.UTF8.GetByteCount("{\"subtasks\":[\"secret\"]}"));

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IWorkflowMapPlanObservationReader>();
        var exact = await reader.ReadAsync(new WorkflowMapPlanObservationRequest(exactRun, teamId, WorkflowRunViewScope.AttemptOnly), CancellationToken.None);
        var oversized = await reader.ReadAsync(new WorkflowMapPlanObservationRequest(oversizedRun, teamId, WorkflowRunViewScope.AttemptOnly), CancellationToken.None);
        var unavailable = await reader.ReadAsync(new WorkflowMapPlanObservationRequest(foreignArtifactRun, teamId, WorkflowRunViewScope.AttemptOnly), CancellationToken.None);
        var hidden = await reader.ReadAsync(new WorkflowMapPlanObservationRequest(exactRun, foreignTeamId, WorkflowRunViewScope.AttemptOnly), CancellationToken.None);

        exact!.Planners.ShouldHaveSingleItem().SubtasksState.ShouldBe(WorkflowMapPlanLeafState.Exact);
        exact.Planners[0].SubtasksTotalCount.ShouldBe(2);
        oversized!.Planners.ShouldHaveSingleItem().SubtasksState.ShouldBe(WorkflowMapPlanLeafState.Truncated);
        oversized.Planners[0].Subtasks.ShouldBeNull("an oversized prefix is never promoted to a normal plan");
        unavailable!.Planners.ShouldHaveSingleItem().SubtasksState.ShouldBe(WorkflowMapPlanLeafState.Unavailable,
            "missing and cross-team artifacts are conflated and never leak bytes or identity");
        hidden.ShouldBeNull("foreign and absent run ids remain intentionally conflated");

        var timeline = scope.Resolve<IEnumerable<IRunTimelineSource>>().Single(value => value.SourceKey == MapPlannerTimelineMap.Key);
        var factsSource = scope.Resolve<IEnumerable<IJournalFactsSource>>().Single(value => value is MapPlannerFactsSource);
        (await timeline.ContributeAsync(new RunTimelineContext { RunId = oversizedRun, TeamId = teamId }, CancellationToken.None))
            .ShouldHaveSingleItem().Kind.ShouldBe("observation.coverage");
        var facts = await factsSource.GatherAsync(oversizedRun, teamId, CancellationToken.None);
        facts["map-plan-planner"].Plan.ShouldBeNull();
        facts["map-plan-planner"].ObservationCoverage!.ShouldHaveSingleItem().Reason.ShouldBe(CodeSpace.Messages.Dtos.Sessions.Journal.JournalObservationCoverageReason.TruncatedLeaf);
    }

    [Fact]
    public async Task Pending_wait_observation_returns_only_a_bounded_prompt_and_conflates_foreign_runs()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSnapshotRunAsync(teamId, Definition("bounded wait"), null, null, DateTimeOffset.UtcNow);
        var prefix = new string('p', WorkflowRunPendingWaitObservationReader.MaximumPromptCharacters);
        var bomb = Bomb + new string('x', 2 * 1024 * 1024);
        using (var seed = _fixture.BeginScope())
        {
            seed.Resolve<CodeSpaceDbContext>().WorkflowRunWait.Add(new CodeSpace.Core.Persistence.Entities.WorkflowRunWait
            {
                Id = Guid.NewGuid(), RunId = runId, NodeId = "approval", WaitKind = WorkflowWaitKinds.Approval,
                Token = Guid.NewGuid().ToString("N"), Status = WorkflowWaitStatuses.Pending, CreatedAt = DateTimeOffset.UtcNow,
                PayloadJson = JsonSerializer.Serialize(new { prompt = prefix + "tail", baggage = bomb }),
            });
            await seed.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
        }
        await BulkSeedResolvedWaitsAsync(runId, 10_000);

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IWorkflowRunPendingWaitObservationReader>();
        var observed = await reader.ReadAsync(runId, teamId, CancellationToken.None);
        var hidden = await reader.ReadAsync(runId, foreignTeamId, CancellationToken.None);
        var plan = await ExplainPendingWaitQueryAsync(scope.Resolve<CodeSpaceDbContext>(), runId, teamId);

        observed.ShouldNotBeNull();
        observed!.Wait.ShouldNotBeNull();
        observed.Wait!.PromptState.ShouldBe(WorkflowRunPendingWaitPromptState.Truncated);
        observed.Wait.PromptPrefix.ShouldBe(prefix);
        var wire = JsonSerializer.Serialize(observed);
        wire.ShouldNotContain(Bomb, Case.Sensitive);
        wire.Length.ShouldBeLessThan(5_000);
        hidden.ShouldBeNull();
        plan.ShouldContain("idx_workflow_run_wait_pending_created", Case.Sensitive,
            "the Suspended UI polls this observation, so resolved history must not be scanned or sorted");
        plan.ShouldNotContain("Seq Scan on workflow_run_wait", Case.Sensitive);
        plan.ShouldNotContain("Sort  (", Case.Sensitive);
    }

    private async Task<Guid> PutBombArtifactAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IArtifactStore>().PutAsync(teamId, Encoding.UTF8.GetBytes(Bomb + new string('a', 16 * 1024)), "text/plain", CancellationToken.None);
    }

    private async Task SeedPlannerArtifactAsync(Guid runId, Guid artifactId, int sizeBytes)
    {
        var bombArtifact = await PutBombArtifactAsync(await RunTeamAsync(runId));
        await SeedCellAsync(runId, "planner", string.Empty, WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), bombArtifact);
        await SeedCellAsync(runId, "fan", string.Empty, WorkflowRunRecordTypes.NodeCompleted, Guid.NewGuid(), bombArtifact);
        await AppendStatePayloadAsync(runId, "planner", string.Empty, JsonSerializer.Serialize(new
        {
            outputs = new Dictionary<string, object>
            {
                ["json"] = new Dictionary<string, object> { ["$artifact_ref"] = new { id = artifactId, size_bytes = sizeBytes, content_type = "application/json" } },
            },
        }));
    }

    private async Task<Guid> RunTeamAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var teamId = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.Where(value => value.Id == runId).Select(value => value.TeamId).SingleAsync();
        return teamId;
    }

    private async Task<Guid> SeedSnapshotRunAsync(Guid teamId, WorkflowDefinition definition, Guid? parentRunId, Guid? rootRunId, DateTimeOffset createdAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var definitionJson = JsonSerializer.Serialize(definition, WorkflowJson.Options);

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = parentRunId is null ? WorkflowRunSourceTypes.Snapshot : WorkflowRunSourceTypes.Rerun,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { body = Bomb }),
            RequestMetadataJson = JsonSerializer.Serialize(new { body = Bomb }), Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = createdAt, VerifiedAt = createdAt, NormalizedAt = createdAt,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = parentRunId is null ? WorkflowRunSourceTypes.Snapshot : WorkflowRunSourceTypes.Rerun,
            DefinitionSnapshotJson = definitionJson, DefinitionSnapshotHash = DefinitionHash.Compute(definition),
            ParentRunId = parentRunId, RootRunId = rootRunId, Status = WorkflowRunStatus.Success, OutputsJson = JsonSerializer.Serialize(new { body = Bomb }),
            Error = Bomb, StartedAt = createdAt, CompletedAt = createdAt.AddSeconds(1), CreatedDate = createdAt,
            CreatedBy = SystemUsers.SeederId, LastModifiedDate = createdAt.AddSeconds(1), LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        return runId;
    }

    private async Task SeedCellAsync(Guid runId, string nodeId, string iterationKey, string recordType, Guid agentRunId, Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.NodeStarted, NodeId = nodeId,
            IterationKey = iterationKey, OccurredAt = now.AddSeconds(-1), PayloadJson = JsonSerializer.Serialize(new { inputs = new { body = Bomb }, config = new { body = Bomb } }),
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = recordType, NodeId = nodeId, IterationKey = iterationKey,
            OccurredAt = now, PayloadJson = JsonSerializer.Serialize(new { outputs = new { body = Bomb, artifact = new Dictionary<string, object> { ["$artifact_ref"] = artifactId } }, error = Bomb }),
        });
        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = runId, NodeId = nodeId, IterationKey = iterationKey, WaitKind = WorkflowWaitKinds.AgentRun,
            Token = agentRunId.ToString(), Status = WorkflowWaitStatuses.Resolved, PayloadJson = JsonSerializer.Serialize(new { body = Bomb }), CreatedAt = now, ResolvedAt = now,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<Guid> SeedChildRunAsync(Guid teamId, WorkflowDefinition definition, Guid parentRunId, DateTimeOffset createdAt)
    {
        var runId = await SeedSnapshotRunAsync(teamId, definition, parentRunId, null, createdAt);
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.Where(run => run.Id == runId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.SourceType, WorkflowRunSourceTypes.ChildWorkflow)).ConfigureAwait(false);
        return runId;
    }

    private async Task BulkSeedCellsAsync(Guid runId, int count, bool includeOneToastedBomb)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        const string sql = """
            INSERT INTO workflow_run_record (id, run_id, record_type, node_id, iteration_key, occurred_at, payload_json)
            SELECT gen_random_uuid(), @run_id, 'node.completed', 'leaf', 'map#' || lpad(value::text, 5, '0'), @occurred_at,
                   CASE WHEN @with_bomb AND value = 1 THEN jsonb_build_object('outputs', repeat(@bomb, 65536)) ELSE '{}'::jsonb END
            FROM generate_series(1, @count) AS value
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("occurred_at", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("with_bomb", includeOneToastedBomb);
        command.Parameters.AddWithValue("bomb", Bomb);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        await using var analyze = new NpgsqlCommand("ANALYZE workflow_run_record", connection);
        await analyze.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task BulkSeedResolvedWaitsAsync(Guid runId, int count)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        const string sql = """
            INSERT INTO workflow_run_wait (id, run_id, node_id, iteration_key, wait_kind, token, status, payload_jsonb, created_at, resolved_at)
            SELECT gen_random_uuid(), @run_id, 'history', 'map#' || lpad(value::text, 5, '0'), 'Approval', gen_random_uuid()::text,
                   'Resolved', jsonb_build_object('prompt', @bomb), @created_at - make_interval(secs => value), @created_at
            FROM generate_series(1, @count) AS value
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("bomb", Bomb);
        command.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        await using var analyze = new NpgsqlCommand("ANALYZE workflow_run_wait", connection);
        await analyze.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task AppendStatePayloadAsync(Guid runId, string nodeId, string iterationKey, string payload)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var prior = await db.WorkflowRunRecord.AsNoTracking().Where(value => value.RunId == runId && value.NodeId == nodeId && value.IterationKey == iterationKey)
            .OrderByDescending(value => value.Sequence).FirstAsync().ConfigureAwait(false);
        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = prior.RecordType, NodeId = nodeId, IterationKey = iterationKey,
            OccurredAt = prior.OccurredAt.AddMilliseconds(1), PayloadJson = payload,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<string> ExplainCellQueryAsync(CodeSpaceDbContext db, Guid runId)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + WorkflowRunViewMetadataReader.CellMetadataSql, connection);
            command.Parameters.AddWithValue("run_ids", new[] { runId });
            command.Parameters.Add("node_id", NpgsqlTypes.NpgsqlDbType.Text).Value = DBNull.Value;
            command.Parameters.Add("iteration_key", NpgsqlTypes.NpgsqlDbType.Text).Value = DBNull.Value;
            command.Parameters.AddWithValue("take", WorkflowRunViewMetadataReader.MaximumCells + 1);
            command.Parameters.AddWithValue("max_identity_chars", WorkflowRunViewMetadataReader.MaximumIdentityCharacters);
            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) lines.Add(reader.GetString(0));
            return string.Join('\n', lines);
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> ExplainPendingWaitQueryAsync(CodeSpaceDbContext db, Guid runId, Guid teamId)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + WorkflowRunPendingWaitObservationReader.Sql, connection);
            command.Parameters.AddWithValue("run_id", runId);
            command.Parameters.AddWithValue("team_id", teamId);
            command.Parameters.AddWithValue("pending_status", WorkflowWaitStatuses.Pending);
            command.Parameters.AddWithValue("max_prompt_chars", WorkflowRunPendingWaitObservationReader.MaximumPromptCharacters);
            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) lines.Add(reader.GetString(0));
            return string.Join('\n', lines);
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static WorkflowDefinition Definition(string prompt) => new()
    {
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Label = "Start", Config = WorkflowsTestSeed.Json(JsonSerializer.Serialize(new { prompt })), Inputs = WorkflowsTestSeed.Json(JsonSerializer.Serialize(new { body = Bomb })), Position = new NodePosition { X = 1, Y = 2 } },
            new() { Id = "work", TypeKey = "builtin.terminal", Label = "Work", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson(), Position = new NodePosition { X = 3, Y = 4 } },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "work", Condition = "ok" } },
        Inputs = new[] { new WorkflowVariable { Name = "secret", Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") } },
        Outputs = new[] { new WorkflowVariable { Name = "result", Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") } },
    };

    private static WorkflowDefinition MapDefinition() => new()
    {
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "fan", TypeKey = MapFanout.ContainerKind, Label = "Fan", Config = WorkflowsTestSeed.Json(JsonSerializer.Serialize(new { body = Bomb })), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "worker", TypeKey = "builtin.terminal", ParentId = "fan", Label = "Worker", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "failed", TypeKey = "builtin.terminal", Label = "Failed", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "plain", TypeKey = "builtin.terminal", Label = "Plain", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>(),
    };

    private static WorkflowDefinition PlannerMapDefinition() => new()
    {
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "planner", TypeKey = "llm.complete", Label = "Planner", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "fan", TypeKey = MapFanout.ContainerKind, Label = "Fan", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"items":"{{nodes.planner.outputs.json.subtasks}}"}""") },
            new() { Id = "worker", TypeKey = "builtin.terminal", ParentId = "fan", Label = "Worker", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>(),
    };

    private sealed class BodyReadObservation
    {
        public int ArtifactReads;
        public int InflaterReads;
    }

    private sealed class ObservedArtifactStore : IArtifactStore
    {
        private readonly IArtifactStore _inner;
        private readonly BodyReadObservation _observation;

        public ObservedArtifactStore(IArtifactStore inner, BodyReadObservation observation) { _inner = inner; _observation = observation; }
        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken) => _inner.PutAsync(teamId, bytes, contentType, cancellationToken);
        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) { Interlocked.Increment(ref _observation.ArtifactReads); return _inner.GetBytesAsync(teamId, artifactId, cancellationToken); }
        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) { Interlocked.Increment(ref _observation.ArtifactReads); return _inner.GetMetadataAsync(teamId, artifactId, cancellationToken); }
    }

    private sealed class ObservedOutputInflater : IRunNodeOutputInflater
    {
        private readonly IRunNodeOutputInflater _inner;
        private readonly BodyReadObservation _observation;

        public ObservedOutputInflater(IRunNodeOutputInflater inner, BodyReadObservation observation) { _inner = inner; _observation = observation; }
        public Task<WorkflowRunDetail> InflateAsync(WorkflowRunDetail run, Guid teamId, CancellationToken cancellationToken) { Interlocked.Increment(ref _observation.InflaterReads); return _inner.InflateAsync(run, teamId, cancellationToken); }
        public Task<WorkflowRunDetail> InflateAsync(WorkflowRunDetail run, Guid teamId, IReadOnlySet<string> nodeIds, CancellationToken cancellationToken) { Interlocked.Increment(ref _observation.InflaterReads); return _inner.InflateAsync(run, teamId, nodeIds, cancellationToken); }
    }

    private sealed class FixedMetadataReader : IWorkflowRunViewMetadataReader
    {
        private readonly WorkflowRunViewMetadata _metadata;

        public FixedMetadataReader(WorkflowRunViewMetadata metadata) { _metadata = metadata; }

        public Task<WorkflowRunViewMetadata?> ReadAsync(Guid runId, Guid teamId, WorkflowRunViewScope scope, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowRunViewMetadata?>(_metadata);
    }
}
