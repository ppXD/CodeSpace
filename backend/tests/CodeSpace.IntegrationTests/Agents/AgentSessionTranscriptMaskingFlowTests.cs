using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Agents.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// SX-13 against real Postgres, the real executor, the real durable runner, the real checkpointer and the real artifact
/// store: an agent that echoes its model key and its goal's workflow secret into its OWN session file. The live stdout
/// path was always masked; the session transcript went around it, into <c>result_jsonb.sessionTranscript</c>, the
/// mid-run checkpoint artifacts and the resume payload a retry is handed. Each of the three is read back here from
/// where production stores it, and each must hold the mask — inside JSON that still parses, because the next attempt
/// hands exactly these lines to <c>--resume</c>. Both paths that capture a session are driven: the launch, and the
/// re-attach that finishes a run whose launching worker went away.
///
/// <para>Fidelity: 🟢 high — only the model is scripted (<see cref="SessionKeepingCli"/>). The key reaches the agent
/// through the production credential resolve and projection, and the workflow secret through the production task
/// seal and open.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentSessionTranscriptMaskingFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentSessionTranscriptMaskingFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_key_and_a_workflow_secret_the_agent_wrote_into_its_session_are_masked_wherever_the_session_is_kept()
    {
        if (OperatingSystem.IsWindows()) return;   // the scripted CLI is /bin/sh (Rule 12.1)

        // MUTATIONS this pins: drop the mask from CaptureSessionTranscriptAsync → result_jsonb and the resume payload carry
        // both canaries; drop it from the checkpointer → the checkpoint artifact does.
        var key = $"CANARY-G2-it-sk-ant-{Guid.NewGuid():N}";
        var workflowSecret = $"CANARY-G2-it-wf-{Guid.NewGuid():N}";
        var (teamId, runId) = await SessionKeepingCli.SeedQueuedRunAsync(_fixture, key, workflowSecret);
        var cli = new SessionKeepingCli($"s-{Guid.NewGuid():N}");

        await SessionKeepingCli.ExecuteAsync(_fixture, runId, cli);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var row = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        row.Status.ShouldBe(AgentRunStatus.Succeeded, $"the run must land for its stored session to mean anything: {row.Error}");
        cli.Invocations.ShouldHaveSingleItem().Environment[SessionKeepingCli.KeyEnvVar].ShouldBe(key, "fixture check: the agent was really handed the key it echoes");
        cli.Invocations[0].Goal.ShouldContain(workflowSecret, Case.Sensitive, "fixture check: the agent was really handed the exact goal, opened from its sealed task");

        var result = JsonSerializer.Deserialize<AgentRunResult>(row.ResultJson!, AgentJson.Options)!;
        AssertMaskedSession("result_jsonb.sessionTranscript", result.SessionTranscript, key, workflowSecret);

        var payload = WorkflowResumeAgentRunCompletionNotifier.BuildResumePayload(row);
        payload.ShouldNotContain(key, Case.Sensitive, "the resume payload a retry is handed must not carry the key");
        payload.ShouldNotContain(workflowSecret, Case.Sensitive, "nor the workflow secret");
        AssertMaskedSession("the resume payload's sessionTranscript", JsonDocument.Parse(payload).RootElement.GetProperty("sessionTranscript").GetString(), key, workflowSecret);

        var checkpoints = await SessionKeepingCli.KeptCheckpointsAsync(_fixture, teamId, runId, landed: _ => true);

        checkpoints.ShouldNotBeEmpty("fixture check: the live tick must have checkpointed the session, or this arm proves nothing about checkpoints");

        foreach (var checkpoint in checkpoints) AssertMaskedSession("a checkpoint artifact", checkpoint, key, workflowSecret);
    }

    [Fact]
    public async Task A_session_a_re_attach_captures_after_the_launching_worker_went_away_is_masked_wherever_it_is_kept()
    {
        if (OperatingSystem.IsWindows()) return;   // the scripted CLI is /bin/sh (Rule 12.1)

        // The durability path session continuity exists for: the worker that launched the run goes away while the agent
        // works, and the next one re-attaches to the live process, tails it to its end, checkpoints and captures its
        // session — under the redactor it rebuilds from the sealed task and the credential. The agent names its session
        // only once the first worker is gone, so every checkpoint and the capture are the re-attach's own. The task names
        // its own workspace, which is what lets a re-attach address a session the Claude layout keys on it.
        // MUTATIONS this pins: drop the redactor from the re-attach's capture → result_jsonb and the resume payload carry
        // both canaries; drop it from the re-attach's checkpoint tick → a checkpoint artifact does.
        using var context = new ReattachedRunContext();
        var key = $"CANARY-G2-reattach-sk-ant-{Guid.NewGuid():N}";
        var workflowSecret = $"CANARY-G2-reattach-wf-{Guid.NewGuid():N}";
        var (teamId, runId) = await SessionKeepingCli.SeedQueuedRunAsync(_fixture, key, workflowSecret, task => task with { WorkspaceDirectory = context.Workspace });
        var cli = new SessionKeepingCli($"s-{Guid.NewGuid():N}", context.ReleaseFile);

        await LaunchAndLoseTheWorkerAsync(runId, cli);

        var reattach = SessionKeepingCli.ReattachAsync(_fixture, await ReserveReattachAsync(runId), cli);
        context.Release();
        (await Task.WhenAny(reattach, Task.Delay(TimeSpan.FromSeconds(90)))).ShouldBe(reattach, $"the re-attach of run {runId} did not land within 90s — check agent_run.status and whether its spool holds an exit marker");
        await reattach;

        using var verify = _fixture.BeginScope();
        var row = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        row.Status.ShouldBe(AgentRunStatus.Succeeded, $"the re-attach must tail the agent to its end for its session to mean anything: {row.Error}");
        cli.Invocations.ShouldHaveSingleItem().Goal.ShouldContain(workflowSecret, Case.Sensitive, "fixture check: the agent was handed the exact goal, opened from its sealed task");

        var result = JsonSerializer.Deserialize<AgentRunResult>(row.ResultJson!, AgentJson.Options)!;
        AssertMaskedSession("result_jsonb.sessionTranscript", result.SessionTranscript, key, workflowSecret);

        var payload = WorkflowResumeAgentRunCompletionNotifier.BuildResumePayload(row);
        payload.ShouldNotContain(key, Case.Sensitive, "the resume payload a retry is handed must not carry the key");
        payload.ShouldNotContain(workflowSecret, Case.Sensitive, "nor the workflow secret");

        var checkpoints = await SessionKeepingCli.KeptCheckpointsAsync(_fixture, teamId, runId, landed: _ => true);

        checkpoints.ShouldNotBeEmpty("fixture check: the re-attach's tick must have checkpointed the session, or this arm proves nothing about its checkpoints");

        foreach (var checkpoint in checkpoints) AssertMaskedSession("a checkpoint artifact", checkpoint, key, workflowSecret);
    }

    /// <summary>Launch the run on a worker that then goes away: wait until the launch is on the row, then cancel the job — which leaves the run Running and its agent alive for a re-attach.</summary>
    private async Task LaunchAndLoseTheWorkerAsync(Guid runId, SessionKeepingCli cli)
    {
        using var worker = new CancellationTokenSource();
        var execution = SessionKeepingCli.ExecuteAsync(_fixture, runId, cli, worker.Token);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);

        while (await HandleOfAsync(runId) is null)
        {
            if (execution.IsCompleted) await execution;   // surfaces the launch's own failure instead of waiting out the deadline

            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, $"run {runId} recorded no runner handle within 60s — the launch never reached the durable runner; check agent_run.error for that run");
            await Task.Delay(100);
        }

        await worker.CancelAsync();

        try { await execution; }
        catch (OperationCanceledException) { /* the worker went away — the live agent it left is what the re-attach takes over */ }

        (await HandleOfAsync(runId)).ShouldNotBeNull("fixture check: the run is left for a re-attach");
    }

    private async Task<string?> HandleOfAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        return run.Status == AgentRunStatus.Running ? run.RunnerHandleJson : null;
    }

    /// <summary>Lapse the run's observation lease and reserve the re-attach — the reconciler's atomic step, taken here so the test owns when the next worker takes over.</summary>
    private async Task<AgentRunReattachReservation> ReserveReattachAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");

        return (await scope.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None)).ShouldNotBeNull("fixture check: the lapsed run is reservable for a re-attach");
    }

    /// <summary>The run's own workspace and the file that releases its agent. Disposing releases the agent too, so a failing test never leaves it waiting.</summary>
    private sealed class ReattachedRunContext : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cs-g2-reattach-{Guid.NewGuid():N}");

        public ReattachedRunContext() => Directory.CreateDirectory(Workspace);

        public string Workspace => Path.Combine(_root, "workspace");

        public string ReleaseFile => Path.Combine(_root, "release");

        public void Release() => File.WriteAllText(ReleaseFile, "go");

        public void Dispose()
        {
            try { Release(); } catch (IOException) { /* best effort */ }

            try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort: the agent may still be exiting */ }
        }
    }

    /// <summary>The session as kept: neither canary, every line parses, and the two values the agent wrote are there masked — which also proves the fixture really wrote them.</summary>
    private static void AssertMaskedSession(string where, string? session, string key, string workflowSecret)
    {
        session.ShouldNotBeNullOrEmpty($"{where} must hold the captured session — an empty one would pass every check below");
        session!.ShouldNotContain(key, Case.Sensitive, $"{where} carries the model key the agent echoed");
        session.ShouldNotContain(workflowSecret, Case.Sensitive, $"{where} carries the workflow secret the goal embedded");

        var lines = SessionKeepingCli.ParseLines(session);

        lines.Single(l => l.GetProperty("type").GetString() == "user").GetProperty("tool_result").GetString()
            .ShouldBe(SecretRedactor.Placeholder, $"{where}: the echoed key is masked inside its JSON value");
        lines.Single(l => l.TryGetProperty("text", out var text) && text.GetString()!.StartsWith("Deploy", StringComparison.Ordinal)).GetProperty("text").GetString()
            .ShouldBe($"Deploy the billing service with {SecretRedactor.Placeholder}", $"{where}: the workflow secret is masked inside its JSON value");
    }
}
