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
/// model drives plan → spawn → merge → stop; the stop meets the server's TWO gates in turn, both blocked by the
/// same repository policy — I3 wants an integrated branch the policy forbids pushing, then DC-2b wants a pull
/// request it forbids opening. Each parks ONCE on its own card naming the patch-only conflict, each answer buys
/// exactly ONE fresh server re-attempt (still blocked), and each re-check reporting the SAME blocker stands as
/// the interim waiver. Only then does the live model's stop reach an honest terminal — zero pull requests, zero
/// branches, and the whole adjudication on the durable tape.
///
/// <para>BOTH cards matter to the arc, not just the delivery one. An immutable publish policy can never be
/// satisfied from inside the run, so a gate that only ever RE-ASKED would strand it — which is precisely what
/// each gate did before it learned to release on the blocker a human ruled on. The loop below therefore
/// adjudicates every card the run raises and calls a REPEAT of an already-answered question the dead end.</para>
///
/// <para><b>What the live arm adds over the deterministic tiers</b> (40 unit + flow integration tests already
/// pin the gate's ladder): the two rungs only a live brain can exercise — a REAL model's stop being rejected
/// and substituted twice without derailing it, and the model actually STOPPING AGAIN after the human's answer
/// (completing the release) rather than wandering. The gate mechanics stay deterministic; the model's decisions
/// are genuinely live.</para>
///
/// <para><b>Gate policy</b> (three-way, the reaction-arc shape): a CODE FAULT reds the blessed wire at once —
/// above all the exact regression this arm exists to kill: the run terminalizing Success while the required-PR
/// contract was never parked on a human (vacuous success), a re-park after adjudication — a card whose exact
/// question a human ALREADY answered (the released-state dead-end) — or an engine Failure. A CAPABILITY MISS (the model parked short of ever stopping) is REPORTED,
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

        // Chassis parity with RealModelSupervisorWholeLoopE2ETests: the throwable DI mutation first, then the env
        // set. Integrate-at-stop ON matches the proven headline arc — without it a merge is tape-only and the
        // I3/delivery ladder walks a different (unproven-in-live) branch than the one the deterministic tiers pin.
        SetDeciderMode(useLiveModel: true);
    }

    public void Dispose()
    {

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDeciderMode>().UseLiveModel = false;   // restore the shared-fixture default for siblings
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;
    }

    [SkippableFact]
    public async Task A_live_brain_under_a_patch_only_policy_parks_on_the_delivery_conflict_and_completes_only_after_adjudication()
    {
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass: surfaced loudly as NOT EVALUATED
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip green proving nothing.");

        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script
        if (!await GitReadyAsync()) return;

        using var cli = new FileWritingFakeCli();   // agents always succeed with a real patch — agent capability is not this arm's subject

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
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
                    + $"(publishes={afterDrive.PublishCount}, gateCards={afterDrive.GateCardCount}, lastDecision={afterDrive.LastDecisionKind ?? "(none)"}, "
                    + $"forcedStopReason={afterDrive.LastStopForcedReason ?? "(none — a model-authored stop)"}, decisions=[{afterDrive.KindTrail}])");

            if (afterDrive.PendingActionToken is null)
                return (RealModelOutcome.CapabilityMiss, $"the live model never drove to a gate-parked stop (runStatus={afterDrive.RunStatus}, decisions=[{afterDrive.KindTrail}]) — reported, not gating");

            // EITHER server gate: a patch-only stop meets I3 first (no branch the policy lets it push) and DC-2b
            // second (no pull request it lets it open). Both are the SAME conflict, asked by the rung that owns it.
            if (!IsServerGateCard(afterDrive.PendingQuestion))
                return (RealModelOutcome.CapabilityMiss, $"the run parked on a NON-gate card ('{Truncate(afterDrive.PendingQuestion)}') before the delivery conflict ever surfaced — reported, not gating");

            if (ParkedCardFault(afterDrive.PendingQuestion, afterDrive.AgentManifestCount, afterDrive.AnyAgentShowsWork) is { } cardFault) return cardFault;

            // ── Phase 2: adjudicate every card the arc raises; each answer buys ONE re-attempt, then releases. ──
            var answered = new List<string>();
            var final = afterDrive;

            while (final.PendingActionToken is not null && IsServerGateCard(final.PendingQuestion))
            {
                if (answered.Contains(final.PendingQuestion!, StringComparer.Ordinal))
                    return (RealModelOutcome.CodeFault, $"a gate RE-PARKED on the state the human already adjudicated ('{Truncate(final.PendingQuestion)}') — the released-state dead-end both gates' releases exist to close (decisions=[{final.KindTrail}])");

                if (answered.Count >= MaxAdjudications)
                    return (RealModelOutcome.CodeFault, $"the gates raised more than {MaxAdjudications} distinct cards ('{Truncate(final.PendingQuestion)}') — one immutable policy must not cost a human an unbounded number of rulings (decisions=[{final.KindTrail}])");

                answered.Add(final.PendingQuestion!);

                await AnswerAsync(final.PendingActionToken, userId, teamId, "understood — patch-only is accepted, finish without the pull request");
                await jobClient.WaitForPendingAsync();

                final = await SnapshotAsync(runId, teamId);

                if (final.RunStatus == WorkflowRunStatus.Failure)
                    return (RealModelOutcome.CodeFault, $"the run FAILED after an adjudication answer (error={final.RunError ?? "(none)"})");
            }

            if (final.RunStatus != WorkflowRunStatus.Success)
                return (RealModelOutcome.CapabilityMiss, $"the live model did not drive to a terminal after the answer (runStatus={final.RunStatus}, decisions=[{final.KindTrail}]) — reported, not gating");

            // ── The honest terminal: exactly one adjudicated re-attempt, zero pull requests, model-authored stop. ──
            // At least TWO publishes: DC-2b's own first attempt (which earns its card) and the ONE fresh re-attempt
            // its answer buys. A direct release would turn every "fix it and retry" answer into a silent waiver.
            if (final.PublishCount < 2)
                return (RealModelOutcome.CodeFault, $"the delivery answer did not buy the ONE fresh re-attempt (publishes={final.PublishCount}, cards adjudicated={answered.Count}; decisions=[{final.KindTrail}]) — a direct release would turn 'fix it and retry' answers into silent waivers");

            if (final.AnyPublishSatisfied)
                return (RealModelOutcome.CodeFault, "a publish reported an Opened/AlreadyOpened PR against a patch-only repo — the policy guard did not hold");

            if (final.IntegrationManifestWithPr)
                return (RealModelOutcome.CodeFault, "a PublishManifest row carries a pull-request reference — a PR was opened despite the patch-only policy");

            if (final.LastDecisionKind != SupervisorDecisionKinds.Stop || final.LastStopForcedReason is not null)
                return (RealModelOutcome.CapabilityMiss, $"the terminal was not a model-authored stop (last={final.LastDecisionKind}, forcedReason={final.LastStopForcedReason ?? "(none)"}) — reported, not gating");

            // ── The word the operator is owed: derived, persisted and projected (the wire no scripted stop reaches).
            if (await HonestOutcomeProbe.FaultAsync(_fixture, runId, teamId) is { } outcomeFault)
                return (RealModelOutcome.CodeFault, $"{outcomeFault} (decisions=[{final.KindTrail}])");

            // Arc-specific and NOT derivable from the tape shape: this terminal was asserted above to be a
            // model-authored, human-adjudicated stop, so the word must not be a degraded one. If a clean finish like
            // this started rendering as "Gave up" or "Cut short", every honest run in the index would read as failed.
            if (final.Outcome != nameof(SupervisorStopKind.Succeeded))
                return (RealModelOutcome.CodeFault, $"a model-authored, human-adjudicated stop rendered '{final.Outcome ?? "(null)"}' rather than a plain success — the index would report this honest finish as a degraded one");

            var verdict = $"{Provider} '{model}': the live brain drove real work; the server gates parked the patch-only conflict on {answered.Count} human card(s), "
                        + $"each answer bought exactly one re-attempt (publishes={final.PublishCount}, all policy-skipped), and the adjudicated stop terminalized honestly with ZERO pull requests as outcome '{final.Outcome}'.";
            Console.WriteLine($"[delivery-gate-e2e] {verdict}");
            return (RealModelOutcome.Drove, verdict);
        });
    }

    /// <summary>How many DISTINCT gate cards one immutable policy may cost a human. Two by construction — I3's branch and DC-2b's pull request — with one spare for a genuinely different blocker (a provider fault) the live run may also hit. Past that the run is asking more than it can honestly justify.</summary>
    private const int MaxAdjudications = 3;

    /// <summary>Whether the parked card is one of the SERVER's own stop gates, either rung. Recognized by the pinned question prefixes the gates themselves own, so a model-authored ask (which the clamp strips those tokens from) can never be mistaken for one.</summary>
    private static bool IsServerGateCard(string? question) =>
        question?.StartsWith(SupervisorDeliveryGate.QuestionPrefix, StringComparison.Ordinal) == true
        || question?.StartsWith(SupervisorPublishGate.QuestionPrefix, StringComparison.Ordinal) == true;

    // ─── The parked card's verdict ───────────────────────────────────────────────────

    /// <summary>
    /// Phase 1's verdict on the card the gate parked on, as a PURE decision over the only three facts it has: the
    /// card's own wording, the Agent-kind manifest count, and the tape's INDEPENDENT work signal. Null means the card
    /// NAMES the patch-only conflict — the honest arc this arm exists to prove — and Phase 1 proceeds.
    ///
    /// <para>The gate's ladder has an HONEST empty-result card ("no published branch to open one from") for a run
    /// whose agents captured nothing at all — under the 2026-07 gateway model drift, agents routinely "succeed" with
    /// an empty diff, so zero manifests ⇒ that card is the truth and the miss is the MODEL's (first seen run
    /// 29235518700). Only a mis-named card OVER captured work is a code fault. Zero manifests is the MODEL's miss
    /// only when the tape independently shows no work — the manifest ledger must never grade its own absence: agents
    /// whose results show changed files / a produced branch with NOTHING captured means the capture/publish pipeline
    /// swallowed the work.</para>
    ///
    /// <para>ONE method rather than nested statements on purpose. The three readings used to be an unbraced
    /// <c>if (!names-the-conflict) if (count > 0) return …;</c> followed by a second <c>return</c> that LOOKED
    /// nested but was not: it escaped the outer guard and fired for every run — including the honest patch-only
    /// card — reporting "zero publish manifests were captured" over a run that captured one, and making Phase 2
    /// unreachable (run 33946934743 on main, where the opener's mint evidence was real). An early return whose
    /// condition is the method's own first line cannot drift that way again.</para>
    /// </summary>
    internal static (RealModelOutcome Outcome, string Note)? ParkedCardFault(string question, int agentManifestCount, bool anyAgentShowsWork)
    {
        if (question.Contains("patch-only", StringComparison.OrdinalIgnoreCase)) return null;

        if (agentManifestCount > 0)
            return (RealModelOutcome.CodeFault, $"the gate parked but its card does not name the patch-only policy conflict despite {agentManifestCount} captured manifest(s): '{Truncate(question)}'");

        return anyAgentShowsWork
            ? (RealModelOutcome.CodeFault, $"agent results on the tape SHOW work but zero publish manifests were captured — the capture/publish pipeline swallowed it: '{Truncate(question)}'")
            : (RealModelOutcome.CapabilityMiss, $"the agents captured NO work at all (zero publish manifests, and no agent result shows work) — the gate's empty-publish card is honest; the live model never produced a diff to publish: '{Truncate(question)}' — reported, not gating");
    }

    // ─── Tape/state snapshot ─────────────────────────────────────────────────────────

    private sealed record Snapshot(WorkflowRunStatus RunStatus, string? RunError, string? PendingActionToken, string? PendingQuestion,
        int PublishCount, int GateCardCount, bool AnyPublishSatisfied, bool IntegrationManifestWithPr, int AgentManifestCount,
        bool AnyAgentShowsWork, string? LastDecisionKind, string? LastStopForcedReason, string KindTrail,
        string? Outcome);

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

        // Agent-kind ONLY, of ANY shape: patch-only evidence is BRANCHLESS by design (RepositoryPolicyPublishGuard
        // skips the push, the captured diff still records a row), and an Integration row is the SERVER's own PR
        // bookkeeping, never agent work. The exact rows SupervisorPullRequestOpener.CapturedWorkByRepositoryAsync
        // reads as its patch-only mint evidence — one definition of "the agents captured something", so the arm and
        // the code it grades can never disagree about whether a run produced work.
        var agentManifestCount = manifests.Count(m => m.Kind == PublishManifestKind.Agent);

        // The INDEPENDENT work signal (audit fix): agent results on the tape, not the manifest ledger — the
        // ledger must never grade its own absence.
        var anyAgentShowsWork = decisions
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .Any(SupervisorOutcome.ResultShowsWork);

        var last = decisions.LastOrDefault();
        var lastStopReason = last?.DecisionKind == SupervisorDecisionKinds.Stop ? SupervisorOutcome.ReadStopReason(last.PayloadJson) : null;


        return new Snapshot(run.Status, run.Error, pendingWait?.Token,
            pendingQuestion,
            publishes.Count,
            decisions.Count(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman && ReadQuestion(d.PayloadJson)?.StartsWith(SupervisorDeliveryGate.QuestionPrefix, StringComparison.Ordinal) == true),
            anySatisfied, prOnManifest, agentManifestCount,
            anyAgentShowsWork,
            last?.DecisionKind, lastStopReason,
            string.Join("→", decisions.Select(d => d.DecisionKind)),
            run.Outcome);
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
              "agentProfile": {{RealModelSupervisorWholeLoopE2ETests.AgentProfileJson(repoId, RealModelSupervisorWholeLoopE2ETests.FakeAgentTimeoutSeconds, "", "")}},
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
        private readonly CodeSpace.E2ETests.Infrastructure.GitTestRemoteServer _server;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
            _server = new CodeSpace.E2ETests.Infrastructure.GitTestRemoteServer(_root);
        }

        public string Url => _server.Url;

        public async Task SeedBaseAsync(Dictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            await Git(_root, "--git-dir", _bare, "config", "http.receivepack", "true");
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

        private static Task<string> Git(string workdir, params string[] args) =>
            CodeSpace.E2ETests.Infrastructure.GitTestRemoteServer.RunFixtureGitAsync(workdir, args);

        public void Dispose()
        {
            _server.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
