using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): the live behavioral proof that the run-record / agent-event data the new run-canvas
/// FOOTERS render is actually PRODUCED — not merely that the projection can format a hand-seeded row. Two arms, both
/// against a REAL large model:
/// <list type="bullet">
/// <item><b>Test 1 (footers B1 externalCall + B2 tokenStream)</b> drives a minimal real graph
/// <c>trigger → llm.complete → terminal</c> through the durable <see cref="IWorkflowEngine"/> — the highest-fidelity
/// path, because the engine pushes the real <c>LlmCallScope(Kind:"llm.complete")</c> and builds the real
/// <c>NodeObservability</c>, so BOTH record families land with zero test-side plumbing. Streaming is FORCED (the
/// <c>CODESPACE_LLM_STREAMING_THRESHOLD_TOKENS=1</c> override, set in the ctor here so a LOCAL real run streams too and
/// pinned to <c>'1'</c> on the CI lane) so a large-enough real output persists coalesced <c>interaction.delta</c> rows —
/// the exact source the footer's live-token view reads. Asserts the <c>external_call.started/completed</c> pair (B1) AND
/// the streamed <c>interaction.started</c> + ≥1 <c>interaction.delta</c> + <c>interaction.completed</c> feed (B2).</item>
/// <item><b>Test 2 (footer B3 agentFeed)</b> drives <see cref="IAgentRunExecutor.ExecuteAsync"/> directly with a REAL
/// <c>claude</c> CLI over a goal that forces a tool + file edit + a shell command, then reads <c>agent_run_event</c> for
/// the run: ≥1 tool/file/command event, ≥1 assistant message, and a terminal Completed — the footer's agent feed. B5
/// (the agent node's engine <c>node.suspended</c>) is deterministically covered by the durable-engine agent-node
/// integration suite, so this arm keeps to the cheapest proven executor-direct B3 path (no engine-drive flake surface).</item>
/// </list>
///
/// <para><b>Gate policy:</b> both arms produce near-deterministic data (a length-forced completion streams; a
/// file-create-then-<c>ls</c> goal drives tool/file/command events), so each GATES the blessed Anthropic wire via
/// <see cref="RealModelGate.AssessLiveBestOfNAsync(string, System.Func{System.Threading.Tasks.Task{System.ValueTuple{bool, string}}}, int?)"/> —
/// a persistent absence of the footer's own data REDs main. A GATEWAY/transport fault is a non-gating LOUD skip
/// (classified by <see cref="RealModelGate.IsGatewayInfraError"/> for the engine run / <see cref="RealModelRunClassifier.IsGatewayInfra"/>
/// for the agent run); a completed run MISSING the records is a REAL miss the gate REDs on. A no-creds / no-CLI run
/// self-skips LOUDLY (skip ≠ pass). POSIX-only. <c>[Category=RealModel]</c> so it runs ONLY on the real-model lane.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelFooterSignalsE2ETests : IDisposable
{
    private const string Provider = "Anthropic";
    private const string LlmNodeId = "gen";

    private readonly PostgresFixture _fixture;

    private readonly string? _streamingThresholdBefore;

    public RealModelFooterSignalsE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;

        // Force the llm.complete node onto its STREAMING path for a LOCAL real run too (CI pins the same override to
        // '1'): threshold 1 means any maxTokens > 1 streams, so a length-forced completion persists interaction.delta
        // rows — the footer's live-token source. Restored in Dispose. The env only affects llm.complete streaming, so
        // it is inert for the agent-run arm.
        _streamingThresholdBefore = Environment.GetEnvironmentVariable(LlmModelCapabilities.StreamingThresholdEnvVar);
        Environment.SetEnvironmentVariable(LlmModelCapabilities.StreamingThresholdEnvVar, "1");
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(LlmModelCapabilities.StreamingThresholdEnvVar, _streamingThresholdBefore);

    // ─── Test 1 — footers B1 externalCall + B2 tokenStream (engine-driven llm.complete) ─────────

    [Fact]
    public async Task A_real_llm_complete_run_emits_external_call_and_streaming_interaction_delta()
    {
        if (ReadLiveSecretsOrSkip() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedBrainModelAsync(teamId, live.BaseUrl, live.ApiKey, live.Model);   // credentialed-model pool the node resolves
        var workflowId = await CreateLlmCompleteWorkflowAsync(teamId, userId, live.Model);

        // GATING best-of-N on the blessed wire: a FRESH run per attempt; a persistent absence of the footer's own
        // records REDs, a gateway outage is a non-gating LOUD skip (does not consume a capability slot).
        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            await RunEngineAsync(runId);

            return await EvaluateFooterSignalsAsync(runId, live.Model);
        });
    }

    /// <summary>Assert the engine-driven llm.complete produced the footer's B1 external_call pair AND B2 streamed interaction feed. A gateway-infra node failure throws a <see cref="TimeoutException"/> (the gate's non-gating skip); any other failure, or a completed run MISSING the records, returns <c>(false, reason)</c> so the gate REDs on the real regression.</summary>
    private async Task<(bool Ok, string Verdict)> EvaluateFooterSignalsAsync(Guid runId, string model)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        if (run.Status == WorkflowRunStatus.Failure)
        {
            var nodeFailure = await db.WorkflowRunRecord.AsNoTracking()
                .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeFailed && r.NodeId == LlmNodeId)
                .OrderByDescending(r => r.Sequence).Select(r => r.PayloadJson).FirstOrDefaultAsync();

            if (RealModelGate.IsGatewayInfraError(nodeFailure))
                throw new TimeoutException($"the llm.complete node's gateway failed (NON-GATING infra skip): {nodeFailure}");

            return (false, $"{Provider} '{model}': the llm.complete run FAILED and it is NOT a gateway-infra signature — a real regression: {nodeFailure ?? "(no node.failed payload)"}");
        }

        var records = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId)
            .OrderBy(r => r.Sequence)
            .ToListAsync();

        // ── B1: the external_call.* pair the footer's externalCall card reads ──
        var extStarted = records.Where(r => r.RecordType == WorkflowRunRecordTypes.ExternalCallStarted).ToList();
        var extCompleted = records.Where(r => r.RecordType == WorkflowRunRecordTypes.ExternalCallCompleted).ToList();

        if (extStarted.Count != 1 || extCompleted.Count != 1)
            return (false, $"{Provider} '{model}': expected exactly one external_call.started + one .completed, saw {extStarted.Count}/{extCompleted.Count}");

        if (extStarted[0].CorrelationId is null || extStarted[0].CorrelationId != extCompleted[0].CorrelationId)
            return (false, $"{Provider} '{model}': the external_call pair is not correlated (started={extStarted[0].CorrelationId}, completed={extCompleted[0].CorrelationId})");

        var extPayload = JsonDocument.Parse(extStarted[0].PayloadJson).RootElement;
        var target = extPayload.TryGetProperty("target", out var t) ? t.GetString() : null;
        var method = extPayload.TryGetProperty("method", out var m) ? m.GetString() : null;
        var expectedTarget = $"{Provider.ToLowerInvariant()}:{model}";

        if (target != expectedTarget)
            return (false, $"{Provider} '{model}': external_call.started target was '{target}', expected '{expectedTarget}'");
        if (method != "complete")
            return (false, $"{Provider} '{model}': external_call.started method was '{method}', expected 'complete'");

        // ── B2: the streamed interaction feed the footer's tokenStream view reads ──
        var intStarted = records.Where(r => r.RecordType == WorkflowRunRecordTypes.InteractionStarted).ToList();
        var deltas = records.Where(r => r.RecordType == WorkflowRunRecordTypes.InteractionDelta).ToList();
        var intCompleted = records.Where(r => r.RecordType == WorkflowRunRecordTypes.InteractionCompleted).ToList();

        if (intStarted.Count == 0) return (false, $"{Provider} '{model}': no interaction.started record");
        if (intCompleted.Count == 0) return (false, $"{Provider} '{model}': no interaction.completed record");

        var correlationId = intStarted[0].CorrelationId;
        if (correlationId is null) return (false, $"{Provider} '{model}': interaction.started carries no correlation id");

        if (deltas.Count == 0)
            return (false, $"{Provider} '{model}': the streamed call recorded ZERO interaction.delta rows — streaming did not fire (the footer's live-token view would be empty despite the forced streaming threshold)");

        var ordinals = deltas.Select(d => JsonDocument.Parse(d.PayloadJson).RootElement.GetProperty("ordinal").GetInt32()).ToList();
        if (!ordinals.SequenceEqual(Enumerable.Range(0, deltas.Count)))
            return (false, $"{Provider} '{model}': interaction.delta ordinals are not monotonic from 0: [{string.Join(",", ordinals)}]");

        if (!deltas.Any(d => DeltaTextNonEmpty(JsonDocument.Parse(d.PayloadJson).RootElement)))
            return (false, $"{Provider} '{model}': no interaction.delta carried non-empty text");

        if (deltas.Any(d => d.CorrelationId != correlationId))
            return (false, $"{Provider} '{model}': an interaction.delta did not share the interaction.started correlation id");

        if (intCompleted[0].CorrelationId != correlationId)
            return (false, $"{Provider} '{model}': interaction.completed did not share the interaction.started correlation id");

        var usage = JsonDocument.Parse(intCompleted[0].PayloadJson).RootElement.GetProperty("usage");
        var outTok = usage.GetProperty("outputTokens");
        if (outTok.ValueKind != JsonValueKind.Number || outTok.GetInt32() <= 0)
            return (false, $"{Provider} '{model}': interaction.completed usage.outputTokens was {(outTok.ValueKind == JsonValueKind.Number ? outTok.GetInt32().ToString() : "null")}, expected > 0");

        // Every interaction row is attributed to the llm.complete kind (the footer keys its view on this).
        foreach (var r in intStarted.Concat(deltas).Concat(intCompleted))
        {
            var kind = JsonDocument.Parse(r.PayloadJson).RootElement.GetProperty("kind").GetString();
            if (kind != "llm.complete")
                return (false, $"{Provider} '{model}': an interaction row ({r.RecordType}) carried kind '{kind}', expected 'llm.complete'");
        }

        var verdict = $"{Provider} '{model}': the engine-driven llm.complete recorded the footer's B1 external_call pair (target={target}, method={method}) AND B2 streamed interaction feed (started + {deltas.Count} interaction.delta rows, monotonic ordinals, completed usage.outputTokens={outTok.GetInt32()}), all attributed to kind 'llm.complete'";
        Console.WriteLine($"[footer-signals-e2e] llm.complete: {verdict}");
        return (true, verdict);
    }

    /// <summary>An interaction.delta carried real text iff its <c>text</c> field is a non-empty inline string OR an offloaded <c>$artifact_id</c> ref (a large coalesced fragment rides as an artifact, still proving non-empty text).</summary>
    private static bool DeltaTextNonEmpty(JsonElement deltaPayload)
    {
        if (!deltaPayload.TryGetProperty("text", out var text)) return false;

        return text.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrEmpty(text.GetString()),
            JsonValueKind.Object => text.TryGetProperty("$artifact_id", out _),
            _ => false,
        };
    }

    // ─── Test 2 — footer B3 agentFeed (executor-direct real claude agent.run) ────────────────────

    [Fact]
    public async Task A_real_claude_agent_run_emits_a_tool_or_file_event_feed()
    {
        if (ReadLiveSecretsOrSkip() is not { } live) return;   // skip ≠ pass (surfaced loudly)
        if (!await ClaudeReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed (skip ≠ pass)"); return; }

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // GATING best-of-N on the blessed wire: a file-create-then-ls goal is near-deterministic, so a persistent
        // absence of the agent feed REDs; a gateway/exec fault is a non-gating LOUD skip.
        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            var credId = await SeedAgentCredentialAsync(teamId, live.BaseUrl, live.ApiKey);

            var fileName = $"notes-{Guid.NewGuid().ToString("N")[..8]}.txt";
            var task = new AgentTask
            {
                Goal = $"Create a file named '{fileName}' at the repository root containing a single line of text, then run the shell command 'ls' to confirm it exists. Do nothing else.",
                Harness = "claude-code",
                Model = live.Model,
                ModelCredentialId = credId,
                Autonomy = AgentAutonomyLevel.Trusted,
                Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
                TimeoutSeconds = 180,
            };

            Guid runId;
            using (var scope = _fixture.BeginScope())
                runId = (await scope.Resolve<IAgentRunService>().CreateAsync(task, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;

            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);

            using var read = _fixture.BeginScope();
            var svc = read.Resolve<IAgentRunService>();
            var run = await svc.GetAsync(runId, CancellationToken.None);

            if (run.Status != AgentRunStatus.Succeeded)
            {
                var reason = $"status={run.Status}; exitReason={RealModelRunClassifier.ExitReasonOf(run)}; error={run.Error ?? "(none)"}";

                if (RealModelRunClassifier.IsGatewayInfra(run))
                    throw new AgentExecutionInfraException($"the claude run did not complete — gateway/exec infra (non-gating skip): {reason}");

                return (false, $"{Provider} '{live.Model}': the real claude agent's run did NOT complete — likely a harness/agent-feed regression, not gateway infra: {reason}");
            }

            var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);

            // B3: the footer's agent feed renders the tool/file/command steps + assistant text; the terminal Completed
            // closes the feed. Reading agent_run_event via the production service (the SAME read the footer binds).
            var toolish = events.Where(e => (e.Kind is AgentEventKind.ToolCall or AgentEventKind.FileChanged or AgentEventKind.CommandExecuted) && !string.IsNullOrWhiteSpace(e.Text)).ToList();
            var assistantMessages = events.Count(e => e.Kind == AgentEventKind.AssistantMessage);
            var completed = events.Any(e => e.Kind == AgentEventKind.Completed);
            var kindTrail = string.Join(",", events.Select(e => e.Kind).Distinct());

            if (toolish.Count == 0)
                return (false, $"{Provider} '{live.Model}': the agent run Succeeded but emitted NO ToolCall/FileChanged/CommandExecuted event — the footer's B3 agent feed would be empty (kinds seen: [{kindTrail}])");
            if (assistantMessages == 0)
                return (false, $"{Provider} '{live.Model}': the agent run emitted no AssistantMessage (kinds seen: [{kindTrail}])");
            if (!completed)
                return (false, $"{Provider} '{live.Model}': the agent run carries no terminal Completed event (kinds seen: [{kindTrail}])");

            var verdict = $"{Provider} '{live.Model}': the real claude agent.run produced the footer's B3 event feed — {toolish.Count} tool/file/command event(s) [{string.Join(",", toolish.Select(e => e.Kind).Distinct())}], {assistantMessages} assistant message(s), and a terminal Completed. "
                        + "B5 (the agent node's engine node.suspended) is deterministically covered by the durable-engine agent-node integration suite; this executor-direct arm proves the live B3 feed without the engine-drive flake surface.";
            Console.WriteLine($"[footer-signals-e2e] agent.run: {verdict}");
            return (true, verdict);
        });
    }

    // ─── gate + seeding ──────────────────────────────────────────────────────────

    private readonly record struct LiveSecrets(string BaseUrl, string ApiKey, string Model);

    /// <summary>Read the live-model secrets or self-skip LOUDLY (skip ≠ pass). Returns null when the run cannot go live: all-absent → honest fork/local skip; partial config → hard fail (a rotated/blanked single secret can't silently mask the lane); Windows → the harness + sandbox are /bin/sh based.</summary>
    private static LiveSecrets? ReadLiveSecretsOrSkip()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return null; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip green proving nothing.");

        if (OperatingSystem.IsWindows()) return null;

        return new LiveSecrets(baseUrl!.TrimEnd('/'), apiKey!, model!);
    }

    /// <summary>Seed a KEYED credentialed-model row so <c>IModelPoolSelector</c> resolves the live model + credential for the llm.complete node (the node reads its key + base url from this DB row, never in-process).</summary>
    private async Task SeedBrainModelAsync(Guid teamId, string baseUrl, string apiKey, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "footer-signals e2e brain cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });

        await db.SaveChangesAsync();
    }

    /// <summary>Seed an encrypted gateway <see cref="ModelCredential"/> the executor resolves via <c>ModelCredentialId</c> and the ClaudeCodeHarness projects onto its env (ANTHROPIC_BASE_URL / ANTHROPIC_API_KEY). The live key is read from the DB, never in-process.</summary>
    private async Task<Guid> SeedAgentCredentialAsync(Guid teamId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "footer-signals e2e agent cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return credId;
    }

    /// <summary>A minimal real graph <c>trigger.manual → llm.complete → builtin.terminal</c>. The llm.complete config pins the live model + a deterministic length-forcing prompt + a maxTokens the forced streaming threshold streams; the pool resolves the credential seeded by <see cref="SeedBrainModelAsync"/>.</summary>
    private async Task<Guid> CreateLlmCompleteWorkflowAsync(Guid teamId, Guid userId, string model)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        const string userPrompt = "List the numbers 1 to 40, one per line, each followed by a three-word description of that number. Do not add any preamble or summary.";
        var config = $$"""{"provider":"{{Provider}}","model":"{{model}}","maxTokens":2048}""";

        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = LlmNodeId, TypeKey = "llm.complete", Config = WorkflowsTestSeed.Json(config), Inputs = WorkflowsTestSeed.Json(JsonSerializer.Serialize(new { userPrompt })) },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = LlmNodeId },
                new() { From = LlmNodeId, To = "end" },
            },
        };

        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "footer-signals-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private static async Task<bool> ClaudeReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }
}
