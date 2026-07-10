using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): H1's vacuous-delivery-success fix proven against a LIVE brain through the REAL
/// durable engine — the <see cref="RealModelSupervisorWholeLoopE2ETests"/> chassis (production
/// <c>LlmSupervisorDecider</c> via the <see cref="SupervisorDeciderMode"/> seam, real jobs, real git on a bare
/// <c>file://</c> remote, real Postgres) with a PATCH-ONLY repository and an operator delivery contract that
/// REQUIRES a pull request. The two operator intents conflict by construction, so the honest arc is: the live
/// model drives plan → spawn → merge → stop; the delivery gate forces a server publish (policy-Skipped, zero
/// PRs); the NEXT live stop parks on the gate's own card naming the patch-only conflict; a human answer buys
/// exactly ONE fresh re-attempt (still Skipped); and only then does the answer stand as the interim waiver and
/// release the live model's stop to an honest terminal — with zero pull requests and the whole adjudication on
/// the durable tape.
///
/// <para><b>What the live arm adds over the deterministic tiers</b> (40 unit + flow integration tests already
/// pin the gate's ladder): the two rungs only a live brain can exercise — a REAL model's stop being rejected
/// and substituted twice without derailing it, and the model actually STOPPING AGAIN after the human's answer
/// (completing the release) rather than wandering. The gate mechanics stay deterministic; the model's decisions
/// are genuinely live.</para>
///
/// <para><b>Gate policy</b> (three-way, the reaction-arc shape): a CODE FAULT reds the blessed wire at once —
/// above all the exact regression this arm exists to kill: the run terminalizing Success while the required-PR
/// contract was never parked on a human (vacuous success), a re-park after adjudication (the released-state
/// dead-end), or an engine Failure. A CAPABILITY MISS (the model parked short of ever stopping) is REPORTED,
/// never gated — model capability is the headline whole-loop arc's criterion, not this arm's. Self-skips
/// LOUDLY without <c>CODESPACE_LLM_*</c> (skip ≠ pass); FAILS on a partial secret config. POSIX-only.
/// <c>[Category=RealModel]</c> so it runs ONLY on the real-model lane.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelDeliveryGateE2ETests : IDisposable
{
    private const string NodeId = "sup";
    private const string Provider = "Anthropic";

    private readonly PostgresFixture _fixture;

    public RealModelDeliveryGateE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;
        SetDeciderMode(useLiveModel: true);
    }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDeciderMode>().UseLiveModel = false;   // restore the shared-fixture default for siblings
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;
    }

    [Fact]
    public async Task A_live_brain_under_a_patch_only_policy_parks_on_the_delivery_conflict_and_completes_only_after_adjudication()
    {
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass: surfaced loudly as NOT EVALUATED
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip green proving nothing.");

        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script
        if (!await GitReadyAsync()) return;

        using var cli = new FileWritingFakeCli();   // agents always succeed with a real patch — agent capability is not this arm's subject

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var conversationId = await SeedConversationAsync(teamId, userId);   // the surface the gate parks its card on

        using var remote = new BareRemote();
        // A structural acceptance floor (exit 0) — the acceptance grade is not this arm's subject; the DELIVERY
        // contract is. The agents' work still really integrates on the bare remote before the stop reaches the gate.
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main", RepositoryPublishMode.PatchOnly);

        var (brainModelId, _) = await SeedBrainModelAsync(teamId, baseUrl!.TrimEnd('/'), apiKey!, model!);

        var workflowId = await CreateDeliveryContractWorkflowAsync(teamId, userId, repoId, brainModelId, conversationId);

        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            jobClient.Clear();   // SAFE under [Collection(PostgresCollection)] (serial); a no-op-on-empty
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            // ── Phase 1: the live model must have driven to a stop, and the gate must have PARKED it. ──
            var afterDrive = await SnapshotAsync(runId, teamId);

            if (afterDrive.RunStatus == WorkflowRunStatus.Failure)
                return (RealModelOutcome.CodeFault, $"the run FAILED mid-arc (error={afterDrive.RunError ?? "(none)"}) — an engine fault, not a model outcome");

            if (afterDrive.RunStatus == WorkflowRunStatus.Success)
                return (RealModelOutcome.CodeFault,
                    "the run terminalized Success WITHOUT ever parking the required-PR conflict on a human — the exact vacuous-success false-green H1 exists to kill "
                    + $"(publishes={afterDrive.PublishCount}, gateCards={afterDrive.GateCardCount})");

            if (afterDrive.PendingActionToken is null)
                return (RealModelOutcome.CapabilityMiss, $"the live model never drove to a gate-parked stop (runStatus={afterDrive.RunStatus}, decisions=[{afterDrive.KindTrail}]) — reported, not gating");

            if (afterDrive.PendingQuestion?.StartsWith(SupervisorDeliveryGate.QuestionPrefix, StringComparison.Ordinal) != true)
                return (RealModelOutcome.CapabilityMiss, $"the run parked on a NON-gate card ('{Truncate(afterDrive.PendingQuestion)}') before the delivery conflict ever surfaced — reported, not gating");

            if (!afterDrive.PendingQuestion.Contains("patch-only", StringComparison.OrdinalIgnoreCase))
                return (RealModelOutcome.CodeFault, $"the gate parked but its card does not name the patch-only policy conflict: '{Truncate(afterDrive.PendingQuestion)}'");

            afterDrive.PublishCount.ShouldBeGreaterThanOrEqualTo(1, "the gate must have forced the first server publish before parking");

            // ── Phase 2: the human adjudicates; the answer must buy exactly ONE re-attempt, then release. ──
            await AnswerAsync(afterDrive.PendingActionToken, userId, teamId, "understood — patch-only is accepted, finish without the pull request");
            await jobClient.WaitForPendingAsync();

            var final = await SnapshotAsync(runId, teamId);

            if (final.RunStatus == WorkflowRunStatus.Failure)
                return (RealModelOutcome.CodeFault, $"the run FAILED after the adjudication answer (error={final.RunError ?? "(none)"})");

            if (final.PendingActionToken is not null && final.PendingQuestion?.StartsWith(SupervisorDeliveryGate.QuestionPrefix, StringComparison.Ordinal) == true)
                return (RealModelOutcome.CodeFault, $"the gate RE-PARKED on the state the human already adjudicated ('{Truncate(final.PendingQuestion)}') — the released-state dead-end H1's release exists to close");

            if (final.RunStatus != WorkflowRunStatus.Success)
                return (RealModelOutcome.CapabilityMiss, $"the live model did not drive to a terminal after the answer (runStatus={final.RunStatus}, decisions=[{final.KindTrail}]) — reported, not gating");

            // ── The honest terminal: exactly one adjudicated re-attempt, zero pull requests, model-authored stop. ──
            if (final.PublishCount < afterDrive.PublishCount + 1)
                return (RealModelOutcome.CodeFault, $"the answer did not buy the ONE fresh re-attempt (publishes before={afterDrive.PublishCount}, after={final.PublishCount}) — a direct release would turn 'fix it and retry' answers into silent waivers");

            if (final.AnyPublishSatisfied)
                return (RealModelOutcome.CodeFault, "a publish reported an Opened/AlreadyOpened PR against a patch-only repo — the policy guard did not hold");

            if (final.IntegrationManifestWithPr)
                return (RealModelOutcome.CodeFault, "a PublishManifest row carries a pull-request reference — a PR was opened despite the patch-only policy");

            if (final.LastDecisionKind != SupervisorDecisionKinds.Stop || final.LastStopForcedReason is not null)
                return (RealModelOutcome.CapabilityMiss, $"the terminal was not a model-authored stop (last={final.LastDecisionKind}, forcedReason={final.LastStopForcedReason ?? "(none)"}) — reported, not gating");

            var verdict = $"{Provider} '{model}': the live brain drove real work to an integrated head; the delivery gate parked the required-PR × patch-only conflict on a human card, "
                        + $"the answer bought exactly one re-attempt (publishes={final.PublishCount}, all policy-skipped), and the adjudicated stop terminalized honestly with ZERO pull requests.";
            Console.WriteLine($"[delivery-gate-e2e] {verdict}");
            return (RealModelOutcome.Drove, verdict);
        });
    }

    // ─── Tape/state snapshot ─────────────────────────────────────────────────────────

    private sealed record Snapshot(WorkflowRunStatus RunStatus, string? RunError, string? PendingActionToken, string? PendingQuestion,
        int PublishCount, int GateCardCount, bool AnyPublishSatisfied, bool IntegrationManifestWithPr,
        string? LastDecisionKind, string? LastStopForcedReason, string KindTrail);

    private async Task<Snapshot> SnapshotAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        var decisions = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence).ToListAsync();

        var pendingWait = await db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending)
            .FirstOrDefaultAsync();

        var pendingQuestion = pendingWait is null
            ? null
            : decisions.Where(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman)
                .Select(d => ReadQuestion(d.PayloadJson)).LastOrDefault(q => q is not null);

        var publishes = decisions.Where(d => d.DecisionKind == SupervisorDecisionKinds.Publish).ToList();
        var anySatisfied = publishes.Any(p =>
            (SupervisorOutcome.ReadPublishResult(p.OutcomeJson)?.PullRequests ?? Array.Empty<RoomPullRequestOpened>())
                .Any(r => r.Disposition is RoomPullRequestDisposition.Opened or RoomPullRequestDisposition.AlreadyOpened));

        var manifests = await scope.Resolve<IPublishManifestStore>().ListForWorkflowRunAsync(runId, teamId, CancellationToken.None);
        var prOnManifest = manifests.Any(m => m.PullRequestNumber is not null || m.PullRequestUrl is not null);

        var last = decisions.LastOrDefault();
        var lastStopReason = last?.DecisionKind == SupervisorDecisionKinds.Stop ? SupervisorOutcome.ReadStopReason(last.PayloadJson) : null;

        return new Snapshot(run.Status, run.Error, pendingWait?.Token,
            pendingQuestion,
            publishes.Count,
            decisions.Count(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman && ReadQuestion(d.PayloadJson)?.StartsWith(SupervisorDeliveryGate.QuestionPrefix, StringComparison.Ordinal) == true),
            anySatisfied, prOnManifest,
            last?.DecisionKind, lastStopReason,
            string.Join("→", decisions.Select(d => d.DecisionKind)));
    }

    private static string? ReadQuestion(string? payloadJson)
    {
        if (payloadJson is null) return null;
        try { return JsonSerializer.Deserialize<SupervisorAskHumanPayload>(payloadJson, AgentJson.Options)?.Question; }
        catch (JsonException) { return null; }
    }

    private async Task AnswerAsync(string token, Guid actorUserId, Guid teamId, string answer)
    {
        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IWorkflowResumeService>()
            .ResumeByActionTokenAsync(token, Core.Services.Supervisor.Executors.RealSupervisorActionExecutor.AnswerActionKey, actorUserId, answer, values: null, teamId, CancellationToken.None);

        result.ShouldBe(ActionResumeResult.Resumed, "the human's answer resolves the gate's Action wait via the real token-correlated resume path");
    }

    // ─── Seeding (mirrors RealModelSupervisorWholeLoopE2ETests' own fixtures) ─────────

    private async Task<Guid> CreateDeliveryContractWorkflowAsync(Guid teamId, Guid userId, Guid repoId, Guid brainModelId, Guid conversationId)
    {
        // The operator's OWN pre-declared delivery contract (deliverySpec.openPullRequest=true) — path ② of the
        // DC-2b authorization ladder — against a repo whose PublishMode is PatchOnly: the conflict is structural.
        var supConfig = $$"""
            {
              "goal": "Add server-side email-format validation to the signup endpoint, with unit tests.",
              "supervisorModelId": "{{brainModelId}}",
              "agentProfile": { "repositoryId": "{{repoId}}", "pushBranch": true, "integrateBranches": true },
              "acceptanceChecks": ["sh", "check.sh"],
              "conversationId": "{{conversationId}}",
              "deliverySpec": { "openPullRequest": true }
            }
            """;

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-delivery-gate-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(supConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch, RepositoryPublishMode publishMode)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = $"https://local/{instanceId:N}" });

        var serializer = scope.Resolve<CodeSpace.Core.Services.Credentials.ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "delivery-gate-e2e-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
            PublishMode = publishMode,
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task<(Guid RowId, Guid CredId)> SeedBrainModelAsync(Guid teamId, string baseUrl, string apiKey, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "live brain cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        var rowId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });

        await db.SaveChangesAsync();
        return (rowId, credId);
    }

    private async Task<Guid> SeedConversationAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var slug = "sup-dg-" + Guid.NewGuid().ToString("N")[..8];
        return await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, userId, CancellationToken.None);
    }

    private void SetDeciderMode(bool useLiveModel)
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDeciderMode>().UseLiveModel = useLiveModel;
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);

    private static string Truncate(string? s, int max = 200) =>
        s is null ? "(none)" : (s.Length <= max ? s : s[..max] + "…").ReplaceLineEndings(" ");

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the remote — base-seeding + best-effort cleanup (mirrors the whole-loop's own).</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-delivery-gate-e2e-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedBaseAsync(Dictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Git(seed, "config", "user.email", "test@codespace.dev");
            await Git(seed, "config", "user.name", "Test");
            await Git(seed, "config", "commit.gpgsign", "false");
            foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(seed, name), content);
            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");
        }

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
            if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");
            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
