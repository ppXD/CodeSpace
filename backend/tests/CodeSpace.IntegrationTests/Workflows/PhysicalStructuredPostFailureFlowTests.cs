using System.Data.Common;
using System.Net;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Exceptions;
using CodeSpace.Core.Services.Workflows.ModelCalls;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

public sealed partial class PhysicalStructuredPostAccountingFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Throwing_content_disposal_cannot_replace_the_primary_observation_failure(bool cancel)
    {
        var scenario = await SeedAsync();
        var content = new ThrowingDisposeContent(cancel);
        var observer = new AccountingFailureObserver();
        using var handler = new ProtocolHandler("Anthropic", new[] { new Reply(HttpStatusCode.OK, "", Content: () => content) }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler, o => o.MaxEnvelopeBytes = 32, s => s.AddHttpClient(nameof(CodeSpace.Core.Services.Workflows.Llm.Anthropic.AnthropicClient))
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Insert(handlers.IndexOf(handlers.Single(h => h is PhysicalLlmAccountingHandler)), observer)));
        using var scope = fixture.BeginScope();
        using var cancellation = new CancellationTokenSource();
        try
        {
            using var ambient = Push(scope, scenario);
            var pending = Decorated("Anthropic", services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request("Anthropic", "single"), cancellation.Token);
            await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) cancellation.Cancel();
            if (cancel) await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            else await Should.ThrowAsync<PhysicalLlmAccountingException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            // Observe the accounting boundary inside retry without replacing its exception. The outer retry/
            // HttpClient pipeline may create a new cancellation exception and omit arbitrary diagnostic Data.
            var error = observer.Error.ShouldNotBeNull();
            if (cancel) error.ShouldBeOfType<OperationCanceledException>();
            else error.ShouldBeOfType<PhysicalLlmAccountingException>();
            error.Data["physical_response_cleanup_failure"].ShouldBe(typeof(IOException).FullName);
        }
        finally { content.Dispose(); }
        handler.Admissions.Count.ShouldBe(1);
        using var read = fixture.BeginScope();
        var attempt = await read.Resolve<CodeSpaceDbContext>().WorkflowRunModelCallAttempt.SingleAsync(a => a.WorkflowRunId == scenario.RunId);
        attempt.ErrorCode.ShouldBe(cancel ? "observation-cancelled" : "observation-limit");
        attempt.CostAmount.ShouldBeNull();
    }

    [Fact]
    public async Task A_capped_provider_without_the_registered_physical_transport_fails_before_sending()
    {
        var scenario = await SeedAsync();
        using var handler = new ProtocolHandler("Anthropic", new[] { Success("Anthropic") }, () => ReservationIdsAsync(scenario));
        using var scope = fixture.BeginScope();
        using (Push(scope, scenario)) await Should.ThrowAsync<PhysicalLlmAccountingException>(() => Decorated("Anthropic", new UnregisteredFactory(handler)).CompleteStructuredAsync(Request("Anthropic", "single"), CancellationToken.None));
        handler.Admissions.ShouldBeEmpty();
        (await ReservationIdsAsync(scenario)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Provider_echoed_credentials_never_enter_native_or_logical_accounting_metadata()
    {
        var scenario = await SeedAsync();
        var response = Success("Anthropic");
        response = response with { Body = response.Body[..^1] + ",\"stop_reason\":\"synthetic-fixture-key\"}" };
        using var handler = new ProtocolHandler("Anthropic", new[] { response }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        using (var scope = fixture.BeginScope())
        using (Push(scope, scenario)) await Decorated("Anthropic", services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request("Anthropic", "single"), CancellationToken.None);
        using var read = fixture.BeginScope();
        var db = read.Resolve<CodeSpaceDbContext>();
        var attempt = await db.WorkflowRunModelCallAttempt.SingleAsync(a => a.WorkflowRunId == scenario.RunId);
        attempt.FinishReason.ShouldNotContain("synthetic-fixture-key");
        var records = await db.WorkflowRunRecord.Where(r => r.RunId == scenario.RunId && r.RecordType.StartsWith("interaction.")).Select(r => r.PayloadJson).ToArrayAsync();
        records.ShouldAllBe(r => !r.Contains("synthetic-fixture-key", StringComparison.Ordinal));
        attempt.CostAmount.ShouldBe(0.000015m);
    }

    [Fact]
    public async Task Direct_structured_provider_use_cannot_bypass_a_workflow_cost_cap()
    {
        var scenario = await SeedAsync();
        using var handler = new ProtocolHandler("Anthropic", new[] { Success("Anthropic") }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        using var scope = fixture.BeginScope();
        using (Push(scope, scenario, 0m)) await Should.ThrowAsync<LlmBudgetExceededException>(() => new CodeSpace.Core.Services.Workflows.Llm.Anthropic.AnthropicClient(services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request("Anthropic", "single"), CancellationToken.None));
        handler.Admissions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removing_or_moving_the_native_handler_outside_retry_refuses_before_primary_Send(bool moveOutsideRetry)
    {
        var scenario = await SeedAsync();
        using var handler = new ProtocolHandler("Anthropic", new[] { Success("Anthropic") }, () => ReservationIdsAsync(scenario));
        var collection = new ServiceCollection();
        collection.AddLlmHttpClients();
        foreach (var name in LlmHttpClientRegistration.ClientNames)
            collection.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler).ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
            {
                var physical = handlers.Single(h => h is PhysicalLlmAccountingHandler);
                handlers.Remove(physical);
                if (moveOutsideRetry) handlers.Insert(0, physical);
            });
        using var services = collection.BuildServiceProvider();
        using var scope = fixture.BeginScope();
        using (Push(scope, scenario)) await Should.ThrowAsync<PhysicalLlmAccountingException>(() => Decorated("Anthropic", services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request("Anthropic", "single"), CancellationToken.None));
        handler.Admissions.ShouldBeEmpty();
        (await ReservationIdsAsync(scenario)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Native_receipts_do_not_reappear_as_legacy_shadow_attempts()
    {
        var scenario = await SeedAsync();
        using var handler = new ProtocolHandler("Anthropic", Replies("Anthropic", "parse-reask"), () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        using (var scope = fixture.BeginScope())
        using (Push(scope, scenario)) await Decorated("Anthropic", services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request("Anthropic", "parse-reask"), CancellationToken.None);
        using var projection = fixture.BeginScope();
        await projection.Resolve<IWorkflowRunModelCallProjector>().SweepAsync(1000, CancellationToken.None);
        var db = projection.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRunModelCall.CountAsync(c => c.WorkflowRunId == scenario.RunId)).ShouldBe(1);
        (await db.WorkflowRunModelCallAttempt.CountAsync(c => c.WorkflowRunId == scenario.RunId)).ShouldBe(3);
        var attempts = await db.WorkflowRunModelCallAttempt.AsNoTracking().Where(a => a.WorkflowRunId == scenario.RunId).ToArrayAsync();
        var reservations = await db.BudgetReservation.AsNoTracking().Where(a => a.WorkflowRunId == scenario.RunId).ToArrayAsync();
        attempts.Select(a => a.BudgetReservationId!.Value).Order().ShouldBe(reservations.Select(a => a.Id).Order());
        attempts.Select(a => a.CandidateId).Distinct().Count().ShouldBe(1);
        reservations.All(r => r.ParentReservationId == null).ShouldBeTrue();
        reservations.Sum(r => r.SettledUsd).ShouldBe(0.000415m);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_lost_database_commit_ACK_never_causes_an_extra_provider_POST(bool admission)
    {
        var scenario = await SeedAsync();
        var interceptor = new LosePhysicalCommitAck(admission);
        using var scope = InterceptedPhysicalScope(interceptor);
        using var handler = new ProtocolHandler("Anthropic", new[] { Success("Anthropic") }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        StructuredLLMCompletion completion;
        using (Push(scope, scenario)) completion = await Decorated("Anthropic", services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request("Anthropic", "single"), CancellationToken.None);
        completion.Json.GetProperty("approved").GetBoolean().ShouldBeTrue();
        interceptor.Lost.ShouldBe(1);
        handler.Admissions.Count.ShouldBe(1);
        using var read = fixture.BeginScope();
        var claim = await read.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().SingleAsync(r => r.WorkflowRunId == scenario.RunId);
        claim.SettledUsd.ShouldBe(0.000015m);
        completion.Usage.IsPartial.ShouldBe(!admission, "settlement ACK uncertainty is honest even when the DB committed");
    }

    [Fact]
    public async Task An_uncertain_admission_commit_with_unavailable_readback_cannot_send()
    {
        var scenario = await SeedAsync();
        var loss = new LosePhysicalCommitAck(true);
        using var scope = InterceptedPhysicalScope(loss, new RejectCommitReadback(loss));
        using var handler = new ProtocolHandler("Anthropic", new[] { Success("Anthropic") }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        using (Push(scope, scenario)) await Should.ThrowAsync<PhysicalLlmAccountingException>(() => Decorated("Anthropic", services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request("Anthropic", "single"), CancellationToken.None));
        loss.Lost.ShouldBe(1);
        handler.Admissions.ShouldBeEmpty();
        using var read = fixture.BeginScope();
        var claim = await read.Resolve<CodeSpaceDbContext>().BudgetReservation.SingleAsync(r => r.WorkflowRunId == scenario.RunId);
        claim.State.ShouldBe(BudgetReservationStates.Indeterminate);
        claim.SettledUsd.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Accounting_persistence_failure_cannot_authorize_an_additional_POST(bool admission)
    {
        var scenario = await SeedAsync();
        var interceptor = new RejectPhysicalWrite(admission);
        using var scope = InterceptedPhysicalScope(interceptor);
        using var handler = new ProtocolHandler("Anthropic", new[] { Structured("Anthropic", "{}", 10), Success("Anthropic") }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        using (Push(scope, scenario)) await Should.ThrowAsync<PhysicalLlmAccountingException>(() => Decorated("Anthropic", services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request("Anthropic", "schema-reask"), CancellationToken.None));
        interceptor.Rejected.ShouldBeGreaterThan(0);
        handler.Admissions.Count.ShouldBe(admission ? 0 : 1);
        using var read = fixture.BeginScope();
        var db = read.Resolve<CodeSpaceDbContext>();
        var claims = await db.BudgetReservation.AsNoTracking().Where(r => r.WorkflowRunId == scenario.RunId).ToArrayAsync();
        claims.Length.ShouldBe(admission ? 0 : 1);
        if (!admission)
        {
            claims[0].State.ShouldBe(BudgetReservationStates.Indeterminate);
            claims[0].SettledUsd.ShouldBeNull();
            (await db.WorkflowRunModelCallAttempt.SingleAsync(a => a.WorkflowRunId == scenario.RunId)).CostAmount.ShouldBeNull();
        }
    }

    [Fact]
    public async Task An_oversized_response_keeps_the_sent_receipt_unknown_and_does_not_retry()
    {
        var scenario = await SeedAsync();
        using var handler = new ProtocolHandler("Anthropic", new[] { Success("Anthropic") }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler, o => o.MaxEnvelopeBytes = 32);
        using var scope = fixture.BeginScope();
        using (Push(scope, scenario)) await Should.ThrowAsync<PhysicalLlmAccountingException>(() => Decorated("Anthropic", services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request("Anthropic", "single"), CancellationToken.None));
        handler.Admissions.Count.ShouldBe(1);
        using var read = fixture.BeginScope();
        var attempt = await read.Resolve<CodeSpaceDbContext>().WorkflowRunModelCallAttempt.SingleAsync(a => a.WorkflowRunId == scenario.RunId);
        attempt.ErrorCode.ShouldBe("observation-limit");
        attempt.CostAmount.ShouldBeNull();
        (await read.Resolve<CodeSpaceDbContext>().BudgetReservation.SingleAsync(a => a.WorkflowRunId == scenario.RunId)).State.ShouldBe(BudgetReservationStates.Indeterminate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_observation_timeout_or_cancellation_keeps_the_sent_claim(bool cancel)
    {
        var scenario = await SeedAsync();
        using var content = new BlockedEnvelopeContent();
        using var handler = new ProtocolHandler("Anthropic", new[] { new Reply(HttpStatusCode.OK, "", Content: () => content) }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler, o => o.EnvelopeTimeout = cancel ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(30));
        using var scope = fixture.BeginScope();
        using var cancellation = new CancellationTokenSource();
        using (Push(scope, scenario))
        {
            var pending = Decorated("Anthropic", services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request("Anthropic", "single"), cancellation.Token);
            await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) cancellation.Cancel();
            if (cancel) await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            else await Should.ThrowAsync<PhysicalLlmAccountingException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        handler.Admissions.Count.ShouldBe(1);
        using var read = fixture.BeginScope();
        var db = read.Resolve<CodeSpaceDbContext>();
        var attempt = await db.WorkflowRunModelCallAttempt.SingleAsync(a => a.WorkflowRunId == scenario.RunId);
        attempt.ErrorCode.ShouldBe(cancel ? "observation-cancelled" : "observation-limit");
        attempt.CostAmount.ShouldBeNull();
        (await db.BudgetReservation.SingleAsync(a => a.WorkflowRunId == scenario.RunId)).State.ShouldBe(BudgetReservationStates.Indeterminate);
    }

    [Fact]
    public async Task A_physical_identity_replay_cannot_change_its_requested_model_or_frozen_price_intent()
    {
        var scenario = await SeedAsync();
        var input = NativeAdmission(scenario);
        using var first = fixture.BeginScope();
        var ledger = first.Resolve<IPhysicalLlmInvocationLedger>();
        (await ledger.AdmitPhysicalAsync(input, CancellationToken.None)).Admitted.ShouldBeTrue();
        var changed = await ledger.AdmitPhysicalAsync(input with { RequestedModel = "different-model" }, CancellationToken.None);
        changed.Admitted.ShouldBeFalse();
        changed.IsReplay.ShouldBeTrue();
    }

    private static PhysicalLlmAdmission NativeAdmission(Scenario scenario) => new()
    {
        InvocationId = Guid.NewGuid(), LogicalCallId = Guid.NewGuid(), CandidateId = Guid.NewGuid(), CandidateOrdinal = 1,
        RunId = scenario.RunId, TeamId = scenario.TeamId, Purpose = "physical-receipt-test", Provider = "synthetic", RequestedModel = "fixture-model", EstimateUsd = 1m, CapUsd = 1m,
        PricingSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, ModelPrice> { ["fixture-model"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 1m } }), PricingVersion = "fixture-price-v1",
    };

    private ILifetimeScope InterceptedPhysicalScope(params IInterceptor[] interceptors) => fixture.BeginScope(builder =>
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(interceptors).Options;
        builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    });

    private sealed class LosePhysicalCommitAck(bool admission) : DbTransactionInterceptor
    {
        public int Lost { get; private set; }
        private int _commits;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            _commits++;
            if (_commits == (admission ? 1 : 2)) { Lost++; throw new IOException("synthetic physical accounting commit ACK loss"); }
            return Task.CompletedTask;
        }
    }

    private sealed class RejectPhysicalWrite(bool admission) : DbCommandInterceptor
    {
        public int Rejected { get; private set; }
        private void Reject(DbCommand command)
        {
            if (admission ? command.CommandText.Contains("INSERT INTO workflow_run_model_call_attempt", StringComparison.Ordinal) : command.CommandText.StartsWith("UPDATE workflow_run_model_call_attempt", StringComparison.Ordinal))
            { Rejected++; throw new IOException("synthetic physical receipt write failure"); }
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Reject(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Reject(command); return ValueTask.FromResult(result); }
    }

    private sealed class UnregisteredFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RejectCommitReadback(LosePhysicalCommitAck loss) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (loss.Lost > 0 && command.CommandText.Contains("FROM workflow_run_model_call_attempt", StringComparison.Ordinal)) throw new IOException("synthetic unavailable commit readback");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockedEnvelopeContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await never.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class AccountingFailureObserver : DelegatingHandler
    {
        public Exception? Error { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try { return await base.SendAsync(request, cancellationToken); }
            catch (Exception ex) { Error = ex; throw; }
        }
    }

    private sealed class ThrowingDisposeContent(bool block) : HttpContent
    {
        private int _disposals;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (block) await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
            else await stream.WriteAsync(new byte[128], cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Interlocked.Increment(ref _disposals) == 1) throw new IOException("synthetic content cleanup failure");
        }
    }
}
