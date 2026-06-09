using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Render markdown (a README / .md file) to HTML using the repo's provider renderer, in the repo's
/// context. The SPA falls back to client-side rendering on any failure (incl. providers without a
/// render capability). Membership enforced via <see cref="IRequireRepositoryAccess"/>.
/// </summary>
public sealed record RenderRepositoryMarkdownQuery : IQuery<RemoteRenderedMarkdown>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
    public required string Markdown { get; init; }
}
