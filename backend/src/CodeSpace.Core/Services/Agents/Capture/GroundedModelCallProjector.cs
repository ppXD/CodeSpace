using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The projector that turns a harness's OWN model-call record into a row of the model-call plane 0124/0130 defines —
/// a logical call with its one physical attempt, in the same two tables a workflow LLM node's calls land in.
///
/// <para><b>What it closes.</b> A node's calls reach that plane because they pass through <c>ILLMClient</c> and a
/// recording decorator; a harness CLI's own calls never touch either, so the platform keeps one derived per-run token
/// aggregate and nothing that says WHICH model did what at WHAT cost. This projector reads the frames the CLI printed
/// about its own calls and makes each one a row, so the same question has one answer shape for both kinds of call. The
/// per-run aggregate is untouched beside it and keeps working exactly as it does.</para>
///
/// <para><b>Why it reads the RECORD and never the line.</b> Same rule as <see cref="GroundedFrameProjector"/>, with
/// more at stake: a cost figure inferred from a line that merely mentions a model is a number that looks measured. So
/// the harness answers a narrow question about its own frame (<see cref="IAgentModelCallFrameReader"/>), a harness
/// with no such frame answers null, and this projector reads <see cref="NativeRecordV1.InlinePayload"/> — the bytes the
/// row will carry — so the fidelity claim and the stored evidence are the same thing rather than two statements that
/// could drift.</para>
///
/// <para><b>Three things it refuses to invent.</b> A figure the record did not state is declared unavailable rather
/// than stored as zero (<see cref="ModelCallFigures"/>). A cost for a model this deployment has no price for is absent
/// and declared, never zero — <see cref="AgentCostPricing"/> already returns null for an unknown model and that null is
/// carried through rather than coalesced. And a retry: the record describes a response, so one attempt is projected per
/// stated response and never a second the frames do not evidence.</para>
///
/// <para><b>Idempotence, and why the ROW ID is derived too.</b> Both the admission key
/// (<see cref="HarnessModelCallProjectionV1.SourceCorrelationId"/>) and the logical call's own identity
/// (<see cref="HarnessModelCallProjectionV1.ModelCallId"/>) come from <see cref="Derived"/> over the harness's own id
/// for the response, never from a delivery position: the capture seam re-delivers frames across a re-attach, and a
/// position-derived value would make one response look like two calls. The row id matters as much as the key, because
/// the plane SKIPS a call it has already admitted while <see cref="NamedEvent"/> cites the id regardless — a freshly
/// minted id would leave that event pointing at a row the skip decided not to write, and a reader joining on it would
/// read the miss as a data gap rather than as the no-op it is. Derived, the id a re-projection cites is the id the
/// existing row already carries.</para>
///
/// <para><b>Why nothing is projected without a workflow run.</b> The model-call plane is keyed to one, so a standalone
/// Agent Run's internal calls can have no row there at all. Projecting them anyway would mint exactly the dangling id
/// above, permanently rather than transiently, so an opening that names no workflow run
/// (<see cref="NativeRecordCaptureHandle.WorkflowRunId"/>) yields nothing here — no row, no cited id, and no named fact
/// in the reduction that folds these events.</para>
/// </summary>
internal static class GroundedModelCallProjector
{
    /// <summary>The source this row was observed through, written to both <c>source_kind</c> and <c>capture_source</c> so "did a decorator see this call or did we read a frame about it" is answerable without inference.</summary>
    internal const string SourceKind = "harness-native-record/v1";

    /// <summary>The transport the call actually went over: the harness's own provider client, whose wire this platform never saw.</summary>
    internal const string TransportKind = "harness-native/v1";

    /// <summary>What the call was FOR, at the only granularity a response record supports: the agent's own inference. A harness record does not say which of the agent's purposes a given call served.</summary>
    internal const string Purpose = "harness-inference/v1";

    /// <summary>
    /// What priced the row: this platform's agent price table. It names the MECHANISM, not a frozen price vintage —
    /// <see cref="AgentCostPricing.PriceTableEnvVar"/> lets an operator override any entry, so a row cannot honestly
    /// claim a dated price list it may not have used.
    /// </summary>
    internal const string PricingVersion = "codespace-agent-model-prices/v1";

