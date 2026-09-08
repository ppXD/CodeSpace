using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// Live CLI qualification for background review delegation. The parent is a server-admitted, claimed authority
/// fixture, not a model-executed producer. Each child runs the actual Claude harness and production executor in an
/// anonymous scope. Two private repository fixtures require a source-grounded blocker and an independent approval.
/// This measures one installed toolchain/model; it does not establish model diversity or universal read-only enforcement.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelAnswerReviewDelegationE2ETests
{
    private const string Provider = "Anthropic";
    private const string Goal = "Implement discount_points(amount) for non-negative integers divisible by ten, returning exactly amount divided by ten. Inspect REQUIREMENTS.md and pricing.py. In your review rationale, quote the audit token found only in pricing.py; include that token in the evidence of every blocker. Judge the arithmetic against the requirement; do not change any files.";
    private readonly PostgresFixture _fixture;

    public RealModelAnswerReviewDelegationE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [SkippableFact]
    public async Task A_background_review_child_uses_the_live_CLI_and_its_parent_grant()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);
        var present = new[] { baseUrl, apiKey, model }.Count(value => !string.IsNullOrWhiteSpace(value));
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent — live agent reviewer delegation NOT EVALUATED (skip is not pass)");
        present.ShouldBe(3, "all three live gateway settings are required; partial configuration must fail");
        RealModelGate.IsRequired(Provider).ShouldBeTrue("this is a strict capability gate, not a report-only wire");
        OperatingSystem.IsWindows().ShouldBeFalse("the live reviewer gate requires the installed POSIX coding CLI");
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar)).ShouldBeTrue("a scripted CLI override cannot qualify as a real-model reviewer");
        var version = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = ["--version"], TimeoutSeconds = 15 }, CancellationToken.None);
        version.Status.ShouldBe(SandboxStatus.Success, "the required live CLI must be installed");
        RealModelGate.WholeLoopAttemptDeadline().ShouldBeGreaterThan(TimeSpan.FromSeconds(60), "the attempt needs independent time to cancel and reap its durable CLI");

        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            using var attempt = new CancellationTokenSource(RealModelGate.WholeLoopAttemptDeadline() - TimeSpan.FromSeconds(30));
            // Both cases must succeed in the SAME bounded attempt; results are never pooled across retries.
            foreach (var flawed in new[] { true, false })
            {
                var outcome = await RunCaseAsync(new LiveCase(baseUrl!.TrimEnd('/'), apiKey!, model!, flawed), attempt.Token);
                if (outcome.Outcome != RealModelOutcome.Drove) return outcome;
            }

            return (RealModelOutcome.Drove, "Two actual CLI reviewer children retained their parent grants, observed repository-only evidence, blocked incorrect arithmetic and approved the correct implementation.");
        });
    }

    // Token matching proves source observation, not semantic correctness. The separate, known arithmetic oracle
    // requires opposite verdicts on incorrect/correct implementations; neither a blanket blocker nor approval passes.
    internal static bool MeetsCase(CriticVerdict verdict, bool flawed, string auditToken)
    {
        if (string.IsNullOrWhiteSpace(auditToken) || verdict.Failed || !verdict.Rationale.Contains(auditToken, StringComparison.Ordinal)) return false;
        return flawed
            ? !verdict.Approved && verdict.Issues.Any(issue => issue.Severity == CriticSeverity.Blocker && issue.Evidence?.Contains(auditToken, StringComparison.Ordinal) == true)
            : verdict.Approved && verdict.Issues.All(issue => issue.Severity != CriticSeverity.Blocker);
    }

    private async Task<(RealModelOutcome Outcome, string Note)> RunCaseAsync(LiveCase input, CancellationToken cancellationToken)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        using var remote = new ReviewRepository();
        try
        {
            await remote.SeedAsync(input.Flawed, cancellationToken);
            var repositoryId = await SeedBoundRepositoryAsync(teamId, remote.Url, cancellationToken);
            var modelRowId = await SeedModelAsync(teamId, input, cancellationToken);
            var task = new AgentTask { Goal = Goal, Harness = "claude-code", RepositoryId = repositoryId, ReviewerAgent = true, ReviewerModelId = modelRowId, Autonomy = AgentAutonomyLevel.Trusted, Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted) };
            AgentRun parent;
            AgentRunOwnerToken owner;
            using (var admission = _fixture.BeginScopeAs(userId, teamId))
            {
                var runs = admission.Resolve<IAgentRunService>();
                parent = await runs.CreateAsync(task, teamId, null, null, cancellationToken: cancellationToken);
                owner = (await runs.ClaimOwnershipAsync(parent.Id, cancellationToken)).ShouldNotBeNull();
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            using var heartbeatCancellation = new CancellationTokenSource();
            Exception? heartbeatFailure = null;
            var heartbeat = HeartbeatLoop.RunAsync(async cancellationToken =>
            {
                using var scope = _fixture.BeginScope();
                await scope.Resolve<IAgentRunService>().HeartbeatAsync(owner, cancellationToken);
            }, TimeSpan.FromSeconds(5), error => { Interlocked.CompareExchange(ref heartbeatFailure, error, null); deadline.Cancel(); }, heartbeatCancellation.Token);

            try
            {
                CriticVerdict verdict;
                try
                {
                    using var background = _fixture.BeginScopeAs(null, null);
                    var claude = background.Resolve<IAgentHarnessRegistry>().All.Single(harness => harness.Kind == "claude-code");
                    // Model the CI deployment's one installed toolchain without replacing its real harness, model or executor.
                    var runner = new AgentReviewRunner(background.Resolve<IAgentRunService>(), new AgentHarnessRegistry([claude]), background.Resolve<IServiceScopeFactory>(), NullLogger<AgentReviewRunner>.Instance);
                    verdict = await new AgentOutputReviewer(runner).ReviewAsync(owner, task, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "fixture-context", ProducedBranch = "main", ChangedFiles = ["pricing.py"] }, parent, deadline.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && heartbeatFailure is null)
                {
                    return (RealModelOutcome.CapabilityMiss, "The actual reviewer did not complete its bounded three-minute inspection; no approval or fallback qualifies.");
                }

                heartbeatFailure.ShouldBeNull("the server-owned parent's heartbeat must remain valid throughout review");
                using var read = _fixture.BeginScope();
                var db = read.Resolve<CodeSpaceDbContext>();
                var children = await db.AgentRun.AsNoTracking().Where(run => run.TeamId == teamId && run.Id != parent.Id).ToListAsync(cancellationToken);
                children.Count.ShouldBe(1, "the facade must create exactly one actual reviewer child; an in-process critic fallback proves nothing");
                var child = children.Single();
                AssertDelegation(parent, owner, child, userId, repositoryId);
                if (child.Status != AgentRunStatus.Succeeded)
                {
                    var note = $"review child {child.Id}: status={child.Status}; exitReason={RealModelRunClassifier.ExitReasonOf(child)}; error={child.Error}";
                    if (RealModelRunClassifier.IsGatewayInfra(child)) throw new AgentExecutionInfraException(note);
                    return (RealModelOutcome.CodeFault, note);
                }

                var result = JsonSerializer.Deserialize<AgentRunResult>(child.ResultJson!, AgentJson.Options).ShouldNotBeNull();
                result.TokenUsage.ShouldNotBeNull().InputTokens.ShouldBeGreaterThanOrEqualTo(0);
                result.TokenUsage.OutputTokens.ShouldBeGreaterThan(0, "the actual reviewer must have observed output-token usage");
                result.SessionId.ShouldNotBeNullOrWhiteSpace();
                result.Model.ShouldNotBeNullOrWhiteSpace();
                RealModelGate.ObserveModel(result.Model);
                var events = await db.AgentRunEvent.AsNoTracking().Where(item => item.AgentRunId == child.Id).ToListAsync(cancellationToken);
                var tools = events.Where(item => item.Kind is AgentEventKind.ToolCall or AgentEventKind.CommandExecuted).ToList();
                tools.ShouldAllBe(item => item.WriterKind == "worker" && item.WriterOwnerId == child.OwnerId && item.WriterEpoch == child.FenceEpoch, "native tool observations must carry the child's explicit owner fence");
                var met = tools.Count > 0 && MeetsCase(verdict, input.Flawed, remote.AuditToken);
                Console.WriteLine($"[live-review-delegation] parent={parent.Id}; child={child.Id}; expected={(input.Flawed ? "block" : "approve")}; met={met}; nativeTools={tools.Count}; outputTokens={result.TokenUsage.OutputTokens}; verdict={JsonSerializer.Serialize(verdict, AgentJson.Options)}");
                return met
                    ? (RealModelOutcome.Drove, "The actual reviewer produced the required independent verdict with repository-only observation evidence.")
                    : (RealModelOutcome.CapabilityMiss, $"The actual CLI review did not satisfy the {(input.Flawed ? "source-grounded blocker" : "source-grounded approval")} oracle; nativeTools={tools.Count}; failed={verdict.Failed}; approved={verdict.Approved}.");
            }
            finally
            {
                await heartbeatCancellation.CancelAsync();
                await heartbeat;
            }
        }
        finally
        {
            await CancelFixtureRunsAsync(teamId);
        }
    }

    private static void AssertDelegation(AgentRun parent, AgentRunOwnerToken owner, AgentRun child, Guid userId, Guid repositoryId)
    {
        var task = JsonSerializer.Deserialize<AgentTask>(child.TaskJson, AgentJson.Options).ShouldNotBeNull();
        var authority = task.ExecutionAuthority.ShouldNotBeNull();
        var source = JsonSerializer.Deserialize<AgentTask>(parent.TaskJson, AgentJson.Options).ShouldNotBeNull().ExecutionAuthority.ShouldNotBeNull();
        authority.SourceKind.ShouldBe("agent-review");
        authority.ParentAgentRunId.ShouldBe(parent.Id);
        authority.ParentAuthorityHash.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(source, AgentJson.Options)))));
        authority.DefinitionHash.ShouldBe(source.DefinitionHash);
        authority.TeamId.ShouldBe(parent.TeamId);
        authority.LogicalRunId.ShouldBe(child.Id);
        authority.GrantedCeiling.ShouldBe(AgentAutonomyLevel.Confined);
        authority.Subjects.ShouldNotBeEmpty();
        authority.Subjects.ShouldAllBe(subject => subject.UserId == userId);
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(authority.Subjects, AgentJson.Options), JsonSerializer.SerializeToElement(source.Subjects, AgentJson.Options)).ShouldBeTrue("the child must preserve the complete parent subject intersection");
        child.TaskJson.Contains(owner.OwnerId.ToString(), StringComparison.OrdinalIgnoreCase).ShouldBeFalse("a parent's observation token must not become model-visible authority");
        child.OwnerId.ShouldNotBeNull().ShouldNotBe(owner.OwnerId);
        child.FenceEpoch.ShouldBeGreaterThan(0);
        task.Workspace.ShouldNotBeNull().Repositories.Count.ShouldBe(1);
        task.Workspace.Primary.ShouldNotBeNull().RepositoryId.ShouldBe(repositoryId);
        task.Workspace.Primary.Access.ShouldBe(WorkspaceAccess.Read);
        task.Workspace.Primary.Ref.ShouldBe("main");
        task.Autonomy.ShouldBe(AgentAutonomyLevel.Confined);
        task.Permissions.Network.ShouldBe(AgentNetworkAccess.Off);
        task.Permissions.WriteScope.ShouldBe(AgentWriteScope.ReadOnly);
        task.EnableMcpEndpoint.ShouldBe(false);
        task.PushProducedBranch.ShouldBe(false);
        task.OutputReviewMode.ShouldBe(ReviewMode.None);
        task.ReviewerAgent.ShouldBeFalse();
        task.MaxReviseRounds.ShouldBe(0);
        task.Acceptance.ShouldBeNull();
    }

    private async Task<Guid> SeedModelAsync(Guid teamId, LiveCase input, CancellationToken cancellationToken)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credentialId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential { Id = credentialId, TeamId = teamId, Provider = Provider, DisplayName = "live reviewer", EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(input.ApiKey), BaseUrl = input.BaseUrl, Status = CredentialStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        var rowId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credentialId, ModelId = input.Model, Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync(cancellationToken);
        return rowId;
    }

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrl, CancellationToken cancellationToken)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });
        var credentialId = Guid.NewGuid();
        var payload = scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "local-file-clone-fixture" });
        db.Credential.Add(new Credential { Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "local clone", EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(payload), Status = CredentialStatus.Active });
        var repositoryId = Guid.NewGuid();
        db.Repository.Add(new Repository { Id = repositoryId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = repositoryId.ToString(), NamespacePath = "org", Name = "review", FullPath = "org/review", DefaultBranch = "main", CloneUrlHttps = cloneUrl, WebUrl = "https://local/org/review" });
        await db.SaveChangesAsync(cancellationToken);
        return repositoryId;
    }

    private async Task CancelFixtureRunsAsync(Guid teamId)
    {
        // Cancellation of observation does not terminate a durable CLI. This is explicit administration of our private
        // fixture's runs, with fresh bounded cleanup independent of the expired model token and guarded durable handles.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var scope = _fixture.BeginScope();
        var runs = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(run => run.TeamId == teamId).ToListAsync(cleanup.Token);
        foreach (var run in runs)
        {
            await scope.Resolve<IAgentRunService>().CancelRunningAsync(run.Id, "live reviewer fixture teardown", AgentRunAbandonCause.OperatorCancelled, cleanup.Token);
            if (run.RunnerHandleJson is not { } handleJson) continue;
            var handle = JsonSerializer.Deserialize<SandboxHandle>(handleJson, AgentJson.Options).ShouldNotBeNull();
            var runner = scope.Resolve<ISandboxRunnerRegistry>().Resolve(handle.Kind).ShouldBeAssignableTo<ISandboxDurableRunner>();
            await runner.TerminateAsync(handle, cleanup.Token);
        }
    }

    private sealed record LiveCase(string BaseUrl, string ApiKey, string Model, bool Flawed);

    private sealed class ReviewRepository : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-live-review-" + Guid.NewGuid().ToString("N"));
        private string Bare => Path.Combine(_root, "remote.git");
        public string Url => new Uri(Bare).AbsoluteUri;
        public string AuditToken { get; } = "source-only-" + Guid.NewGuid().ToString("N");

        public async Task SeedAsync(bool flawed, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_root);
            await GitAsync(_root, cancellationToken, "init", "--bare", "-b", "main", Bare);
            var seed = Path.Combine(_root, "seed");
            await GitAsync(_root, cancellationToken, "clone", Bare, seed);
            await GitAsync(seed, cancellationToken, "config", "user.email", "review-fixture@codespace.dev");
            await GitAsync(seed, cancellationToken, "config", "user.name", "Review Fixture");
            await GitAsync(seed, cancellationToken, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, "REQUIREMENTS.md"), "discount_points(amount) accepts a non-negative integer divisible by ten and returns exactly amount divided by ten. For example, 20 returns 2, 100 returns 10, and 0 returns 0. No other API or feature is required.\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(seed, "pricing.py"), $"# audit token: {AuditToken}\ndef discount_points(amount):\n    return amount // {(flawed ? 2 : 10)}\n", cancellationToken);
            await GitAsync(seed, cancellationToken, "add", "-A");
            await GitAsync(seed, cancellationToken, "commit", "-m", "implementation");
            await GitAsync(seed, cancellationToken, "push", "origin", "main");
        }

        private static async Task GitAsync(string directory, CancellationToken cancellationToken, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = directory, TimeoutSeconds = 30 }, cancellationToken);
            result.Status.ShouldBe(SandboxStatus.Success, result.Stderr);
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
