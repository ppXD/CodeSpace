using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Core.Services.Completion.Exceptions;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Core.Services.Workflows.RunSources.Admission;
using CodeSpace.Core.Services.Workflows.RunSources.Admission.Exceptions;
using CodeSpace.Core.Services.Workflows.RunSources.Matchers;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;
using System.Text.Json;
using CodeSpace.UnitTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The dispatcher's two tenancy decisions, without Postgres: WHICH activations a delivery may reach (only the
/// event repository's own team's — so an activation naming no repository means "any repository of this team",
/// never "any repository anywhere"), and HOW MUCH one activation's refusal costs (its own run, audited — never
/// its siblings'). The starter is a recording fake so the assertion is on the envelopes the dispatcher hands
/// over; the Postgres-backed half lives in <c>WebhookEventToRunDispatchFlowTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class RunSourceDispatcherTenancyTests
{
    private const string PrOpened = "trigger.pr.opened";

    [Fact]
    public async Task A_catch_all_activation_in_another_team_is_never_handed_this_teams_event()
    {
        using var world = new World();
        var own = world.Workflow(world.EventTeamId, PrOpened, "{}");
        var foreign = world.Workflow(Guid.NewGuid(), PrOpened, "{}");
        await world.SaveAsync();

        await world.DispatchAsync(world.OpenedEvent());

        world.Starter.Started.Select(e => e.WorkflowId).ShouldBe(new[] { own }, customMessage: "only the event repository's own team may receive its payload");
        world.Starter.Started.ShouldAllBe(e => e.TeamId == world.EventTeamId);
        world.Starter.Started.ShouldNotContain(e => e.WorkflowId == foreign);
    }

    [Fact]
    public async Task A_delivery_for_a_repository_no_team_holds_starts_nothing_and_audits_nothing()
    {
        using var world = new World();
        world.Workflow(world.EventTeamId, PrOpened, "{}");
        await world.SaveAsync();

        await world.DispatchAsync(world.OpenedEvent(repositoryId: Guid.NewGuid()));

        world.Starter.Started.ShouldBeEmpty(customMessage: "a repository nobody holds has no team to run in");
        world.Auditor.NoMatches.ShouldBeEmpty(customMessage: "and no team to attribute an audit row to");
    }

    [Fact]
    public async Task A_removed_repository_is_held_by_no_team()
    {
        using var world = new World(repositoryDeleted: true);
        world.Workflow(world.EventTeamId, PrOpened, "{}");
        await world.SaveAsync();

        await world.DispatchAsync(world.OpenedEvent());

        world.Starter.Started.ShouldBeEmpty(customMessage: "a repository the team removed must not keep starting its runs");
    }

    [Theory]
    [InlineData(Refusal.CompletionAdmission, WorkflowRunRequestRejectionReasons.CompletionAdmissionRefused)]
    [InlineData(Refusal.Authority, "agent.authority_denied")]
    public async Task A_refusing_activation_is_audited_and_its_sibling_still_starts(Refusal refusal, string expectedReason)
    {
        using var world = new World();
        var refusing = world.Workflow(world.EventTeamId, PrOpened, "{}");
        var sibling = world.Workflow(world.EventTeamId, PrOpened, "{}");
        await world.SaveAsync();
        world.Starter.Refuse(refusing, refusal);

        var delivery = world.OpenedEvent();
        await world.DispatchAsync(delivery);

        world.Starter.Started.Select(e => e.WorkflowId).ShouldBe(new[] { sibling }, customMessage: "one activation refusing must cost only its own run");
        var audit = world.Auditor.Rejections.ShouldHaveSingleItem();
        audit.TeamId.ShouldBe(world.EventTeamId);
        audit.Reason.ShouldBe(expectedReason);
        audit.Detail.ShouldContain(refusing.ToString());
        audit.ExternalEventId.ShouldBe(delivery.ProviderEventId);
        audit.DedupKey.ShouldNotBeNull().ShouldContain(delivery.ProviderEventId, customMessage: "a provider retry of the same delivery must collapse onto one row");
        world.Auditor.NoMatches.ShouldBeEmpty(customMessage: "a refusal is its own reason — it must not also read as 'nothing was listening'");
    }

    [Theory]
    [InlineData(PullRequestRefusal.AuthorNotMember, WorkflowRunRequestRejectionReasons.AuthorNotMember)]
    [InlineData(PullRequestRefusal.Debounced, WorkflowRunRequestRejectionReasons.PullRequestDebounced)]
    public async Task A_pull_request_admission_refusal_costs_its_own_run_and_collapses_on_the_refusals_own_key(PullRequestRefusal refusal, string expectedReason)
    {
        using var world = new World();
        var refusing = world.Workflow(world.EventTeamId, PrOpened, "{}");
        var sibling = world.Workflow(world.EventTeamId, PrOpened, """{"authors":"any"}""");
        await world.SaveAsync();
        world.Admission.Refuse(refusing, refusal);

        await world.DispatchAsync(world.OpenedEvent());

        world.Starter.Started.Select(e => e.WorkflowId).ShouldBe(new[] { sibling }, customMessage: "an author or debounce refusal belongs to one activation");
        var audit = world.Auditor.Rejections.ShouldHaveSingleItem();
        audit.Reason.ShouldBe(expectedReason);
        audit.DedupKey.ShouldBe(RecordingAdmission.AuditKey, customMessage: "a reopen loop is a stream of genuinely signed deliveries — the row collapses per PR, not per delivery");
        world.Auditor.NoMatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Admission_is_asked_only_after_the_matcher_said_yes_and_sees_the_activations_own_config()
    {
        using var world = new World();
        world.Workflow(world.EventTeamId, PrOpened, $$"""{"repositories":[{"repositoryId":"{{Guid.NewGuid()}}"}]}""");
        var matching = world.Workflow(world.EventTeamId, PrOpened, """{"authors":"members"}""");
        await world.SaveAsync();

        await world.DispatchAsync(world.OpenedEvent());

        world.Admission.Asked.ShouldBe(new (Guid, string?)[] { (matching, "members") });
    }

    [Fact]
    public async Task Activations_are_visited_in_one_order_whatever_order_they_were_stored_in()
    {
        // Each visit can take a row lock — the PR debounce claim — held until the delivery commits. Two concurrent
        // deliveries for one PR that visited the same activations in different orders could each hold the lock the
        // other waits on. Visiting by id gives every delivery the same order.
        using var world = new World();
        var ids = new[] { 3, 1, 2 }.Select(n => new Guid($"00000000-0000-0000-0000-00000000000{n}")).ToList();
        foreach (var id in ids) world.Workflow(world.EventTeamId, PrOpened, "{}", activationId: id);
        await world.SaveAsync();

        await world.DispatchAsync(world.OpenedEvent());

        world.Admission.ActivationIds.ShouldBe(ids.OrderBy(id => id).ToList(), customMessage: "check the ORDER BY in RunSourceDispatcher.LoadActiveActivationsAsync");
    }

    [Fact]
    public async Task An_infrastructure_failure_is_not_a_refusal_and_propagates()
    {
        using var world = new World();
        var failing = world.Workflow(world.EventTeamId, PrOpened, "{}");
        await world.SaveAsync();
        world.Starter.Fail(failing, new InvalidOperationException("connection reset"));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => world.DispatchAsync(world.OpenedEvent()));

        error.Message.ShouldBe("connection reset", customMessage: "only a typed admission refusal is isolated; anything else must roll the delivery back so the provider retries");
        world.Auditor.Rejections.ShouldBeEmpty();
    }

    public enum Refusal { CompletionAdmission, Authority }

    public enum PullRequestRefusal { AuthorNotMember, Debounced }

    /// <summary>One in-memory database holding the event's repository in its team, plus the fakes the dispatcher talks to.</summary>
    private sealed class World : IDisposable
    {
        private readonly CodeSpaceDbContext _db = EmptyTestDb.New();

        public Guid EventTeamId { get; } = Guid.NewGuid();
        public Guid RepositoryId { get; } = Guid.NewGuid();
        public RecordingStarter Starter { get; } = new();
        public RecordingAuditor Auditor { get; } = new();
        public RecordingAdmission Admission { get; } = new();

        public World(bool repositoryDeleted = false)
        {
            _db.Repository.Add(new Repository { Id = RepositoryId, TeamId = EventTeamId, ProviderInstanceId = Guid.NewGuid(), ExternalId = "1", NamespacePath = "acme", Name = "api", FullPath = "acme/api", WebUrl = "https://x", DeletedDate = repositoryDeleted ? DateTimeOffset.UtcNow : null });
        }

        public Guid Workflow(Guid teamId, string typeKey, string configJson, Guid? activationId = null)
        {
            var workflowId = Guid.NewGuid();
            var publisher = Guid.NewGuid();

            _db.Workflow.Add(new Workflow { Id = workflowId, TeamId = teamId, Slug = $"wf-{workflowId:N}", Name = "wf", DefinitionJson = "{}", LatestVersion = 1, Enabled = true, CreatedBy = publisher, LastModifiedBy = publisher });
            _db.WorkflowActivation.Add(new WorkflowActivation { Id = activationId ?? Guid.NewGuid(), WorkflowId = workflowId, TypeKey = typeKey, ConfigJson = configJson, Enabled = true, CreatedBy = publisher, LastModifiedBy = publisher });

            return workflowId;
        }

        public Task SaveAsync() => _db.SaveChangesAsync();

        public PullRequestOpenedEvent OpenedEvent(Guid? repositoryId = null) => new()
        {
            RepositoryId = repositoryId ?? RepositoryId,
            ProviderEventId = $"delivery-{Guid.NewGuid():N}",
            OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "100",
            Number = 42,
            Title = "private title",
            Body = "private body",
            SourceBranch = "feat/x",
            TargetBranch = "main",
            AuthorExternalId = "u-1",
            AuthorName = "alice",
            WebUrl = "https://x/pull/42",
            Labels = [],
        };

        public Task DispatchAsync(PullRequestOpenedEvent normalizedEvent)
        {
            var registry = new RunSourceMatcherRegistry(new IRunSourceMatcher[] { new PrOpenedMatcher(), new PrUpdatedMatcher(), new PrMergedMatcher(), new PushMatcher() });
            var dispatcher = new RunSourceDispatcher(_db, registry, Starter, new NoopRunDispatcher(), Auditor, new InlinePostCommitActions(), Admission, NullLogger<RunSourceDispatcher>.Instance);

            return dispatcher.Handle(normalizedEvent, CancellationToken.None);
        }

        public void Dispose() => _db.Dispose();
    }

    private sealed class RecordingStarter : IRunStarter
    {
        private readonly Dictionary<Guid, Exception> _failures = new();

        public List<RunSourceEnvelope> Started { get; } = new();

        public void Refuse(Guid workflowId, Refusal refusal) => _failures[workflowId] = refusal switch
        {
            Refusal.CompletionAdmission => new CompletionAdmissionRefusedException("Definition opts into Enforced but mode 'generic' has no registered conformance profile"),
            _ => new AgentAuthorityDeniedException("publisher_not_member"),
        };

        public void Fail(Guid workflowId, Exception error) => _failures[workflowId] = error;

        public Task<Guid> StartAsync(RunSourceEnvelope envelope, CancellationToken cancellationToken)
        {
            if (_failures.TryGetValue(envelope.WorkflowId, out var error)) throw error;

            Started.Add(envelope);
            return Task.FromResult(Guid.NewGuid());
        }
    }

    /// <summary>Admits everything unless told to refuse one workflow; records what it was asked, so a test can see the dispatcher asks AFTER matching.</summary>
    private sealed class RecordingAdmission : IPullRequestTriggerAdmission
    {
        public const string AuditKey = "rejected:test:per-pr";

        private readonly Dictionary<Guid, PullRequestRefusal> _refusals = new();

        public List<(Guid WorkflowId, string? Authors)> Asked { get; } = new();

        /// <summary>The activations asked about, in the order the dispatcher visited them.</summary>
        public List<Guid> ActivationIds { get; } = new();

        public void Refuse(Guid workflowId, PullRequestRefusal refusal) => _refusals[workflowId] = refusal;

        public Task EnsureAdmittedAsync(WorkflowActivation activation, NormalizedEvent normalizedEvent, JsonElement activationConfig, CancellationToken cancellationToken)
        {
            Asked.Add((activation.WorkflowId, activationConfig.TryGetProperty("authors", out var authors) ? authors.GetString() : null));
            ActivationIds.Add(activation.Id);

            if (!_refusals.TryGetValue(activation.WorkflowId, out var refusal)) return Task.CompletedTask;

            Exception error = refusal == PullRequestRefusal.Debounced
                ? new PullRequestTriggerDebouncedException(42, "head-1", TimeSpan.FromSeconds(60), AuditKey)
                : new PullRequestAuthorRefusedException("pull request #42 was written by an author whose standing is 'none'", AuditKey);

            return Task.FromException(error);
        }
    }

    private sealed class RecordingAuditor : IIngestionAuditor
    {
        public List<WebhookRejectionContext> Rejections { get; } = new();
        public List<NormalizedEvent> NoMatches { get; } = new();

        public Task WriteWebhookRejectedAsync(WebhookRejectionContext context, CancellationToken cancellationToken)
        {
            Rejections.Add(context);
            return Task.CompletedTask;
        }

        public Task WriteNoMatchRejectedAsync(NormalizedEvent normalizedEvent, Guid teamId, CancellationToken cancellationToken)
        {
            NoMatches.Add(normalizedEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopRunDispatcher : IWorkflowRunDispatcher
    {
        public Task<bool> DispatchAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class InlinePostCommitActions : IPostCommitActions
    {
        public Task RunAfterCommitAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) => action(cancellationToken);
        public Task RunAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public int CreateCheckpoint() => 0;
        public void RollbackTo(int checkpoint) { }
    }
}
