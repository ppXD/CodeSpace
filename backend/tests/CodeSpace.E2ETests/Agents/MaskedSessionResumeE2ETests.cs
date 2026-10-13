using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Agents.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>
/// The continuation a masked session must still buy. SX-13 masks every secret an agent wrote into its session before
/// the session is kept — and the kept session is exactly what the next attempt restores for <c>--resume</c>. So the
/// whole chain runs here, attempt to attempt: a first attempt writes its model credential and its goal's workflow
/// secret into its session; the executor captures it masked; the resume payload a retry is handed is built from that
/// row; the respawn turns the payload into the continuation exactly as <c>agent.run</c> does; the executor restores the
/// masked session into the second attempt's config home; and the second attempt's CLI reads it back line by line and
/// continues.
///
/// <para>Each attempt is brokered, as a deployment that requires confinement (this lane's) launches it and as any
/// deployment does whose worker can front the credential: the agent holds the lease's run token where the key would be,
/// and that token is what it echoes. Unbrokered, such a deployment refuses the launch before the agent starts.</para>
///
/// <para>Fidelity: 🟢 high for everything but the model. The executor, model-credential broker, durable runner,
/// config-home materialization, capture, resume payload (<see cref="WorkflowResumeAgentRunCompletionNotifier"/>) and
/// respawn hint (<see cref="AgentCodeNode"/>) are production; the CLI is <see cref="SessionKeepingCli"/>. The pinned
/// real CLI resumes a masked session in the sandbox lane (<c>GoalChannelE2ETests</c>, the masked-resume arm); the
/// direct-key path is masked in <c>AgentSessionTranscriptMaskingFlowTests</c>.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class MaskedSessionResumeE2ETests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_second_attempt_resumes_from_the_masked_session_the_first_one_left()
    {
        if (OperatingSystem.IsWindows()) return;   // the scripted CLI is /bin/sh (Rule 12.1)

        // MUTATION this pins: capture the session unmasked → the restored session carries the credential the first
        // attempt echoed and its workflow secret → red. A mask that broke a line fails ParseLines here and the CLI's
        // read-back below; the escaped-value cases of the mask itself are pinned in SessionTranscriptRedactionTests.
        var key = $"CANARY-G2-e2e-sk-ant-{Guid.NewGuid():N}";
        var workflowSecret = $"CANARY-G2-e2e-wf-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";
        var firstCli = new SessionKeepingCli(sessionId);

        var (_, first) = await SessionKeepingCli.SeedQueuedRunAsync(fixture, key, workflowSecret);
        await SessionKeepingCli.ExecuteAsync(fixture, first, firstCli, brokered: true);

        var payload = await ResumePayloadAsync(first);
        payload.GetProperty("sessionId").GetString().ShouldBe(sessionId, "fixture check: the first attempt captured its session");
        var echoed = EchoedCredential(firstCli, key);

        var cli = new SessionKeepingCli($"s-{Guid.NewGuid():N}");   // a fresh id, so only the restored session can name the right file
        var (_, second) = await SessionKeepingCli.SeedQueuedRunAsync(fixture, key, workflowSecret, task => AgentCodeNode.ApplyRespawnResumeHint(task, payload));
        await SessionKeepingCli.ExecuteAsync(fixture, second, cli, brokered: true);

        var continued = cli.Invocations.ShouldHaveSingleItem();
        continued.ResumeFromSessionId.ShouldBe(sessionId, "the respawn continues the first attempt's session");
        continued.Goal.ShouldContain(workflowSecret, Case.Sensitive, "the respawn re-sends its sealed goal, so the agent still has the value its restored turn holds masked");

        var restored = continued.RestoredTranscript.ShouldNotBeNull("the continuation was handed the first attempt's session");
        restored.ShouldNotContain(echoed, Case.Sensitive, "the restored session never carries the credential the first attempt echoed");
        restored.ShouldNotContain(key, Case.Sensitive, "nor the key that credential fronts");
        restored.ShouldNotContain(workflowSecret, Case.Sensitive, "nor the workflow secret its goal embedded");
        var restoredLines = SessionKeepingCli.ParseLines(restored);
        restoredLines.Count.ShouldBe(3, "fixture check: init, the echoed credential, the restated goal");
        restoredLines.Single(line => line.GetProperty("type").GetString() == "user").GetProperty("tool_result").GetString()
            .ShouldBe(SecretRedactor.Placeholder, "the echoed credential is in the restored session, masked inside its JSON value");

        using var verify = fixture.BeginScope();
        var row = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == second);
        row.Status.ShouldBe(AgentRunStatus.Succeeded, $"the continuation must run to the end over its masked session: {row.Error}");

        var read = SessionKeepingCli.ParseLines(JsonSerializer.Deserialize<AgentRunResult>(row.ResultJson!, AgentJson.Options)!.Transcript);
        read.Select(line => line.GetRawText()).Take(restoredLines.Count).ShouldBe(restoredLines.Select(line => line.GetRawText()), "the CLI read back exactly the masked session it was restored, every line parsing");
        read[^1].GetProperty("text").GetString().ShouldBe(SessionKeepingCli.ResumedText, "and continued past it");
    }

    /// <summary>The resume payload a retry of the first attempt is handed, built from its stored row by the production notifier.</summary>
    private async Task<JsonElement> ResumePayloadAsync(Guid agentRunId)
    {
        using var scope = fixture.BeginScope();
        var row = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentRunId);

        row.Status.ShouldBe(AgentRunStatus.Succeeded, $"fixture check: the first attempt landed — {row.Error}");

        return JsonDocument.Parse(WorkflowResumeAgentRunCompletionNotifier.BuildResumePayload(row)).RootElement.Clone();
    }

    /// <summary>The credential an attempt's agent was handed, and echoed into its session: the lease's run token, never <paramref name="key"/>.</summary>
    private static string EchoedCredential(SessionKeepingCli cli, string key)
    {
        var environment = cli.Invocations.ShouldHaveSingleItem().Environment;

        environment.Values.ShouldNotContain(key, "fixture check: the attempt was brokered, so the key never reached its agent");
        environment[SessionKeepingCli.KeyEnvVar].ShouldNotBeNullOrWhiteSpace("fixture check: the agent was handed a run token to echo");

        return environment[SessionKeepingCli.KeyEnvVar];
    }
}
