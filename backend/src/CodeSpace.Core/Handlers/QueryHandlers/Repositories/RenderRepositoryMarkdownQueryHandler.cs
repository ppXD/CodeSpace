using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class RenderRepositoryMarkdownQueryHandler : IRequestHandler<RenderRepositoryMarkdownQuery, RemoteRenderedMarkdown>
{
    private readonly IRepositoryMarkdownRenderService _service;

    public RenderRepositoryMarkdownQueryHandler(IRepositoryMarkdownRenderService service) { _service = service; }

    public async Task<RemoteRenderedMarkdown> Handle(RenderRepositoryMarkdownQuery request, CancellationToken cancellationToken) =>
        await _service.RenderMarkdownAsync(request.RepositoryId, request.Markdown, cancellationToken).ConfigureAwait(false);
}
