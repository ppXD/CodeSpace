using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Plans;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// The golden-run CORPUS for the room↔journal parity gate (<see cref="RoomJournalParityFlowTests"/>). One seed method per
/// canonical run SHAPE, each exercising a distinct set of room emitters (plan checklist, spawn agents, retry, respawn +
/// deep failure, live pending decision, rerun ladder) so the parity assertion runs over every surface the room can show.
///
/// <para>The seed primitives are the SAME ones <see cref="RoomProjectorFlowTests"/> + <see cref="JournalProjectorFlowTests"/>
/// use (raw entity inserts through the real <see cref="CodeSpaceDbContext"/>) — centralised here so the parity gate seeds a
/// shape ONCE and projects it through BOTH the room and the journal projector. Pure seeding: no assertions, no projection.</para>
/// </summary>
public static class SessionCorpusSeed
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The canonical run shapes the parity corpus spans — each one lights up a different room emitter branch.</summary>
    public enum Shape
    {
        /// <summary>A supervisor turn: plan (persisted work_plan → checklist) + a 2-agent spawn + success. Room: plan_checklist, agent_group, execution_map.</summary>
        SupervisorPlanSpawn,
        /// <summary>A plain single-agent run, no supervisor tape. Room: final_answer from the agent's own result; no beats in the journal.</summary>
        SingleAgent,
        /// <summary>A supervisor turn that failed a subtask then retried it. Room: the failed original + the retry's own card (the retry line/rationale is a journal ③ beat post-P6).</summary>
        Retry,
        /// <summary>A supervisor turn with a re-spawned wave and a deep node.failed error. Room: the wave's cards + a diagnostic (the respawn line is a journal ③ beat post-P6).</summary>
        RespawnCrash,
        /// <summary>An ACTIVE run parked on a pending node decision. Room: a live decision block + the live_activity ticker (the interactive surfaces the journal leaves to the room frame).</summary>
        PendingDecision,
        /// <summary>A reran turn (attempt 1 failed, attempt 2 succeeded). Room + journal: the attempt ladder, oldest→newest.</summary>
        ReranTurn,
    }

    /// <summary>The identifiers a seeded corpus run exposes — the team + the member (the journal projector needs an authenticated scope), and the run to project (the focused attempt for a rerun).</summary>
    public sealed record Seeded(Guid TeamId, Guid UserId, Guid SessionId, Guid RunId);

    /// <summary>Seed one corpus <paramref name="shape"/> and return the run to project through both projectors.</summary>
    public static async Task<Seeded> SeedAsync(PostgresFixture fixture, Shape shape)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var sessionId = await SeedSessionAsync(fixture, teamId, shape.ToString());

        var runId = shape switch
        {
            Shape.SupervisorPlanSpawn => await SeedSupervisorPlanSpawnAsync(fixture, teamId, sessionId),
            Shape.SingleAgent => await SeedSingleAgentAsync(fixture, teamId, sessionId),
            Shape.Retry => await SeedRetryAsync(fixture, teamId, sessionId),
            Shape.RespawnCrash => await SeedRespawnCrashAsync(fixture, teamId, sessionId),
            Shape.PendingDecision => await SeedPendingDecisionAsync(fixture, teamId, sessionId),
            Shape.ReranTurn => await SeedReranTurnAsync(fixture, teamId, sessionId),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unhandled corpus shape"),
        };

        return new Seeded(teamId, userId, sessionId, runId);
    }

    // ─── Shape builders ───────────────────────────────────────────────────────────

    private static async Task<Guid> SeedSupervisorPlanSpawnAsync(PostgresFixture fixture, Guid teamId, Guid sessionId)
    {
        var run = await SeedTurnAsync(fixture, teamId, sessionId, turn: 1, goal: "Do the thing", resultSummary: "Shipped it.");

        await SeedPlanDecisionAsync(fixture, teamId, run, "Trace DI registration", "Analyze the template store");
        await SaveWorkPlanAsync(fixture, teamId, run, ("s1", "Trace DI registration", null), ("s2", "Analyze the template store", "s1"));
        await SeedSpawnDecisionAsync(fixture, teamId, run, (Guid.NewGuid(), new[] { "b.cs", "a.cs" }), (Guid.NewGuid(), new[] { "a.cs", "c.cs" }));

        return run;
    }

    private static async Task<Guid> SeedSingleAgentAsync(PostgresFixture fixture, Guid teamId, Guid sessionId)
    {
        var run = await SeedTurnAsync(fixture, teamId, sessionId, turn: 1, goal: "Print exactly: PONG", resultSummary: null);
        await SeedAgentNodeAsync(fixture, teamId, run, summary: "Printed PONG.", changedFiles: new[] { "out.txt" });
        return run;
    }

    private static async Task<Guid> SeedRetryAsync(PostgresFixture fixture, Guid teamId, Guid sessionId)
    {
        var run = await SeedTurnAsync(fixture, teamId, sessionId, turn: 1, goal: "Do the work", resultSummary: "Done.");

        await SeedPlanDecisionAsync(fixture, teamId, run, "Sub 0");
        await SeedRetryScenarioAsync(fixture, teamId, run, Guid.NewGuid(), Guid.NewGuid());

        return run;
    }

    private static async Task<Guid> SeedRespawnCrashAsync(PostgresFixture fixture, Guid teamId, Guid sessionId)
    {
        var run = await SeedTurnAsync(fixture, teamId, sessionId, turn: 1, goal: "Investigate", resultSummary: null, status: WorkflowRunStatus.Failure);
        await SeedRespawnScenarioAsync(fixture, teamId, run, (Guid.NewGuid(), Guid.NewGuid()), (Guid.NewGuid(), Guid.NewGuid()));
        return run;
    }

    private static async Task<Guid> SeedPendingDecisionAsync(PostgresFixture fixture, Guid teamId, Guid sessionId)
    {
        // ACTIVE (Running) so the room pins its live_activity ticker; a pending node decision so the room surfaces the live gate.
        var run = await SeedTurnAsync(fixture, teamId, sessionId, turn: 1, goal: "Decide the path", resultSummary: null, status: WorkflowRunStatus.Running);

        await SeedNodeDecisionAsync(fixture, teamId, run, "Pick a path", DateTimeOffset.UtcNow.AddMinutes(10), new[]
        {
            new DecisionOption { Id = "safe", Label = "Stay safe" },
            new DecisionOption { Id = "deploy", Label = "Deploy now", IsSideEffecting = true },
        });

        return run;
    }

    private static async Task<Guid> SeedReranTurnAsync(PostgresFixture fixture, Guid teamId, Guid sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        var original = await SeedAttemptAsync(fixture, teamId, sessionId, turnIndex: 1, rootRunId: null, WorkflowRunStatus.Failure, createdAt: now.AddMinutes(-5), error: "vitest timed out");
        var winner = await SeedAttemptAsync(fixture, teamId, sessionId, turnIndex: null, rootRunId: original, WorkflowRunStatus.Success, createdAt: now);
        return winner;
    }

    // ─── Seed primitives (raw entity inserts — mirror the flow tests) ───────────────

    private static async Task<Guid> SeedSessionAsync(PostgresFixture fixture, Guid teamId, string title)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.WorkSession.Add(new WorkSession { Id = id, TeamId = teamId, Title = title, Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedTurnAsync(PostgresFixture fixture, Guid teamId, Guid sessionId, int turn, string goal, string? resultSummary, WorkflowRunStatus status = WorkflowRunStatus.Success)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var outputs = string.IsNullOrEmpty(resultSummary) ? "{}" : JsonSerializer.Serialize(new { summary = resultSummary, branch = (string?)null });

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal }),
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = status, SessionId = sessionId, SessionTurnIndex = turn,
            OutputsJson = outputs,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    private static async Task<Guid> SeedAttemptAsync(PostgresFixture fixture, Guid teamId, Guid sessionId, int? turnIndex, Guid? rootRunId, WorkflowRunStatus status, DateTimeOffset createdAt, string? error = null)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Rerun, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = createdAt, VerifiedAt = createdAt, NormalizedAt = createdAt,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Rerun,
            Status = status, SessionId = sessionId, SessionTurnIndex = turnIndex, RootRunId = rootRunId, Error = error,
            OutputsJson = "{}", CreatedDate = createdAt, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    private static async Task SeedPlanDecisionAsync(PostgresFixture fixture, Guid teamId, Guid runId, params string[] subtasks)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var payload = JsonSerializer.Serialize(new { subtasks = subtasks.Select((t, i) => new { id = $"s{i}", title = t, instruction = "do it" }).ToArray() });

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Plan, IdempotencyKey = $"plan:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = "{}",
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedSpawnDecisionAsync(PostgresFixture fixture, Guid teamId, Guid runId, params (Guid AgentRunId, string[] Files)[] agents)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        var outcome = JsonSerializer.Serialize(new
        {
            agentCount = agents.Length,
            agentRunIds = agents.Select(a => a.AgentRunId).ToArray(),
            agentResults = agents.Select(a => new { agentRunId = a.AgentRunId, status = "Succeeded", changedFiles = a.Files, summary = $"Edited {a.Files.Length} files" }).ToArray(),
        });

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        foreach (var (agentRunId, _) in agents)
            db.AgentRun.Add(AgentRow(teamId, runId, agentRunId, AgentRunStatus.Succeeded, now));

        await db.SaveChangesAsync();
    }

    private static async Task SeedRetryScenarioAsync(PostgresFixture fixture, Guid teamId, Guid runId, Guid failedAgent, Guid retryAgent)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtaskIds = new[] { "s0" } }),
            OutcomeJson = JsonSerializer.Serialize(new { agentCount = 1, agentRunIds = new[] { failedAgent } }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Retry, IdempotencyKey = $"retry:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtaskId = "s0", rationale = new { why = "The first attempt missed the edge cases.", evidence = "attempt 1 failed its acceptance check." } }),
            OutcomeJson = JsonSerializer.Serialize(new { agentCount = 1, agentRunIds = new[] { retryAgent } }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        db.AgentRun.Add(AgentRow(teamId, runId, failedAgent, AgentRunStatus.Failed, now));
        db.AgentRun.Add(AgentRow(teamId, runId, retryAgent, AgentRunStatus.Succeeded, now));

        await db.SaveChangesAsync();
    }

    private static async Task SeedRespawnScenarioAsync(PostgresFixture fixture, Guid teamId, Guid runId, (Guid A, Guid B) wave1, (Guid A, Guid B) wave2)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.SupervisorDecisionRecord.Add(SupDecision(teamId, runId, 1, SupervisorDecisionKinds.Plan, "{}",
            """{"planned":[],"count":2,"phases":[{"id":"inv","title":"Investigate","subtaskIds":["sa","sb"]}]}"""));

        db.SupervisorDecisionRecord.Add(SupDecision(teamId, runId, 2, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["sa","sb"]}""",
            JsonSerializer.Serialize(new { agentCount = 2, agentRunIds = new[] { wave1.A, wave1.B } })));
        db.SupervisorDecisionRecord.Add(SupDecision(teamId, runId, 3, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["sa","sb"]}""",
            JsonSerializer.Serialize(new { agentCount = 2, agentRunIds = new[] { wave2.A, wave2.B } })));

        db.AgentRun.Add(AgentRow(teamId, runId, wave1.A, AgentRunStatus.Succeeded, now));
        db.AgentRun.Add(AgentRow(teamId, runId, wave1.B, AgentRunStatus.Succeeded, now));
        db.AgentRun.Add(AgentRow(teamId, runId, wave2.A, AgentRunStatus.Succeeded, now));
        db.AgentRun.Add(AgentRow(teamId, runId, wave2.B, AgentRunStatus.Failed, now));

        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.NodeFailed, NodeId = "sup", IterationKey = "", OccurredAt = now,
            PayloadJson = JsonSerializer.Serialize(new { error = "OpenAI API error (no-status, Transient): the request timed out before the gateway responded" }),
        });

        await db.SaveChangesAsync();
    }

    private static async Task SeedAgentNodeAsync(PostgresFixture fixture, Guid teamId, Guid runId, string summary, string[] changedFiles)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var agentId = Guid.NewGuid();

        db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = "node.started", NodeId = "agent", IterationKey = "", OccurredAt = now.AddSeconds(-5), PayloadJson = "{}" });
        db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = "node.completed", NodeId = "agent", IterationKey = "", OccurredAt = now, PayloadJson = "{}" });

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = runId, NodeId = "agent", IterationKey = "",
            WaitKind = WorkflowWaitKinds.AgentRun, Token = agentId.ToString(), WakeAt = now,
            Status = WorkflowWaitStatuses.Resolved, PayloadJson = "{}", CreatedAt = now,
        });

        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles };
        db.AgentRun.Add(new AgentRun
        {
            Id = agentId, TeamId = teamId, WorkflowRunId = runId, NodeId = "agent", IterationKey = "",
            Harness = "codex-cli", Status = AgentRunStatus.Succeeded, TaskJson = "{}",
            ResultJson = JsonSerializer.Serialize(result, AgentJson.Options),
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    private static async Task SeedNodeDecisionAsync(PostgresFixture fixture, Guid teamId, Guid runId, string question, DateTimeOffset deadline, IReadOnlyList<DecisionOption> options)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var envelope = Envelope(question, deadline, runId, options);

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = runId, NodeId = "decide", IterationKey = string.Empty,
            WaitKind = WorkflowWaitKinds.Decision, Token = Guid.NewGuid().ToString("N"), WakeAt = deadline,
            Status = WorkflowWaitStatuses.Pending, PayloadJson = JsonSerializer.Serialize(envelope, Web),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SaveWorkPlanAsync(PostgresFixture fixture, Guid teamId, Guid runId, params (string Id, string Title, string? DependsOn)[] items)
    {
        using var scope = fixture.BeginScope();
        await scope.Resolve<IWorkPlanService>().SaveVersionAsync(new WorkPlanDraft
        {
            TeamId = teamId,
            WorkflowRunId = runId,
            OriginKind = WorkPlanOrigins.Supervisor,
            OriginKey = "sup#turn0",
            Goal = "Do the thing",
            Items = items.Select(i => new WorkPlanItem
            {
                Id = i.Id, Title = i.Title, Instruction = "do it",
                DependsOn = i.DependsOn is null ? Array.Empty<string>() : new[] { i.DependsOn },
            }).ToArray(),
        }, CancellationToken.None);
    }

    // ─── Row/envelope helpers ───────────────────────────────────────────────────────

    private static AgentRun AgentRow(Guid teamId, Guid runId, Guid agentId, AgentRunStatus status, DateTimeOffset now) => new()
    {
        Id = agentId, TeamId = teamId, WorkflowRunId = runId, NodeId = "sup", IterationKey = "sup",
        Harness = "codex-cli", Status = status, TaskJson = "{}",
        CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
    };

    private static SupervisorDecisionRecord SupDecision(Guid teamId, Guid runId, long sequence, string kind, string payloadJson, string? outcomeJson) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
        DecisionKind = kind, IdempotencyKey = $"{kind}:{Guid.NewGuid():N}", InputHash = new string('0', 64),
        Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
        CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
    };

    private static DecisionRequest Envelope(string question, DateTimeOffset deadline, Guid workflowRunId, IReadOnlyList<DecisionOption> options) => new()
    {
        Id = Guid.NewGuid(),
        RootTraceId = Guid.NewGuid(),
        WorkflowRunId = workflowRunId,
        NodeId = "decide",
        Scope = DecisionScopes.Node,
        RequesterType = DecisionRequesterTypes.WorkflowNode,
        DecisionType = DecisionTypes.ChooseOne,
        Question = question,
        Options = options,
        RecommendedOption = options.Count > 0 ? options[0].Id : null,
        BlockingReason = "needs a human",
        RiskLevel = DecisionRiskLevels.High,
        Policy = DecisionPolicies.HumanRequired,
        TimeoutAt = deadline,
        DedupeKey = Guid.NewGuid().ToString("N"),
        ResumeBackend = DecisionResumeBackends.WorkflowWait,
    };
}
