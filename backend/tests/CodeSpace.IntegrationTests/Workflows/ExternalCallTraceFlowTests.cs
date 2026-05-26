using System.Net;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Pins the contract that external-call nodes emit a paired (external_call.started,
/// external_call.completed) into the ledger, with the started row linked back to the
/// enclosing node row via parent_record_id and both rows sharing one correlation_id.
///
/// <para>Why this matters: when an operator opens the run-detail page two months from now
/// and asks "what HTTP call did the http.request node make and what did it get back", the
/// only honest answer is "read the ledger". Without these records the answer becomes "read
/// the server logs that we no longer have". Pin the contract here.</para>
///
/// <para>The test stands up a lightweight loopback HTTP server using <see cref="HttpListener"/>,
/// runs a one-node workflow whose http.request node calls the loopback, then queries
/// <c>workflow_run_record</c> directly.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ExternalCallTraceFlowTests
{
    private readonly PostgresFixture _fixture;

    public ExternalCallTraceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Http_node_emits_external_call_pair_linked_by_correlation_id()
    {
        await using var server = await LoopbackHttpServer.StartAsync(statusCode: 200, body: "{\"ok\":true}");

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateHttpWorkflowAsync(teamId, userId, server.BaseUrl);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // ── Started + completed records both exist + share a correlation id. ──
        var callRecords = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && (r.RecordType == WorkflowRunRecordTypes.ExternalCallStarted || r.RecordType == WorkflowRunRecordTypes.ExternalCallCompleted))
            .OrderBy(r => r.Sequence)
            .ToListAsync();

        callRecords.Count.ShouldBe(2,
            "http.request MUST emit exactly one started + one completed record per call; got: " + string.Join(",", callRecords.Select(r => r.RecordType)));

        var started = callRecords.Single(r => r.RecordType == WorkflowRunRecordTypes.ExternalCallStarted);
        var completed = callRecords.Single(r => r.RecordType == WorkflowRunRecordTypes.ExternalCallCompleted);

        completed.CorrelationId.ShouldBe(started.CorrelationId,
            "the two records MUST share a correlation_id so the run-detail UI can pair request with response");

        // ── Started carries operator-readable target + method. ──
        var startedPayload = JsonDocument.Parse(started.PayloadJson).RootElement;
        startedPayload.GetProperty("target").GetString()!.ShouldStartWith("http://127.0.0.1:");
        // started.target MUST be the HTTP URL the operator can see at a glance.
        startedPayload.GetProperty("method").GetString().ShouldBe("GET");

        // ── Started chains back to the enclosing node.started row via parent_record_id. ──
        var nodeStarted = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeStarted && r.NodeId == "call")
            .SingleAsync();

        started.ParentRecordId.ShouldBe(nodeStarted.Id,
            "external_call.started.parent_record_id MUST point at the node.started row of the node that issued the call — that's how the timeline tree renders correctly");

        // ── Completed payload carries the protocol-level status code. ──
        // The payload field is "status" (set by RunRecordLogger.ExternalCallCompletedAsync);
        // the manifest-friendly name "status_code" lives on the INodeObservability surface.
        var completedPayload = JsonDocument.Parse(completed.PayloadJson).RootElement;
        completedPayload.GetProperty("status").GetInt32().ShouldBe(200,
            "completed.status MUST surface the HTTP status so operators don't have to dig into node outputs to triage");
    }

    [Fact]
    public async Task Http_node_emits_external_call_failed_when_transport_fails()
    {
        // Hit a port that's almost certainly closed. The http.request node should emit
        // external_call.failed (NOT completed) and the run should land in Failure.
        var deadUrl = "http://127.0.0.1:1/"; // port 1 = always-rejected on every reasonable host

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateHttpWorkflowAsync(teamId, userId, deadUrl);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var failedRecord = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.ExternalCallFailed)
            .SingleOrDefaultAsync();

        failedRecord.ShouldNotBeNull("external_call.failed MUST be emitted when the underlying HTTP call throws");
        var failedPayload = JsonDocument.Parse(failedRecord!.PayloadJson).RootElement;
        failedPayload.GetProperty("error").GetString().ShouldNotBeNullOrEmpty(
            "failed.error MUST surface the underlying exception message so operators can diagnose");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateHttpWorkflowAsync(Guid teamId, Guid userId, string url)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();

        // A two-node graph: trigger → http.request("call") → terminal. We can't end at the
        // http.request because non-terminal nodes can't be terminal nodes; add a Terminal sink.
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "call",  TypeKey = "http.request",
                        Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json($$"""{"url":"{{url}}","method":"GET"}""") },
                new() { Id = "end",   TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "call" },
                new() { From = "call", To = "end" },
            },
        };

        return await mediator.Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "external-call-trace-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    /// <summary>
    /// Minimal loopback HTTP server for the http.request node to call. Stands up on an
    /// ephemeral loopback port so concurrent test runs don't collide (Rule 12.8).
    /// </summary>
    private sealed class LoopbackHttpServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveLoop;

        public string BaseUrl { get; }

        private LoopbackHttpServer(HttpListener listener, int port, int statusCode, string body)
        {
            _listener = listener;
            BaseUrl = $"http://127.0.0.1:{port}/echo";
            _serveLoop = ServeAsync(statusCode, body, _cts.Token);
        }

        public static async Task<LoopbackHttpServer> StartAsync(int statusCode, string body)
        {
            // Pick a free ephemeral port up-front, then bind HttpListener to it. We can't ask
            // HttpListener for "any free port" directly, so the TcpListener probe is the
            // canonical pattern (Rule 12.8).
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            await Task.Yield();
            return new LoopbackHttpServer(listener, port, statusCode, body);
        }

        private async Task ServeAsync(int statusCode, string body, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                context.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* best-effort */ }
            try { await _serveLoop.ConfigureAwait(false); } catch { /* best-effort */ }
            _listener.Close();
            _cts.Dispose();
        }
    }
}
