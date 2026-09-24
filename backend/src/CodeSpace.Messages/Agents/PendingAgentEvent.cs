namespace CodeSpace.Messages.Agents;

/// <summary>
/// A normalized event on its way to the run's append-only log, carrying the row id its writer minted when it buffered
/// it. The id is what makes an append safe to OFFER TWICE: a writer that retries after a fault the database may have
/// committed before the client heard the acknowledgement re-offers the same ids, and a row that already exists is kept
/// rather than inserted a second time.
/// </summary>
public sealed record PendingAgentEvent(Guid Id, AgentEvent Event);
