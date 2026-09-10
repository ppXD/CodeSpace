using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Exceptions;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Budget;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Budget;

public sealed partial class BudgetLedger
{
    public const string PhysicalLlmSource = "structured-post/v1";
    private const string PhysicalLlmKind = "llm:physical-post/v1";

    public async Task<BudgetAdmission> AdmitPhysicalAsync(PhysicalLlmAdmission input, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input.EstimateUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(input.CapUsd);
        if (input.InvocationId == Guid.Empty || input.LogicalCallId == Guid.Empty || input.CandidateId == Guid.Empty || input.CandidateOrdinal <= 0)
            throw new PhysicalLlmAccountingException("Physical admission requires complete server-owned causal identities.");
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await TakeAdmissionLocksAsync(input.RunId, input.TeamId, input.CapUsd, cancellationToken).ConfigureAwait(false);
        if (!await _db.WorkflowRun.AsNoTracking().AnyAsync(r => r.Id == input.RunId && r.TeamId == input.TeamId, cancellationToken).ConfigureAwait(false))
            throw new PhysicalLlmAccountingException("The physical admission workflow scope is unavailable.");
        var existing = await _db.WorkflowRunModelCallAttempt.AsNoTracking().SingleOrDefaultAsync(a => a.Id == input.InvocationId, cancellationToken).ConfigureAwait(false);
        var committed = await CommittedInTxAsync(input.RunId, input.TeamId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var claim = await _db.BudgetReservation.AsNoTracking().SingleOrDefaultAsync(r => r.Id == existing.BudgetReservationId, cancellationToken).ConfigureAwait(false);
            var logical = await _db.WorkflowRunModelCall.AsNoTracking().SingleOrDefaultAsync(c => c.Id == existing.ModelCallId, cancellationToken).ConfigureAwait(false);
            var matches = Matches(existing, claim, input) && MatchesLogical(logical, input);
            return new BudgetAdmission(matches, matches ? claim!.Id : null, committed, input.CapUsd, matches ? "physical-invocation-already-admitted" : "physical-invocation-intent-mismatch") { IsReplay = true, ReservationState = matches ? claim!.State : null };
        }
        if (committed + input.EstimateUsd > input.CapUsd)
            return new BudgetAdmission(false, null, committed, input.CapUsd, "The next physical POST would exceed the run's admission commitments.") { RefusedGrain = BudgetCapGrain.Run };

        // P15-5b-ii: the physical POST mints a reservation like any other admission, so it answers to the team's
        // standing cap too — otherwise this path would be the one way to spend past it.
        if (await TeamRefusalAsync(input.TeamId, input.EstimateUsd, input.CapUsd, committed, cancellationToken).ConfigureAwait(false) is { } teamRefusal) return teamRefusal;

        var call = await _db.WorkflowRunModelCall.AsNoTracking().SingleOrDefaultAsync(c => c.Id == input.LogicalCallId, cancellationToken).ConfigureAwait(false);
        if (call is not null && !MatchesLogical(call, input))
            throw new PhysicalLlmAccountingException("The logical model call does not match the physical admission scope.");
        var candidates = await _db.WorkflowRunModelCallAttempt.AsNoTracking().Where(a => a.ModelCallId == input.LogicalCallId && (a.CandidateId == input.CandidateId || a.CandidateOrdinal == input.CandidateOrdinal))
            .Select(a => new { a.CandidateId, a.CandidateOrdinal, a.CandidateModel, a.EffectiveProvider, a.PricingVersion }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Any(a => a.CandidateId != input.CandidateId || a.CandidateOrdinal != input.CandidateOrdinal || a.CandidateModel != input.RequestedModel || a.EffectiveProvider != input.Provider || a.PricingVersion != input.PricingVersion))
            throw new PhysicalLlmAccountingException("The provider candidate's immutable identity does not match.");
        var ordinal = (await _db.WorkflowRunModelCallAttempt.Where(a => a.ModelCallId == input.LogicalCallId).MaxAsync(a => (int?)a.AttemptOrdinal, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
        var now = DateTimeOffset.UtcNow;
        var reservation = new BudgetReservation
        {
            Id = Guid.NewGuid(), TeamId = input.TeamId, WorkflowRunId = input.RunId, Kind = PhysicalLlmKind, ScopeKey = input.InvocationId.ToString("N"),
            State = BudgetReservationStates.Indeterminate, ReservedUsd = input.EstimateUsd, CapUsd = input.CapUsd, PriceVersion = input.PricingVersion,
        };
        var attempt = new WorkflowRunModelCallAttempt
        {
            Id = input.InvocationId, TeamId = input.TeamId, WorkflowRunId = input.RunId, ModelCallId = input.LogicalCallId, AttemptOrdinal = ordinal,
            CandidateId = input.CandidateId, CandidateOrdinal = input.CandidateOrdinal, CandidateModel = input.RequestedModel, BudgetReservationId = reservation.Id,
            PricingSnapshotJson = input.PricingSnapshotJson, PricingVersion = input.PricingVersion, EffectiveProvider = input.Provider,
            TransportKind = "http-json/v1", Status = "Running", CaptureSource = PhysicalLlmSource, CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial,
            StartedAt = now, UsageIsPartial = true, UnavailableFigures = UnavailableFigures(null),
        };
        if (call is null)
            _db.WorkflowRunModelCall.Add(new WorkflowRunModelCall
            {
                Id = input.LogicalCallId, TeamId = input.TeamId, WorkflowRunId = input.RunId, NodeId = input.NodeId, IterationKey = input.IterationKey,
                Purpose = input.Purpose, RequestedProvider = input.Provider, RequestedModel = input.RequestedModel,
                SourceKind = PhysicalLlmSource, SourceCorrelationId = input.LogicalCallId, CaptureSource = PhysicalLlmSource, CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial,
            });
        _db.BudgetReservation.Add(reservation);
        _db.WorkflowRunModelCallAttempt.Add(attempt);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        try { await tx.CommitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            // Commit ACK may have been lost. Re-read this PREMINTED identity after disposing the uncertain
            // transaction; only a matching committed receipt can authorize this still-unsent invocation.
            await tx.DisposeAsync().ConfigureAwait(false);
            var confirmed = await _db.WorkflowRunModelCallAttempt.AsNoTracking().SingleOrDefaultAsync(a => a.Id == input.InvocationId, cancellationToken).ConfigureAwait(false);
            var claim = await _db.BudgetReservation.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reservation.Id, cancellationToken).ConfigureAwait(false);
            var logical = await _db.WorkflowRunModelCall.AsNoTracking().SingleOrDefaultAsync(c => c.Id == input.LogicalCallId, cancellationToken).ConfigureAwait(false);
            if (confirmed is null || !Matches(confirmed, claim, input) || !MatchesLogical(logical, input)) throw;
        }
        return new BudgetAdmission(true, reservation.Id, committed + input.EstimateUsd, input.CapUsd, null) { ReservationState = BudgetReservationStates.Indeterminate };
    }

    public async Task SettlePhysicalAsync(PhysicalLlmSettlement input, CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await TakeRunLockAsync(input.RunId, cancellationToken).ConfigureAwait(false);
        var attempt = await _db.WorkflowRunModelCallAttempt.AsNoTracking().SingleOrDefaultAsync(a => a.Id == input.InvocationId && a.TeamId == input.TeamId && a.WorkflowRunId == input.RunId && a.CaptureSource == PhysicalLlmSource, cancellationToken).ConfigureAwait(false)
            ?? throw new PhysicalLlmAccountingException("The physical usage receipt is missing or outside its workflow scope.");
        var prices = JsonSerializer.Deserialize<Dictionary<string, ModelPrice>>(attempt.PricingSnapshotJson!);
        var usage = input.Usage;
        var observedModel = input.ObservedModel;
        var finishReason = usage.FinishReason is { Length: <= 100 } reason ? reason : null;
        if (attempt.EffectiveModel is { } previousModel && observedModel is { } nextModel && previousModel != nextModel
            || attempt.InputTokens is { } previousInput && usage.InputTokens is { } nextInput && previousInput != nextInput
            || attempt.OutputTokens is { } previousOutput && usage.OutputTokens is { } nextOutput && previousOutput != nextOutput
            || attempt.HttpStatusCode is { } previousHttp && input.HttpStatusCode is { } nextHttp && previousHttp != nextHttp
            || attempt.FinishReason is { } previousFinish && finishReason is { } nextFinish && previousFinish != nextFinish
            || attempt.ErrorCode is { } previousError && input.ErrorCode is { } nextError && previousError != nextError
            || attempt.Status is "Succeeded" or "Failed" && input.Status is { } nextStatus && attempt.Status != nextStatus)
            throw new PhysicalLlmAccountingException("A late physical usage receipt conflicts with previously observed provider facts.");
        observedModel ??= attempt.EffectiveModel;
        usage = usage with { InputTokens = usage.InputTokens ?? (int?)attempt.InputTokens, OutputTokens = usage.OutputTokens ?? (int?)attempt.OutputTokens, IsPartial = usage.IsPartial || (attempt.UsageIsPartial && !usage.HasCompleteTokenCounts) };
        var cost = FrozenCost(observedModel, usage, prices);
        if (attempt.CostAmount is not null)
        {
            if (cost is not null && cost != attempt.CostAmount) throw new PhysicalLlmAccountingException("A confirmed physical cost receipt cannot be replaced.");
            return;
        }
        var now = DateTimeOffset.UtcNow < attempt.StartedAt ? attempt.StartedAt : DateTimeOffset.UtcNow;
        // Missing late metadata is absence of new evidence. Keep the first completion observation and
        // previously observed wire facts even while usage or actual cost remains unresolved.
        var status = input.Status ?? (attempt.Status == "Running" ? "Indeterminate" : attempt.Status);
        var httpStatus = input.HttpStatusCode ?? attempt.HttpStatusCode;
        var errorCode = input.ErrorCode ?? attempt.ErrorCode;
        finishReason ??= attempt.FinishReason;
        var completedAt = attempt.CompletedAt ?? now;
        var unavailable = UnavailableFigures(cost);
        await _db.WorkflowRunModelCallAttempt.Where(a => a.Id == input.InvocationId && a.TeamId == input.TeamId && a.WorkflowRunId == input.RunId && a.CostAmount == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.EffectiveModel, observedModel).SetProperty(a => a.InputTokens, (long?)usage.InputTokens).SetProperty(a => a.OutputTokens, (long?)usage.OutputTokens)
                .SetProperty(a => a.UsageIsPartial, usage.IsPartial)
                .SetProperty(a => a.CostAmount, cost).SetProperty(a => a.CostCurrency, cost != null ? "USD" : null).SetProperty(a => a.Status, status)
                .SetProperty(a => a.HttpStatusCode, httpStatus).SetProperty(a => a.ErrorCode, errorCode).SetProperty(a => a.FinishReason, finishReason)
                .SetProperty(a => a.CompletedAt, completedAt).SetProperty(a => a.UnavailableFigures, unavailable).SetProperty(a => a.LastModifiedDate, now), cancellationToken).ConfigureAwait(false);
        await _db.BudgetReservation.Where(r => r.Id == attempt.BudgetReservationId && r.TeamId == input.TeamId && r.WorkflowRunId == input.RunId && r.State != BudgetReservationStates.Settled)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, cost != null ? BudgetReservationStates.Settled : BudgetReservationStates.Indeterminate)
                .SetProperty(r => r.SettledUsd, cost).SetProperty(r => r.LastModifiedDate, now), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static decimal? FrozenCost(string? model, LlmUsage usage, Dictionary<string, ModelPrice>? prices)
    {
        if (model is null || prices is null) return null;
        var frozen = new Dictionary<string, ModelPrice>(prices, StringComparer.OrdinalIgnoreCase);
        // Do not let the pricer fall back to a later environment/default table when the frozen snapshot lacks a key.
        return frozen.ContainsKey(model.Trim()) ? LlmUsageCost.Usd(model, usage, frozen) : null;
    }

