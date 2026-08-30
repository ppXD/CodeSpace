using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the one table that says what a read exception MEANS to the storage plane. It used to exist three times —
/// once per reader — and the whole-object read had no copy at all, which is how a rotted offloaded output reached an
/// operator as "your request was malformed".
///
/// <para>Pins both halves of the contract: every lane the plane knows becomes its kind, and an exception the table
/// does not name is NOT claimed. The second half is the one that matters most — a classifier that answered for every
/// exception would launder a null-reference bug into "the storage backend is unavailable", and each caller's
/// <c>when</c> filter relies on the refusal to let such an exception propagate.</para>
/// </summary>
[Trait("Category", "Unit")]
public class ArtifactReadFailureClassifierTests
{
    [Theory]
    [InlineData(typeof(FileNotFoundException), ArtifactContentUnavailableKind.PhysicalObjectMissing)]
    [InlineData(typeof(DirectoryNotFoundException), ArtifactContentUnavailableKind.PhysicalObjectMissing)]
    [InlineData(typeof(UnauthorizedAccessException), ArtifactContentUnavailableKind.AccessDenied)]
    [InlineData(typeof(InvalidDataException), ArtifactContentUnavailableKind.IntegrityFailure)]
    [InlineData(typeof(EndOfStreamException), ArtifactContentUnavailableKind.IntegrityFailure)]
    // A backend REFUSING a locator raises this: LocalFileArtifactBlobBackend.ResolveUnderRoot, for a url whose scheme
    // it cannot serve or that resolves outside the store root. Bug-shaped as the type looks, dropping the arm sends a
    // real refusal back out untyped — and back to 400. A read that knows its own verdict types it at the throw instead.
    [InlineData(typeof(InvalidOperationException), ArtifactContentUnavailableKind.IntegrityFailure)]
    [InlineData(typeof(IOException), ArtifactContentUnavailableKind.BackendUnavailable)]
    public void A_provider_fault_is_classified_as_the_lane_it_belongs_to(Type faultType, ArtifactContentUnavailableKind expected)
    {
        var fault = (Exception)Activator.CreateInstance(faultType)!;

        ArtifactReadFailureClassifier.TryClassify(fault, out var kind).ShouldBeTrue($"{faultType.Name} is a storage-plane fact the plane must be able to name");
        kind.ShouldBe(expected);
    }

    [Fact]
    public void Unparseable_stored_json_is_an_integrity_failure()
    {
        // JsonException does not derive from IOException, so the arm that catches provider IO would never have seen it.
        var fault = Should.Throw<JsonException>(() => JsonDocument.Parse("not-json"));

        ArtifactReadFailureClassifier.TryClassify(fault, out var kind).ShouldBeTrue();
        kind.ShouldBe(ArtifactContentUnavailableKind.IntegrityFailure);
    }

    [Fact]
    public void An_already_classified_failure_keeps_the_kind_it_arrived_with()
    {
        var fault = new ArtifactContentUnavailableException(Guid.NewGuid(), ArtifactContentUnavailableKind.AccessDenied);

        ArtifactReadFailureClassifier.TryClassify(fault, out var kind).ShouldBeTrue();
        kind.ShouldBe(ArtifactContentUnavailableKind.AccessDenied, "a verdict the routed plane already reached is never re-decided");
    }

    [Fact]
    public void A_disposal_bug_is_refused_even_though_it_arrives_as_an_InvalidOperationException()
    {
        // ObjectDisposedException DERIVES from the claimed InvalidOperationException, so silence is not refusal here:
        // without an arm of its own the base type answers first and a stream read after its lease was let go comes
        // back as "the stored copy does not match" — a storage verdict for a fault no storage action can fix.
        var fault = new ObjectDisposedException("ArtifactCasVerifyingReadStream");

        ArtifactReadFailureClassifier.TryClassify(fault, out _).ShouldBeFalse(
            "a disposal bug must reach its caller as itself; laundering it into a storage lane hides the only thing that could be fixed");
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(KeyNotFoundException))]
    // Only the BOUNDED read hands a provider an offset, so every other reader that meets a rejected window met a bug.
    // That arm stays local to ReadRangeCoreAsync rather than becoming a verdict the fail-closed ladders can inherit.
    [InlineData(typeof(ArgumentOutOfRangeException))]
    public void An_exception_the_plane_does_not_own_is_left_alone(Type faultType)
    {
        var fault = (Exception)Activator.CreateInstance(faultType)!;

        ArtifactReadFailureClassifier.TryClassify(fault, out _).ShouldBeFalse(
            $"{faultType.Name} is a bug or a caller leaving, not a storage fact — claiming it would hide it behind a storage excuse");
    }
}
