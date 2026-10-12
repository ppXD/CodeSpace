using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The records a node writes for itself, through the real engine against real Postgres, with a team secret in scope:
/// the failed record of a call cancelled in flight, the inline excerpt and the artifact of a command whose output was
/// cut inside the secret, and the streamed <c>interaction.delta</c> rows of a model that printed the secret across a
/// flush. Every one is read by any member of the team (the records endpoint, the SSE tail, the artifact download), so
/// none may hold the secret or any fragment of it.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NodeRecordRedactionFlowTests(PostgresFixture fixture)
{
    /// <summary>A shutdown or an operator cancel while the call is in flight writes the failed record from the cancellation branch.</summary>
    [Fact]
    public async Task Cancelling_a_run_while_its_http_request_is_in_flight_keeps_the_secret_out_of_every_record()
    {
        var canary = Canary();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        await SetTeamSecretAsync(teamId, userId, canary);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Guid runId;
        try
        {
            var accepted = listener.AcceptTcpClientAsync();
            runId = await SeedRunAsync(teamId, userId, Definition(new NodeDefinition
            {
                Id = "call", TypeKey = "http.request", Config = WorkflowsTestSeed.Json("""{ "timeoutSeconds": 60 }"""),
                Inputs = WorkflowsTestSeed.Json($$$"""{ "url": "http://127.0.0.1:{{{port}}}/hold?api_key={{team.API_KEY}}", "method": "GET" }"""),
            }));

            var engine = Task.Run(() => RunEngineAsync(runId));
            using var held = await accepted.WaitAsync(TimeSpan.FromSeconds(30));

            using (var operatorScope = fixture.BeginScopeAs(userId, teamId, Roles.Admin))
                (await operatorScope.Resolve<IMediator>().Send(new CancelRunCommand { RunId = runId })).ShouldNotBeNull();

            try { await engine.WaitAsync(TimeSpan.FromSeconds(60)); }
            catch (OperationCanceledException) { /* the engine surfaces the operator's cancel */ }
        }
        finally { listener.Stop(); }

        var records = await RecordsAsync(runId);
        var failed = records.Single(r => r.RecordType == WorkflowRunRecordTypes.ExternalCallFailed);
        JsonDocument.Parse(failed.PayloadJson).RootElement.GetProperty("target").GetString()
            .ShouldBe($"http://127.0.0.1:{port}/hold?api_key={PersistenceSecretRedactor.Marker}", customMessage: "the call in flight at the cancel is named exactly as external_call.started named it");
        ShouldHoldNoFragment(records, canary);
    }

    [Fact]
    public async Task A_command_whose_inline_cap_lands_inside_the_secret_keeps_no_fragment_inline_or_in_its_artifact()
    {
        var canary = Canary();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        await SetTeamSecretAsync(teamId, userId, canary);

        // "deploying with key=" is 19 characters; the 40-character cap keeps a 26-character head, 7 into the secret.
        var runId = await SeedRunAsync(teamId, userId, Definition(Command("printf 'deploying with key=%s\\n' \"$1\"; head -c 4000 /dev/zero | tr '\\000' x", maxOutputChars: 40)));
        await RunEngineAsync(runId);

        var outputs = await NodeOutputsAsync(runId);
        outputs.GetProperty("stdout").GetString()!.ShouldStartWith("deploying with key=");
        var artifact = await ArtifactTextAsync(teamId, outputs.GetProperty("stdoutArtifactId").GetGuid());
        artifact.ShouldStartWith("deploying with key=" + PersistenceSecretRedactor.Marker + "\n", customMessage: "the artifact is the whole output, redacted");

        ShouldHoldNoFragment(outputs.GetRawText(), canary, "node outputs");
        ShouldHoldNoFragment(artifact, canary, "the stdout artifact");
        ShouldHoldNoFragment(await RecordsAsync(runId), canary);
    }

    [Fact]
    public async Task A_command_whose_capture_budget_ends_inside_the_secret_keeps_no_fragment_of_it()
    {
        var canary = Canary();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        await SetTeamSecretAsync(teamId, userId, canary);

        // The runner keeps a 1 MiB prefix. Ten bytes short of it the secret begins, so the capture ends ten characters in.
        var script = $"head -c {SandboxCaptureBudget.DefaultBytes - 10} /dev/zero | tr '\\000' x; printf '%s' \"$1\"; head -c 100 /dev/zero | tr '\\000' y";
        var runId = await SeedRunAsync(teamId, userId, Definition(Command(script, maxOutputChars: 64)));
        await RunEngineAsync(runId);

        var outputs = await NodeOutputsAsync(runId);
        outputs.GetProperty("stdoutCaptureComplete").GetBoolean().ShouldBeFalse("precondition: the runner's budget cut the capture");
        outputs.GetProperty("stdout").GetString()!.ShouldEndWith(PersistenceSecretRedactor.Marker, customMessage: "the excerpt's tail is the masked start of the secret");
        var excerpt = await ArtifactTextAsync(teamId, outputs.GetProperty("stdoutCapturedArtifactId").GetGuid());
        excerpt.ShouldEndWith("x" + PersistenceSecretRedactor.Marker);

        ShouldHoldNoFragment(outputs.GetRawText(), canary, "node outputs");
        ShouldHoldNoFragment(excerpt, canary, "the captured-excerpt artifact");
    }

    /// <summary>
    /// The engine's own path: llm.complete above the streaming threshold, the production recording decorators, the node
    /// scope's capture redactor built from the run's secret paths, and the real ledger the SSE tail reads.
    /// </summary>
    [Fact]
    public async Task A_model_streaming_the_secret_across_a_delta_flush_leaves_no_fragment_in_any_delta_row()
    {
        var canary = Canary();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(fixture, teamId, "secret-echo-model", SecretEchoStreamingLlmClient.ProviderTag);
        await SetTeamSecretAsync(teamId, userId, canary);

        var runId = await SeedRunAsync(teamId, userId, Definition(new NodeDefinition
        {
            Id = "llm", TypeKey = "llm.complete",
            Config = JsonSerializer.SerializeToElement(new { provider = SecretEchoStreamingLlmClient.ProviderTag, maxTokens = 60000 }),
            Inputs = JsonSerializer.SerializeToElement(new { userPrompt = SecretEchoStreamingLlmClient.EchoMarker + "{{team.API_KEY}}" }),
        }));
        await RunEngineAsync(runId);

        var records = await RecordsAsync(runId);
        var deltas = new List<string>();
        foreach (var row in records.Where(r => r.RecordType == WorkflowRunRecordTypes.InteractionDelta && r.NodeId == "llm"))
            deltas.Add(await CapturedTextAsync(teamId, JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("text")));
        deltas.Count.ShouldBeGreaterThanOrEqualTo(2, "precondition: the 32-KiB bound flushed mid-stream, so the echo was split across rows");
        string.Concat(deltas).ShouldBe(SecretEchoStreamingLlmClient.Filler + PersistenceSecretRedactor.Marker + " done", "the rows concatenate to the masked output: nothing lost, nothing duplicated");

        foreach (var delta in deltas) ShouldHoldNoFragment(delta, canary, "an interaction.delta row");
        ShouldHoldNoFragment(records, canary);
    }

    /// <summary>
    /// An operator's cancel while the model is part-way into the secret: the one-second flush has written the settled
    /// text and is holding back the start of the secret, whose rest never arrives. The cancel's own flush must not
    /// release that start verbatim.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_run_while_its_model_streams_the_start_of_the_secret_leaves_no_fragment_in_any_delta_row()
    {
        var canary = Canary();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(fixture, teamId, "secret-echo-model", SecretEchoStreamingLlmClient.ProviderTag);
        await SetTeamSecretAsync(teamId, userId, canary);

        var runId = await SeedRunAsync(teamId, userId, Definition(new NodeDefinition
        {
            Id = "llm", TypeKey = "llm.complete",
            Config = JsonSerializer.SerializeToElement(new { provider = SecretEchoStreamingLlmClient.ProviderTag, maxTokens = 60000 }),
            Inputs = JsonSerializer.SerializeToElement(new { userPrompt = SecretEchoStreamingLlmClient.HoldMarker + "{{team.API_KEY}}" }),
        }));

        var engine = Task.Run(() => RunEngineAsync(runId));
        await WaitForRecordAsync(runId, WorkflowRunRecordTypes.InteractionDelta, "the one-second flush writes the settled text while the stream is held");

        using (var operatorScope = fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            (await operatorScope.Resolve<IMediator>().Send(new CancelRunCommand { RunId = runId })).ShouldNotBeNull();

        try { await engine.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (OperationCanceledException) { /* the engine surfaces the operator's cancel */ }

        var records = await RecordsAsync(runId);
        records.ShouldContain(r => r.RecordType == WorkflowRunRecordTypes.InteractionFailed, "precondition: the cancel cut the stream");
        var deltas = new List<string>();
        foreach (var row in records.Where(r => r.RecordType == WorkflowRunRecordTypes.InteractionDelta && r.NodeId == "llm"))
            deltas.Add(await CapturedTextAsync(teamId, JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("text")));

        deltas.ToArray().ShouldBe(new[] { SecretEchoStreamingLlmClient.HeldLead, PersistenceSecretRedactor.Marker }, "the settled text, then the held start of the secret masked by the cut");
        ShouldHoldNoFragment(records, canary);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task WaitForRecordAsync(Guid runId, string recordType, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await RecordsAsync(runId)).Any(r => r.RecordType == recordType))
        {
            if (DateTime.UtcNow > deadline) throw new ShouldAssertException($"{because}: no {recordType} record for run {runId} within 30s. Check the run's workflow_run_record rows and its node's status for why the stream never flushed.");
            await Task.Delay(50);
        }
    }

    /// <summary>A unique secret whose random part is upper-case consonants, so no six of its characters can turn up by chance in a GUID, a hex digest or prose elsewhere in a record.</summary>
    private static string Canary() => "CANARY-" + string.Concat(Enumerable.Range(0, 24).Select(_ => "BCDFGHJKLMNPQRSTVWZ"[Random.Shared.Next(19)]));

    private static NodeDefinition Command(string script, int maxOutputChars) => new()
    {
        Id = "command", TypeKey = "agent.run_command", Config = WorkflowsTestSeed.EmptyJson(),
        Inputs = JsonSerializer.SerializeToElement(new { command = "/bin/sh", args = new[] { "-c", script, "sh", "{{team.API_KEY}}" }, maxOutputChars }),
    };

    private static WorkflowDefinition Definition(NodeDefinition node) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            node,
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = node.Id }, new() { From = node.Id, To = "end" } },
    };

    private async Task SetTeamSecretAsync(Guid teamId, Guid userId, string value)
    {
        using var scope = fixture.BeginScope();
        await scope.Resolve<IVariableService>().SetAsync(VariableScope.Team, teamId, teamId, "API_KEY", VariableValueType.Secret, JsonSerializer.SerializeToElement(value), null, userId, CancellationToken.None);
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        Guid workflowId;
        using (var scope = fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = "f2-redact-" + Guid.NewGuid().ToString("N")[..8], Definition = definition, Activations = new List<WorkflowActivationInput>(), Enabled = true });
        }

        return await WorkflowsTestSeed.SeedManualRunAsync(fixture, workflowId, teamId);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<List<WorkflowRunRecord>> RecordsAsync(Guid runId)
    {
        using var scope = fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().Where(r => r.RunId == runId).OrderBy(r => r.Sequence).ToListAsync();
    }

    private async Task<JsonElement> NodeOutputsAsync(Guid runId)
    {
        using var scope = fixture.BeginScope();
        var node = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "command");
        node.Status.ShouldBe(NodeStatus.Success, node.Error);
        return JsonDocument.Parse(node.OutputsJson).RootElement.Clone();
    }

    private async Task<string> ArtifactTextAsync(Guid teamId, Guid artifactId)
    {
        using var scope = fixture.BeginScope();
        var artifact = (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None)).ShouldNotBeNull($"artifact {artifactId} was stored for the team");
        return Encoding.UTF8.GetString(artifact.Bytes);
    }

    /// <summary>A captured text field: inline, or offloaded to the team's artifact store behind a <c>$artifact_id</c> ref.</summary>
    private async Task<string> CapturedTextAsync(Guid teamId, JsonElement field) =>
        field.ValueKind == JsonValueKind.String ? field.GetString()! : await ArtifactTextAsync(teamId, field.GetProperty("$artifact_id").GetGuid());

    private static void ShouldHoldNoFragment(IEnumerable<WorkflowRunRecord> records, string canary)
    {
        foreach (var record in records) ShouldHoldNoFragment(record.PayloadJson, canary, $"workflow_run_record {record.RecordType} (node {record.NodeId})");
    }

    /// <summary>Any six consecutive characters of the secret: a whole value AND the head a cut would leave behind.</summary>
    private static void ShouldHoldNoFragment(string text, string canary, string where)
    {
        for (var start = 0; start + 6 <= canary.Length; start++)
        {
            var fragment = canary.Substring(start, 6);
            text.Contains(fragment, StringComparison.Ordinal).ShouldBeFalse($"{where} holds '{fragment}', a fragment of the team secret");
        }
    }
}
