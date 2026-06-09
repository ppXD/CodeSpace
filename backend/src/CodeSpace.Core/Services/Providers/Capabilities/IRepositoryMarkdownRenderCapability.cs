using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Renders markdown to HTML using the provider's OWN renderer (GitHub <c>POST /markdown</c>, GitLab
/// <c>POST /api/v4/markdown</c>), in the repository's context so @mentions, #issue links, and relative
/// references resolve the way they do on the provider's site — and so emoji / alerts / future markdown
/// features are covered automatically.
///
/// Optional capability: a generic-Git provider has no such endpoint, so consumers fall back to
/// client-side markdown rendering when a provider doesn't implement this. Scope: the repo-read family.
/// </summary>
public interface IRepositoryMarkdownRenderCapability : IProviderCapability
{
    Task<RemoteRenderedMarkdown> RenderMarkdownAsync(ProviderContext context, RemoteRepository repository, string markdown, CancellationToken cancellationToken);
}