    /// <summary>The currency <see cref="AgentCostPricing"/> prices in.</summary>
    internal const string CostCurrency = "USD";

    /// <summary>Width of <c>effective_model</c>. A stated model longer than the column is not projected rather than stored truncated, because a truncated value under an exactness claim is exactly the laundering this plane forbids.</summary>
    private const int ModelWidth = 500;

    /// <summary>Width of <c>finish_reason</c>, refused on the same terms as <see cref="ModelWidth"/>.</summary>
    private const int FinishReasonWidth = 100;

    /// <summary>The event type projected beside the call — its own type, because no <see cref="AgentEventKind"/> means "the harness recorded a model call here".</summary>
    internal const string ModelCalledEventType = AgentNativeRecordPump.EventTypeNamespace + "harness-model-called";

    /// <summary>Schema generation of <see cref="ModelCalledEventType"/>'s payload, one-based and independent of the plane's contract version.</summary>
    internal const int ModelCalledEventSchemaVersion = 1;

    /// <summary>
    /// The model call this captured frame records, or null when none may be projected from it — the answer for a
    /// harness with no model-call reader at all, for an opening that belongs to no workflow run and therefore to no
    /// row of the run-keyed plane, for every frame that is not one of its response records, and for a frame whose
    /// captured bytes could not support a fidelity claim.
    ///
    /// <para>The harness's stated id and model are re-checked HERE and not only in the reader, because this is the one
    /// gate every projected call passes through and a harness is free to build the record directly. An unnamed call
    /// could not be deduplicated and an unnamed model is precisely the row this plane exists to stop being the only one
    /// available, so either absence yields nothing rather than a row with a hole in it.</para>
    /// </summary>
    internal static HarnessModelCallProjectionV1? Project(IAgentHarness harness, NativeRecordCaptureHandle handle, NativeRecordV1 record)
    {
        if (harness is not IAgentModelCallFrameReader reader) return null;
        if (handle.WorkflowRunId is null) return null;
        if (record.InlinePayload is not { } captured) return null;
        if (ClaimableOver(record) is not { } fidelity) return null;
        if (reader.ReadModelCallFrame(captured) is not { } stated) return null;
        if (string.IsNullOrWhiteSpace(stated.CallId) || string.IsNullOrWhiteSpace(stated.Model)) return null;
        if (stated.Model.Length > ModelWidth || stated.FinishReason?.Length > FinishReasonWidth) return null;

        return Projected(handle, record, stated, fidelity);
    }

    /// <summary>The exactly-grounded projection of the same frame, carrying the call it names so the semantic plane and the model-call plane join on a fact both read out of one frame's bytes.</summary>
    internal static AgentSemanticEventV1 NamedEvent(NativeRecordCaptureHandle handle, HarnessModelCallProjectionV1 projection) => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        EventId = Guid.NewGuid(),
        EventType = ModelCalledEventType,
        EventSchemaVersion = ModelCalledEventSchemaVersion,
        SourceNativeRecordIds = new[] { projection.SourceNativeRecordId },
        ExecutionId = handle.ExecutionId,
        ModelCallId = projection.ModelCallId,

