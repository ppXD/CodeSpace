using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// One model call a harness made INSIDE itself, shaped for the model-call plane 0124/0130 already defines: a LOGICAL
/// call plus the one PHYSICAL attempt of it the harness stated. Same tables as a workflow LLM node's calls, same split,
/// so "which model did what, at what cost" has one answer shape for both.
///
/// <para><b>Why one attempt and never two.</b> A harness's own record describes a response it received. A retry the CLI
/// performed internally and did not print is not in the stream, so this plane records one attempt per stated response
/// and never synthesises a second — it cannot observe a retry, and inventing one would answer "did it retry" with
/// fiction. Two responses the CLI did print are two logical calls, each with its own attempt, because nothing in the
/// frames says they were tries of the same request.</para>
///
/// <para><b>Idempotence is carried, not hoped for.</b> BOTH <see cref="SourceCorrelationId"/> and
/// <see cref="ModelCallId"/> are derived from the harness's own identity for the response, so re-projecting the same
/// frames yields the same key AND the same row id: <c>ux_workflow_run_model_call_source_identity</c> admits the call
/// once, the writer skips a call it already holds, and the semantic event beside a skipped re-projection still cites a
/// row that exists. A freshly minted row id would make that event name a row the skip decided not to write, which is
/// worse than naming none — a reader joins on it and reads the miss as a data gap.</para>
///
/// <para><b>What it deliberately leaves NULL.</b> The requested route (<c>requested_provider</c> /
/// <c>requested_model</c> / <c>selection_policy</c>) — a response record states what was SERVED, never what was asked
/// for. The execution-identity triple — the semantic event projected beside this call carries the harness execution,
/// which is the join that is actually grounded. And every figure named in <see cref="UnavailableFigures"/>.</para>
/// </summary>
public sealed record HarnessModelCallProjectionV1
{
    public required int ContractVersion { get; init; }

    /// <summary>Identity of the logical call row, DERIVED from the response the frame states — not minted fresh — so the semantic event projected from the same frame cites an id the writer will hold a row for even when it skips the insert.</summary>
    public required Guid ModelCallId { get; init; }

    /// <summary>Identity of the one physical attempt row.</summary>
    public required Guid AttemptId { get; init; }

    /// <summary>The captured frame this projection was read out of — the row's only evidence, and what the semantic event cites.</summary>
    public required Guid SourceNativeRecordId { get; init; }

    /// <summary>Open source-kind name written to <c>source_kind</c> / <c>capture_source</c>, so a reader can tell a harness-observed row from an in-process one without inferring it.</summary>
    public required string SourceKind { get; init; }

    /// <summary>The idempotent projection key: derived from the harness execution and the harness's OWN id for this response, never from a delivery position that a re-delivered frame would change.</summary>
    public required Guid SourceCorrelationId { get; init; }

    /// <summary>
    /// A stable within-run display ordinal, one-based: the captured frame's position in its capture stream, plus one.
    /// It is NOT a global sequence and NOT a count of the calls a run made — a stream restarts at zero for each
    /// physical process of an execution, so two processes' calls can share an ordinal. Ordering a run's calls is
    /// <c>ix_workflow_run_model_call_run_created</c>'s job.
    /// </summary>
    public required long CallOrdinal { get; init; }

    public required string Purpose { get; init; }

    /// <summary>The transport the call actually went over: the harness's own client, which this platform never saw the wire of.</summary>
    public required string TransportKind { get; init; }

    /// <summary>The model that served the response, as the harness named it.</summary>
    public required string Model { get; init; }

    public string? FinishReason { get; init; }

    public required long InputTokens { get; init; }

    public required long OutputTokens { get; init; }

    public long? CacheReadTokens { get; init; }

    public long? CacheWriteTokens { get; init; }

    /// <summary>The priced cost, or null when this deployment has no price for <see cref="Model"/> — in which case <see cref="UnavailableFigures"/> names it. Never zero for an unpriced model.</summary>
    public decimal? CostAmount { get; init; }

    public string? CostCurrency { get; init; }

    /// <summary>What priced this row, present exactly when <see cref="CostAmount"/> is.</summary>
    public string? PricingVersion { get; init; }

    /// <summary>Every figure this projection could not produce, from <see cref="ModelCallFigures"/>. Each one's column is left NULL rather than zeroed.</summary>
    public required IReadOnlyList<string> UnavailableFigures { get; init; }

