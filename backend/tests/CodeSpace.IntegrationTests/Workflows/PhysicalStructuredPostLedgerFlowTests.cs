using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Exceptions;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows;

public sealed partial class PhysicalStructuredPostAccountingFlowTests
{
    [Theory]
    [InlineData("node")]
    [InlineData("iteration")]
    [InlineData("purpose")]
    public async Task A_physical_replay_must_match_its_complete_logical_scope(string changed)
    {
        var scenario = await SeedAsync();
        var input = NativeAdmission(scenario);
        using var scope = fixture.BeginScope();
        var ledger = scope.Resolve<IPhysicalLlmInvocationLedger>();
        await ledger.AdmitPhysicalAsync(input, CancellationToken.None);
        var modified = changed switch { "node" => input with { NodeId = "different-node" }, "iteration" => input with { IterationKey = "different-iteration" }, _ => input with { Purpose = "different-purpose" } };
        var replay = await ledger.AdmitPhysicalAsync(modified, CancellationToken.None);
        replay.Admitted.ShouldBeFalse();
        replay.IsReplay.ShouldBeTrue();
        (await ledger.AdmitPhysicalAsync(input, CancellationToken.None)).Admitted.ShouldBeTrue();
    }

    [Fact]
    public async Task An_empty_late_receipt_preserves_known_HTTP_facts_and_conflicting_metadata_is_refused()
    {
        var scenario = await SeedAsync();
        var input = NativeAdmission(scenario);
        using var scope = fixture.BeginScope();
        var ledger = scope.Resolve<IPhysicalLlmInvocationLedger>();
        await ledger.AdmitPhysicalAsync(input, CancellationToken.None);
        var known = new PhysicalLlmSettlement { InvocationId = input.InvocationId, RunId = scenario.RunId, TeamId = scenario.TeamId, ObservedModel = "fixture-model", HttpStatusCode = 200, Status = "Succeeded", Usage = new LlmUsage { InputTokens = 10, FinishReason = "end_turn", IsPartial = true } };
        await ledger.SettlePhysicalAsync(known, CancellationToken.None);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var first = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(a => a.Id == input.InvocationId);
        await ledger.SettlePhysicalAsync(new PhysicalLlmSettlement { InvocationId = input.InvocationId, RunId = scenario.RunId, TeamId = scenario.TeamId }, CancellationToken.None);
        var retained = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(a => a.Id == input.InvocationId);
        retained.HttpStatusCode.ShouldBe(200);
        retained.Status.ShouldBe("Succeeded");
        retained.FinishReason.ShouldBe("end_turn");
        retained.CompletedAt.ShouldBe(first.CompletedAt);
        retained.CostAmount.ShouldBeNull();
        await Should.ThrowAsync<PhysicalLlmAccountingException>(() => ledger.SettlePhysicalAsync(known with { HttpStatusCode = 500 }, CancellationToken.None));
        await Should.ThrowAsync<PhysicalLlmAccountingException>(() => ledger.SettlePhysicalAsync(known with { Usage = known.Usage with { FinishReason = "length" } }, CancellationToken.None));
        await Should.ThrowAsync<PhysicalLlmAccountingException>(() => ledger.SettlePhysicalAsync(known with { Status = "Failed" }, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_native_candidate_provider_and_transport_cannot_be_rewritten(bool provider)
    {
        var scenario = await SeedAsync();
        var input = NativeAdmission(scenario);
        using var scope = fixture.BeginScope();
        await scope.Resolve<IPhysicalLlmInvocationLedger>().AdmitPhysicalAsync(input, CancellationToken.None);
        var rows = scope.Resolve<CodeSpaceDbContext>().WorkflowRunModelCallAttempt.Where(a => a.Id == input.InvocationId);
        if (provider) await Should.ThrowAsync<Npgsql.PostgresException>(() => rows.ExecuteUpdateAsync(s => s.SetProperty(a => a.EffectiveProvider, "different-provider")));
        else await Should.ThrowAsync<Npgsql.PostgresException>(() => rows.ExecuteUpdateAsync(s => s.SetProperty(a => a.TransportKind, "different-transport")));
    }

    [Fact]
    public async Task Eight_independent_native_admissions_share_one_atomic_run_cap()
    {
        var scenario = await SeedAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = fixture.BeginScope();
            await start.Task;
            return await scope.Resolve<IPhysicalLlmInvocationLedger>().AdmitPhysicalAsync(NativeAdmission(scenario), CancellationToken.None);
        }).ToArray();
        start.SetResult();
        var outcomes = await Task.WhenAll(attempts);
        outcomes.Count(o => o.Admitted).ShouldBe(1);
        outcomes.Count(o => !o.Admitted).ShouldBe(7);
        using var read = fixture.BeginScope();
        (await read.Resolve<CodeSpaceDbContext>().WorkflowRunModelCallAttempt.CountAsync(a => a.WorkflowRunId == scenario.RunId)).ShouldBe(1);
        (await read.Resolve<IBudgetLedger>().CommittedUsdAsync(scenario.RunId, scenario.TeamId, CancellationToken.None)).ShouldBe(1m);
    }

    [Fact]
    public async Task Concurrent_late_receipts_settle_one_invocation_once_and_conflicting_facts_are_refused()
    {
        var scenario = await SeedAsync();
        var input = NativeAdmission(scenario);
        using (var admission = fixture.BeginScope()) await admission.Resolve<IPhysicalLlmInvocationLedger>().AdmitPhysicalAsync(input, CancellationToken.None);
        var receipt = new PhysicalLlmSettlement { InvocationId = input.InvocationId, RunId = scenario.RunId, TeamId = scenario.TeamId, ObservedModel = "fixture-model", Usage = new LlmUsage { InputTokens = 10, OutputTokens = 5 }, Status = "Succeeded" };
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var worker = fixture.BeginScope();
            await worker.Resolve<IPhysicalLlmInvocationLedger>().SettlePhysicalAsync(receipt, CancellationToken.None);
        }));
        using var conflict = fixture.BeginScope();
        await Should.ThrowAsync<PhysicalLlmAccountingException>(() => conflict.Resolve<IPhysicalLlmInvocationLedger>().SettlePhysicalAsync(receipt with { Usage = receipt.Usage with { OutputTokens = 6 } }, CancellationToken.None));
        var db = conflict.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRunModelCallAttempt.SingleAsync(a => a.Id == input.InvocationId)).CostAmount.ShouldBe(0.000015m);
        (await db.BudgetReservation.SingleAsync(a => a.WorkflowRunId == scenario.RunId)).SettledUsd.ShouldBe(0.000015m);
    }

    [Fact]
    public async Task An_explicit_partial_receipt_cannot_be_completed_by_an_empty_late_observation()
    {
        var scenario = await SeedAsync();
        var input = NativeAdmission(scenario);
        using var scope = fixture.BeginScope();
        var ledger = scope.Resolve<IPhysicalLlmInvocationLedger>();
        await ledger.AdmitPhysicalAsync(input, CancellationToken.None);
        var partial = new PhysicalLlmSettlement { InvocationId = input.InvocationId, RunId = scenario.RunId, TeamId = scenario.TeamId, ObservedModel = "fixture-model", Usage = new LlmUsage { InputTokens = 10, OutputTokens = 5, IsPartial = true } };
        await ledger.SettlePhysicalAsync(partial, CancellationToken.None);
        await ledger.SettlePhysicalAsync(partial with { Usage = LlmUsage.None }, CancellationToken.None);
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.BudgetReservation.AsNoTracking().SingleAsync(r => r.WorkflowRunId == scenario.RunId)).SettledUsd.ShouldBeNull();
        await ledger.SettlePhysicalAsync(partial with { Usage = partial.Usage with { IsPartial = false } }, CancellationToken.None);
        (await db.BudgetReservation.AsNoTracking().SingleAsync(r => r.WorkflowRunId == scenario.RunId)).SettledUsd.ShouldBe(0.000015m);
    }

    [Theory]
    [InlineData("0.0000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("0.00000000000000000000001", null)]
    public async Task Late_usage_after_reconciliation_uses_frozen_prices_and_never_rounds_unknown_to_zero(string priceText, string? expectedText)
    {
        var scenario = await SeedAsync();
        var price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);
        var input = NativeAdmission(scenario) with { PricingSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, ModelPrice> { ["fixture-model"] = new() { InputPerMillionUsd = price, OutputPerMillionUsd = price } }) };
        using (var admission = fixture.BeginScope()) await admission.Resolve<IPhysicalLlmInvocationLedger>().AdmitPhysicalAsync(input, CancellationToken.None);
        using (var unknown = fixture.BeginScope()) await unknown.Resolve<IPhysicalLlmInvocationLedger>().SettlePhysicalAsync(new PhysicalLlmSettlement { InvocationId = input.InvocationId, RunId = scenario.RunId, TeamId = scenario.TeamId }, CancellationToken.None);
        using (var recovery = fixture.BeginScope()) await recovery.Resolve<IBudgetLedger>().ReconcileDanglingAsync("llm:physical-post", 1000, CancellationToken.None);
        using (var late = fixture.BeginScope())
        {
            var receipt = new PhysicalLlmSettlement { InvocationId = input.InvocationId, RunId = scenario.RunId, TeamId = scenario.TeamId, ObservedModel = "fixture-model", Usage = new LlmUsage { InputTokens = 1, OutputTokens = 0 }, Status = "Succeeded", HttpStatusCode = 200 };
            await late.Resolve<IPhysicalLlmInvocationLedger>().SettlePhysicalAsync(receipt, CancellationToken.None);
            await late.Resolve<IPhysicalLlmInvocationLedger>().SettlePhysicalAsync(receipt, CancellationToken.None);
        }
        using var read = fixture.BeginScope();
        var db = read.Resolve<CodeSpaceDbContext>();
        var attempt = await db.WorkflowRunModelCallAttempt.SingleAsync(a => a.Id == input.InvocationId);
        var claim = await db.BudgetReservation.SingleAsync(r => r.Id == attempt.BudgetReservationId);
        var expected = expectedText is null ? (decimal?)null : decimal.Parse(expectedText, System.Globalization.CultureInfo.InvariantCulture);
        attempt.CostAmount.ShouldBe(expected);
        claim.SettledUsd.ShouldBe(expected);
        (await read.Resolve<IBudgetLedger>().CommittedUsdAsync(scenario.RunId, scenario.TeamId, CancellationToken.None)).ShouldBe(expected ?? 1m);
        attempt.InputTokens.ShouldBe(1);
        attempt.OutputTokens.ShouldBe(0);
    }

    [Fact]
    public async Task Foreign_team_cannot_admit_settle_or_retarget_a_native_receipt()
    {
        var first = await SeedAsync();
        var foreign = await SeedAsync();
        var input = NativeAdmission(first);
        using var scope = fixture.BeginScope();
        var ledger = scope.Resolve<IPhysicalLlmInvocationLedger>();
        await Should.ThrowAsync<PhysicalLlmAccountingException>(() => ledger.AdmitPhysicalAsync(input with { TeamId = foreign.TeamId }, CancellationToken.None));
        await ledger.AdmitPhysicalAsync(input, CancellationToken.None);
        await Should.ThrowAsync<PhysicalLlmAccountingException>(() => ledger.SettlePhysicalAsync(new PhysicalLlmSettlement { InvocationId = input.InvocationId, RunId = first.RunId, TeamId = foreign.TeamId, Usage = new LlmUsage { InputTokens = 0, OutputTokens = 0 } }, CancellationToken.None));
        var db = scope.Resolve<CodeSpaceDbContext>();
        await Should.ThrowAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_run_model_call_attempt SET team_id = {foreign.TeamId} WHERE id = {input.InvocationId}"));
        (await db.BudgetReservation.AsNoTracking().SingleAsync(r => r.WorkflowRunId == first.RunId)).SettledUsd.ShouldBeNull();
    }
}
