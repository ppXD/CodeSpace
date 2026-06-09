using CodeSpace.Core.Services.Providers.Source;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Source;

/// <summary>
/// Normalization of each provider's raw tree-entry-type token into the cross-provider enum. Pins both
/// vocabularies (GitHub file/dir/submodule/symlink, GitLab blob/tree/commit) and the safe File fallback.
/// </summary>
[Trait("Category", "Unit")]
public class RepositoryTreeEntryTypeMapTests
{
    [Theory]
    [InlineData("file", RemoteTreeEntryType.File)]
    [InlineData("blob", RemoteTreeEntryType.File)]
    [InlineData("dir", RemoteTreeEntryType.Directory)]
    [InlineData("tree", RemoteTreeEntryType.Directory)]
    [InlineData("submodule", RemoteTreeEntryType.Submodule)]
    [InlineData("commit", RemoteTreeEntryType.Submodule)]
    [InlineData("symlink", RemoteTreeEntryType.Symlink)]
    public void Maps_known_provider_tokens(string raw, RemoteTreeEntryType expected) =>
        RepositoryTreeEntryTypeMap.From(raw).ShouldBe(expected);

    [Theory]
    [InlineData("Tree", RemoteTreeEntryType.Directory)]
    [InlineData("BLOB", RemoteTreeEntryType.File)]
    [InlineData("  dir  ", RemoteTreeEntryType.Directory)]
    [InlineData("Submodule", RemoteTreeEntryType.Submodule)]
    public void Is_case_and_whitespace_insensitive(string raw, RemoteTreeEntryType expected) =>
        RepositoryTreeEntryTypeMap.From(raw).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mystery")]
    public void Unknown_or_null_falls_back_to_file(string? raw) =>
        RepositoryTreeEntryTypeMap.From(raw).ShouldBe(RemoteTreeEntryType.File);
}
