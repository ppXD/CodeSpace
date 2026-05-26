using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Outbox;

/// <summary>
/// Reset outbox rows whose worker crashed mid-process so they're dispatchable again.
/// Dispatched by the recurring job; the handler delegates to
/// <c>IOutboxLeaseReaper.ReapAsync</c>.
///
/// <para>NOT tenant-scoped — system-wide cleanup operation.</para>
/// </summary>
public sealed record ReapOutboxLeasesCommand : ICommand<ReapOutboxLeasesResponse>;

public sealed record ReapOutboxLeasesResponse
{
    /// <summary>How many Claimed rows were reset to Pending this sweep.</summary>
    public required int Reaped { get; init; }
}
