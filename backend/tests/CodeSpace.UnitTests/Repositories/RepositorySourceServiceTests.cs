using CodeSpace.Core.Services.Repositories;
using Shouldly;

namespace CodeSpace.UnitTests.Repositories;

/// <summary>
/// The required-path guard is the one piece of <see cref="RepositorySourceService"/> that runs BEFORE any
/// dependency (db / registry / scope checker) is touched, so a stub-constructed service exercises it
/// directly. The downstream preflight (repo lookup → credential null-check → scope check → capability
/// dispatch) mirrors PullRequestService.ResolveAsync and is covered by the provider-capability path.
/// </summary>
[Trait("Category", "Unit")]
public class RepositorySourceServiceTests
{
    private static readonly RepositorySourceService Service = new(null!, null!, null!);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetFile_requires_a_non_empty_path(string? path)
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Service.GetFileAsync(Guid.NewGuid(), path!, reference: null, CancellationToken.None));

        ex.Message.ShouldContain("path is required");
    }
}
