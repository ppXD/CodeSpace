using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Settings.OutboundHttp;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// What a Viewer of the team reads over HTTP after a run had a team secret in scope: the raw records, the live SSE tail,
/// and the artifacts the run stored. Three node-owned writes used to put the secret there: the failed record of a call
/// cancelled in flight, the excerpt and artifact of a command output cut inside the secret, and the streamed
/// <c>interaction.delta</c> rows of a model that printed the secret across a flush.
///
/// <para>Tier: High fidelity. The real app host (JWT, team scope, controllers, the records reader and SSE streamer, the
/// artifact download), the real engine drained behind <c>POST /api/workflows/{id}/run</c>, a real loopback socket held
/// open for the http.request, a real <c>/bin/sh</c> for the command. The one stand-in is the model: a streaming client
/// under its own provider tag, wrapped by the production recording decorators.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class NodeRecordRedactionEndpointE2ETests : IClassFixture<NodeRecordRedactionEndpointE2ETests.LoopbackModelApiFactory>
{
    private readonly LoopbackModelApiFactory _factory;

    public NodeRecordRedactionEndpointE2ETests(LoopbackModelApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_viewer_reading_a_run_cancelled_mid_http_request_finds_no_secret_in_its_records_or_its_sse_tail()
    {
        var world = await SeedWorldAsync();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Guid runId;
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync();
            runId = await LaunchAsync(world, new { id = "call", typeKey = "http.request", config = new { timeoutSeconds = 60 }, inputs = new { url = $"http://127.0.0.1:{port}/hold?api_key={{{{team.API_KEY}}}}", method = "GET" } });

            var engine = Task.Run(() => _factory.JobClient.DrainAsync());
            using var held = await accepted.WaitAsync(TimeSpan.FromSeconds(30));

            (await SendAsync(HttpMethod.Post, $"/api/workflows/runs/{runId}/cancel", world.OwnerId, world.TeamId)).StatusCode.ShouldBe(HttpStatusCode.OK, "the owner cancels the run while its call is held open");

            try { await engine.WaitAsync(TimeSpan.FromSeconds(60)); }
            catch (OperationCanceledException) { /* the engine surfaces the operator's cancel */ }
        }
        finally { listener.Stop(); }

        var records = await ReadAsViewerAsync(world, $"/api/workflows/runs/{runId}/records");
        records.ShouldContain(WorkflowRunRecordTypes.ExternalCallFailed, customMessage: "precondition: the cancelled call left its failed record");
        ShouldHoldNoFragment(records, world.Canary, "GET /records");
        ShouldHoldNoFragment(await ReadAsViewerAsync(world, $"/api/workflows/runs/{runId}/records/stream?after=0"), world.Canary, "the SSE tail");
    }

    [Fact]
    public async Task A_viewer_downloading_a_command_output_cut_inside_the_secret_finds_no_fragment_of_it()
    {
        var world = await SeedWorldAsync();

        var script = "printf 'deploying with key=%s\\n' \"$1\"; head -c 4000 /dev/zero | tr '\\000' x";
        var runId = await LaunchAsync(world, new { id = "command", typeKey = "agent.run_command", config = new { }, inputs = new { command = "/bin/sh", args = new[] { "-c", script, "sh", "{{team.API_KEY}}" }, maxOutputChars = 40 } });
        await _factory.JobClient.DrainAsync();

        var detail = await ReadAsViewerAsync(world, $"/api/workflows/runs/{runId}");
        var artifactId = await NodeOutputGuidAsync(runId, "command", "stdoutArtifactId");
        var artifact = await ReadAsViewerAsync(world, $"/api/artifacts/{artifactId}");

        artifact.ShouldStartWith("deploying with key=[REDACTED]", customMessage: "the downloaded artifact is the whole output, redacted");
        ShouldHoldNoFragment(detail, world.Canary, "GET /runs/{id}");
        ShouldHoldNoFragment(artifact, world.Canary, "GET /api/artifacts/{id}");
        ShouldHoldNoFragment(await ReadAsViewerAsync(world, $"/api/workflows/runs/{runId}/records"), world.Canary, "GET /records");
    }

    [Fact]
    public async Task A_viewer_tailing_model_deltas_streamed_across_a_flush_finds_no_fragment_of_the_secret()
    {
        var world = await SeedWorldAsync();
        await SeedEchoModelAsync(world.TeamId);

        var runId = await LaunchAsync(world, new { id = "llm", typeKey = "llm.complete", config = new { provider = SecretEchoStreamingLlmClient.ProviderTag, maxTokens = 60000 }, inputs = new { userPrompt = SecretEchoStreamingLlmClient.EchoMarker + "{{team.API_KEY}}" } });
        await _factory.JobClient.DrainAsync();

        var tail = await ReadAsViewerAsync(world, $"/api/workflows/runs/{runId}/records/stream?after=0");
        var deltas = DeltaTexts(tail);
        deltas.Count.ShouldBeGreaterThanOrEqualTo(2, "precondition: the 32-KiB bound flushed mid-stream, so the echo was split across rows");

        var resolved = new List<string>();
        foreach (var delta in deltas) resolved.Add(delta.ValueKind == JsonValueKind.String ? delta.GetString()! : await ReadAsViewerAsync(world, $"/api/artifacts/{delta.GetProperty("$artifact_id").GetString()}"));

        string.Concat(resolved).ShouldBe(SecretEchoStreamingLlmClient.Filler + "[REDACTED] done", "the live view concatenates the rows into the masked output");
        foreach (var text in resolved) ShouldHoldNoFragment(text, world.Canary, "an interaction.delta row as the Viewer's live view folds it");
        ShouldHoldNoFragment(tail, world.Canary, "the SSE tail");
    }

    [Fact]
    public async Task A_viewer_tailing_a_run_cancelled_while_its_model_streams_the_start_of_the_secret_finds_no_fragment_of_it()
    {
        var world = await SeedWorldAsync();
        await SeedEchoModelAsync(world.TeamId);

        var runId = await LaunchAsync(world, new { id = "llm", typeKey = "llm.complete", config = new { provider = SecretEchoStreamingLlmClient.ProviderTag, maxTokens = 60000 }, inputs = new { userPrompt = SecretEchoStreamingLlmClient.HoldMarker + "{{team.API_KEY}}" } });
        var engine = Task.Run(() => _factory.JobClient.DrainAsync());
        await WaitForRecordAsync(runId, WorkflowRunRecordTypes.InteractionDelta, "the one-second flush writes the settled text while the stream is held");

        (await SendAsync(HttpMethod.Post, $"/api/workflows/runs/{runId}/cancel", world.OwnerId, world.TeamId)).StatusCode.ShouldBe(HttpStatusCode.OK, "the owner cancels the run while its model is part-way into the secret");

        try { await engine.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (OperationCanceledException) { /* the engine surfaces the operator's cancel */ }

        // The cut's own row lands after run.cancelled, where the SSE tail of a terminal run stops; GET /records has it.
        var records = await ReadAsViewerAsync(world, $"/api/workflows/runs/{runId}/records");
        RecordDeltaTexts(records).ShouldBe(new[] { SecretEchoStreamingLlmClient.HeldLead, "[REDACTED]" }, "the settled text, then the held start of the secret masked by the cut");
        ShouldHoldNoFragment(records, world.Canary, "GET /records");
        ShouldHoldNoFragment(await ReadAsViewerAsync(world, $"/api/workflows/runs/{runId}/records/stream?after=0"), world.Canary, "the SSE tail");
    }

    // ─── World ──────────────────────────────────────────────────────────────────

    private sealed record World(Guid OwnerId, Guid ViewerId, Guid TeamId, string Canary);

    /// <summary>An Owner who authors and runs, a Viewer who only reads, and the team secret the run resolves — set through the real variables endpoint.</summary>
    private async Task<World> SeedWorldAsync()
    {
        var teamId = Guid.NewGuid();
        var ownerId = await SeedUserAsync(teamId, TeamRole.Owner, createTeam: true);
        var viewerId = await SeedUserAsync(teamId, TeamRole.Viewer, createTeam: false);
        var canary = "CANARY-" + string.Concat(Enumerable.Range(0, 24).Select(_ => "BCDFGHJKLMNPQRSTVWZ"[Random.Shared.Next(19)]));

        var set = await SendAsync(HttpMethod.Put, "/api/team-variables/API_KEY", ownerId, teamId, new { name = "API_KEY", valueType = nameof(VariableValueType.Secret), value = canary });
        set.StatusCode.ShouldBe(HttpStatusCode.NoContent, await set.Content.ReadAsStringAsync());

        return new World(ownerId, viewerId, teamId, canary);
    }

    private async Task<Guid> SeedUserAsync(Guid teamId, TeamRole role, bool createTeam)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();

        if (createTeam) db.Team.Add(new Team { Id = teamId, Slug = $"f2-{teamId:N}"[..20], Name = "F2", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.User.Add(new User { SecurityStamp = TestToken.SeedStamp, Id = userId, Email = $"f2-{userId:N}@test.local", Name = role.ToString(), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return userId;
    }

    /// <summary>The team's pool row for the echo model: keyless, because the stand-in never authenticates.</summary>
    private async Task SeedEchoModelAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var credentialId = Guid.NewGuid();

        db.ModelCredential.Add(new ModelCredential { Id = credentialId, TeamId = teamId, Provider = SecretEchoStreamingLlmClient.ProviderTag, DisplayName = "echo", Status = CredentialStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credentialId, ModelId = "secret-echo-model", Source = ModelSource.Manual, Enabled = true });

        await db.SaveChangesAsync();
    }

    /// <summary>The Owner authors trigger → node → terminal over HTTP and launches it; the run executes when the test drains the job client.</summary>
    private async Task<Guid> LaunchAsync(World world, object node)
    {
        var nodeId = JsonSerializer.SerializeToElement(node).GetProperty("id").GetString()!;
        var workflow = new
        {
            name = $"f2-{Guid.NewGuid():N}"[..20],
            definition = new
            {
                schemaVersion = 1,
                nodes = new[] { new { id = "start", typeKey = "trigger.manual", config = new { }, inputs = new { } }, node, new { id = "end", typeKey = "builtin.terminal", config = new { }, inputs = new { } } },
                edges = new object[] { new { from = "start", to = nodeId }, new { from = nodeId, to = "end" } },
            },
            activations = Array.Empty<object>(),
            enabled = true,
        };

        var created = await SendAsync(HttpMethod.Post, "/api/workflows", world.OwnerId, world.TeamId, workflow);
        created.IsSuccessStatusCode.ShouldBeTrue(await created.Content.ReadAsStringAsync());
        var workflowId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var launched = await SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/run", world.OwnerId, world.TeamId, new { });
        launched.IsSuccessStatusCode.ShouldBeTrue(await launched.Content.ReadAsStringAsync());

        return JsonDocument.Parse(await launched.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetGuid();
    }

    private async Task WaitForRecordAsync(Guid runId, string recordType, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (true)
        {
            using (var scope = _factory.Services.CreateScope())
                if (await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().AnyAsync(r => r.RunId == runId && r.RecordType == recordType)) return;

            if (DateTime.UtcNow > deadline) throw new ShouldAssertException($"{because}: no {recordType} record for run {runId} within 30s. Check the run's workflow_run_record rows and its node's status for why the stream never flushed.");
            await Task.Delay(50);
        }
    }

    private async Task<Guid> NodeOutputGuidAsync(Guid runId, string nodeId, string output)
    {
        using var scope = _factory.Services.CreateScope();
        var node = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == nodeId);

        node.Status.ShouldBe(NodeStatus.Success, node.Error);
        return JsonDocument.Parse(node.OutputsJson).RootElement.GetProperty(output).GetGuid();
    }

    /// <summary>The <c>text</c> of every <c>interaction.delta</c> frame on the SSE tail: inline, or the <c>$artifact_id</c> ref the Room fetches.</summary>
    private static List<JsonElement> DeltaTexts(string tail)
    {
        var texts = new List<JsonElement>();
        var lines = tail.Split('\n');

        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (lines[i].Trim() != "event: " + WorkflowRunRecordTypes.InteractionDelta) continue;

            var data = lines.Skip(i + 1).First(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            texts.Add(DeltaPayload(JsonDocument.Parse(data).RootElement).GetProperty("text").Clone());
        }

        return texts;
    }

    /// <summary>The inline <c>text</c> of every <c>interaction.delta</c> in a <c>GET /records</c> page, in sequence order.</summary>
    private static string[] RecordDeltaTexts(string page) => JsonDocument.Parse(page).RootElement.GetProperty("records").EnumerateArray()
        .Where(record => record.GetProperty("recordType").GetString() == WorkflowRunRecordTypes.InteractionDelta)
        .Select(record => DeltaPayload(record).GetProperty("text").GetString()!)
        .ToArray();

    /// <summary>An SSE frame's data is the record; its payload rides as a JSON string or an object depending on the projection.</summary>
    private static JsonElement DeltaPayload(JsonElement frame)
    {
        var payload = frame.TryGetProperty("payloadJson", out var json) ? json : frame.GetProperty("payload");

        return payload.ValueKind == JsonValueKind.String ? JsonDocument.Parse(payload.GetString()!).RootElement : payload;
    }

    private async Task<string> ReadAsViewerAsync(World world, string url)
    {
        using var response = await SendAsync(HttpMethod.Get, url, world.ViewerId, world.TeamId);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"the Viewer reads {url}: {body}");
        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid userId, Guid teamId, object? body = null)
    {
        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);   // a terminal run's SSE tail closes itself; this only bounds a broken one

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());
        if (body is not null) request.Content = JsonContent.Create(body);

        return await client.SendAsync(request);
    }

    /// <summary>Any six consecutive characters of the secret: the whole value, and the head a cut would leave behind.</summary>
    private static void ShouldHoldNoFragment(string text, string canary, string where)
    {
        for (var start = 0; start + 6 <= canary.Length; start++)
        {
            var fragment = canary.Substring(start, 6);
            text.Contains(fragment, StringComparison.Ordinal).ShouldBeFalse($"{where} gives a Viewer '{fragment}', a fragment of the team secret");
        }
    }

    /// <summary>
    /// The app with two seams: a streaming stand-in model under its own provider tag (wrapped by the production recording
    /// decorators like any client), and loopback admitted for http.request so a test can hold a call open on a local socket.
    /// </summary>
    public sealed class LoopbackModelApiFactory : TaskLaunchApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { [$"{OutboundHttpAllowlistSetting.ConfigurationKey}:0"] = "127.0.0.1" }));
        }

        protected override void ConfigureContractTestServices(ContainerBuilder builder)
        {
            builder.RegisterType<SecretEchoStreamingLlmClient>().As<ILLMClient>().As<IStructuredLLMClient>().As<IStreamingLLMClient>().SingleInstance();
        }
    }
}
