using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events.PullRequest;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// End-to-end contract for the webhook dispatch chain.
///
/// <para>The other integration tests cover each link in isolation:
/// <list type="bullet">
///   <item><c>ReceiveWebhookFlowTests</c> — HTTP POST + signature + normalize + Mediator publish</item>
///   <item><c>TriggerMatcherTests</c> (unit) — matcher Match / BuildPayload contract</item>
///   <item><c>RunStarterFlowTests</c> — RunStarter envelope validation + row writes</item>
///   <item><c>WorkflowEngineFlowTests</c> — engine execution of a pre-seeded run</item>
/// </list>
/// What none of them prove together: publishing a real <see cref="PullRequestOpenedEvent"/>
/// causes the <c>RunSourceDispatcher</c> to actually iterate active activations, fire the
/// matcher, build a payload, write a <c>workflow_run_request</c> + <c>workflow_run</c> pair,
/// and dispatch the run — for every config shape the matcher recognises.</para>
///
/// <para>This class fills that gap. Each test publishes via <see cref="IMediator.Publish"/>
/// (which is what <c>ReceiveWebhookCommandHandler</c> does after normalisation) and asserts
/// on the actual DB rows the dispatcher must have produced. Covers:</para>
/// <list type="number">
///   <item>New shape config <c>{repositories: [{repositoryId}]}</c> → run created + payload correct</item>
///   <item>Legacy shape <c>{repositoryId}</c> → run created (backward-compat shim works end-to-end)</item>
///   <item>Match-all (no <c>repositories</c> key) → run created regardless of event repo</item>
///   <item>Empty list <c>{repositories: []}</c> → no run, no-match audit row written</item>
///   <item>Activation disabled → no run, no fan-out side-effects</item>
///   <item>Labels filter — AND semantics applied to the event's actual labels</item>
///   <item>Idempotency — same provider delivery id replayed → only one run per activation</item>
/// </list>
///
/// <para>Why through <see cref="IMediator.Publish"/> instead of the HTTP webhook command?
/// The signature / normalize layers are already exhaustively tested upstream; publishing
/// the normalized event keeps THIS test focused on the dispatcher's contract without
/// re-asserting on layers covered elsewhere (Karpathy Rule 2 — smallest test that
/// proves the gap).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WebhookEventToRunDispatchFlowTests
{
    private readonly PostgresFixture _fixture;

    public WebhookEventToRunDispatchFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─── Match path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task NewShape_config_matches_event_then_creates_run_with_full_matcher_payload()
    {
        var ctx = await SeedAsync();
        var repoId = ctx.RepositoryId;
        var configJson = $$"""{ "repositories": [{ "repositoryId": "{{repoId}}" }] }""";
        await SeedActivationAsync(ctx.WorkflowId, configJson);

        var ev = BuildOpenedEvent(repoId, labels: new[] { "bug", "needs-review" });
        await PublishAndCommitAsync(ev);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking()
            .Include(r => r.RunRequest)
            .SingleOrDefaultAsync(r => r.WorkflowId == ctx.WorkflowId)
            .ConfigureAwait(false);

        run.ShouldNotBeNull(customMessage:
            "Dispatcher MUST create exactly one workflow_run for an activation that matches the event. " +
            "If null: the matcher returned false (check PrTriggerMatcherFilter.MatchesNewShape) " +
            "OR RunStarter rejected the envelope (check ArgumentException stack).");

        // The activation snapshot the run carries proves the dispatcher copied the right
        // activation row (replay tooling depends on this).
        run.RunRequest.SourceType.ShouldBe("trigger.pr.opened");
        run.RunRequest.ActorType.ShouldBe(WorkflowRunActorTypes.Webhook);
        run.RunRequest.ActorId.ShouldBeNull();
        run.RunRequest.ActivationId.ShouldNotBeNull();
        run.RunRequest.ActivationSnapshotJson.ShouldNotBeNullOrEmpty();
        run.RunRequest.ExternalEventId.ShouldBe(ev.ProviderEventId,
            customMessage: "Provider delivery id MUST land on request.ExternalEventId so an operator can grep audit by delivery.");

        // The normalised payload IS what BuildPayload emitted — every {{trigger.*}} the
        // engine later resolves reads from this column. Pin every documented key.
        var payload = JsonDocument.Parse(run.RunRequest.NormalizedPayloadJson).RootElement;
        payload.GetProperty("repositoryId").GetString().ShouldBe(repoId.ToString());
        payload.GetProperty("number").GetInt32().ShouldBe(42);
        payload.GetProperty("title").GetString().ShouldBe("Add feature");
        payload.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ShouldBe(new[] { "bug", "needs-review" });
    }

    [Fact]
    public async Task LegacyShape_config_matches_event_then_creates_run()
    {
        // Backward-compat shim: configs saved before PR #23 use { repositoryId } at the
        // top level. Matcher precedence rule #3 accepts this shape; this test proves the
        // shim is not just a unit-test artifact but actually drives the full dispatch chain
        // (matcher.Match → BuildPayload → RunStarter → run row).
        var ctx = await SeedAsync();
        var configJson = $$"""{ "repositoryId": "{{ctx.RepositoryId}}" }""";
        await SeedActivationAsync(ctx.WorkflowId, configJson);

        await PublishAndCommitAsync(BuildOpenedEvent(ctx.RepositoryId));

        await AssertExactlyOneRunAsync(ctx.WorkflowId, expectedRunCount: 1);
    }

    [Fact]
    public async Task MatchAll_config_creates_run_regardless_of_event_repository()
    {
        // Match-all in production is signalled by an activation config that has NO
        // `repositories` key at all (the picker's "Match every repository" checkbox emits
        // this shape via undefined → JSON.stringify drop). The matcher's empty-config
        // precedence rule #4 returns true unconditionally for this trigger type.
        var ctx = await SeedAsync();
        await SeedActivationAsync(ctx.WorkflowId, configJson: "{}");

        // Event from a DIFFERENT repository than the seeded one — still must fire because
        // the activation didn't scope to anything.
        var unrelatedRepoId = Guid.NewGuid();
        await PublishAndCommitAsync(BuildOpenedEvent(unrelatedRepoId));

        await AssertExactlyOneRunAsync(ctx.WorkflowId, expectedRunCount: 1);
    }

    [Fact]
    public async Task EmptyList_config_creates_no_run_and_writes_no_match_audit()
    {
        // The safe default the picker now emits (PR #29). Operator dropped a trigger node
        // but hasn't added any repos and hasn't checked match-all. Wire-format intent:
        // "match nothing". The dispatcher MUST NOT fire any workflow but SHOULD audit the
        // event so the operator can see "your PR was detected but no workflow listened".
        var ctx = await SeedAsync();
        await SeedActivationAsync(ctx.WorkflowId, configJson: """{ "repositories": [] }""");

        await PublishAndCommitAsync(BuildOpenedEvent(ctx.RepositoryId));

        await AssertExactlyOneRunAsync(ctx.WorkflowId, expectedRunCount: 0);
        await AssertNoMatchAuditWrittenAsync(ctx.TeamId);
    }

    [Fact]
    public async Task DisabledActivation_creates_no_run()
    {
        // Even with a matching config, an activation with Enabled=false MUST be excluded
        // by LoadActiveActivationsAsync. Otherwise a paused workflow would keep firing
        // and operators would have no way to silence it short of deleting the activation.
        var ctx = await SeedAsync();
        var configJson = $$"""{ "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}" }] }""";
        await SeedActivationAsync(ctx.WorkflowId, configJson, enabled: false);

        await PublishAndCommitAsync(BuildOpenedEvent(ctx.RepositoryId));

        await AssertExactlyOneRunAsync(ctx.WorkflowId, expectedRunCount: 0);
    }

    [Fact]
    public async Task LabelsFilter_AND_semantics_excludes_event_missing_a_required_label()
    {
        // The schema description + UI both promise AND-match. If the matcher silently
        // shifted to OR, every workflow that uses label-scoping would over-fire. Pin the
        // semantic end-to-end (the unit test pins it on the matcher in isolation; this
        // test pins it through the actual dispatcher path so a future refactor can't
        // wire in an alternative matcher with looser semantics).
        var ctx = await SeedAsync();
        var configJson = $$"""
            { "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}", "labels": ["bug", "wip"] }] }
            """;
        await SeedActivationAsync(ctx.WorkflowId, configJson);

        // Event has "bug" but NOT "wip" — AND requires both.
        await PublishAndCommitAsync(BuildOpenedEvent(ctx.RepositoryId, labels: new[] { "bug" }));

        await AssertExactlyOneRunAsync(ctx.WorkflowId, expectedRunCount: 0);
    }

    [Fact]
    public async Task LabelsFilter_AND_semantics_fires_when_every_required_label_is_present()
    {
        var ctx = await SeedAsync();
        var configJson = $$"""
            { "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}", "labels": ["bug", "wip"] }] }
            """;
        await SeedActivationAsync(ctx.WorkflowId, configJson);

        await PublishAndCommitAsync(BuildOpenedEvent(ctx.RepositoryId, labels: new[] { "wip", "bug", "extra" }));

        await AssertExactlyOneRunAsync(ctx.WorkflowId, expectedRunCount: 1);
    }

    [Fact]
    public async Task DuplicateDelivery_dedupes_to_one_run_per_activation()
    {
        // RunStarter synthesises an idempotency key of {sourceType}:{deliveryId}:{activationId}.
        // The same provider delivery replayed (GitHub / GitLab retry the same event on 5xx
        // until they get a 200) MUST NOT produce two workflow_run rows for the same
        // activation — operators would see two duplicated workflow firings per real PR.
        var ctx = await SeedAsync();
        var configJson = $$"""{ "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}" }] }""";
        await SeedActivationAsync(ctx.WorkflowId, configJson);

        var deliveryId = $"replay-{Guid.NewGuid():N}";
        var first = BuildOpenedEvent(ctx.RepositoryId, providerEventId: deliveryId);
        var second = BuildOpenedEvent(ctx.RepositoryId, providerEventId: deliveryId);

        await PublishAndCommitAsync(first);
        await PublishAndCommitAsync(second);

        await AssertExactlyOneRunAsync(ctx.WorkflowId, expectedRunCount: 1);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private sealed record SeedContext(Guid TeamId, Guid UserId, Guid WorkflowId, Guid RepositoryId);

    /// <summary>
    /// Seed: team + user + workflow + repository row so the dispatcher's no-match audit
    /// path can look up team_id from the event's repository_id. The Repository row is the
    /// only thing the dispatcher's helper queries; PR-trigger matching itself reads only
    /// the activation config + the event's RepositoryId / Labels.
    /// </summary>
    private async Task<SeedContext> SeedAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        Guid repoId;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            // ProviderInstance is required by Repository's FK; minimal seed.
            var provider = new ProviderInstance
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                Provider = ProviderKind.GitHub,
                DisplayName = "GH",
                BaseUrl = $"https://gh-{Guid.NewGuid():N}.local",
            };
            db.ProviderInstance.Add(provider);

            repoId = Guid.NewGuid();
            db.Repository.Add(new Repository
            {
                Id = repoId,
                TeamId = teamId,
                ProviderInstanceId = provider.Id,
                ExternalId = $"ext-{Guid.NewGuid():N}",
                NamespacePath = "acme",
                Name = "api",
                FullPath = "acme/api",
                WebUrl = "https://gh.local/acme/api",
            });

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new SeedContext(teamId, userId, workflowId, repoId);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "dispatch-chain-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        }).ConfigureAwait(false);
    }

    private async Task SeedActivationAsync(Guid workflowId, string configJson, bool enabled = true)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowActivation.Add(new WorkflowActivation
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            TypeKey = "trigger.pr.opened",
            ConfigJson = configJson,
            Enabled = enabled,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static PullRequestOpenedEvent BuildOpenedEvent(Guid repositoryId, string[]? labels = null, string? providerEventId = null) => new()
    {
        RepositoryId = repositoryId,
        ProviderEventId = providerEventId ?? $"delivery-{Guid.NewGuid():N}",
        OccurredAt = DateTimeOffset.UtcNow,
        ExternalPullRequestId = "100",
        Number = 42,
        Title = "Add feature",
        Body = "body",
        SourceBranch = "feat/x",
        TargetBranch = "main",
        AuthorExternalId = "user-1",
        AuthorName = "alice",
        WebUrl = "https://gh.local/acme/api/pull/42",
        Labels = labels ?? Array.Empty<string>(),
    };

    /// <summary>
    /// Publish via IMediator and wait for ALL handlers (including <c>RunSourceDispatcher</c>)
    /// to settle. The dispatcher persists inside its own SaveChanges + dispatches to the
    /// background-job client in-process; by the time Publish returns, the DB writes have
    /// committed and follow-up assertions can read them in a fresh scope.
    /// </summary>
    private async Task PublishAndCommitAsync(PullRequestOpenedEvent ev)
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();
        await mediator.Publish(ev).ConfigureAwait(false);
    }

    private async Task AssertExactlyOneRunAsync(Guid workflowId, int expectedRunCount)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var runs = await db.WorkflowRun.AsNoTracking()
            .Where(r => r.WorkflowId == workflowId)
            .ToListAsync().ConfigureAwait(false);
        runs.Count.ShouldBe(expectedRunCount,
            customMessage: $"Expected {expectedRunCount} run(s) for workflow {workflowId}; got {runs.Count}. " +
                            "Check the dispatcher's loop in RunSourceDispatcher.DispatchAsync.");
    }

    private async Task AssertNoMatchAuditWrittenAsync(Guid teamId)
    {
        // The dispatcher's IIngestionAuditor.WriteNoMatchRejectedAsync persists the audit
        // as a workflow_run_request row with SourceType="provider.unmatched" and
        // Status=Rejected. That's the production shape — operators query the same column
        // family for both fired and rejected ingestions, with the status discriminator
        // telling them which is which.
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var audited = await db.WorkflowRunRequest.AsNoTracking()
            .AnyAsync(r => r.TeamId == teamId
                        && r.Status == WorkflowRunRequestStatus.Rejected
                        && r.SourceType.StartsWith(WorkflowRunSourceTypes.ProviderPrefix))
            .ConfigureAwait(false);
        audited.ShouldBeTrue(
            customMessage: "Dispatcher MUST write a Rejected workflow_run_request when an activation existed but its config excluded the event. " +
                            "Without this row the operator can't tell whether the webhook arrived at all.");
    }
}
