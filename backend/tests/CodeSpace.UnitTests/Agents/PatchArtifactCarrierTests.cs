using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>The pre-completion patch carrier contract: a full artifact ref wins over its bounded inline copy.</summary>
[Trait("Category", "Unit")]
public sealed class PatchArtifactCarrierTests
{
    private static readonly Guid TeamId = Guid.NewGuid();

    [Fact]
    public async Task An_artifact_reference_is_authoritative_over_a_bounded_inline_copy()
    {
        var artifactId = Guid.NewGuid();
        var offloader = new FakeOffloader((inline, id) =>
        {
            inline.ShouldBeNullOrEmpty("the compatibility copy must not mask the full artifact");
            id.ShouldBe(artifactId);
            return "full-patch-with-tail";
        });

        var patch = await offloader.ResolvePatchRequiredAsync(TeamId, "bounded-copy", artifactId, CancellationToken.None);

        patch.ShouldBe("full-patch-with-tail");
        offloader.ReadCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, ArtifactContentUnavailableKind.MetadataMissing)]
    [InlineData(true, ArtifactContentUnavailableKind.IntegrityFailure)]
    public async Task An_unavailable_artifact_fails_closed_without_inline_fallback(bool corrupt, ArtifactContentUnavailableKind expectedKind)
    {
        var artifactId = Guid.NewGuid();
        var offloader = new FakeOffloader((inline, _) =>
        {
            inline.ShouldBeNullOrEmpty("missing, foreign, purged, or corrupt bytes must not revive the bounded copy");
            if (corrupt) throw new InvalidOperationException("checksum mismatch");
            return "";
        });

        var exception = await Should.ThrowAsync<ArtifactContentUnavailableException>(() =>
            offloader.ResolvePatchRequiredAsync(TeamId, "bounded-copy", artifactId, CancellationToken.None));

        exception.ArtifactId.ShouldBe(artifactId);
        exception.Kind.ShouldBe(expectedKind);
        offloader.ReadCount.ShouldBe(1);
    }

    [Fact]
    public async Task No_artifact_reference_preserves_inline_bytes_without_storage_io()
    {
        const string inline = "diff --git a/x b/x\r\n+byte-identical\r\n";
        var offloader = new FakeOffloader((_, _) => throw new InvalidOperationException("storage must not be read"));

        var patch = await offloader.ResolvePatchRequiredAsync(TeamId, inline, null, CancellationToken.None);

        patch.ShouldBe(inline);
        offloader.ReadCount.ShouldBe(0);
    }

    private sealed class FakeOffloader(Func<string?, Guid?, string> resolve) : IArtifactOffloader
    {
        public int ReadCount { get; private set; }

        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken cancellationToken)
        {
            teamId.ShouldBe(TeamId);
            ReadCount++;
            return Task.FromResult(resolve(inline, artifactId));
        }

        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the read contract never writes");
    }
}