        // Ignorable, stated rather than assumed: nothing routes this event yet, and the model-call rows beside it are
        // where the figures live — a reader that cannot route this loses no fact it is accountable for.
        Necessity = SemanticEventNecessity.Ignorable,
        ProjectionQuality = projection.Fidelity,
    };

    /// <summary>
    /// The strongest fidelity the CAPTURED bytes support, or null for bytes that support none — the same reading
    /// <see cref="GroundedFrameProjector"/> takes, because it is the same rule: verbatim bytes back
    /// <see cref="SemanticProjectionQuality.Exact"/>, masked bytes back only
    /// <see cref="SemanticProjectionQuality.RedactedExact"/>, and a frame that was never captured backs nothing that
    /// could have been read "out of it".
    /// </summary>
    private static SemanticProjectionQuality? ClaimableOver(NativeRecordV1 record) => record.Redaction switch
    {
        NativeRecordRedaction.None => SemanticProjectionQuality.Exact,
        NativeRecordRedaction.Masked => SemanticProjectionQuality.RedactedExact,
        _ => null,
    };

    private static HarnessModelCallProjectionV1 Projected(NativeRecordCaptureHandle handle, NativeRecordV1 record, GroundedModelCallFrame stated, SemanticProjectionQuality fidelity)
    {
        var cost = AgentCostPricing.CostUsd(stated.Model, stated.InputTokens, stated.OutputTokens);

        return new HarnessModelCallProjectionV1
        {
            ContractVersion = WorkflowRunDataContract.CurrentVersion,
            ModelCallId = CallIdentity(handle.ExecutionId, stated.CallId),
            AttemptId = Guid.NewGuid(),
            SourceNativeRecordId = record.RecordId,
            SourceKind = SourceKind,
            SourceCorrelationId = Correlation(handle.ExecutionId, stated.CallId),
            CallOrdinal = record.Ordinal + 1,
            Purpose = Purpose,
            TransportKind = TransportKind,
            Model = stated.Model,
            FinishReason = stated.FinishReason,
            InputTokens = stated.InputTokens,
            OutputTokens = stated.OutputTokens,
            CacheReadTokens = stated.CacheReadTokens,
            CacheWriteTokens = stated.CacheWriteTokens,
            CostAmount = cost,
            CostCurrency = cost is null ? null : CostCurrency,
            PricingVersion = cost is null ? null : PricingVersion,
            UnavailableFigures = Unavailable(stated, cost),

            // Partial, and never Exact, however verbatim the bytes were: a row several of whose columns are figures the
            // frame could not supply is not a complete record of the call, and capture_completeness answers "how
            // complete is this row", which fidelity does not.
            Completeness = WorkflowRunCaptureCompleteness.Partial,
            Fidelity = fidelity,
            ObservedAt = record.IngestedAt,
        };
    }

    /// <summary>
    /// Every figure this row could not produce, canonically ordered. Four are structural for a harness-observed call:
    /// the provider's request id is not printed, no timing is stated (which is why <c>first_token_at</c> and
    /// <c>completed_at</c> stay absent rather than repeating the ingest instant and claiming a call of zero duration),
    /// and no per-call reasoning-token figure is reported. The other two are conditional and are the honest half of
    /// this plane: a cache figure the record omitted, and a cost for a model with no price entry.
    /// </summary>
    private static IReadOnlyList<string> Unavailable(GroundedModelCallFrame stated, decimal? cost)
    {
        var figures = new List<string>
        {
            ModelCallFigures.ProviderRequestId, ModelCallFigures.ReasoningTokens,
            ModelCallFigures.FirstTokenAt, ModelCallFigures.CompletedAt,
        };

        if (stated.CacheReadTokens is null) figures.Add(ModelCallFigures.CacheReadTokens);
        if (stated.CacheWriteTokens is null) figures.Add(ModelCallFigures.CacheWriteTokens);
        if (cost is null) figures.Add(ModelCallFigures.CostAmount);

        return ModelCallFigures.Canonical(figures);
    }

    /// <summary>
    /// The projection's admission key. Two frames stating the same response id are the same response — which is what a
    /// re-delivered frame is — so the plane's unique source identity collapses them instead of billing the call twice.
    /// </summary>
    private static Guid Correlation(Guid executionId, string callId) => Derived("source-identity", executionId, callId);

    /// <summary>
    /// The logical call ROW's identity, derived from the same response so the id <see cref="NamedEvent"/> cites is the
    /// id the row already has when the plane skips an admitted call. A separate facet from
    /// <see cref="Correlation"/> rather than the same value reused, because a row's primary key and a producer's
    /// idempotence key are different columns with different owners, and collapsing them would make any future change to
    /// one silently a change to the other.
    /// </summary>
    private static Guid CallIdentity(Guid executionId, string callId) => Derived("model-call", executionId, callId);

    /// <summary>
    /// One identity derived from what the harness stated: a digest of the source kind, the facet being named, the
    /// harness execution and the harness's OWN id for the response. The execution is in the digest because a response
    /// id is the harness's, not this platform's, and two executions must not be able to collide on one.
    /// </summary>
    private static Guid Derived(string facet, Guid executionId, string callId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{SourceKind}|{facet}|{executionId:D}|{callId}")).AsSpan(0, 16));
}
