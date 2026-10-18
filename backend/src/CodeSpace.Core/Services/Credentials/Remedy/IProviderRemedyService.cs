using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Dtos.Repositories;

namespace CodeSpace.Core.Services.Credentials.Remedy;

/// <summary>
/// Answers "what is wrong with this provider connection, and what can I do about it" for the person asking: from the failure a
/// run recorded, or from the trigger refusals that blame a repository's credential. The answer is read fresh each time — it
/// names what is true now (a credential reconnected since reads as resolved) and what THIS caller may do (an Owner, a Member and
/// a Viewer get different buttons for the same failure) — which is why it is never cached with the page that shows it.
/// </summary>
public interface IProviderRemedyService
{
    /// <summary>The remedy for the provider failure a run recorded, or null when it recorded none (or is not the team's). Team-scoped.</summary>
    Task<ProviderRemedy?> GetForRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// The remedy for the deliveries a repository's trigger refused because of its credential — <c>credential_rejected</c> and
    /// <c>author_standing_unknown</c> — or null when none of <paramref name="deliveries"/> was. One remedy serves them all: it is about the
    /// credential the repository runs through NOW, so it is built from the newest such refusal, and a credential validated since that
    /// refusal reads as fixed.
    /// </summary>
    Task<ProviderRemedy?> GetForRefusedDeliveriesAsync(Guid repositoryId, IReadOnlyList<RejectedDelivery> deliveries, Guid teamId, CancellationToken cancellationToken);
}
