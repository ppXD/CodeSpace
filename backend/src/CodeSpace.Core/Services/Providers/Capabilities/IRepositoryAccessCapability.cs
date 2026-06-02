using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Answers "can THIS credential's user make attributable contributions (review / comment) to this
/// repository?" — the pre-flight behind the chat-driven act-as-user loop. Checked at chat-respond
/// time so a responder who lacks repo access learns it SYNCHRONOUSLY (their click is refused, the
/// card stays open) instead of the write failing later in the background after a false "success".
///
/// Provider-defined threshold, so the rule matches what each provider actually enforces:
///   • GitHub — the repo is ACCESSIBLE (read is enough to submit a review there).
///   • GitLab — Developer+ access level (what an MR approval / note needs).
///
/// Rule 7 (ISP): a focused capability — a provider implements it only if it can answer the question;
/// the registry resolves it by type, no wiring. An inconclusive probe (transient error) returns
/// CanContribute=true so a blip never blocks a legitimate click — the write path stays the backstop.
/// </summary>
public interface IRepositoryAccessCapability : IProviderCapability
{
    Task<RepositoryActorAccess> GetActorAccessAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken);
}
