using CodeSpace.Core.Services.Workflows.Artifacts;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class ArtifactRangeReadResultTests
{
    [Fact]
    public void Available_requires_bytes_and_identity_metadata()
    {
        Should.Throw<ArgumentNullException>(() => ArtifactRangeReadResult.Available(null!, 1, new string('a', 64), "text/plain", false));
        Should.Throw<ArgumentException>(() => ArtifactRangeReadResult.Available(new byte[] { 1 }, 1, "", "text/plain", false));
    }

    [Fact]
    public void Failure_cannot_forge_an_available_state()
    {
        Should.Throw<ArgumentException>(() => ArtifactRangeReadResult.Failed(ArtifactRangeReadState.Available));
        ArtifactRangeReadResult.Failed(ArtifactRangeReadState.PhysicalObjectMissing).Bytes.ShouldBeNull();
    }
}
