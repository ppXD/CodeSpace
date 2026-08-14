using CodeSpace.Core.Settings.Logging;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

/// <summary>
/// The value every log line carries as <c>Build</c>, and the one the boot banner prints.
///
/// <para>It exists to answer the first question of any deployment incident — is this pod running the
/// code I think it is — so the only failure that matters is it being unable to distinguish two
/// builds. A bare <c>1.0.0</c> does exactly that, and is what a deployed image reported until the
/// Dockerfiles began passing <c>SourceRevisionId</c>.</para>
/// </summary>
[Trait("Category", "Unit")]
public class BuildIdentityTests
{
    [Fact]
    public void It_reports_something()
    {
        BuildIdentity.Value.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Under <c>dotnet test</c> the SDK stamps the revision from the local checkout, so the sha is
    /// present here exactly as it must be in a published image. This fails if the SDK ever stops
    /// appending it — the silent version of the deployed <c>1.0.0</c>.
    /// </summary>
    [Fact]
    public void It_carries_the_source_revision()
    {
        BuildIdentity.Value.ShouldContain("+",
            customMessage: $"Build identity is '{BuildIdentity.Value}' — a version with no commit sha cannot tell two " +
                           "deployments apart, which is the only thing it is for. Publishes must pass " +
                           "-p:SourceRevisionId (see backend/Dockerfile.api).");

        BuildIdentity.Value.Split('+')[^1].Length.ShouldBeGreaterThanOrEqualTo(7,
            customMessage: "The suffix after '+' must be a commit sha long enough to identify a commit.");
    }
}
