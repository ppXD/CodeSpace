using System.Text.Json;
using Autofac;
using CodeSpace.Core.Failures;
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
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ExternalTriggerIngressFlowTests(PostgresFixture fixture)
{
    private const string Config = """{"slug":"alerts","inputSchema":{"type":"object","properties":{"service":{"type":"string"}},"required":["service"]}}""";
    private sealed record Seed(Guid TeamId, Guid UserId, Guid WorkflowId, Guid ActivationId, Guid TriggerId, string Slug, string Key);
    private sealed record RollbackProbe : CodeSpace.Messages.Mediation.ICommand<ExternalTriggerOutcome>;

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
    public async Task An_unknown_trigger_is_unauthorized()
    {
        var seed = await SeedAsync();
        (await ReceiveAsync(seed with { TriggerId = Guid.NewGuid() })).Kind.ShouldBe(ExternalTriggerOutcomeKind.Unauthorized);
    }

    [Fact]
    public async Task Cheap_verification_does_not_require_a_definition_or_activation()
    {
        var seed = await SeedAsync();
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Workflow.Where(w => w.Id == seed.WorkflowId).ExecuteUpdateAsync(s => s.SetProperty(w => w.LatestVersion, 999));
        await db.WorkflowActivation.Where(a => a.WorkflowId == seed.WorkflowId).ExecuteDeleteAsync();
        (await scope.Resolve<IExternalTriggerIngress>().VerifyAsync(seed.TriggerId, seed.Key, CancellationToken.None))
            .Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_ticket_cannot_bypass_a_rotation_or_retirement_during_body_read(bool retire)
    {
        var seed = await SeedAsync();
        using var scope = fixture.BeginScope();
        var ingress = scope.Resolve<IExternalTriggerIngress>();
        (await ingress.VerifyAsync(seed.TriggerId, seed.Key, CancellationToken.None)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        using (var edit = fixture.BeginScope())
        {
            var db = edit.Resolve<CodeSpaceDbContext>();
            var row = await db.WorkflowExternalTrigger.SingleAsync(t => t.Id == seed.TriggerId);
            if (retire) row.DeletedDate = DateTimeOffset.UtcNow;
            else row.KeyDigest = ExternalTriggerKeySealer.Digest("rotated");
            await db.SaveChangesAsync();
        }
        (await ingress.ReceiveAsync(new(seed.TriggerId, seed.Key, null, "{}", false), CancellationToken.None))
            .Kind.ShouldBe(ExternalTriggerOutcomeKind.Unauthorized);
        (await RunsAsync(seed)).ShouldBeEmpty();
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
            var trigger = await db.WorkflowExternalTrigger.SingleAsync(t => t.Id == seed.TriggerId);
            trigger.NodeId = "removed-node";
            await db.SaveChangesAsync();
        }

        var outcome = await ReceiveAsync(seed, presentedKey: correctKey ? seed.Key : "wrong");
        outcome.Kind.ShouldBe(correctKey ? ExternalTriggerOutcomeKind.NotFound : ExternalTriggerOutcomeKind.Unauthorized);
        (await RunsAsync(seed)).ShouldBeEmpty();
        (await RequestsAsync(seed)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deduplicated_delivery_without_a_matching_run_is_a_server_error_without_dispatch()
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

        var error = await Record.ExceptionAsync(() => scope.Resolve<IExternalTriggerIngress>().ReceiveAsync(
            new(seed.TriggerId, seed.Key, "missing-run", """{"service":"api"}""", false), CancellationToken.None));
        error.ShouldNotBeNull();
        FailureClassifier.Classify(error).Kind.ShouldBe(FailureKind.Internal);
        FailureClassifier.Classify(error).ClientMessage.ShouldBe(FailureClassifier.MaskedMessage);
        dispatcher.Calls.ShouldBe(0);
        jobs.Calls.Count.ShouldBe(before);
        (await RunsAsync(seed)).ShouldBeEmpty();
        (await RequestsAsync(seed)).ShouldBeEmpty();
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
                case "deleted-trigger": (await db.WorkflowExternalTrigger.SingleAsync(t => t.Id == seed.TriggerId)).DeletedDate = DateTimeOffset.UtcNow; break;
                case "multiple-activations": db.WorkflowActivation.Add(Activation(seed, Guid.NewGuid(), Config)); break;
            }
            await db.SaveChangesAsync();
        }
        (await ReceiveAsync(seed, presentedKey: "wrong", body: "bad")).Kind.ShouldBe(ExternalTriggerOutcomeKind.Unauthorized);
        (await ReceiveAsync(seed)).Kind.ShouldBe(state == "deleted-trigger" ? ExternalTriggerOutcomeKind.Unauthorized : ExternalTriggerOutcomeKind.NotFound);
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
    public async Task An_authenticated_delivery_cannot_exceed_the_per_trigger_run_budget()
    {
        var seed = await SeedAsync();
        using var scope = fixture.BeginScope(b => b.RegisterInstance(new ExternalTriggerRunAdmission()).As<ExternalTriggerRunAdmission>().SingleInstance());
        var ingress = scope.Resolve<IExternalTriggerIngress>();
        var admitted = 0;
        for (var i = 0; i < ExternalTriggerRunAdmission.PermitsPerWindow + 1; i++)
        {
            var outcome = await ingress.ReceiveAsync(new ExternalTriggerCall(seed.TriggerId, seed.Key, null, """{"service":"api"}""", false), CancellationToken.None);
            if (outcome.Kind == ExternalTriggerOutcomeKind.Accepted) admitted++;
        }

        admitted.ShouldBe(ExternalTriggerRunAdmission.PermitsPerWindow);
        (await RunsAsync(seed)).Length.ShouldBe(ExternalTriggerRunAdmission.PermitsPerWindow);
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
    public async Task Reusing_an_idempotency_key_with_different_content_is_a_conflict()
    {
        var seed = await SeedAsync();
        var first = await ReceiveAsync(seed, idempotencyKey: "conflict");
        var second = await ReceiveAsync(seed, idempotencyKey: "conflict", body: "{\"service\":\"different\"}");
        second.Kind.ShouldBe(ExternalTriggerOutcomeKind.Unprocessable);
        (await RunsAsync(seed)).ShouldHaveSingleItem().Id.ShouldBe(first.RunId);
    }

    [Fact]
    public async Task Replays_do_not_consume_the_next_new_event_capacity()
    {
        var seed = await SeedAsync();
        var first = await ReceiveAsync(seed, idempotencyKey: "replay");
        for (var i = 0; i < ExternalTriggerRunAdmission.PermitsPerWindow; i++)
        {
            var replay = await ReceiveAsync(seed, idempotencyKey: "replay");
            replay.Kind.ShouldBe(ExternalTriggerOutcomeKind.Deduplicated);
            replay.RunId.ShouldBe(first.RunId);
        }
        (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
    }

    [Fact]
    public async Task An_equivalent_replay_precedes_the_current_schema()
    {
        var seed = await SeedAsync();
        var first = await ReceiveAsync(seed, idempotencyKey: "same", body: "{\"service\":\"api\",\"count\":1}");
        using (var edit = fixture.BeginScope())
        {
            var db = edit.Resolve<CodeSpaceDbContext>();
            var workflow = await db.Workflow.SingleAsync(w => w.Id == seed.WorkflowId);
            var definition = JsonSerializer.Deserialize<WorkflowDefinition>(workflow.DefinitionJson, WorkflowJson.Options)!;
            definition = definition with { Nodes = definition.Nodes.Select(n => n.Id == "start" ? n with
            {
                Config = WorkflowsTestSeed.Json("{\"inputSchema\":{\"type\":\"object\",\"required\":[\"newField\"]}}"),
            } : n).ToArray() };
            workflow.LatestVersion = 2;
            workflow.DefinitionJson = JsonSerializer.Serialize(definition, WorkflowJson.Options);
            db.WorkflowVersion.Add(new WorkflowVersion { WorkflowId = seed.WorkflowId, Version = 2, DefinitionJson = workflow.DefinitionJson, DefinitionHash = "test", CommittedAt = DateTimeOffset.UtcNow, CreatedBy = seed.UserId });
            await db.SaveChangesAsync();
        }
        var replay = await ReceiveAsync(seed, idempotencyKey: "same", body: "{\"count\":1.0, \"service\":\"a\\u0070i\"}");
        replay.Kind.ShouldBe(ExternalTriggerOutcomeKind.Deduplicated);
        replay.RunId.ShouldBe(first.RunId);
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
    public async Task Concurrent_different_payloads_with_one_key_have_one_winner()
    {
        var seed = await SeedAsync();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ReceiveAsync(seed,
            idempotencyKey: "different", body: $"{{\"service\":\"{i}\"}}")));
        outcomes.Count(o => o.Kind == ExternalTriggerOutcomeKind.Accepted).ShouldBe(1);
        outcomes.Count(o => o.Kind == ExternalTriggerOutcomeKind.Unprocessable).ShouldBe(7);
        (await RunsAsync(seed)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Tiny_json_numbers_round_trip_without_becoming_zero()
    {
        var seed = await SeedAsync();
        (await ReceiveAsync(seed, body: "{\"service\":\"api\",\"tiny\":1e-330}")).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        using var persisted = JsonDocument.Parse((await RunsAsync(seed)).ShouldHaveSingleItem().RunRequest.NormalizedPayloadJson);
        using var expected = JsonDocument.Parse("1e-330");
        JsonElement.DeepEquals(persisted.RootElement.GetProperty("tiny"), expected.RootElement).ShouldBeTrue();
    }

    [Fact]
    public async Task An_outer_rollback_releases_capacity_and_does_not_dispatch()
    {
        var seed = await SeedAsync();
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var behavior = new TransactionalBehavior<RollbackProbe, ExternalTriggerOutcome>(
            db, scope.Resolve<IPostCommitActions>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TransactionalBehavior<RollbackProbe, ExternalTriggerOutcome>>.Instance,
            scope.Resolve<TransactionCompletionCallbacks>());
        var request = new RollbackProbe();
        var error = await Record.ExceptionAsync(() => behavior.Handle(request, async ct =>
        {
            (await scope.Resolve<IExternalTriggerIngress>().ReceiveAsync(new(seed.TriggerId, seed.Key, null, "{\"service\":\"api\"}", false), ct)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
            throw new OperationCanceledException();
        }, CancellationToken.None));
        error.ShouldBeOfType<OperationCanceledException>();
        (await RunsAsync(seed)).ShouldBeEmpty();
        var admission = scope.Resolve<ExternalTriggerRunAdmission>();
        var reservations = Enumerable.Range(0, 60).Select(_ => admission.Reserve(seed.TriggerId)).ToArray();
        try { reservations.ShouldAllBe(r => r != null); }
        finally { foreach (var reservation in reservations) reservation?.Release(); }
    }

    [Fact]
    public async Task A_recreated_activation_is_resolved_fresh_and_the_trigger_still_deduplicates()
    {
        var seed = await SeedAsync();
        using var scope = fixture.BeginScope();
        var ingress = scope.Resolve<IExternalTriggerIngress>();
        var call = new ExternalTriggerCall(seed.TriggerId, seed.Key, "evt-1", """{"service":"api"}""", false);
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
        outcome.Kind.ShouldBe(ExternalTriggerOutcomeKind.Deduplicated);
        (await RunsAsync(seed)).ShouldHaveSingleItem().RunRequest.ActivationId.ShouldBe(seed.ActivationId);
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
    [InlineData(false, "agent.authority_denied", ExternalTriggerOutcomeKind.Forbidden)]
    [InlineData(true, "completion_admission_refused", ExternalTriggerOutcomeKind.Unprocessable)]
    public async Task A_deterministic_admission_refusal_is_classified_without_a_run_and_durably_audited(
        bool completion, string reason, ExternalTriggerOutcomeKind expectedKind)
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
            outcome.Kind.ShouldBe(expectedKind);
            outcome.RunId.ShouldBe(Guid.Empty);
        }
        (await RunsAsync(seed)).ShouldBeEmpty();
        using var verify = fixture.BeginScope();
        var audit = (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunRequest.Where(r => r.TeamId == seed.TeamId).ToListAsync()).ShouldHaveSingleItem();
        audit.Status.ShouldBe(WorkflowRunRequestStatus.Rejected);
        audit.SourceType.ShouldBe("trigger.external");
        audit.Error.ShouldNotBeNull();
        audit.Error!.ShouldContain(reason);
        audit.IdempotencyKey.ShouldStartWith($"refused:");
        audit.IdempotencyKey.ShouldContain($":{reason}:");
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
            var outcome = await scope.Resolve<IExternalTriggerIngress>().ReceiveAsync(new(seed.TriggerId, seed.Key, null, """{"service":"api"}""", false), CancellationToken.None);
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
        var seed = new Seed(teamId, userId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "alerts-" + Guid.NewGuid().ToString("N"), key.Key);
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
        db.WorkflowExternalTrigger.Add(new WorkflowExternalTrigger { Id = seed.TriggerId, TeamId = teamId, WorkflowId = seed.WorkflowId, NodeId = "start", KeyDigest = key.Digest, KeyCiphertext = key.Ciphertext, CreatedBy = userId, LastModifiedBy = userId });
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
        return await scope.Resolve<IExternalTriggerIngress>().ReceiveAsync(new(seed.TriggerId, presentedKey == "use-seeded-key" ? seed.Key : presentedKey, idempotencyKey, body, rejected), CancellationToken.None);
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
