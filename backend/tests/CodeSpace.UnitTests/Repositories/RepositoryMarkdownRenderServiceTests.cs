using CodeSpace.Core.Services.Repositories;
using Shouldly;

namespace CodeSpace.UnitTests.Repositories;

/// <summary>
/// The empty-markdown guard is the one piece of <see cref="RepositoryMarkdownRenderService"/> that runs
/// BEFORE any dependency (db / registry / scope checker) is touched, so a stub-constructed service
/// exercises it directly — empty in, empty HTML out, no provider round-trip. The downstream preflight
/// (repo lookup → credential null-check → scope check → capability dispatch) mirrors
/// PullRequestService.ResolveAsync and is covered by the provider-capability path.
/// </summary>
[Trait("Category", "Unit")]
public class RepositoryMarkdownRenderServiceTests
{
    private static readonly RepositoryMarkdownRenderService Service = new(null!, null!, null!);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RenderMarkdown_returns_empty_html_without_touching_provider(string? markdown)
    {
        var result = await Service.RenderMarkdownAsync(Guid.NewGuid(), markdown!, CancellationToken.None);

        result.Html.ShouldBe(string.Empty);
    }
}
