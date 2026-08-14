namespace CodeSpace.Messages.Dtos.Repositories;

/// <summary>
/// One repository's recent refused deliveries, newest first, and the ceiling on how many of them
/// this answer can carry.
///
/// <para><see cref="Cap"/> travels with the rows because the reader has to be told. A provider that
/// cannot reach us retries on a ladder and an unreachable instance produces thousands of refusals in
/// an afternoon; a list that silently stops at fifty would read as "fifty happened". The number is
/// the server's, so the sentence on the page cannot drift from the query that produced it.</para>
/// </summary>
public sealed record RepositoryRejectedDeliveries
{
    /// <summary>Newest first — the most recent refusal is the one being asked about.</summary>
    public required IReadOnlyList<RejectedDelivery> Deliveries { get; init; }

    /// <summary>The most rows this answer will ever carry. A full list cannot be told apart from exactly-the-cap — the read asks for the cap and gets no count beyond it, so the UI says the list stops there rather than claiming more exist.</summary>
    public required int Cap { get; init; }
}
