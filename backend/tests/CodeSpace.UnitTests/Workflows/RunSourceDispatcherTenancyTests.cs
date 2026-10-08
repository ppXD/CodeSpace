using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Core.Services.Completion.Exceptions;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Core.Services.Workflows.RunSources.Matchers;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;
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

    /// <summary>One in-memory database holding the event's repository in its team, plus the fakes the dispatcher talks to.</summary>
    private sealed class World : IDisposable
    {
        private readonly CodeSpaceDbContext _db = EmptyTestDb.New();

        public Guid EventTeamId { get; } = Guid.NewGuid();
        public Guid RepositoryId { get; } = Guid.NewGuid();
        public RecordingStarter Starter { get; } = new();
        public RecordingAuditor Auditor { get; } = new();

        public World(bool repositoryDeleted = false)
        {
            _db.Repository.Add(new Repository { Id = RepositoryId, TeamId = EventTeamId, ProviderInstanceId = Guid.NewGuid(), ExternalId = "1", NamespacePath = "acme", Name = "api", FullPath = "acme/api", WebUrl = "https://x", DeletedDate = repositoryDeleted ? DateTimeOffset.UtcNow : null });
        }

        public Guid Workflow(Guid teamId, string typeKey, string configJson)
        {
            var workflowId = Guid.NewGuid();
            var publisher = Guid.NewGuid();

            _db.Workflow.Add(new Workflow { Id = workflowId, TeamId = teamId, Slug = $"wf-{workflowId:N}", Name = "wf", DefinitionJson = "{}", LatestVersion = 1, Enabled = true, CreatedBy = publisher, LastModifiedBy = publisher });
            _db.WorkflowActivation.Add(new WorkflowActivation { Id = Guid.NewGuid(), WorkflowId = workflowId, TypeKey = typeKey, ConfigJson = configJson, Enabled = true, CreatedBy = publisher, LastModifiedBy = publisher });

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
            var dispatcher = new RunSourceDispatcher(_db, registry, Starter, new NoopRunDispatcher(), Auditor, new InlinePostCommitActions(), NullLogger<RunSourceDispatcher>.Instance);

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
