using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Dynamic-workflows substrate (PR1) — a <c>WorkflowRun</c> whose definition is an INLINE FROZEN
/// SNAPSHOT carried by the run itself, with NO <c>workflow</c> row and NO <c>workflow_version</c>
/// row. The run flows through the EXACT same durable engine (executor → suspend/resume → dispatch)
/// as an authored run; only the definition SOURCE forks. This is the load-bearing tier: the engine
/// is real, Postgres is real, and the runs are staged by the real <see cref="IRunFromSnapshotStarter"/>.
///
/// <para>Pinned guarantees:
///   (a) a snapshot run walks start → terminal and lands Success through the real engine;
///   (b) NO workflow / workflow_version row is created for it (the substrate's core promise);
///   (c) a SUSPEND → RESUME cycle re-loads the inline snapshot on re-entry (durable resume reads
///       the run's own definition, not a version row);
///   (d) a corrupted snapshot hash trips the same tamper guard an authored version gets;
///   (e) the AUTHORED path is unaffected — proven by the rest of the engine/workflow suites;
///   (f) a finished snapshot/dynamic run is REPLAYABLE — replay clones the run's exact frozen
///       definition + variable snapshot onto a NEW snapshot run that re-walks the engine, freezing
///       plain values against live mutation and carrying replay lineage. This used to throw
///       NotSupportedException (snapshot runs had no persisted version to re-pin); the
///       <c>WorkflowService.ReplayRunAsync</c> snapshot branch closes that gap.</para>
///
/// <para>Tier: high-fidelity — real <see cref="IRunFromSnapshotStarter"/> (runs DefinitionValidator
/// + DefinitionHash + the dispatcher's CAS) and real <see cref="IWorkflowEngine"/> over real
/// Postgres. No mocks.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SnapshotRunFlowTests
{
    private readonly PostgresFixture _fixture;
    public SnapshotRunFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Snapshot_run_walks_to_terminal_success_through_the_real_engine()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runId = await StartSnapshotAsync(teamId, userId, EchoInputDefinition(), launchPayloadJson: """{"ticket":"SNAP-1"}""");

        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "a snapshot run must validate + walk start → terminal through the real engine, same as an authored run");
        run.WorkflowId.ShouldBeNull("a snapshot run is not a child of any workflow");
        run.WorkflowVersion.ShouldBeNull("a snapshot run has no pinned version");
        run.DefinitionSnapshotJson.ShouldNotBeNull("the inline frozen definition is the run's own definition source");

        using var outputs = JsonDocument.Parse(run.OutputsJson!);
        outputs.RootElement.GetProperty("echoed").GetString().ShouldBe("SNAP-1",
            customMessage: "the launch payload must map by-name onto {{input.ticket}} for a snapshot run too");
    }

    [Fact]
    public async Task Snapshot_run_creates_no_workflow_and_no_workflow_version_row()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var workflowCountBefore = await CountWorkflowsAsync(teamId);
        var versionCountBefore = await CountWorkflowVersionsAsync();

        var runId = await StartSnapshotAsync(teamId, userId, EchoInputDefinition(), launchPayloadJson: "{}");
        await RunEngineAsync(runId);

        (await LoadRunAsync(runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        // The substrate's core promise: a one-shot snapshot run is a RUN, not a persisted/listable
        // workflow. Zero new rows in either table — counting both team-scoped (workflow) and
        // global (workflow_version, which has no team column) confirms NOTHING leaked.
        (await CountWorkflowsAsync(teamId)).ShouldBe(workflowCountBefore,
            customMessage: "a snapshot run must create NO workflow row — it is not a child of any workflow");
        (await CountWorkflowVersionsAsync()).ShouldBe(versionCountBefore,
            customMessage: "a snapshot run must create NO workflow_version row — its definition is inline on the run");

        // And the run row itself points at neither (the FK columns the migration relaxed to NULL).
        var run = await LoadRunAsync(runId);
        run.WorkflowId.ShouldBeNull();
        run.WorkflowVersion.ShouldBeNull();
    }

    [Fact]
    public async Task Snapshot_run_denormalises_source_type_onto_the_run_row()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runId = await StartSnapshotAsync(teamId, userId, EchoInputDefinition(), launchPayloadJson: "{}");

        // The team runs index filters + orders on workflow_run.source_type WITHOUT joining the request
        // (and its partial keyset index excludes children by that column). So the real starter MUST
        // denormalise source_type onto the run row at creation — left null it would violate NOT NULL and
        // the substrate that makes the index JOIN-free would be a lie. A snapshot run is sourced 'snapshot'.
        var run = await LoadRunAsync(runId);
        run.SourceType.ShouldBe(WorkflowRunSourceTypes.Snapshot,
            customMessage: "RunFromSnapshotStarter must write source_type onto the run, not just the request");

        var request = await LoadRequestAsync(run.RunRequestId);
        run.SourceType.ShouldBe(request.SourceType,
            customMessage: "the run's denorm must mirror its request's source_type exactly");

        // The actor is denormalised the same way — a task/snapshot run is user-launched, so actor_id == the launcher.
        run.ActorId.ShouldBe(userId, "the snapshot starter denormalises the launching actor onto the run");
        run.ActorId.ShouldBe(request.ActorId, "the run's actor denorm mirrors its request's actor_id");
    }

    [Fact]
    public async Task Snapshot_run_suspends_then_resumes_reloading_the_inline_definition_on_reentry()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runId = await StartSnapshotAsync(teamId, userId, ApprovalDefinition(), launchPayloadJson: "{}");

        // Pass 1 — the wait_approval node parks the run. It is NOT terminal.
        await RunEngineAsync(runId);
        (await LoadRunAsync(runId)).Status.ShouldBe(WorkflowRunStatus.Suspended,
            customMessage: "the wait_approval node must suspend the snapshot run, same as on an authored run");

        // Resolve the wait + flip Suspended → Pending (mirrors the operator-approve resume path),
        // then re-dispatch into the engine. The durable walker re-enters and MUST re-load the
        // inline snapshot from the run row (there is no workflow_version to fall back to) to
        // continue past the resumed node.
        await ResumeApprovalWaitAsync(runId);
        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "durable resume must re-load the inline snapshot on re-entry and walk the run to its terminal — " +
                           "if the engine couldn't source the definition from the run, the resumed pass would fail to find the graph");
        run.WorkflowId.ShouldBeNull();
    }

    [Fact]
    public async Task Snapshot_run_with_a_corrupted_hash_trips_the_tamper_guard()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runId = await StartSnapshotAsync(teamId, userId, EchoInputDefinition(), launchPayloadJson: "{}");

        // Corrupt the stored snapshot hash so it no longer matches the frozen definition_json. This
        // is exactly the authored-version tamper case (release_hash drift), applied to the inline
        // snapshot — the engine must refuse with the same fatal Failure semantics.
        await CorruptSnapshotHashAsync(runId);

        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure,
            customMessage: "a snapshot whose hash no longer matches its definition_json must be rejected pre-walk, like a tampered authored version");
        run.Error.ShouldNotBeNull();
        run.Error!.ShouldContain("tampering",
            customMessage: "the bootstrap-failure ledger surfaces the tamper exception so the operator sees the cause");
    }

    [Fact]
    public async Task Snapshot_run_pre_flights_the_act_as_user_identity_gate_on_resume()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, userId);

        // Identity NOT linked → the gate must throw at click time. The whole point: on a snapshot run
        // the gate sources the definition from the run's INLINE snapshot. If the gate still read only
        // workflow_version (WorkflowId/WorkflowVersion are NULL for a snapshot run), it would find no
        // definition and silently no-op — letting the wait resolve with NO identity pre-flight.
        var (repoId, providerInstanceId) = await SeedRepoAsync(teamId, userId, linkIdentity: false);

        var runId = await StartSnapshotAsync(teamId, userId, ReviewDefinition(channelId, userId, repoId), launchPayloadJson: "{}");

        await RunEngineAsync(runId);   // posts the card + parks on flow.wait_action (git.pr_review downstream)
        var messageId = await ReadPostedMessageIdAsync(runId);

        var ex = await Should.ThrowAsync<ActorIdentityRequiredException>(() => RespondAsync(teamId, messageId, userId));
        ex.ProviderInstanceId.ShouldBe(providerInstanceId,
            customMessage: "the gate must derive the act-as-user requirement from the snapshot run's INLINE definition — " +
                           "a workflow_version-only read would no-op (WorkflowId/WorkflowVersion are NULL on a snapshot run)");

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Suspended,
            customMessage: "the wait must NOT resolve — the snapshot run stays parked so a retry-after-link can succeed");
        run.WorkflowId.ShouldBeNull("a snapshot run is not a child of any workflow");
    }

    [Fact]
    public async Task Snapshot_run_with_a_corrupted_definition_json_trips_the_tamper_guard()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runId = await StartSnapshotAsync(teamId, userId, EchoInputDefinition(), launchPayloadJson: "{}");

        // The INVERSE of the hash-corruption case: mutate the frozen definition_json out-of-band while
        // leaving the stored hash untouched. On re-load the engine recomputes the hash from the mutated
        // JSON, finds it no longer matches the (untouched) stored hash, and must refuse — proving the
        // guard catches a tampered definition, not just a tampered hash.
        await CorruptSnapshotJsonAsync(runId);

        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure,
            customMessage: "a snapshot whose definition_json was mutated out-of-band must be rejected pre-walk by the recompute-vs-stored-hash check");
        run.Error.ShouldNotBeNull();
        run.Error!.ShouldContain("tampering");
    }

    [Fact]
    public async Task Starting_a_snapshot_run_with_an_invalid_definition_is_rejected_before_any_db_write()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runsBefore = await CountRunsAsync(teamId);

        // The starter must DefinitionValidator-validate BEFORE staging — a dangling edge (To a node that
        // doesn't exist) is rejected, and the throw lands strictly before any workflow_run row is written.
        await Should.ThrowAsync<WorkflowValidationException>(() =>
            StartSnapshotAsync(teamId, userId, InvalidDanglingEdgeDefinition(), launchPayloadJson: "{}"));

        (await CountRunsAsync(teamId)).ShouldBe(runsBefore,
            customMessage: "an invalid definition must be rejected before any DB write — no orphaned run row left behind");
    }

    [Fact]
    public async Task Replaying_a_snapshot_run_clones_the_frozen_definition_and_freezes_variable_values()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // The snapshot run reads + freezes this team variable on its first pass.
        await SetTeamVarAsync(teamId, userId, "MIRROR", "snapshot-frozen-value");

        var originalRunId = await StartSnapshotAsync(teamId, userId, EchoTeamVarSnapshotDefinition(), launchPayloadJson: "{}");
        await RunEngineAsync(originalRunId);

        var original = await LoadRunAsync(originalRunId);
        original.Status.ShouldBe(WorkflowRunStatus.Success);
        JsonDocument.Parse(original.OutputsJson!).RootElement.GetProperty("out").GetString().ShouldBe("snapshot-frozen-value");

        // Mutate the LIVE team variable after the original froze it. A correct replay must read the frozen
        // snapshot value, NOT this mutated live value — same rotation-safety contract authored replays obey.
        await SetTeamVarAsync(teamId, userId, "MIRROR", "mutated-after-original");

        // THE HEADLINE: replaying a snapshot/dynamic run used to throw NotSupportedException. It now stages a
        // NEW snapshot run carrying the original's EXACT frozen definition + a clone of its variable snapshot.
        var replayRunId = await ReplayAsync(originalRunId, teamId, userId);
        await RunEngineAsync(replayRunId);

        var replay = await LoadRunAsync(replayRunId);

        replay.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "a replayed snapshot run must walk start → terminal through the real engine");
        replay.WorkflowId.ShouldBeNull("the replay of a snapshot run is itself a snapshot run — not a child of any workflow");
        replay.WorkflowVersion.ShouldBeNull();
        replay.DefinitionSnapshotJson.ShouldBe(original.DefinitionSnapshotJson,
            customMessage: "replay must clone the original's EXACT frozen definition JSON byte-for-byte (no re-validate / re-freeze)");
        replay.DefinitionSnapshotHash.ShouldBe(original.DefinitionSnapshotHash,
            customMessage: "replay reuses the original hash — the engine's snapshot tamper-check passes because the hash travels with the JSON");
        replay.ParentRunId.ShouldBe(originalRunId, "replay lineage: the new run points back at the original");

        JsonDocument.Parse(replay.OutputsJson!).RootElement.GetProperty("out").GetString()
            .ShouldBe("snapshot-frozen-value",
                customMessage: "replay MUST read the FROZEN snapshot value, NOT the (now-mutated) live variable — " +
                               "proving the cloned variable snapshot drove the replay scope path, not a fresh re-resolution");

        var request = await LoadRequestAsync(replay.RunRequestId);
        request.SourceType.ShouldBe(WorkflowRunSourceTypes.Replay, "the replay run's request is sourced as a replay");
        request.CausationId.ShouldBe(original.RunRequestId, "replay lineage: the request links back to the original's request");

        (await HasRunReplayedRecordAsync(replayRunId)).ShouldBeTrue(
            "the engine emits run.replayed once the cloned snapshot drives the replay scope path");
    }

    [Fact]
    public async Task Replaying_a_variable_less_snapshot_run_re_runs_from_the_frozen_payload_and_carries_lineage()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // No team/workflow variables → the original captures an EMPTY snapshot. Replay still works: it clones
        // the frozen definition + the frozen launch payload and re-runs through the engine. Proves the slice
        // doesn't depend on the original having variables to freeze (the common task-launch case).
        var originalRunId = await StartSnapshotAsync(teamId, userId, EchoInputDefinition(), launchPayloadJson: """{"ticket":"SNAP-R"}""");
        await RunEngineAsync(originalRunId);
        var original = await LoadRunAsync(originalRunId);
        original.Status.ShouldBe(WorkflowRunStatus.Success);

        var replayRunId = await ReplayAsync(originalRunId, teamId, userId);
        await RunEngineAsync(replayRunId);

        var replay = await LoadRunAsync(replayRunId);
        replay.Status.ShouldBe(WorkflowRunStatus.Success);
        replay.WorkflowId.ShouldBeNull("the replay of a snapshot run is still a snapshot run");
        replay.ParentRunId.ShouldBe(originalRunId, "replay lineage is preserved even with an empty variable snapshot");

        JsonDocument.Parse(replay.OutputsJson!).RootElement.GetProperty("echoed").GetString()
            .ShouldBe("SNAP-R", customMessage: "the replay reuses the original's frozen launch payload byte-for-byte");

        var request = await LoadRequestAsync(replay.RunRequestId);
        request.SourceType.ShouldBe(WorkflowRunSourceTypes.Replay);
        request.CausationId.ShouldBe(original.RunRequestId, "replay lineage: the request links back to the original's request");

        (await HasRunReplayedRecordAsync(replayRunId)).ShouldBeFalse(
            customMessage: "an empty variable snapshot takes the engine's FRESH scope path (isReplay=false), so replay lineage lives on " +
                           "ParentRunId + the request — NOT a run.replayed record. Pinned so a future engine change can't silently flip this asymmetry.");
    }

    [Fact]
    public async Task Replaying_another_teams_run_is_rejected_as_not_found()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runId = await StartSnapshotAsync(teamA, userA, EchoInputDefinition(), launchPayloadJson: "{}");
        await RunEngineAsync(runId);

        // Tenancy boundary: team B must NOT be able to replay team A's run. The load filters on
        // (id, team) and conflates not-found with not-yours — a cross-team replay is a KeyNotFound,
        // never a silent stage under the wrong team.
        await Should.ThrowAsync<KeyNotFoundException>(() => ReplayAsync(runId, teamB, userB));
    }

    [Fact]
    public async Task Snapshot_run_records_launch_scope_repositories_and_derives_projects_then_clones_on_replay()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (repoId, projA, projB) = await SeedRepoInTwoProjectsAsync(teamId, userId);

        // The real starter stamps the launch repo set AND derives the repo's projects (a repo can be in several).
        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(
                EchoInputDefinition(), teamId, userId, launchPayloadJson: "{}", scopeRepositoryIds: new[] { repoId }, projectionKind: "supervisor", CancellationToken.None);

        var run = await LoadRunAsync(runId);
        run.ScopeRepositoryIds.ShouldBe(new[] { repoId }, ignoreOrder: true, customMessage: "the launch repo set is stamped onto the run");
        run.ScopeProjectIds.ShouldBe(new[] { projA, projB }, ignoreOrder: true,
            customMessage: "projects are DERIVED from the repo's project_repository links at launch — a repo in two projects yields both");
        run.ProjectionKind.ShouldBe("supervisor", "the route's projection kind is denormalised onto the run");
        run.RunKind.ShouldBe(RunKinds.Task, "run_kind is the GENERATED classification of source_type=snapshot → task");

        // Replay clones the scope arrays verbatim (point-in-time snapshot — no re-derivation), like the frozen definition.
        await RunEngineAsync(runId);
        var replay = await LoadRunAsync(await ReplayAsync(runId, teamId, userId));
        replay.ScopeRepositoryIds.ShouldBe(new[] { repoId }, ignoreOrder: true, customMessage: "replay clones the scope repos");
        replay.ScopeProjectIds.ShouldBe(new[] { projA, projB }, ignoreOrder: true, "replay clones the derived projects — no re-derivation");
        replay.ProjectionKind.ShouldBe("supervisor", "replay clones the projection kind");
    }

    /// <summary>Seed a real repository linked into TWO projects (a repo may belong to several) — so the run's project derivation has a multi-project case to resolve.</summary>
    private async Task<(Guid RepoId, Guid ProjectA, Guid ProjectB)> SeedRepoInTwoProjectsAsync(Guid teamId, Guid userId)
    {
        var (repoId, _) = await SeedRepoAsync(teamId, userId, linkIdentity: false);
        var projA = Guid.NewGuid();
        var projB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        foreach (var (pid, slug) in new[] { (projA, "alpha"), (projB, "beta") })
            db.Project.Add(new Project { Id = pid, TeamId = teamId, Slug = slug, Name = slug, Description = null, CreatedDate = now, CreatedBy = userId, LastModifiedDate = now, LastModifiedBy = userId });
        foreach (var pid in new[] { projA, projB })
            db.ProjectRepository.Add(new ProjectRepository { ProjectId = pid, RepositoryId = repoId, TeamId = teamId, CreatedDate = now, CreatedBy = userId, LastModifiedDate = now, LastModifiedBy = userId });

        await db.SaveChangesAsync();
        return (repoId, projA, projB);
    }

    // ─── Definitions ────────────────────────────────────────────────────────────

    /// <summary>manual → terminal. One declared input "ticket"; the terminal echoes {{input.ticket}}.</summary>
    private static WorkflowDefinition EchoInputDefinition()
    {
        var terminalInputsJson = JsonSerializer.Serialize(new { echoed = "{{input.ticket}}" });

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Inputs = new[] { new WorkflowVariable { Name = "ticket", Schema = WorkflowsTestSeed.Json("""{"type":"string"}"""), Required = false } },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",   TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(terminalInputsJson) },
            },
            Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
        };
    }

    /// <summary>manual → terminal echoing {{team.MIRROR}} → output "out". Proves a snapshot run freezes its team-scoped plain variable and that REPLAY reads the frozen value, not the live (mutated) one.</summary>
    private static WorkflowDefinition EchoTeamVarSnapshotDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end",   TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"out":"{{team.MIRROR}}"}""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
    };

    /// <summary>manual → wait_approval → terminal. The approval node suspends the run for the resume test.</summary>
    private static WorkflowDefinition ApprovalDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "approval", TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.Json("""{"prompt":"Ship the snapshot run?"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "approval" },
            new() { From = "approval", To = "end" },
        },
    };

    /// <summary>manual → post → wait → git.pr_review (acts AS the responder) → terminal. git.pr_review's actAsUserId is wired to the wait's `by`, so resolving the wait must pre-flight the responder's linked identity.</summary>
    private static WorkflowDefinition ReviewDefinition(Guid channelId, Guid reviewerId, Guid repoId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new()
            {
                Id = "post",
                TypeKey = "chat.post_message",
                Config = WorkflowsTestSeed.EmptyJson(),
                Inputs = WorkflowsTestSeed.Json(JsonSerializer.Serialize(new
                {
                    conversationId = channelId.ToString(),
                    body = "Review PR #5?",
                    actions = new[] { new { key = "approve", label = "Approve" } },
                    allowedResponderUserIds = new[] { reviewerId.ToString() },
                })),
            },
            new() { Id = "wait", TypeKey = "flow.wait_action", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "token": "{{nodes.post.outputs.token}}" }""") },
            new()
            {
                Id = "review",
                TypeKey = "git.pr_review",
                Config = WorkflowsTestSeed.EmptyJson(),
                Inputs = WorkflowsTestSeed.Json(JsonSerializer.Serialize(new
                {
                    repositoryId = repoId.ToString(),
                    number = 5,
                    verdict = "{{nodes.wait.outputs.action}}",
                    actAsUserId = "{{nodes.wait.outputs.by}}",
                })),
            },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "post" },
            new() { From = "post", To = "wait" },
            new() { From = "wait", To = "review" },
            new() { From = "review", To = "end" },
        },
    };

    /// <summary>A structurally INVALID definition: an edge whose <c>To</c> targets a node that doesn't exist. DefinitionValidator rejects it, so the starter must throw before any DB write.</summary>
    private static WorkflowDefinition InvalidDanglingEdgeDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "ghost" } },   // 'ghost' node doesn't exist
    };

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> StartSnapshotAsync(Guid teamId, Guid userId, WorkflowDefinition definition, string? launchPayloadJson)
    {
        using var scope = _fixture.BeginScope();
        var starter = scope.Resolve<IRunFromSnapshotStarter>();
        return await starter.StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson, scopeRepositoryIds: null, projectionKind: null, CancellationToken.None);
    }

    /// <summary>Replay a finished run via the real production seam — the same call the ReplayRunCommand handler makes.</summary>
    private async Task<Guid> ReplayAsync(Guid originalRunId, Guid teamId, Guid actorUserId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().ReplayRunAsync(originalRunId, teamId, actorUserId, CancellationToken.None);
    }

    private async Task SetTeamVarAsync(Guid teamId, Guid userId, string name, string value)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IVariableService>().SetAsync(
            VariableScope.Team, teamId, teamId, name, VariableValueType.String,
            JsonString(value), null, userId, CancellationToken.None);
    }

    private static JsonElement JsonString(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();

    private async Task<WorkflowRunRequest> LoadRequestAsync(Guid requestId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowRunRequest.AsNoTracking().SingleAsync(r => r.Id == requestId);
    }

    private async Task<bool> HasRunReplayedRecordAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowRunRecord.AsNoTracking()
            .AnyAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunReplayed);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    /// <summary>Resolve the run's pending approval wait + flip Suspended → Pending, mirroring the operator-approve resume path. The resume service re-dispatches so the next RunEngineAsync re-enters the durable walker.</summary>
    private async Task ResumeApprovalWaitAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var waitId = await db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending)
            .Select(w => w.Id)
            .SingleAsync();

        var payload = JsonSerializer.Serialize(new { approved = true, comment = "ok" });
        var resumed = await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, payload, CancellationToken.None);
        resumed.ShouldBeTrue("resolving the snapshot run's pending wait must flip Suspended → Pending and re-dispatch");
    }

    private async Task CorruptSnapshotHashAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE workflow_run SET definition_snapshot_hash = 'deadbeef-not-the-real-hash' WHERE id = {runId}");
    }

    /// <summary>Mutate the frozen definition_json out-of-band (flip the terminal's echoed expression) while leaving the stored hash untouched — the recompute-vs-stored-hash check must then fire.</summary>
    private async Task CorruptSnapshotJsonAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE workflow_run SET definition_snapshot_jsonb = replace(definition_snapshot_jsonb::text, 'input.ticket', 'input.tampered')::jsonb WHERE id = {runId}");
    }

    private async Task<int> CountRunsAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowRun.AsNoTracking().CountAsync(r => r.TeamId == teamId);
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private async Task<int> CountWorkflowsAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.Workflow.AsNoTracking().CountAsync(w => w.TeamId == teamId);
    }

    private async Task<int> CountWorkflowVersionsAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowVersion.AsNoTracking().CountAsync();
    }

    private async Task RespondAsync(Guid teamId, Guid messageId, Guid actorUserId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMessageInteractionService>().RespondAsync(teamId, messageId, "approve", actorUserId, null, null, default);
    }

    private async Task<Guid> ReadPostedMessageIdAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
        var post = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "post");
        return Guid.Parse(JsonDocument.Parse(post.OutputsJson).RootElement.GetProperty("messageId").GetString()!);
    }

    private async Task<Guid> SeedChannelAsync(Guid teamId, Guid ownerId)
    {
        using var scope = _fixture.BeginScope();
        var slug = "snap-ident-" + Guid.NewGuid().ToString("N")[..8];
        return await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, ownerId, default);
    }

    /// <summary>Seed a Git provider instance + connection-credentialled repo under the existing team; optionally link the owner's own identity on that instance. Mirrors ResponderIdentityPrecheckFlowTests' seed.</summary>
    private async Task<(Guid RepositoryId, Guid ProviderInstanceId)> SeedRepoAsync(Guid teamId, Guid ownerId, bool linkIdentity)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        string Pat(string token) => encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = token }));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "instance",
            BaseUrl = $"https://git-{suffix}.local", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret")
        };
        var connection = new Credential
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService,
            AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = Pat("conn"), Status = CredentialStatus.Active
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, CredentialId = connection.Id,
            ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://git.local/acme/api", Status = RepositoryStatus.Active
        };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(connection);
        db.Repository.Add(repo);

        if (linkIdentity)
        {
            var actorCred = new Credential
            {
                Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, OwnerUserId = ownerId,
                Ownership = CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = "actor", EncryptedPayload = Pat("actor"), Status = CredentialStatus.Active
            };
            db.Credential.Add(actorCred);
            db.UserProviderIdentity.Add(new UserProviderIdentity
            {
                Id = Guid.NewGuid(), UserId = ownerId, ProviderInstanceId = instance.Id, CredentialId = actorCred.Id,
                ProviderUserId = "42", ProviderUsername = "tester"
            });
        }

        await db.SaveChangesAsync();

        return (repo.Id, instance.Id);
    }
}
