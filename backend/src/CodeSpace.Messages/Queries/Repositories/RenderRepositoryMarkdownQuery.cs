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
    /// <summary>Set by the controller from the route segment via `query with { RepositoryId = ... }`. Non-required so System.Text.Json doesn't 400-fail when the POST body omits it (the URL is authoritative).</summary>
    public Guid RepositoryId { get; init; }

    public required string Markdown { get; init; }
}
