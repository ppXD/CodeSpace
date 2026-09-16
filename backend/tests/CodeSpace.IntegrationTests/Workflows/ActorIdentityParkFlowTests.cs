using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Binding;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + real engine + the real <c>git.pr_review</c> node + the real act-as-user
/// enforcement seam): "park, don't die" for a missing actor identity.
///
/// <para>The behaviour under test is the one the field hit: a teammate clicks "Request changes" on a chat card,
/// the click returns 204, and a minute later the run is DEAD with "submit_review failed" — because the person the
/// step acts as had never connected their GitLab account. That is a fact a person can fix in seconds, so the run
/// must WAIT for them, not die. These pin: the run parks (never fails) on an <c>ActorIdentityLink</c> wait naming
/// who must connect what; the deadline wake RE-RUNS the node and completes it once the identity exists; the
/// provider is called EXACTLY ONCE across the whole park/resume cycle (the park can never double-fire a review);
/// and a whole exhausted window ends the node honestly rather than parking forever.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ActorIdentityParkFlowTests
{
    private readonly PostgresFixture _fixture;

    public ActorIdentityParkFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_unlinked_actor_parks_the_run_and_linking_lets_the_deadline_wake_finish_it()
    {
        var seed = await SeedAsync();
        var runId = await StartRunAsync(seed);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);

            Guid waitId;
            string markerJson;
            using (var mid = _fixture.BeginScope())
            {
                var db = mid.Resolve<CodeSpaceDbContext>();

                var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
                var nodes = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId).ToListAsync();
                var diag = $"runError={run.Error} nodes=[" + string.Join(" | ", nodes.Select(n => $"{n.NodeId}:{n.Status}:{n.Error}")) + "]";

                run.Status.ShouldBe(WorkflowRunStatus.Suspended, $"an unlinked actor identity must park the run, never terminalize it ({diag})");

                var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
                wait.WaitKind.ShouldBe(WorkflowWaitKinds.ActorIdentityLink);
                wait.NodeId.ShouldBe("review");
                wait.WakeAt.ShouldNotBeNull("the deadline IS the wake — nothing else resolves this wait");
                (wait.WakeAt!.Value - DateTimeOffset.UtcNow).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(40), "park 1 rides the 30s rung (+20% jitter)");

                var marker = JsonDocument.Parse(wait.PayloadJson!).RootElement;
                marker.GetProperty("actorUserId").GetGuid().ShouldBe(seed.ActorUserId, "the run detail has to be able to name WHO must connect — the actor is not the person watching");
                marker.GetProperty("provider").GetString().ShouldBe(nameof(ProviderKind.Git));
                marker.GetProperty("providerInstanceId").GetGuid().ShouldBe(seed.ProviderInstanceId);

                waitId = wait.Id;
                markerJson = wait.PayloadJson!;
            }

            Reviews().For(seed.RepositoryFullPath).ShouldBeEmpty("nothing may have reached the provider while the actor had no identity");

            await AssertTheRunDetailCanSayWhyAsync(runId, seed);
            await AssertTheLedgerDoesNotClaimTheNodeFailedAsync(runId);

            // The person connects their account (the PAT path's end state), then the scheduled deadline fires.
            await LinkActorIdentityAsync(seed);
            await FireDeadlineAsync(waitId, markerJson);
            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            var verifyDb = verify.Resolve<CodeSpaceDbContext>();

            var finished = await verifyDb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
            var reviewNode = await verifyDb.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "review");

            reviewNode.Status.ShouldBe(NodeStatus.Success, $"the wake re-runs the node and it succeeds now that the identity exists ({reviewNode.Error})");
            finished.Status.ShouldBe(WorkflowRunStatus.Success);

            var calls = Reviews().For(seed.RepositoryFullPath);
            calls.Count.ShouldBe(1, "the park/resume cycle must fire the review EXACTLY once — the identity is resolved BEFORE the write, so the parked pass reached no provider");
            calls[0].Verdict.ShouldBe(PullRequestReviewVerdict.RequestChanges);
            calls[0].Number.ShouldBe(7);

            (await verifyDb.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending))
                .ShouldBe(0, "nothing is left parked behind the finished run");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task An_exhausted_park_window_ends_the_node_honestly_instead_of_parking_forever()
    {
        var seed = await SeedAsync();
        var runId = await StartRunAsync(seed);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);

            Guid waitId;
            using (var mid = _fixture.BeginScope())
                waitId = (await mid.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                    .SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending)).Id;

            // The wake arrives with an anchor that is already older than the whole window, and the identity STILL
            // isn't linked — the node must stop asking rather than park on into next week.
            var doctored = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [ActorIdentityPark.MarkerField] = true,
                ["parks"] = 6,
                ["firstParkedAtUtc"] = (DateTimeOffset.UtcNow - ActorIdentityPark.MaxParkWindow - TimeSpan.FromHours(1)).ToString("o"),
            });

            await FireDeadlineAsync(waitId, doctored);
            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var reviewNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "review");
            reviewNode.Status.ShouldBe(NodeStatus.Failure, "a run can never park forever — the window ends it");
            reviewNode.Error.ShouldNotBeNull();
            reviewNode.Error!.ShouldContain("Git", Case.Sensitive, "the ending must still name the provider that was never connected");

            (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending))
                .ShouldBe(0, "the honest ending leaves nothing parked behind it");

            Reviews().For(seed.RepositoryFullPath).ShouldBeEmpty("no identity ever appeared, so nothing may have reached the provider");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    /// <summary>
    /// The parked reason must survive the trip the run detail actually takes. That surface reads the wait through
    /// <see cref="IWorkflowRunPendingWaitObservationReader"/>, which extracts exactly ONE payload key in SQL —
    /// <c>prompt</c> — and hands back a bounded descriptor; everything else stays behind the database seam. So a
    /// reason written under any other key, or as a non-string, is INVISIBLE to the person who has to act on it.
    /// Asserted through the real reader rather than the payload the park wrote, because "the park names the actor"
    /// and "the operator can read it" are different claims and only the second one matters here.
    /// </summary>
    private async Task AssertTheRunDetailCanSayWhyAsync(Guid runId, Seed seed)
    {
        using var scope = _fixture.BeginScope();
        var observation = await scope.Resolve<IWorkflowRunPendingWaitObservationReader>().ReadAsync(runId, seed.TeamId, CancellationToken.None);

        observation.ShouldNotBeNull();
        observation!.Wait.ShouldNotBeNull("the run detail must see a pending wait to render anything at all");
        observation.Wait!.Kind.ShouldBe(WorkflowWaitKinds.ActorIdentityLink);
        observation.Wait.PromptState.ShouldBe(WorkflowRunPendingWaitPromptState.Exact, "a Missing/Invalid/Truncated prompt renders as a bare 'Suspended' with no reason — the quiet death this whole change exists to end");
        observation.Wait.PromptPrefix.ShouldNotBeNull();
        observation.Wait.PromptPrefix!.ShouldContain(nameof(ProviderKind.Git), Case.Sensitive, "the reason a person reads has to name the account they must connect");
    }

    /// <summary>
    /// The durable ledger must not say the node failed. A parked attempt DOES leave an <c>external_call.failed</c>
    /// record — the attempt really did start and not complete, and that line carries the reason — but the NODE-grain
    /// verdict has to read <c>node.suspended</c>, with no <c>node.failed</c> and no <c>attempt.failed</c> burning the
    /// retry budget. That distinction is the whole difference between "parked, waiting for a person" and the dead run
    /// this change replaces.
    /// </summary>
    private async Task AssertTheLedgerDoesNotClaimTheNodeFailedAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var records = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.NodeId == "review")
            .Select(r => r.RecordType)
            .ToListAsync();

        records.ShouldContain(WorkflowRunRecordTypes.NodeSuspended, "the node-grain verdict is 'parked', and the run detail reads it");
        records.ShouldNotContain(WorkflowRunRecordTypes.NodeFailed, "a parked node has NOT failed — claiming it did is the signal that sent someone hunting a bug that was a missing account");
        records.ShouldNotContain(WorkflowRunRecordTypes.AttemptFailed, "a park is not a failed attempt — counting it would spend the node's retry budget on something no retry can fix");
    }

    [Fact]
    public async Task A_sibling_wait_resolving_does_not_restart_the_parked_nodes_ladder_or_its_window()
    {
        // The park's whole bound — "measured from the FIRST park, so a run can never park forever" — depends on the
        // ladder position surviving EVERY re-entry, not just the deadline wake. `LoadResolvedWaitsAsync` injects
        // resume payloads from RESOLVED waits only, so a run with a second, human-resolvable wait beside the parked
        // node is the case that decides whether the window is real: if the ladder re-anchors here, someone answering
        // an approval every few hours would keep a parked node alive indefinitely.
        var seed = await SeedAsync();
        var runId = await StartRunAsync(seed, withSiblingApproval: true);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);

            // Ride one rung first, so a reset shows up as a number and not only as a timestamp.
            var (parkWaitId, markerJson) = await ReadParkAsync(runId);
            await FireDeadlineAsync(parkWaitId, markerJson);
            await RunEngineAsync(runId);

            var (_, afterSecondPark) = await ReadParkAsync(runId);
            var second = JsonDocument.Parse(afterSecondPark).RootElement;
            second.GetProperty("parks").GetInt32().ShouldBe(2, "precondition: the deadline wake advanced the ladder");
            var anchor = second.GetProperty("firstParkedAtUtc").GetDateTimeOffset();

            // Now the SIBLING resolves — someone answers the approval sitting beside the parked node.
            await ResolveSiblingApprovalAsync(runId);
            await RunEngineAsync(runId);

            var (_, afterSibling) = await ReadParkAsync(runId);
            var marker = JsonDocument.Parse(afterSibling).RootElement;

            marker.GetProperty("parks").GetInt32().ShouldBeGreaterThanOrEqualTo(2,
                "a sibling's resolution must not restart the parked node's ladder — a ladder that resets never reaches its last rung");
            marker.GetProperty("firstParkedAtUtc").GetDateTimeOffset().ShouldBe(anchor,
                "and it must not re-anchor the window, or unrelated activity on the same run pushes the 24h bound out forever");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    /// <summary>The run's single pending park wait + its stored marker — the durable state a re-entry must continue from.</summary>
    private async Task<(Guid WaitId, string MarkerJson)> ReadParkAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var wait = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
            .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.ActorIdentityLink && w.Status == WorkflowWaitStatuses.Pending);

        return (wait.Id, wait.PayloadJson!);
    }

    private async Task ResolveSiblingApprovalAsync(Guid runId)
    {
        Guid waitId;
        using (var read = _fixture.BeginScope())
            waitId = (await read.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Approval && w.Status == WorkflowWaitStatuses.Pending)).Id;

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, "{\"approved\":true}", CancellationToken.None);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private TestPullRequestReviewCapture Reviews()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<TestPullRequestReviewCapture>();
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

    private async Task FireDeadlineAsync(Guid waitId, string timeoutPayloadJson)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowResumeService>().ResumeByDeadlineAsync(waitId, timeoutPayloadJson, CancellationToken.None))
            .ShouldBeTrue($"the deadline must resolve pending wait {waitId} — inspect workflow_run_wait manually if this fails");
    }

    private async Task<Guid> StartRunAsync(Seed seed, bool withSiblingApproval = false)
    {
        using var scope = _fixture.BeginScopeAs(seed.ActorUserId, seed.TeamId, Roles.Admin);
        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "actor-identity-park-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = ReviewDefinition(seed, withSiblingApproval),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, seed.TeamId);
    }

    // start → review (acts AS the seeded user) → end. The repository and the actor are bound as literals so the
    // graph itself is not what is under test — the identity is.
    private static WorkflowDefinition ReviewDefinition(Seed seed, bool withSiblingApproval = false)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new()
            {
                Id = "review",
                TypeKey = "git.pr_review",
                Config = WorkflowsTestSeed.EmptyJson(),
                Inputs = WorkflowsTestSeed.Json($$"""
                    {"repositoryId":"{{{seed.RepositoryId}}}","number":7,"verdict":"request_changes","body":"needs another pass","actAsUserId":"{{{seed.ActorUserId}}}"}
                    """),
            },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        };

        var edges = new List<EdgeDefinition> { new() { From = "start", To = "review" }, new() { From = "review", To = "end" } };

        // A second, HUMAN-resolvable wait in its own branch — the re-dispatch source a real run has and a
        // single-branch fixture does not.
        if (withSiblingApproval)
        {
            nodes.Insert(2, new NodeDefinition { Id = "hold", TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.Json(HoldConfigJson), Inputs = WorkflowsTestSeed.EmptyJson() });
            edges.Add(new EdgeDefinition { From = "start", To = "hold" });
            edges.Add(new EdgeDefinition { From = "hold", To = "end" });
        }

        return new WorkflowDefinition { SchemaVersion = 1, CompletionMode = WorkflowDefinition.CompletionModeShadow, Nodes = nodes, Edges = edges };
    }

    private const string HoldConfigJson = "{\"prompt\":\"hold here\"}";

    /// <summary>A team that owns both the workflow and the repository, with a bound connection credential and NO actor identity yet.</summary>
    private async Task<Seed> SeedAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "instance",
            BaseUrl = $"https://git-{suffix}.local", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret"),
        };
        var connection = new Credential
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService,
            AuthType = AuthType.Pat, DisplayName = "connection",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "conn" })), Status = CredentialStatus.Active,
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, CredentialId = connection.Id,
            ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = $"api-{suffix}", FullPath = $"acme/api-{suffix}",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://git.local/acme/api", Status = RepositoryStatus.Active,
        };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(connection);
        db.Repository.Add(repo);
        await db.SaveChangesAsync();

        return new Seed(teamId, userId, instance.Id, repo.Id, repo.FullPath);
    }

    /// <summary>What the PAT / OAuth link paths leave behind: a live Personal credential plus the identity row pointing at it.</summary>
    private async Task LinkActorIdentityAsync(Seed seed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var actorCredential = new Credential
        {
            Id = Guid.NewGuid(), TeamId = seed.TeamId, ProviderInstanceId = seed.ProviderInstanceId, OwnerUserId = seed.ActorUserId,
            Ownership = CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = "actor",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "actor" })), Status = CredentialStatus.Active,
        };

        db.Credential.Add(actorCredential);
        db.UserProviderIdentity.Add(new UserProviderIdentity
        {
            Id = Guid.NewGuid(), UserId = seed.ActorUserId, ProviderInstanceId = seed.ProviderInstanceId,
            CredentialId = actorCredential.Id, ProviderUserId = "42", ProviderUsername = "tester",
        });

        await db.SaveChangesAsync();
    }

    private sealed record Seed(Guid TeamId, Guid ActorUserId, Guid ProviderInstanceId, Guid RepositoryId, string RepositoryFullPath);
}
