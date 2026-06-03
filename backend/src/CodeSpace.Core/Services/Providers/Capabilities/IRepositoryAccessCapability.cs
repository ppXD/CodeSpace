using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Reports "what REPOSITORY ROLE does THIS credential's user hold on this repo?" — the pre-flight behind
/// the chat-driven act-as-user loop. Checked at chat-respond time so a responder whose role is too low
/// learns it SYNCHRONOUSLY (their click is refused, the card stays open) instead of the write failing later
/// in the background after a false "success".
///
/// The provider only REPORTS the role (mapping its native levels onto the neutral RepositoryRole ladder —
/// GitHub pull/triage/push/maintain/admin, GitLab Guest/Reporter/Developer/Maintainer/Owner). Whether that
/// role is ENOUGH is decided by the gate, generically, against the role the node's declared capability needs
/// (per-provider data on the module) — so different actions can require different roles without touching
/// this method.
///
/// Rule 7 (ISP): a focused capability — a provider implements it only if it can answer the question; the
/// registry resolves it by type, no wiring. An inconclusive probe (transient error) returns Role=null so a
/// blip never blocks a legitimate click — the write path stays the backstop.
/// </summary>
public interface IRepositoryAccessCapability : IProviderCapability
{
    Task<RepositoryActorAccess> GetActorAccessAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken);
}
