using System.Text.RegularExpressions;
using CodeSpace.Core.Settings;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

/// <summary>
/// Where the durable roots land when nobody configured them.
///
/// <para>They used to land under the system temp directory, so <see cref="DurableRootsGuard"/> made configuring
/// both a condition of starting in Production — and a real deployment lost its boot to that. The images already
/// answer the question: both Dockerfiles create and chown <c>/var/lib/codespace/{artifacts,spool}</c> before
/// dropping privileges, so the deployment was being asked to restate a decision that had already been taken.</para>
/// </summary>
[Trait("Category", "Unit")]
public class DurableRootsTests
{
    [Fact]
    public void A_configured_root_always_wins()
    {
        DurableRoots.ArtifactStore("/mnt/volume/artifacts").ShouldBe(Path.GetFullPath("/mnt/volume/artifacts"));
        DurableRoots.AgentRunSpool("/mnt/volume/spool").ShouldBe(Path.GetFullPath("/mnt/volume/spool"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_configured_still_gives_a_usable_root(string? configured)
    {
        DurableRoots.ArtifactStore(configured).ShouldNotBeNullOrWhiteSpace();
        DurableRoots.AgentRunSpool(configured).ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>The whole point: temp is where this data must never end up, and it is what the old default was.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_default_ever_lands_under_the_temp_directory(string? configured)
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;

        DurableRoots.ArtifactStore(configured).ShouldNotStartWith(temp);
        DurableRoots.AgentRunSpool(configured).ShouldNotStartWith(temp);
    }

    [Fact]
    public void The_two_roots_do_not_share_a_directory()
    {
        // They have different lifetimes — a spool is swept when its run ends, a blob outlives everything that
        // references it — so sharing a directory would make one's cleanup the other's data loss.
        DurableRoots.ArtifactStore(null).ShouldNotBe(DurableRoots.AgentRunSpool(null));
    }

    /// <summary>
    /// The constants exist so the image and the process agree on one path. If the <c>mkdir</c> in either
    /// Dockerfile moves without this moving too, the container writes somewhere it was never given rights to and
    /// finds out at the first artifact.
    /// </summary>
    [Theory]
    [InlineData("Dockerfile.api")]
    [InlineData("Dockerfile.worker")]
    public void The_container_paths_are_the_ones_the_image_prepares(string dockerfile)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "backend", dockerfile));

        var mkdir = Regex.Match(text, @"RUN mkdir -p (?<paths>[^\n&]+)");

        mkdir.Success.ShouldBeTrue($"{dockerfile} no longer prepares the durable roots — either it moved, or the image stopped creating them and the process will write somewhere it does not own");

        var prepared = mkdir.Groups["paths"].Value;

        prepared.ShouldContain(DurableRoots.ContainerArtifactStore, customMessage: $"{dockerfile} prepares '{prepared.Trim()}', which does not include DurableRoots.ContainerArtifactStore");
        prepared.ShouldContain(DurableRoots.ContainerAgentRunSpool, customMessage: $"{dockerfile} prepares '{prepared.Trim()}', which does not include DurableRoots.ContainerAgentRunSpool");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
