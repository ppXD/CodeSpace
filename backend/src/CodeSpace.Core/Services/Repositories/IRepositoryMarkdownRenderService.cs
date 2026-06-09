using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Repositories;

/// <summary>
/// Renders markdown (a README, a .md file) to HTML via the repo's provider renderer, in the repo's
/// context. Throws NotSupportedException when the provider has no markdown-render capability — the Code
/// tab treats that (and any error) as "fall back to client-side rendering".
/// </summary>
public interface IRepositoryMarkdownRenderService
{
    Task<RemoteRenderedMarkdown> RenderMarkdownAsync(Guid repositoryId, string markdown, CancellationToken cancellationToken);
}
