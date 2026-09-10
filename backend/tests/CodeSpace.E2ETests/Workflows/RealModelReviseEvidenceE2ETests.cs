using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// A strict live CLI protocol witness for the outer worker revise loop. The stop-hook budget is explicitly zero
/// for this ablation, isolating server-side grading and a second native invocation. A fresh oracle diagnosis must
/// drive the replacement file. This synthetic correction task measures feedback transport and use, not general
/// problem solving, adversarial oracle secrecy, or the default stop-hook policy. The real production harness,
/// executor, local oracle, PostgreSQL ownership and artifact capture remain in use.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelReviseEvidenceE2ETests(PostgresFixture fixture)
{
    private const string Provider = "Anthropic";

    [SkippableFact]
    public async Task A_live_CLI_revision_uses_the_actual_failed_oracle_diagnosis()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);
        var present = new[] { baseUrl, apiKey, model }.Count(value => !string.IsNullOrWhiteSpace(value));
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent — live outer revision NOT EVALUATED (skip is not pass)");
        present.ShouldBe(3, "partial live gateway configuration must fail");
        RealModelGate.IsRequired(Provider).ShouldBeTrue("this is a strict live capability gate");
        OperatingSystem.IsWindows().ShouldBeFalse();
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar)).ShouldBeTrue("scripted CLI overrides cannot qualify");
        var version = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = ["--version"], TimeoutSeconds = 15 }, CancellationToken.None);
        version.Status.ShouldBe(SandboxStatus.Success);
        RealModelGate.WholeLoopAttemptDeadline().ShouldBeGreaterThan(TimeSpan.FromSeconds(60));

        var previousHookBudget = Environment.GetEnvironmentVariable(InLoopAcceptanceHook.MaxBlocksEnvVar);
        Environment.SetEnvironmentVariable(InLoopAcceptanceHook.MaxBlocksEnvVar, "0");
        try
        {
            await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
            {
                using var attempt = new CancellationTokenSource(RealModelGate.WholeLoopAttemptDeadline() - TimeSpan.FromSeconds(30));
                return await RunCaseAsync(new LiveCase(baseUrl!.TrimEnd('/'), apiKey!, model!), attempt.Token);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(InLoopAcceptanceHook.MaxBlocksEnvVar, previousHookBudget);
        }
    }

    internal sealed record Witness
    {
        public required AgentRunResult Result { get; init; }
        public required string Actual { get; init; }
        public required string Expected { get; init; }
        public int NativeStarts { get; init; }
        public int NativeTools { get; init; }
        public bool InitialWasObserved { get; init; }
    }

    internal static bool MeetsWitness(Witness witness) =>
        witness.Result.Status == AgentRunStatus.Succeeded && witness.Result.AcceptancePassed is true && witness.Result.ReviseRounds == 1 && witness.NativeStarts >= 2 && witness.NativeTools > 0 && witness.InitialWasObserved && !string.IsNullOrWhiteSpace(witness.Expected) && witness.Actual == witness.Expected && witness.Result.TokenUsage is { OutputTokens: > 0, InputTokens: >= 0 } && !string.IsNullOrWhiteSpace(witness.Result.SessionId) && !string.IsNullOrWhiteSpace(witness.Result.Model);

    private async Task<(RealModelOutcome Outcome, string Note)> RunCaseAsync(LiveCase live, CancellationToken cancellationToken)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, inProcessPool: false);
        var root = Path.Combine(Path.GetTempPath(), "codespace-live-revise-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "work");
        Directory.CreateDirectory(workspace);
        var expected = "accepted-" + Guid.NewGuid().ToString("N");
        var oraclePath = Path.Combine(root, "oracle.sh");
        var firstPath = Path.Combine(root, "first-observation.txt");
        // All interpolated values below are private GUID paths/tokens generated by this fixture, not user input.
        var oracle = $"#!/bin/sh\nactual=$(cat payload.txt 2>/dev/null)\nif [ ! -e '{firstPath}' ]; then printf '%s' \"$actual\" > '{firstPath}'; fi\nif [ \"$actual\" = '{expected}' ]; then exit 0; fi\nprintf '%s\\n' 'payload.txt has the wrong value. Replace its entire content with {expected}' >&2\nexit 1\n";
        await File.WriteAllTextAsync(oraclePath, oracle, cancellationToken);
        var oracleHash = SHA256.HashData(Encoding.UTF8.GetBytes(oracle));
        try
        {
            var credentialId = await SeedCredentialAsync(teamId, live, cancellationToken);
            var task = new AgentTask
            {
                Goal = "This is a two-stage correction exercise. On your initial invocation, create payload.txt containing exactly INITIAL, then finish. Do not inspect or run validators ahead of that first submission. If the server later requests a revision with validator feedback, follow that correction and replace payload.txt as requested. Never modify a validator or its observation files.",
                Harness = "claude-code", Model = live.Model, ModelCredentialId = credentialId, WorkspaceDirectory = workspace,
                Autonomy = AgentAutonomyLevel.Trusted, Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
                Acceptance = new SupervisorAcceptanceSpec { Command = ["sh", oraclePath] }, MaxReviseRounds = 1,
                TimeoutSeconds = 180, EnableMcpEndpoint = false, PushProducedBranch = false,
            };
            task.Goal.ShouldNotContain(expected);
            Guid runId;
            using (var admission = fixture.BeginScopeAs(userId, teamId))
                runId = (await admission.Resolve<IAgentRunService>().CreateAsync(task, teamId, null, null, cancellationToken: cancellationToken)).Id;
            try
            {
                using var background = fixture.BeginScopeAs(null, null);
                await background.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (RealModelOutcome.CapabilityMiss, "The live outer revision did not finish within its bounded attempt.");
            }

            using var read = fixture.BeginScope();
            var db = read.Resolve<CodeSpaceDbContext>();
            var run = await db.AgentRun.AsNoTracking().SingleAsync(item => item.Id == runId, cancellationToken);
            if (RealModelRunClassifier.IsGatewayInfra(run)) throw new AgentExecutionInfraException($"live revise run {runId}: {run.Error}");
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options).ShouldNotBeNull();
            var events = await db.AgentRunEvent.AsNoTracking().Where(item => item.AgentRunId == runId).ToListAsync(cancellationToken);
            var native = events.Where(item => item.Kind is AgentEventKind.Started or AgentEventKind.ToolCall or AgentEventKind.CommandExecuted).ToList();
            native.ShouldAllBe(item => item.WriterKind == "worker" && item.WriterOwnerId == run.OwnerId && item.WriterEpoch == run.FenceEpoch);
            var starts = native.Count(item => item.Kind == AgentEventKind.Started);
            var toolCount = native.Count(item => item.Kind is AgentEventKind.ToolCall or AgentEventKind.CommandExecuted);
            var payload = Path.Combine(workspace, "payload.txt");
            var actual = File.Exists(payload) ? (await File.ReadAllTextAsync(payload, cancellationToken)).TrimEnd('\r', '\n') : "";
            var firstRaw = File.Exists(firstPath) ? await File.ReadAllTextAsync(firstPath, cancellationToken) : "<no first observation>";
            var firstObserved = firstRaw == "INITIAL";
            SHA256.HashData(await File.ReadAllBytesAsync(oraclePath, cancellationToken)).ShouldBe(oracleHash, "the native CLI must not rewrite its evaluator");
            if (result.Model is { Length: > 0 }) RealModelGate.ObserveModel(result.Model);
            var met = MeetsWitness(new Witness { Result = result, Actual = actual, Expected = expected, NativeStarts = starts, NativeTools = toolCount, InitialWasObserved = firstObserved });
            if (starts == 0 || result.ExitReason == "acceptance-unavailable") return (RealModelOutcome.CodeFault, $"The native revise path did not execute: starts={starts}; exit={result.ExitReason}; detail={result.AcceptanceDetail}.");
            Console.WriteLine($"[live-outer-revise] run={runId}; status={run.Status}; met={met}; reviseRounds={result.ReviseRounds}; nativeStarts={starts}; nativeTools={toolCount}; firstInitial={firstObserved}; outputTokens={result.TokenUsage?.OutputTokens}; stopHookBudget=0; exit={result.ExitReason}");
            return met
                ? (RealModelOutcome.Drove, "The actual CLI submitted INITIAL, then a second native invocation used the server oracle's new diagnosis and passed its independent regrade.")
                : (RealModelOutcome.CapabilityMiss, $"The live outer-revise witness was not met: status={run.Status}; rounds={result.ReviseRounds}; starts={starts}; tools={toolCount}; firstInitial={firstObserved}; exit={result.ExitReason}. {await DescribeMissAsync(run.TeamId, result, native, firstRaw, cancellationToken)}");
        }
        finally
        {
            await CancelFixtureRunsAsync(teamId);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Attribution-only detail for a missed witness: the RAW first oracle observation (so a <c>firstInitial=False</c> round-0 anomaly names what the oracle actually read), the native tool/command lines, and the tail of the captured CLI stream. Bounded, single-line, and never consulted by <see cref="MeetsWitness"/>.</summary>
    private async Task<string> DescribeMissAsync(Guid teamId, AgentRunResult result, IReadOnlyList<AgentRunEvent> native, string firstRaw, CancellationToken cancellationToken)
    {
        var tools = native.Where(item => item.Kind is AgentEventKind.ToolCall or AgentEventKind.CommandExecuted).Select(item => $"{item.Kind}:{Bounded(item.Text, 160)}");
        var stream = await ReadCapturedStreamAsync(teamId, result, cancellationToken).ConfigureAwait(false);

        return $"firstObservation='{Bounded(firstRaw, 120)}'; acceptanceDetail='{Bounded(result.AcceptanceDetail, 240)}'; summary='{Bounded(result.Summary, 240)}'; toolEvents=[{Bounded(string.Join(" || ", tools), 800)}]; streamTail='{Bounded(stream, 600, tail: true)}'";
    }

    /// <summary>The faithful captured CLI stream — inline when small, otherwise resolved through the artifact the executor offloaded it to. A diagnostic read must never turn a CapabilityMiss into an unattributable crash, so a failed resolve degrades to a named marker.</summary>
    private async Task<string> ReadCapturedStreamAsync(Guid teamId, AgentRunResult result, CancellationToken cancellationToken)
    {
        if (result.TranscriptArtifactId is not { } artifactId) return result.Transcript;

        try
        {
            using var scope = fixture.BeginScope();
            var bytes = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, cancellationToken).ConfigureAwait(false);
            return bytes is null ? $"<transcript artifact {artifactId} absent>" : Encoding.UTF8.GetString(bytes.Bytes);
        }
        catch (Exception ex)
        {
            return $"<transcript artifact {artifactId} unreadable: {ex.GetType().Name}>";
        }
    }

    private static string Bounded(string? text, int max, bool tail = false)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var single = text.ReplaceLineEndings("\\n");
        if (single.Length <= max) return single;

        return tail ? "…" + single[^max..] : single[..max] + "…";
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, LiveCase live, CancellationToken cancellationToken)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential { Id = id, TeamId = teamId, Provider = Provider, DisplayName = "live outer revision", EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(live.ApiKey), BaseUrl = live.BaseUrl, Status = CredentialStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync(cancellationToken);
        return id;
    }

    private async Task CancelFixtureRunsAsync(Guid teamId)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var scope = fixture.BeginScope();
        var runs = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(run => run.TeamId == teamId).ToListAsync(cleanup.Token);
        foreach (var run in runs)
        {
            await scope.Resolve<IAgentRunService>().CancelRunningAsync(run.Id, "live revision fixture teardown", AgentRunAbandonCause.OperatorCancelled, cleanup.Token);
            if (run.RunnerHandleJson is not { } handleJson) continue;
            var handle = JsonSerializer.Deserialize<SandboxHandle>(handleJson, AgentJson.Options).ShouldNotBeNull();
            await scope.Resolve<ISandboxRunnerRegistry>().Resolve(handle.Kind).ShouldBeAssignableTo<ISandboxDurableRunner>().TerminateAsync(handle, cleanup.Token);
        }
    }

    private sealed record LiveCase(string BaseUrl, string ApiKey, string Model);
}
