namespace CodeSpace.Messages.Failures;

/// <summary>
/// The provider failure a run recorded, with when it was recorded and where the run was working — what a remedy is computed
/// from. <see cref="At"/> lets a reader tell a credential that was fixed AFTER the failure from one that merely looks fine;
/// <see cref="ScopeRepositoryIds"/> lets a failure that names no repository (a provider's 401) be traced to the repositories the
/// run was launched against.
/// </summary>
public sealed record RecordedFailure
{
    public required PersistedFailure Failure { get; init; }

    /// <summary>When the record naming the failure was written — the node's failing, or the agent run's ending.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The node that ended the run and recorded the failure — the one the run's own error names. Null where the run's record itself carried the failure (a run that could not start) or an agent run was the only witness and named no node.</summary>
    public string? NodeId { get; init; }

    /// <summary>The repositories the run was launched against; empty for a run launched with none (an authored workflow).</summary>
    public IReadOnlyList<Guid> ScopeRepositoryIds { get; init; } = Array.Empty<Guid>();
}
