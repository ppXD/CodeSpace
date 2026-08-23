using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Publish.Exceptions;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class PublishManifestRepositorySelectorTests
{
    [Fact]
    public void Exact_concrete_repository_wins_without_copying_or_rewriting_the_manifest()
    {
        var repositoryId = Guid.NewGuid();
        var exact = Manifest(repositoryId);
        var rows = new[] { Manifest(null), Manifest(Guid.NewGuid()), exact };

        PublishManifestRepositorySelector.Select(rows, repositoryId).ShouldBeSameAs(exact);
    }

    [Fact]
    public void Sole_legacy_null_repository_row_remains_the_byte_identical_fallback()
    {
        var legacy = Manifest(null);

        PublishManifestRepositorySelector.Select(new[] { legacy }, Guid.NewGuid()).ShouldBeSameAs(legacy);
    }

    [Fact]
    public void Sole_concrete_repository_mismatch_is_missing_not_a_fallback()
    {
        PublishManifestRepositorySelector.Select(new[] { Manifest(Guid.NewGuid()) }, Guid.NewGuid()).ShouldBeNull();
    }

    [Fact]
    public void Duplicate_exact_repository_rows_fail_closed_instead_of_selecting_first()
    {
        var repositoryId = Guid.NewGuid();

        var exception = Should.Throw<PublishManifestRepositorySelectionException>(() =>
            PublishManifestRepositorySelector.Select(new[] { Manifest(repositoryId), Manifest(repositoryId) }, repositoryId));

        exception.RepositoryId.ShouldBe(repositoryId);
        exception.MatchCount.ShouldBe(2);
        ((IFailure)exception).Kind.ShouldBe(FailureKind.Internal);
        ((IFailure)exception).Code.ShouldBe(FailureCodes.Internal);
    }

    [Fact]
    public void Multiple_nonmatching_or_legacy_rows_do_not_become_an_authority()
    {
        PublishManifestRepositorySelector.Select(new[] { Manifest(null), Manifest(Guid.NewGuid()) }, Guid.NewGuid()).ShouldBeNull();
    }

    private static PublishManifest Manifest(Guid? repositoryId) => new() { Id = Guid.NewGuid(), RepositoryId = repositoryId };
}