    private static bool Matches(WorkflowRunModelCallAttempt attempt, BudgetReservation? claim, PhysicalLlmAdmission input) =>
        attempt.TeamId == input.TeamId && attempt.WorkflowRunId == input.RunId && attempt.ModelCallId == input.LogicalCallId && attempt.CandidateId == input.CandidateId
        && attempt.CandidateOrdinal == input.CandidateOrdinal && attempt.CandidateModel == input.RequestedModel && attempt.PricingVersion == input.PricingVersion && attempt.EffectiveProvider == input.Provider
        && JsonEqual(attempt.PricingSnapshotJson, input.PricingSnapshotJson)
        && claim is not null && claim.TeamId == input.TeamId && claim.WorkflowRunId == input.RunId && claim.Kind == PhysicalLlmKind && claim.ScopeKey == input.InvocationId.ToString("N")
        && claim.ReservedUsd == input.EstimateUsd && claim.CapUsd == input.CapUsd && claim.PriceVersion == input.PricingVersion;

    private static bool MatchesLogical(WorkflowRunModelCall? call, PhysicalLlmAdmission input) => call is not null && call.Id == input.LogicalCallId
        && call.TeamId == input.TeamId && call.WorkflowRunId == input.RunId && call.SourceKind == PhysicalLlmSource && call.SourceCorrelationId == input.LogicalCallId
        && call.NodeId == input.NodeId && call.IterationKey == input.IterationKey && call.Purpose == input.Purpose;

    private static bool JsonEqual(string? left, string right)
    {
        if (left is null) return false;
        using var first = JsonDocument.Parse(left);
        using var second = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(first.RootElement, second.RootElement);
    }

    private static string[] UnavailableFigures(decimal? cost) => ModelCallFigures.Canonical(new[] { ModelCallFigures.ProviderRequestId, ModelCallFigures.CacheReadTokens, ModelCallFigures.CacheWriteTokens, ModelCallFigures.ReasoningTokens, ModelCallFigures.FirstTokenAt }
        .Concat(cost is null ? new[] { ModelCallFigures.CostAmount } : Array.Empty<string>())).ToArray();
}
