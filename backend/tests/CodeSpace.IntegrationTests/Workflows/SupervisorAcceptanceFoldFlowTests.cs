using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/>.<c>RehydrateFromDecisionLogAsync</c> with
/// a fake grader at the one seam): the A3 OBJECTIVE acceptance fold. Proves the replay-safety contract — the clone+grade
/// runs EXACTLY ONCE at the terminal resolve fold, the verdict is folded + persisted, and every later rehydrate reads
/// the folded verdict off the durable tape WITHOUT re-grading (the named biggest risk, defeated). Plus: a failed grade
/// OVERRIDES the resolver's self-report marker (Unverified → the resolved branch is withheld); a run with no operator
/// acceptance command is byte-identical (never grades); and a grader failure fails closed without stranding the run.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorAcceptanceFoldFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorAcceptanceFoldFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";
    private static readonly string Marker = SupervisorResolverRecipe.TestsPassedMarker;
    private static readonly string[] Command = { "sh", "check.sh" };

    [Fact]
    public async Task A_resolve_is_graded_once_and_the_objective_verdict_is_folded_and_persisted()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "codespace/resolve/x", markerPresent: true));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId, Command), grader);

        grader.CallCount.ShouldBe(1, "the grade runs exactly once at the fold");
        grader.LastCall!.Value.RepositoryId.ShouldBe(repoId);
        grader.LastCall.Value.TeamId.ShouldBe(teamId);
        grader.LastCall.Value.Branch.ShouldBe("codespace/resolve/x", "the resolver's produced branch is graded");
        grader.LastCall.Value.Command.ShouldBe(Command, "the operator's acceptance command is the graded argv");

        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadAcceptanceGradePassed(resolve.OutcomeJson).ShouldBe(true, "the objective verdict is folded into the in-memory outcome");
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ShouldBe(SupervisorResolutionVerdict.Verified);

        SupervisorOutcome.ReadAcceptanceGradePassed(await LedgerOutcomeAsync(runId, teamId))
            .ShouldBe(true, "the grade is PERSISTED onto the durable ledger row (so replay reads it, not re-grades)");
    }

    [Fact]
    public async Task A_failed_grade_overrides_the_resolvers_self_report_and_withholds_the_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        // The resolver SUCCEEDED and self-reported the marker (the self-report WOULD accept) — but the objective grade FAILS.
        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "codespace/resolve/bad", markerPresent: true));

        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId, Command), new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "tests-failed-exit-1" }));

        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ShouldBe(SupervisorResolutionVerdict.Unverified, "the objective grade overrides the self-report marker — the regression A3 closes");
        SupervisorOutcome.ResolvedBranch(resolve).ShouldBeNull("an Unverified resolve surfaces NO clean head → the accept short-circuit is withheld");
    }

    [Fact]
    public async Task Replay_reads_the_folded_verdict_without_re_grading()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "codespace/resolve/x", markerPresent: true));

        await RehydrateAsync(runId, teamId, GoalConfig(repoId, Command), new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" }));

        var persistedAfterFirst = await LedgerOutcomeAsync(runId, teamId);

        // The SECOND rehydrate must NOT re-clone+grade — the once-guard reads the folded verdict off the tape.
        var secondGrader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "WOULD-FLIP-IF-RE-GRADED" });
        var ctx2 = await RehydrateAsync(runId, teamId, GoalConfig(repoId, Command), secondGrader);

        secondGrader.CallCount.ShouldBe(0, "replay reads the durable verdict — the grade I/O never re-runs (replay-deterministic)");
        ctx2.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve).OutcomeJson
            .ShouldBe(persistedAfterFirst, "the verdict is unchanged across replay — a pure tape read");
        (await LedgerOutcomeAsync(runId, teamId)).ShouldBe(persistedAfterFirst, "no redundant UPDATE on replay");
    }

    [Fact]
    public async Task A_run_with_no_acceptance_command_never_grades_and_does_not_rewrite_the_row()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "codespace/resolve/x", markerPresent: true));
        var before = await LedgerOutcomeAsync(runId, teamId);   // jsonb-normalized; compare row-to-row, not to the seeded compact bytes

        // No AcceptanceChecks configured → the marker self-report stands, no grade runs, no spurious write.
        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId: Guid.NewGuid(), acceptanceChecks: null), grader);

        grader.CallCount.ShouldBe(0, "no operator acceptance command → no objective grade runs");
        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadAcceptanceGradePassed(resolve.OutcomeJson).ShouldBeNull("no acceptanceGrade field is folded");
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ShouldBe(SupervisorResolutionVerdict.Verified, "the verdict falls back to the self-report marker");
        (await LedgerOutcomeAsync(runId, teamId)).ShouldBe(before, "the durable resolve row is unchanged — no spurious UPDATE on the no-command path");
    }

    [Fact]
    public async Task A_grader_failure_fails_closed_without_stranding_the_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "codespace/resolve/x", markerPresent: true));

        // An UNEXPECTED grader throw (not the fail-closed Failed grade A2 normally returns) must still not crash the
        // terminal fold (which would strand the row); it degrades to a not-accepted verdict.
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid(), Command), new RecordingGrader(new InvalidOperationException("sandbox exploded")));

        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadAcceptanceGradePassed(resolve.OutcomeJson).ShouldBe(false, "an unexpected grade failure folds not-accepted");
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ShouldBe(SupervisorResolutionVerdict.Unverified);
    }

    [Fact]
    public async Task A_multi_repo_resolve_is_not_graded_and_falls_back_to_the_marker()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        // A resolver result carrying per-repo RepositoryResults = a MULTI-repo resolve. Its top-level ProducedBranch
        // mirrors only the primary, so A3 must NOT grade it (a primary-only check would gate every repo's branch — a
        // false accept if a secondary is broken). The per-repo grade is a deferred follow-up; until then, the marker stands.
        await SeedResolveDecisionAsync(runId, teamId, MultiRepoResolveOutcome());

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid(), Command), grader);

        grader.CallCount.ShouldBe(0, "a multi-repo resolve is not graded by A3 (per-repo grade deferred) — never a primary-only false accept");
        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadAcceptanceGradePassed(resolve.OutcomeJson).ShouldBeNull("no grade is folded onto a multi-repo resolve");
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ShouldBe(SupervisorResolutionVerdict.Verified, "the marker verdict stands for a multi-repo resolve, byte-identical to pre-A3");
    }

    // ─── Crown jewel: the REAL grader (DI-resolved) wired through the real fold against a real repo + branch ───

    [Theory]
    [InlineData(0, "Verified")]     // the resolver's branch genuinely passes the operator check → objectively accepted
    [InlineData(1, "Unverified")]   // it FAILS the check → the objective grade overrides the self-report marker, end-to-end
    public async Task The_real_grade_drives_the_verdict_over_a_real_repo_branch(int checkExitCode, string expectedVerdict)
    {
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        await remote.AddBranchWithCheckAsync("resolve/head", checkExitCode);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        // The resolver SUCCEEDED + self-reported the marker (it would accept), with the produced branch on the remote.
        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "resolve/head", markerPresent: true));

        // Resolve the REAL SupervisorTurnService (real SupervisorAcceptanceGrader) — it clones repoId@resolve/head and
        // runs the operator command for real; no fake at any seam.
        SupervisorTurnContext ctx;
        using (var scope = _fixture.BeginScope())
            ctx = await scope.Resolve<ISupervisorTurnService>()
                .RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, GoalConfig(repoId, Command), CancellationToken.None);

        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ToString()
            .ShouldBe(expectedVerdict, "the REAL acceptance check's exit code drives the verdict, overriding the resolver's self-report");
        if (expectedVerdict == "Unverified")
            SupervisorOutcome.ResolvedBranch(resolve).ShouldBeNull("a really-failing check withholds the accept boundary");
        else
            SupervisorOutcome.ResolvedBranch(resolve).ShouldBe("resolve/head", "a really-passing check accepts the resolved branch");
    }

    // ─── Helpers ───

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = Messages.Enums.ProviderKind.GitHub, DisplayName = "local", BaseUrl = $"https://local/{instanceId:N}" });

        var serializer = scope.Resolve<Core.Services.Credentials.ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<Core.Services.Credentials.IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new Messages.Credentials.PatPayload { Token = "integration-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = Messages.Enums.AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = Messages.Enums.CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new Core.Services.Agents.Sandbox.Runners.LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare file:// remote with a main commit + acceptance branches each carrying a check.sh whose exit code is the start-state — the real branch the real grader clones + checks.</summary>
    private sealed class AcceptanceRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-sup-a3-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;
        private readonly string _seed;

        public AcceptanceRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
            _seed = Path.Combine(_root, "seed");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task InitAsync()
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            Directory.CreateDirectory(_seed);
            await Git(_seed, "clone", _bare, _seed);
            await Config(_seed);
            await File.WriteAllTextAsync(Path.Combine(_seed, "README.md"), "base\n");
            await Git(_seed, "add", "-A");
            await Git(_seed, "commit", "-m", "seed");
            await Git(_seed, "push", "origin", "main");
        }

        public async Task AddBranchWithCheckAsync(string branch, int checkExitCode)
        {
            await Git(_seed, "checkout", "-B", branch, "main");
            await File.WriteAllTextAsync(Path.Combine(_seed, "check.sh"), $"#!/bin/sh\nexit {checkExitCode}\n");
            await Git(_seed, "add", "-A");
            await Git(_seed, "commit", "-m", $"check exit {checkExitCode}");
            await Git(_seed, "push", "origin", branch);
            await Git(_seed, "checkout", "main");
        }

        private static async Task Config(string dir)
        {
            await Git(dir, "config", "user.email", "test@codespace.dev");
            await Git(dir, "config", "user.name", "Test");
            await Git(dir, "config", "commit.gpgsign", "false");
        }

        private static async Task Git(string workdir, params string[] args)
        {
            var result = await new Core.Services.Agents.Sandbox.Runners.LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
            if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private async Task<SupervisorTurnContext> RehydrateAsync(Guid runId, Guid teamId, SupervisorGoalConfig goalConfig, RecordingGrader grader)
    {
        using var scope = _fixture.BeginScope();
        var service = new SupervisorTurnService(
            scope.Resolve<ISupervisorDecisionLog>(),
            scope.Resolve<ISupervisorDecider>(),
            scope.Resolve<ISupervisorActionExecutor>(),
            scope.Resolve<CodeSpaceDbContext>(),
            grader,
            scope.Resolve<IDecisionQueueService>(),
            scope.Resolve<IDecisionArbiter>(),
            scope.Resolve<IDecisionAnswerService>(),
            scope.Resolve<ILogger<SupervisorTurnService>>());

        return await service.RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, goalConfig, CancellationToken.None);
    }

    private static SupervisorGoalConfig GoalConfig(Guid repoId, IReadOnlyList<string>? acceptanceChecks) => new()
    {
        Goal = Goal,
        AcceptanceChecks = acceptanceChecks,
        AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId },
    };

    private static string ResolveOutcome(string producedBranch, bool markerPresent)
    {
        var agentResults = new[] { new { agentRunId = Guid.NewGuid(), status = "Succeeded", summary = markerPresent ? $"reconciled {Marker}" : "reconciled", producedBranch } };

        return JsonSerializer.Serialize(new { agentRunIds = new[] { Guid.NewGuid() }, agentCount = 1, agentResults }, AgentJson.Options);
    }

    private static string MultiRepoResolveOutcome()
    {
        // A multi-repo resolver result: per-repo RepositoryResults present (the discriminator A3 + ResolvedBranch use).
        var result = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(),
            Status = "Succeeded",
            Summary = $"reconciled {Marker}",
            ProducedBranch = "primary",
            RepositoryResults = new[]
            {
                new RepositoryRunResult { Alias = "web", RepositoryId = Guid.NewGuid(), ProducedBranch = "web/x", BaseBranch = "main", ChangedFiles = new[] { "a" }, Access = WorkspaceAccess.Write },
            },
        };

        return JsonSerializer.Serialize(new { agentRunIds = new[] { Guid.NewGuid() }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-accept-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task SeedResolveDecisionAsync(Guid runId, Guid teamId, string outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = runId,
            Sequence = 1,
            DecisionKind = SupervisorDecisionKinds.Resolve,
            IdempotencyKey = $"resolve-{Guid.NewGuid():N}",
            InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = "{}",
            OutcomeJson = outcomeJson,
            FenceEpoch = 1,
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string?> LedgerOutcomeAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Resolve)
            .Select(d => d.OutcomeJson)
            .SingleAsync();
    }

    /// <summary>Records each grade call (count + args) and returns a canned grade — or throws (the unexpected-failure path). The seam A3 grades at, faked so the test asserts call count (the replay-once contract) without real git.</summary>
    private sealed class RecordingGrader : ISupervisorAcceptanceGrader
    {
        private readonly BenchmarkGrade _grade;
        private readonly Exception? _throw;

        public RecordingGrader(BenchmarkGrade grade) => _grade = grade;
        public RecordingGrader(Exception toThrow) { _throw = toThrow; _grade = new BenchmarkGrade { Passed = false, Detail = "unused" }; }

        public int CallCount { get; private set; }
        public (Guid RepositoryId, Guid TeamId, string Branch, IReadOnlyList<string> Command, int TimeoutSeconds)? LastCall { get; private set; }

        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, IReadOnlyList<string> command, int timeoutSeconds, CancellationToken cancellationToken)
        {
            CallCount++;
            LastCall = (repositoryId, teamId, branch, command, timeoutSeconds);
            if (_throw != null) throw _throw;
            return Task.FromResult(_grade);
        }
    }
}