    /// <summary>How complete the ROW is — which is not how faithful it is (<see cref="Fidelity"/>): a row read verbatim out of a frame is still <see cref="WorkflowRunCaptureCompleteness.Partial"/> while any of its columns is a figure the frame could not supply.</summary>
    public required WorkflowRunCaptureCompleteness Completeness { get; init; }

    /// <summary>How faithfully the figures reproduce the frame's captured bytes — the claim the semantic event beside this call carries, and the one the database checks against that frame's redaction.</summary>
    public required SemanticProjectionQuality Fidelity { get; init; }

    /// <summary>
    /// When the platform INGESTED the frame this row was read from — written to <c>started_at</c> because that column
    /// admits no absence, and it is the only instant here that was actually observed. It is NOT when the provider was
    /// dispatched: the record describes a response that had already completed, so the true start is earlier by the
    /// call's own unmeasured duration. <c>first_token_at</c> and <c>completed_at</c> stay NULL and are declared
    /// unavailable rather than filled with this same instant, which would claim a call of zero duration.
    /// </summary>
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>Every reason this projection cannot be written. Empty ⇒ writable. It mirrors the plane's own CHECK constraints on purpose: a row the database would refuse is caught where the capture pump contains it, rather than at commit where it would take the whole batch of frames down as an opaque constraint violation.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!WorkflowRunDataContract.IsSupported(ContractVersion)) errors.Add($"contractVersion '{ContractVersion}' is unsupported");
        if (ModelCallId == Guid.Empty || AttemptId == Guid.Empty) errors.Add("modelCallId and attemptId must be non-empty");
        if (SourceNativeRecordId == Guid.Empty) errors.Add("sourceNativeRecordId must be non-empty — a projected call with no frame behind it is a claim about nothing");
        if (SourceCorrelationId == Guid.Empty) errors.Add("sourceCorrelationId must be non-empty, or the projection has no idempotent identity");
        if (CallOrdinal <= 0) errors.Add("callOrdinal must be one-based");

        errors.AddRange(NamingErrors());
        errors.AddRange(FigureErrors());

        return errors;
    }

    private IEnumerable<string> NamingErrors()
    {
        if (string.IsNullOrWhiteSpace(SourceKind)) yield return "sourceKind must be stated";
        if (string.IsNullOrWhiteSpace(Purpose)) yield return "purpose must be stated";
        if (string.IsNullOrWhiteSpace(TransportKind)) yield return "transportKind must be stated";
        if (string.IsNullOrWhiteSpace(Model)) yield return "model must be stated — a call whose model is unknown is exactly the row this plane exists to stop being the only one available";
        if (!Enum.IsDefined(Completeness)) yield return $"completeness '{Completeness}' is unsupported";
        if (!Enum.IsDefined(Fidelity)) yield return $"fidelity '{Fidelity}' is unsupported";
    }

    private IEnumerable<string> FigureErrors()
    {
        if (InputTokens < 0 || OutputTokens < 0) yield return "token counts must be non-negative";
        if (CacheReadTokens < 0 || CacheWriteTokens < 0) yield return "cache token counts must be non-negative";

        // Both halves, because either alone is unreadable: an amount without a currency cannot be summed, and a
        // currency without an amount claims a cost was priced when none was.
        if ((CostAmount is null) != (CostCurrency is null) || (CostAmount is null) != (PricingVersion is null))
            yield return "cost amount, currency and pricing version are present together or not at all";
        if (CostAmount < 0) yield return "cost must be non-negative";

        foreach (var figure in UnavailableFigures.Where(figure => !ModelCallFigures.IsSupported(figure)))
            yield return $"unavailableFigures names '{figure}', which is not a figure this plane can declare";

        foreach (var figure in UnavailableFigures.Where(Stored))
            yield return $"unavailableFigures names '{figure}' while the row stores a value for it";
    }

    /// <summary>
    /// Whether this projection actually carries the figure it declares unavailable — the one contradiction that would
    /// make the declaration worse than saying nothing. Only the three figures this projection HAS a field for can be
    /// contradicted here; the rest are columns it never populates, and the row's own CHECK is what holds them, which is
    /// where those columns exist to be contradicted at all.
    /// </summary>
    private bool Stored(string figure) => figure switch
    {
        ModelCallFigures.CacheReadTokens => CacheReadTokens is not null,
        ModelCallFigures.CacheWriteTokens => CacheWriteTokens is not null,
        ModelCallFigures.CostAmount => CostAmount is not null,
        _ => false,
    };
}
