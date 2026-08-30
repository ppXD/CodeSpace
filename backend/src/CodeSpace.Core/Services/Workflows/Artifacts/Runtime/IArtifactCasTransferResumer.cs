using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Finishes transfers whose worker never came back.
///
/// <para><c>artifact_transfer_intent</c> has five non-terminal states and, until now, nothing that drove them anywhere.
/// A worker that died between its provider PUT and its commit left the intent parked forever and its bytes on the
/// destination with no <c>artifact_location</c> naming them — invisible to the verifier, the placement reader, the
/// abandonment service and the retirement gate, every one of which reads that table. Migration 0131 shipped
/// <c>ix_artifact_transfer_intent_recovery</c> for exactly this sweep.</para>
///
/// <para>A sibling seam rather than a mode of <see cref="IArtifactCasRuntimeCoordinator"/>: a write request carries the
/// bytes and this carries none, so the two cannot answer the same question. What it does share — deliberately and
/// entirely — is the fenced, leased commit: a resumed transfer takes the same claim, the same lease renewals and the
/// same commit as the writer it is finishing, and never writes a location row of its own.</para>
/// </summary>
public interface IArtifactCasTransferResumer : IScopedDependency
{
    /// <summary>Claims a bounded batch of abandoned transfers — expired lease, non-terminal state — and drives each one as far as its destination allows.</summary>
    Task<ArtifactTransferResumeSummary> ResumeAbandonedAsync(int batchSize, CancellationToken cancellationToken);
}

/// <summary>What one pass saw. Counts rather than rows: the per-transfer answer lives on the intent, which is durable and names its own destination.</summary>
public sealed record ArtifactTransferResumeSummary
{
    /// <summary>Abandoned intents this pass looked at.</summary>
    public required int Examined { get; init; }

    /// <summary>Driven to <c>Committed</c>. Their bytes now have a location row, so every reader of that table can finally see them.</summary>
    public required int Committed { get; init; }

    /// <summary>Settled as a terminal typed failure, because the destination gave a definite answer this transfer can never recover from.</summary>
    public required int Settled { get; init; }

    /// <summary>
    /// Of <see cref="Settled"/>, those whose destination still holds the object while no <c>artifact_location</c> names
    /// it. Bytes nothing else in the system can reach; the intent is the durable record of where they are.
    /// </summary>
    public required int Orphaned { get; init; }

    /// <summary>
    /// The destination could not answer. The transfer's saga is left unsettled — only this pass's claim was written —
    /// so it drops behind everything the sweep has not tried yet and a later pass re-asks once that lease lapses,
    /// instead of the transfer being burned on one outage.
    /// </summary>
    public required int Inconclusive { get; init; }

    /// <summary>Another worker holds the claim. Nothing was asked of the destination on its behalf.</summary>
    public required int Contended { get; init; }
}
