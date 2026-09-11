using System.Text.Json;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Llm.Exceptions;
using Microsoft.Extensions.Options;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>Bounds wire observation, independently of task semantics and provider output policy.</summary>
public sealed class PhysicalLlmObservationOptions
{
    public long MaxEnvelopeBytes { get; set; } = 1024 * 1024;
    public TimeSpan EnvelopeTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Mutable per-logical-call marker riding the reused <see cref="HttpRequestMessage.Options"/> across a Polly retry (the SAME request instance is re-sent on each attempt): whether an EARLIER attempt of this request was sent and produced no response, so the provider may already have billed it. Set by <see cref="PhysicalLlmAccountingHandler"/> (the innermost handler — it sees every attempt); read by <see cref="LlmHttpTransport"/> when the last attempt's outcome becomes the surfaced exception.</summary>
internal sealed class AmbiguousSendMarker { public bool Occurred { get; set; } }

/// <summary>Registered INSIDE retry: each invocation admits, sends and settles its own durable identity.</summary>
public sealed class PhysicalLlmAccountingHandler(IOptions<PhysicalLlmObservationOptions> options) : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<AmbiguousSendMarker> AmbiguousSendKey = new("CodeSpace.LlmAmbiguousSend/v1");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(PhysicalLlmCallContext.DispatchKey, out var dispatch))
            return await SendTrackingAmbiguityAsync(request, cancellationToken).ConfigureAwait(false);

        var candidate = dispatch.Candidate;
        var operation = candidate.Operation;
        var scope = operation.Scope;
        if (operation.PersistenceFailed) throw new PhysicalLlmAccountingException("An earlier physical usage receipt could not be persisted; no additional POST was admitted.");
        var policy = options.Value;
        if (policy.MaxEnvelopeBytes <= 0 || policy.EnvelopeTimeout <= TimeSpan.Zero || policy.EnvelopeTimeout > TimeSpan.FromMinutes(5))
            throw new PhysicalLlmAccountingException("The physical response observation policy is invalid.");
        if (request.Content?.Headers.ContentLength is not { } bytes)
            throw new PhysicalLlmAccountingException("A capped structured POST requires a buffered request with an observed byte length.");
        var estimate = LlmUsageCost.Usd(candidate.Model, new LlmUsage { InputTokens = (int)Math.Min(bytes / 3 + 64, int.MaxValue), OutputTokens = candidate.MaxOutputTokens is > 0 ? candidate.MaxOutputTokens : 8192 }, candidate.Prices);
        if (estimate is null) throw new UnpricedModelUnderCapException(candidate.Model, scope.CapUsd!.Value, "physical structured POST");
        var invocation = new PhysicalLlmAdmission
        {
            InvocationId = Guid.NewGuid(), LogicalCallId = operation.Id, CandidateId = candidate.Id, CandidateOrdinal = candidate.Ordinal,
            RunId = scope.RunId, TeamId = scope.TeamId, NodeId = scope.NodeId, IterationKey = scope.IterationKey, Purpose = scope.Kind,
            Provider = candidate.Redact(candidate.Provider)!, RequestedModel = candidate.Redact(candidate.Model)!, EstimateUsd = estimate.Value, CapUsd = scope.CapUsd!.Value,
            PricingSnapshotJson = candidate.PricingSnapshotJson, PricingVersion = candidate.PricingVersion,
        };
        BudgetAdmission admission;
        try { admission = await operation.Ledger.AdmitPhysicalAsync(invocation, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new PhysicalLlmAccountingException("Physical admission could not be confirmed; the provider POST was not sent.", ex); }
        if (!admission.Admitted || admission.IsReplay) throw new LlmBudgetExceededException(scope.Kind, admission.CommittedUsd, scope.CapUsd!.Value, admission.IsReplay ? "A physical invocation identity cannot authorize a second Send." : admission.Reason);

        var receipt = new PhysicalLlmSettlement { InvocationId = invocation.InvocationId, RunId = scope.RunId, TeamId = scope.TeamId, Status = "Indeterminate" };
        HttpResponseMessage? response = null;
        try
        {
            response = await SendTrackingAmbiguityAsync(request, cancellationToken).ConfigureAwait(false);
            receipt = receipt with { HttpStatusCode = (int)response.StatusCode, Status = response.IsSuccessStatusCode ? "Succeeded" : "Failed" };
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            observation.CancelAfter(policy.EnvelopeTimeout);
            try
            {
                // HttpContent owns one bounded replay buffer; the original bytes remain available to the provider's
                // parser. No raw body, headers or credentials are written to the accounting plane.
                await response.Content.LoadIntoBufferAsync(policy.MaxEnvelopeBytes, observation.Token).ConfigureAwait(false);
                var stream = await response.Content.ReadAsStreamAsync(observation.Token).ConfigureAwait(false);
                try
                {
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: observation.Token).ConfigureAwait(false);
                    var envelope = dispatch.ReadEnvelope(document.RootElement);
                    var model = ObservedLlmModel.FromWire(envelope.Model, candidate.CredentialRedactor, scope.CaptureRedactor);
                    receipt = receipt with { ObservedModel = model, Usage = envelope.Usage with { FinishReason = candidate.Redact(envelope.Usage.FinishReason) } };
                }
                finally { if (stream.CanSeek) stream.Position = 0; }
            }
            catch (JsonException) { receipt = receipt with { ErrorCode = "malformed-envelope" }; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                receipt = receipt with { ErrorCode = cancellationToken.IsCancellationRequested ? "observation-cancelled" : "observation-limit", Status = "Indeterminate" };
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("Physical response observation was cancelled after sending.", ex, cancellationToken);
                throw new PhysicalLlmAccountingException("The sent POST exceeded the response observation size or time bound; its cost remains unknown.", ex);
            }
            return response;
        }
        catch (Exception primary)
        {
            try { response?.Dispose(); }
            catch (Exception cleanup) { primary.Data["physical_response_cleanup_failure"] = cleanup.GetType().FullName; }
            throw;
        }
        finally
        {
            var usage = receipt.Usage with { IsPartial = !receipt.Usage.HasCompleteTokenCounts };
            try
            {
                using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await operation.Ledger.SettlePhysicalAsync(receipt, settlement.Token).ConfigureAwait(false);
            }
            catch
            {
                operation.PersistenceFailed = true;
                usage = usage with { IsPartial = true };
            }
            operation.Observations.Enqueue(new PhysicalLlmCallContext.Observation(candidate.Id, receipt.ObservedModel, usage));
        }
    }

    /// <summary>Send one attempt, marking <see cref="AmbiguousSendKey"/> when it throws a fault that does not prove the request never reached a server (see <see cref="LlmBudgetGuard.NeverReachedAServer"/>) — the same request rides every retry, so a LATER attempt's own clean outcome must not be read as proof this earlier one bought nothing.</summary>
    private async Task<HttpResponseMessage> SendTrackingAmbiguityAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await base.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (!LlmBudgetGuard.NeverReachedAServer(ex))
        {
            if (!request.Options.TryGetValue(AmbiguousSendKey, out var marker))
                request.Options.Set(AmbiguousSendKey, marker = new AmbiguousSendMarker());
            marker.Occurred = true;
            throw;
        }
    }
}
