using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Backends;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

[Trait("Category", "Unit")]
public sealed class ArtifactStreamingWriteContractTests
{
    [Fact]
    public void Streaming_write_is_an_additive_capability_not_a_breaking_blob_backend_widening()
    {
        typeof(IArtifactBlobBackend).GetMethods().Select(method => method.Name).ShouldNotContain(nameof(IArtifactBlobStreamWriter.WriteStreamAsync));
        typeof(IArtifactBlobStreamWriter).IsAssignableFrom(typeof(LocalFileArtifactBlobBackend)).ShouldBeTrue();
    }

    [Fact]
    public void Streaming_placement_has_no_hidden_whole_payload_adapter()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "backend", "src", "CodeSpace.Core", "Services", "Workflows", "Artifacts", "ArtifactStore.Streaming.cs"));

        source.ShouldNotContain("MemoryStream", Case.Sensitive, "a routed retry must reopen its source, not retain a payload-sized memory stream");
        source.ShouldNotContain(".ToArray(", Case.Sensitive, "neither local nor routed placement may secretly reconstruct a whole byte array");
        source.ShouldNotContain("ReadAll", Case.Sensitive, "the streaming face may not delegate to a whole-object read helper");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "backend", "src", "CodeSpace.Core"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root from test output.");
    }
}
