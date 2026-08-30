using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Un-states the expectations of terminal runs whose producers declared more than they ever accounted for and left no
/// gap saying why. It only ever REMOVES a claim: nothing here counts a record, so no run can come out of it reading
/// more complete than it went in.
///
/// <para>No permission marker: dispatched by a recurring job on a processing pod, across every team. Whether a run's
/// record was ever established is not a question a team can ask on its own behalf, and the answer changes no row a
/// user authored — only what the manifest claims about one.</para>
/// </summary>
public sealed record ReconcileRunDataManifestsCommand : ICommand<ReconcileRunDataManifestsResponse>;

public sealed record ReconcileRunDataManifestsResponse
{
    /// <summary>Statements this pass picked up as unattributed shortfalls.</summary>
    public required int Examined { get; init; }

    /// <summary>Statements whose expectation is now indeterminate, and permanently so — a later delta is absorbed rather than folded.</summary>
    public required int Unstated { get; init; }

    /// <summary>Picked up and left alone: an accounting landed in the meantime, or the write was contained and lost. Either way the statement still says what it said.</summary>
    public required int Unchanged { get; init; }
}
