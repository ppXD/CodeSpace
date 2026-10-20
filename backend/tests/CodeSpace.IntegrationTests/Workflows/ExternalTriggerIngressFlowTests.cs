using System.Text.Json;
using Autofac;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ExternalTriggerIngressFlowTests(PostgresFixture fixture)
{
    private const string Config = """{"slug":"alerts","inputSchema":{"type":"object","properties":{"service":{"type":"string"}},"required":["service"]}}""";
    private sealed record Seed(Guid TeamId, Guid UserId, Guid WorkflowId, Guid ActivationId, string Slug, string Key);

    [Fact]
    public async Task A_correct_key_starts_a_run_that_carries_the_activation_lineage()
    {
        var seed = await SeedAsync();
        var outcome = await ReceiveAsync(seed);
        outcome.Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        var run = (await RunsAsync(seed)).ShouldHaveSingleItem();
        outcome.RunId.ShouldBe(run.Id);
        run.RunRequest.SourceType.ShouldBe("trigger.external");
        run.RunRequest.ActorType.ShouldBe("webhook");
        run.RunRequest.ActorId.ShouldBeNull();
        run.RunRequest.ActivationId.ShouldBe(seed.ActivationId);
        run.RunRequest.ActivationSnapshotJson.ShouldNotBeNullOrEmpty();
        using var snapshot = JsonDocument.Parse(run.RunRequest.ActivationSnapshotJson);
        snapshot.RootElement.GetProperty("id").GetGuid().ShouldBe(seed.ActivationId);
        using var payload = JsonDocument.Parse(run.RunRequest.NormalizedPayloadJson);
        payload.RootElement.GetProperty("service").GetString().ShouldBe("api");
        run.Status.ShouldBe(WorkflowRunStatus.Enqueued);
    }

    [Theory]
    [InlineData("ext_wrong")]
    [InlineData(null)]
    public async Task The_wrong_or_missing_key_is_unauthorized_before_payload_validation(string? key)
    {
        var seed = await SeedAsync();
        (await ReceiveAsync(seed, presentedKey: key, body: "bad", rejected: true)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Unauthorized);
        (await RequestsAsync(seed)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{\"service\":7}", "$.service")]
    [InlineData("not json", "$: body must be a JSON object")]
    [InlineData("[]", "$: body must be a JSON object")]
    [InlineData("null", "$: body must be a JSON object")]
    public async Task An_invalid_body_is_refused_without_writing_a_request_row(string body, string error)
    {
        var seed = await SeedAsync();
        var outcome = await ReceiveAsync(seed, body: body);
        outcome.Kind.ShouldBe(ExternalTriggerOutcomeKind.InvalidPayload);
        outcome.Errors.ShouldContain(e => e.Contains(error));
        (await RequestsAsync(seed)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rejected_idempotency_header_precedes_body_validation()
    {
        var seed = await SeedAsync();
        var outcome = await ReceiveAsync(seed, body: "bad", rejected: true);
        outcome.Kind.ShouldBe(ExternalTriggerOutcomeKind.InvalidPayload);
        outcome.Errors.ShouldBe(new[] { "Idempotency-Key must be at most 200 characters from [A-Za-z0-9._~-]" });
        (await RequestsAsync(seed)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_slug_is_not_found()
    {
        var seed = await SeedAsync();
        (await ReceiveAsync(seed with { Slug = "nope" })).Kind.ShouldBe(ExternalTriggerOutcomeKind.NotFound);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_trigger_for_a_replaced_node_is_not_found_before_authentication(bool correctKey)
    {
        var seed = await SeedAsync();
        using (var edit = fixture.BeginScope())
        {
            var db = edit.Resolve<CodeSpaceDbContext>();
            var trigger = await db.WorkflowExternalTrigger.SingleAsync(t => t.Slug == seed.Slug);
            trigger.NodeId = "removed-node";
            await db.SaveChangesAsync();
        }

        var outcome = await ReceiveAsync(seed, presentedKey: correctKey ? seed.Key : "wrong");
        outcome.Kind.ShouldBe(ExternalTriggerOutcomeKind.NotFound);
        (await RunsAsync(seed)).ShouldBeEmpty();
        (await RequestsAsync(seed)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deduplicated_delivery_without_a_matching_run_is_accepted_and_warned_without_dispatch()
    {
        var seed = await SeedAsync();
        var logger = new IngressWarnings();
        var dispatcher = new IngressDispatches();
        using var scope = fixture.BeginScope(b =>
        {
            b.RegisterType<EmptyRunStarter>().As<IRunStarter>().InstancePerLifetimeScope();
            b.RegisterInstance(logger).As<ILogger<ExternalTriggerIngress>>();
            b.RegisterInstance(dispatcher).As<IWorkflowRunDispatcher>();
        });
        var jobs = scope.Resolve<InMemoryBackgroundJobClient>();
        var before = jobs.Calls.Count;

        var outcome = await scope.Resolve<IExternalTriggerIngress>().ReceiveAsync(
            new(seed.Slug, seed.Key, "missing-run", """{"service":"api"}""", false), CancellationToken.None);

        outcome.Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        outcome.RunId.ShouldBe(Guid.Empty);
        dispatcher.Calls.ShouldBe(0);
        jobs.Calls.Count.ShouldBe(before);
        (await RunsAsync(seed)).ShouldBeEmpty();
        (await RequestsAsync(seed)).ShouldBeEmpty();
        logger.Warnings.ShouldContain(w => w.Contains(seed.Slug) && w.Contains("run could not be found"));
    }

    private sealed class EmptyRunStarter : IRunStarter
    {
        public Task<Guid> StartAsync(RunSourceEnvelope envelope, CancellationToken cancellationToken) => Task.FromResult(Guid.Empty);
    }

    private sealed class IngressDispatches : IWorkflowRunDispatcher
    {
        public int Calls { get; private set; }
        public Task<bool> DispatchAsync(Guid runId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed class IngressWarnings : ILogger<ExternalTriggerIngress>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    [Theory]
    [InlineData("disabled-activation")]
    [InlineData("deleted-activation")]
    [InlineData("disabled-workflow")]
    [InlineData("deleted-workflow")]
    [InlineData("deleted-trigger")]
    [InlineData("multiple-activations")]
    public async Task An_unavailable_or_ambiguous_endpoint_is_not_found(string state)
    {
        var seed = await SeedAsync();
        using (var scope = fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var activation = await db.WorkflowActivation.SingleAsync(a => a.Id == seed.ActivationId);
            var workflow = await db.Workflow.SingleAsync(w => w.Id == seed.WorkflowId);
            switch (state)
            {
                case "disabled-activation": activation.Enabled = false; break;
                case "deleted-activation": activation.DeletedDate = DateTimeOffset.UtcNow; break;
                case "disabled-workflow": workflow.Enabled = false; break;
                case "deleted-workflow": workflow.DeletedDate = DateTimeOffset.UtcNow; break;
                case "deleted-trigger": (await db.WorkflowExternalTrigger.SingleAsync(t => t.Slug == seed.Slug)).DeletedDate = DateTimeOffset.UtcNow; break;
                case "multiple-activations": db.WorkflowActivation.Add(Activation(seed, Guid.NewGuid(), Config)); break;
            }
            await db.SaveChangesAsync();
        }
        (await ReceiveAsync(seed, presentedKey: "wrong", body: "bad")).Kind.ShouldBe(ExternalTriggerOutcomeKind.NotFound);
        (await RequestsAsync(seed)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_deliveries_without_an_idempotency_key_are_two_runs()
    {
        var seed = await SeedAsync();
        (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        (await RunsAsync(seed)).Length.ShouldBe(2);
    }

    [Fact]
    public async Task The_same_idempotency_key_twice_starts_one_run_and_returns_the_first_run_id()
    {
        var seed = await SeedAsync();
        var first = await ReceiveAsync(seed, idempotencyKey: "evt-1");
        var second = await ReceiveAsync(seed, idempotencyKey: "evt-1");
        first.Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        second.Kind.ShouldBe(ExternalTriggerOutcomeKind.Deduplicated);
        second.RunId.ShouldBe(first.RunId);
        (await RunsAsync(seed)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_key_start_exactly_one_run()
    {
        var seed = await SeedAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ReceiveAsync(seed, idempotencyKey: "evt-concurrent")));
        (await RunsAsync(seed)).Length.ShouldBe(1);
        results.Select(r => r.RunId).Distinct().ShouldHaveSingleItem().ShouldNotBe(Guid.Empty);
        results.Count(r => r.Kind == ExternalTriggerOutcomeKind.Accepted).ShouldBe(1);
        results.Count(r => r.Kind == ExternalTriggerOutcomeKind.Deduplicated).ShouldBe(7);
    }

    [Fact]
    public async Task A_recreated_activation_is_resolved_fresh_even_in_the_same_scope()
    {
        var seed = await SeedAsync();
        using var scope = fixture.BeginScope();
        var ingress = scope.Resolve<IExternalTriggerIngress>();
        var call = new ExternalTriggerCall(seed.Slug, seed.Key, "evt-1", """{"service":"api"}""", false);
        (await ingress.ReceiveAsync(call, CancellationToken.None)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        var replacement = Guid.NewGuid();
        using (var edit = fixture.BeginScope())
        {
            var db = edit.Resolve<CodeSpaceDbContext>();
            await db.WorkflowActivation.Where(a => a.Id == seed.ActivationId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedDate, DateTimeOffset.UtcNow));
            db.WorkflowActivation.Add(Activation(seed, replacement, Config));
            await db.SaveChangesAsync();
        }
        var outcome = await ingress.ReceiveAsync(call, CancellationToken.None);
        outcome.Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        (await RunsAsync(seed)).Single(r => r.Id == outcome.RunId).RunRequest.ActivationId.ShouldBe(replacement);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"inputSchema\":7}")]
    public async Task A_missing_or_non_object_schema_imposes_no_extra_constraints(string config)
    {
        var seed = await SeedAsync(config);
        (await ReceiveAsync(seed, body: "{}")).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
    }

    [Theory]
    [InlineData(false, "agent.authority_denied")]
    [InlineData(true, "completion_admission_refused")]
    public async Task An_admission_refusal_is_accepted_without_a_run_and_durably_audited(bool completion, string reason)
    {
        var seed = await SeedAsync();
        using (var scope = fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            if (completion)
            {
                var workflow = await db.Workflow.SingleAsync(w => w.Id == seed.WorkflowId);
                var graph = JsonSerializer.Deserialize<WorkflowDefinition>(workflow.DefinitionJson, WorkflowJson.Options)!;
                var definition = JsonSerializer.Serialize(graph with { CompletionMode = WorkflowDefinition.CompletionModeEnforced }, WorkflowJson.Options);
                workflow.LatestVersion = 2;
                workflow.DefinitionJson = definition;
                db.WorkflowVersion.Add(new WorkflowVersion { WorkflowId = seed.WorkflowId, Version = 2, DefinitionJson = definition, DefinitionHash = "legacy", CommittedAt = DateTimeOffset.UtcNow, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = seed.UserId });
            }
            else await db.TeamMembership.Where(m => m.TeamId == seed.TeamId).ExecuteDeleteAsync();
            await db.SaveChangesAsync();
        }
        for (var i = 0; i < 2; i++)
        {
            var outcome = await ReceiveAsync(seed, idempotencyKey: "refused");
            outcome.Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
            outcome.RunId.ShouldBe(Guid.Empty);
        }
        (await RunsAsync(seed)).ShouldBeEmpty();
        using var verify = fixture.BeginScope();
        var audit = (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunRequest.Where(r => r.TeamId == seed.TeamId).ToListAsync()).ShouldHaveSingleItem();
        audit.Status.ShouldBe(WorkflowRunRequestStatus.Rejected);
        audit.SourceType.ShouldBe("trigger.external");
        audit.Error.ShouldNotBeNull();
        audit.Error!.ShouldContain(reason);
        audit.IdempotencyKey.ShouldBe($"rejected:{seed.Slug}:refused");
    }

    [Fact]
    public async Task Dispatch_waits_until_the_ambient_transaction_commits()
    {
        var seed = await SeedAsync();
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        Guid runId;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var outcome = await scope.Resolve<IExternalTriggerIngress>().ReceiveAsync(new(seed.Slug, seed.Key, null, """{"service":"api"}""", false), CancellationToken.None);
            runId = outcome.RunId;
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Pending);
            (await RunsAsync(seed)).ShouldBeEmpty();
            await transaction.CommitAsync();
        }
        await scope.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);
        (await RunsAsync(seed)).ShouldHaveSingleItem().Status.ShouldBe(WorkflowRunStatus.Enqueued);
    }

    private async Task<Seed> SeedAsync(string config = Config)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, inProcessPool: false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var key = scope.Resolve<ExternalTriggerKeySealer>().Mint();
        var seed = new Seed(teamId, userId, Guid.NewGuid(), Guid.NewGuid(), "alerts-" + Guid.NewGuid().ToString("N"), key.Key);
        var graph = WorkflowsTestSeed.MinimalDefinition() with
        {
            Nodes = new[]
            {
                new NodeDefinition { Id = "start", TypeKey = "trigger.external", Config = WorkflowsTestSeed.Json(config), Inputs = WorkflowsTestSeed.EmptyJson() },
                new NodeDefinition { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
        };
        var definition = JsonSerializer.Serialize(graph, WorkflowJson.Options);
        db.Workflow.Add(new Workflow { Id = seed.WorkflowId, TeamId = teamId, Slug = seed.Slug, Name = "External ingress", DefinitionJson = definition, Enabled = true, CreatedBy = userId, LastModifiedBy = userId });
        db.WorkflowVersion.Add(new WorkflowVersion { WorkflowId = seed.WorkflowId, Version = 1, DefinitionJson = definition, DefinitionHash = "test", CommittedAt = DateTimeOffset.UtcNow, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId });
        db.WorkflowActivation.Add(Activation(seed, seed.ActivationId, config));
        db.WorkflowExternalTrigger.Add(new WorkflowExternalTrigger { Id = Guid.NewGuid(), TeamId = teamId, WorkflowId = seed.WorkflowId, NodeId = "start", Slug = seed.Slug, KeyDigest = key.Digest, KeyCiphertext = key.Ciphertext, CreatedBy = userId, LastModifiedBy = userId });
        await db.SaveChangesAsync();
        return seed;
    }

    private static WorkflowActivation Activation(Seed seed, Guid id, string config) => new()
    {
        Id = id, WorkflowId = seed.WorkflowId, TypeKey = "trigger.external", ConfigJson = config,
        Enabled = true, CreatedBy = seed.UserId, LastModifiedBy = seed.UserId,
    };

    private async Task<ExternalTriggerOutcome> ReceiveAsync(Seed seed, string? presentedKey = "use-seeded-key", string? idempotencyKey = null, string body = "{\"service\": \"api\"}", bool rejected = false)
    {
        using var scope = fixture.BeginScope();
        return await scope.Resolve<IExternalTriggerIngress>().ReceiveAsync(new(seed.Slug, presentedKey == "use-seeded-key" ? seed.Key : presentedKey, idempotencyKey, body, rejected), CancellationToken.None);
    }

    private async Task<WorkflowRun[]> RunsAsync(Seed seed)
    {
        using var scope = fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Include(r => r.RunRequest).Where(r => r.WorkflowId == seed.WorkflowId).ToArrayAsync();
    }

    private async Task<WorkflowRunRequest[]> RequestsAsync(Seed seed)
    {
        using var scope = fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking().Where(r => r.WorkflowId == seed.WorkflowId).ToArrayAsync();
    }
}
