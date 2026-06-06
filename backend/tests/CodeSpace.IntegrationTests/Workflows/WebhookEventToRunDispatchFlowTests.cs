using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
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
/// What none of them prove together: publishing a real <c>NormalizedEvent</c> causes the
/// <c>RunSourceDispatcher</c> to actually iterate active activations, fire the matcher,
/// build a payload, write a <c>workflow_run_request</c> + <c>workflow_run</c> pair, and
/// dispatch the run — for every config shape the matcher recognises AND for every PR
/// trigger TypeKey that exists today.</para>
///
/// <para>Parametrized over both PR triggers via <see cref="TriggerKind"/> so PrOpened and
/// PrUpdated share the same contract suite — a regression that only affects one would
/// fail half the cases. Per-trigger payload-content assertions stay as dedicated Facts
/// (the matcher's <c>BuildPayload</c> emits a different key set per trigger, pinned
/// independently in <c>TriggerMatcherTests</c>'s drift detectors).</para>
///
/// <para>Why through <see cref="IMediator.Publish"/> instead of the HTTP webhook command?
/// The signature / normalize layers are exhaustively tested upstream; publishing the
/// normalized event keeps THIS test focused on the dispatcher's contract without
/// re-asserting on layers covered elsewhere (Karpathy Rule 2 — smallest test that
/// proves the gap).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WebhookEventToRunDispatchFlowTests
{
    private readonly PostgresFixture _fixture;

    public WebhookEventToRunDispatchFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>
    /// Discriminator threaded through every <see cref="Theory"/> below. Adding a new PR
    /// trigger TypeKey (e.g. PrMerged, PrClosed) becomes a one-line enum addition + a
    /// branch in <see cref="BuildForTrigger"/>; every contract test then automatically
    /// covers it. Failure messages embed the value via xUnit's default naming so the
    /// blame is unambiguous.
    /// </summary>
    public enum TriggerKind
    {
        Opened,
        Updated,
    }

    // ─── Payload contents (per-trigger Facts — keys differ between matchers) ────

    [Fact]
    public async Task PrOpened_run_carries_full_BuildPayload_keys()
    {
        var ctx = await SeedAsync();
        var configJson = $$"""{ "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}" }] }""";
        await SeedActivationAsync(ctx.WorkflowId, "trigger.pr.opened", configJson);

        var ev = BuildOpenedEvent(ctx.RepositoryId, labels: new[] { "bug", "needs-review" });
        await PublishAndCommitAsync(ev);

        var (run, payload) = await LoadRunAndPayloadAsync(ctx.WorkflowId);

        run.RunRequest.SourceType.ShouldBe("trigger.pr.opened");
        run.RunRequest.ExternalEventId.ShouldBe(ev.ProviderEventId);

        payload.GetProperty("repositoryId").GetString().ShouldBe(ctx.RepositoryId.ToString());
        payload.GetProperty("number").GetInt32().ShouldBe(42);
        payload.GetProperty("title").GetString().ShouldBe("Add feature");
        payload.GetProperty("body").GetString().ShouldBe("body");
        payload.GetProperty("sourceBranch").GetString().ShouldBe("feat/x");
        payload.GetProperty("targetBranch").GetString().ShouldBe("main");
        payload.GetProperty("authorName").GetString().ShouldBe("alice");
        payload.GetProperty("webUrl").GetString().ShouldBe("https://gh.local/acme/api/pull/42");
        payload.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ShouldBe(new[] { "bug", "needs-review" });
        payload.GetProperty("isDraft").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task PrUpdated_run_carries_full_BuildPayload_keys()
    {
        // PrUpdated's BuildPayload emits a different key set than PrOpened (no title /
        // branches / author / webUrl; gains previousHeadSha / newHeadSha). Pin those here
        // — the corresponding drift detector (TriggerMatcherTests) catches OutputSchema
        // misalignment at the unit tier; this test catches dispatch-time payload drift.
        var ctx = await SeedAsync();
        var configJson = $$"""{ "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}" }] }""";
        await SeedActivationAsync(ctx.WorkflowId, "trigger.pr.updated", configJson);

        var ev = BuildSynchronizedEvent(ctx.RepositoryId, labels: new[] { "wip" });
        await PublishAndCommitAsync(ev);

        var (run, payload) = await LoadRunAndPayloadAsync(ctx.WorkflowId);

        run.RunRequest.SourceType.ShouldBe("trigger.pr.updated");
        run.RunRequest.ExternalEventId.ShouldBe(ev.ProviderEventId);

        payload.GetProperty("repositoryId").GetString().ShouldBe(ctx.RepositoryId.ToString());
        payload.GetProperty("number").GetInt32().ShouldBe(42);
        payload.GetProperty("previousHeadSha").GetString().ShouldBe("oldsha");
        payload.GetProperty("newHeadSha").GetString().ShouldBe("newsha");
        payload.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ShouldBe(new[] { "wip" });
        payload.GetProperty("isDraft").GetBoolean().ShouldBeFalse();

        // The trigger-specific keys MUST NOT appear (would mean PrOpened payload got
        // mistakenly built for a sync event — matcher.GetType() switching is the bug).
        payload.TryGetProperty("title", out _).ShouldBeFalse();
        payload.TryGetProperty("sourceBranch", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task PrOpened_run_payload_carries_isDraft_true_for_a_draft_pr()
    {
        // Load-bearing case for #231: a DRAFT pr.opened must persist isDraft:true into the run
        // payload so a workflow can gate on {{trigger.isDraft}}. The full-keys Facts pin the false
        // case; this proves the true value survives publish → dispatch → NormalizedPayloadJson write.
        var ctx = await SeedAsync();
        var configJson = $$"""{ "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}" }] }""";
        await SeedActivationAsync(ctx.WorkflowId, "trigger.pr.opened", configJson);

        await PublishAndCommitAsync(BuildOpenedEvent(ctx.RepositoryId, isDraft: true));

        var (_, payload) = await LoadRunAndPayloadAsync(ctx.WorkflowId);
        payload.GetProperty("isDraft").GetBoolean().ShouldBeTrue();
    }

    // ─── Match-path contract (parametrized — both triggers obey identically) ────

    [Theory]
    [InlineData(TriggerKind.Opened)]
    [InlineData(TriggerKind.Updated)]
    public async Task NewShape_config_matches_event_then_creates_run(TriggerKind trigger)
    {
        var ctx = await SeedAsync();
        var (typeKey, ev) = BuildForTrigger(trigger, ctx.RepositoryId);
        var configJson = $$"""{ "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}" }] }""";
        await SeedActivationAsync(ctx.WorkflowId, typeKey, configJson);

        await PublishAndCommitAsync(ev);

        var (run, _) = await LoadRunAndPayloadAsync(ctx.WorkflowId);
        run.RunRequest.SourceType.ShouldBe(typeKey);
        run.RunRequest.ActorType.ShouldBe(WorkflowRunActorTypes.Webhook);
        run.RunRequest.ActorId.ShouldBeNull();
        run.RunRequest.ActivationId.ShouldNotBeNull();
        run.RunRequest.ActivationSnapshotJson.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(TriggerKind.Opened)]
    [InlineData(TriggerKind.Updated)]
    public async Task LegacyShape_config_matches_event_then_creates_run(TriggerKind trigger)
    {
        // Backward-compat shim: configs saved before PR #23 use { repositoryId } at the
        // top level. Matcher precedence rule #3 accepts this shape; this test proves the
        // shim drives the full dispatch chain (not just unit matcher logic) for BOTH PR
        // triggers — PrUpdated's matcher also delegates to PrTriggerMatcherFilter so the
        // shim works there for free, pinned here so a future refactor can't accidentally
        // break only one.
        var ctx = await SeedAsync();
        var (typeKey, ev) = BuildForTrigger(trigger, ctx.RepositoryId);
        var configJson = $$"""{ "repositoryId": "{{ctx.RepositoryId}}" }""";
        await SeedActivationAsync(ctx.WorkflowId, typeKey, configJson);

        await PublishAndCommitAsync(ev);

        await AssertRunCountAsync(ctx.WorkflowId, expected: 1);
    }

    [Theory]
    [InlineData(TriggerKind.Opened)]
    [InlineData(TriggerKind.Updated)]
    public async Task MatchAll_config_creates_run_regardless_of_event_repository(TriggerKind trigger)
    {
        // Match-all in production is signalled by an activation config with NO
        // `repositories` key at all (the picker's "Match every repository" checkbox emits
        // this via undefined → JSON.stringify drop). Matcher rule #4 returns true
        // unconditionally for this trigger type.
        var ctx = await SeedAsync();
        // Event from a DIFFERENT repository than the seeded one — still must fire because
        // the activation didn't scope to anything.
        var (typeKey, ev) = BuildForTrigger(trigger, repositoryId: Guid.NewGuid());
        await SeedActivationAsync(ctx.WorkflowId, typeKey, configJson: "{}");

        await PublishAndCommitAsync(ev);

        await AssertRunCountAsync(ctx.WorkflowId, expected: 1);
    }

    // ─── No-match contract (parametrized) ──────────────────────────────────────

    [Theory]
    [InlineData(TriggerKind.Opened)]
    [InlineData(TriggerKind.Updated)]
    public async Task EmptyList_config_creates_no_run_and_writes_no_match_audit(TriggerKind trigger)
    {
        // The safe default the picker now emits (PR #29). Operator dropped a trigger node
        // but hasn't added any repos and hasn't checked match-all. Wire-format intent:
        // "match nothing". Dispatcher MUST NOT fire any workflow but SHOULD audit so the
        // operator can see "your PR was detected but no workflow listened".
        var ctx = await SeedAsync();
        var (typeKey, ev) = BuildForTrigger(trigger, ctx.RepositoryId);
        await SeedActivationAsync(ctx.WorkflowId, typeKey, configJson: """{ "repositories": [] }""");

        await PublishAndCommitAsync(ev);

        await AssertRunCountAsync(ctx.WorkflowId, expected: 0);
        await AssertNoMatchAuditWrittenAsync(ctx.TeamId);
    }

    [Theory]
    [InlineData(TriggerKind.Opened)]
    [InlineData(TriggerKind.Updated)]
    public async Task DisabledActivation_creates_no_run(TriggerKind trigger)
    {
        // Even with a matching config, an activation with Enabled=false MUST be excluded
        // by LoadActiveActivationsAsync. Otherwise a paused workflow would keep firing and
        // operators would have no way to silence it short of deleting the activation.
        var ctx = await SeedAsync();
        var (typeKey, ev) = BuildForTrigger(trigger, ctx.RepositoryId);
        var configJson = $$"""{ "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}" }] }""";
        await SeedActivationAsync(ctx.WorkflowId, typeKey, configJson, enabled: false);

        await PublishAndCommitAsync(ev);

        await AssertRunCountAsync(ctx.WorkflowId, expected: 0);
    }

    [Theory]
    [InlineData(TriggerKind.Opened)]
    [InlineData(TriggerKind.Updated)]
    public async Task LabelsFilter_AND_semantics_excludes_event_missing_a_required_label(TriggerKind trigger)
    {
        // The schema description + UI both promise AND-match. If the matcher silently
        // shifted to OR, every workflow that uses label-scoping would over-fire. Pin
        // end-to-end so a future refactor can't wire an alternative matcher with looser
        // semantics for either trigger.
        var ctx = await SeedAsync();
        var (typeKey, ev) = BuildForTrigger(trigger, ctx.RepositoryId, labels: new[] { "bug" });
        var configJson = $$"""
            { "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}", "labels": ["bug", "wip"] }] }
            """;
        await SeedActivationAsync(ctx.WorkflowId, typeKey, configJson);

        await PublishAndCommitAsync(ev);

        await AssertRunCountAsync(ctx.WorkflowId, expected: 0);
    }

    [Theory]
    [InlineData(TriggerKind.Opened)]
    [InlineData(TriggerKind.Updated)]
    public async Task LabelsFilter_AND_semantics_fires_when_every_required_label_is_present(TriggerKind trigger)
    {
        var ctx = await SeedAsync();
        var (typeKey, ev) = BuildForTrigger(trigger, ctx.RepositoryId, labels: new[] { "wip", "bug", "extra" });
        var configJson = $$"""
            { "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}", "labels": ["bug", "wip"] }] }
            """;
        await SeedActivationAsync(ctx.WorkflowId, typeKey, configJson);

        await PublishAndCommitAsync(ev);

        await AssertRunCountAsync(ctx.WorkflowId, expected: 1);
    }

    [Theory]
    [InlineData(TriggerKind.Opened)]
    [InlineData(TriggerKind.Updated)]
    public async Task DuplicateDelivery_dedupes_to_one_run_per_activation(TriggerKind trigger)
    {
        // RunStarter synthesises an idempotency key of {sourceType}:{deliveryId}:{activationId}.
        // The same provider delivery replayed (GitHub / GitLab retry the same event on 5xx
        // until they get a 200) MUST NOT produce two workflow_run rows for the same
        // activation — operators would see two duplicated workflow firings per real PR.
        // PrUpdated's webhook re-delivery happens just as often (sync events fire on every
        // push to a PR) so the dedup contract matters here too.
        var ctx = await SeedAsync();
        var configJson = $$"""{ "repositories": [{ "repositoryId": "{{ctx.RepositoryId}}" }] }""";
        var (typeKey, _) = BuildForTrigger(trigger, ctx.RepositoryId);
        await SeedActivationAsync(ctx.WorkflowId, typeKey, configJson);

        var deliveryId = $"replay-{Guid.NewGuid():N}";
        var first = BuildForTrigger(trigger, ctx.RepositoryId, providerEventId: deliveryId).ev;
        var second = BuildForTrigger(trigger, ctx.RepositoryId, providerEventId: deliveryId).ev;

        await PublishAndCommitAsync(first);
        await PublishAndCommitAsync(second);

        await AssertRunCountAsync(ctx.WorkflowId, expected: 1);
    }

    // ─── Test fixture infrastructure ───────────────────────────────────────────

    private sealed record SeedContext(Guid TeamId, Guid UserId, Guid WorkflowId, Guid RepositoryId);

    /// <summary>
    /// Per-trigger event builder. Centralises the discriminator → (typeKey, event) tuple
    /// the Theory tests thread through. Adding a new PR trigger means adding one enum
    /// value + one branch here; every Theory then automatically covers it.
    /// </summary>
    private static (string typeKey, NormalizedEvent ev) BuildForTrigger(
        TriggerKind kind,
        Guid repositoryId,
        string[]? labels = null,
        string? providerEventId = null) => kind switch
    {
        TriggerKind.Opened => ("trigger.pr.opened", BuildOpenedEvent(repositoryId, labels, providerEventId)),
        TriggerKind.Updated => ("trigger.pr.updated", BuildSynchronizedEvent(repositoryId, labels, providerEventId)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown trigger kind"),
    };

    private static PullRequestOpenedEvent BuildOpenedEvent(Guid repositoryId, string[]? labels = null, string? providerEventId = null, bool isDraft = false) => new()
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
        IsDraft = isDraft,
    };

    private static PullRequestSynchronizedEvent BuildSynchronizedEvent(Guid repositoryId, string[]? labels = null, string? providerEventId = null) => new()
    {
        RepositoryId = repositoryId,
        ProviderEventId = providerEventId ?? $"delivery-{Guid.NewGuid():N}",
        OccurredAt = DateTimeOffset.UtcNow,
        ExternalPullRequestId = "100",
        Number = 42,
        PreviousHeadSha = "oldsha",
        NewHeadSha = "newsha",
        Labels = labels ?? Array.Empty<string>(),
    };

    /// <summary>
    /// Seed: team + user + workflow + repository row so the dispatcher's no-match audit
    /// path can look up team_id from the event's repository_id. The Repository row is
    /// the only thing the dispatcher's helper queries; PR-trigger matching itself reads
    /// only the activation config + the event's RepositoryId / Labels.
    /// </summary>
    private async Task<SeedContext> SeedAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        Guid repoId;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

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

    private async Task SeedActivationAsync(Guid workflowId, string typeKey, string configJson, bool enabled = true)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowActivation.Add(new WorkflowActivation
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            TypeKey = typeKey,
            ConfigJson = configJson,
            Enabled = enabled,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Publish via IMediator and wait for ALL handlers (including <c>RunSourceDispatcher</c>)
    /// to settle. The dispatcher persists inside its own SaveChanges + dispatches to the
    /// background-job client in-process; by the time Publish returns, the DB writes have
    /// committed and follow-up assertions can read them in a fresh scope.
    /// </summary>
    private async Task PublishAndCommitAsync(NormalizedEvent ev)
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();
        await mediator.Publish(ev).ConfigureAwait(false);
    }

    private async Task<(WorkflowRun run, JsonElement payload)> LoadRunAndPayloadAsync(Guid workflowId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking()
            .Include(r => r.RunRequest)
            .SingleOrDefaultAsync(r => r.WorkflowId == workflowId).ConfigureAwait(false);
        run.ShouldNotBeNull(customMessage:
            "Dispatcher MUST create exactly one workflow_run for a matching activation. " +
            "If null: matcher returned false OR RunStarter rejected the envelope.");
        var payload = JsonDocument.Parse(run.RunRequest.NormalizedPayloadJson).RootElement;
        return (run, payload);
    }

    private async Task AssertRunCountAsync(Guid workflowId, int expected)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var actual = await db.WorkflowRun.AsNoTracking()
            .Where(r => r.WorkflowId == workflowId)
            .CountAsync().ConfigureAwait(false);
        actual.ShouldBe(expected,
            customMessage: $"Expected {expected} run(s) for workflow {workflowId}; got {actual}. " +
                            "Check the dispatcher's loop in RunSourceDispatcher.DispatchAsync.");
    }

    private async Task AssertNoMatchAuditWrittenAsync(Guid teamId)
    {
        // The dispatcher's IIngestionAuditor.WriteNoMatchRejectedAsync persists the audit
        // as a workflow_run_request row with SourceType starting with "provider." and
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
