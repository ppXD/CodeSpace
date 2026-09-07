using CodeSpace.Core.Services.Agents.Workspace;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>Real local filesystem continuity observations. These checks do not grant filesystem access or pin a later path-based process launch.</summary>
[Trait("Category", "Integration")]
public sealed class LocalAcceptanceWorkspaceFlowTests
{
    [Fact]
    public async Task Ordinary_candidate_edits_preserve_directory_identity_and_declared_oracle_bytes()
    {
        using var world = new World();
        await File.WriteAllTextAsync(Path.Combine(world.Path, "verify.sh"), "exit 7\n");
        using var observed = await LocalAcceptanceWorkspace.CaptureAsync(world.Path, ["verify.sh"], CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(world.Path, "candidate.txt"), "the candidate may change");
        (await observed.CheckAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Replacing_the_directory_with_identical_bytes_and_timestamps_is_detected()
    {
        using var world = new World();
        using var observed = await LocalAcceptanceWorkspace.CaptureAsync(world.Path, [], CancellationToken.None);
        var timestamp = Directory.GetLastWriteTimeUtc(world.Path);
        Directory.Move(world.Path, world.Path + "-old");
        Directory.CreateDirectory(world.Path);
        Directory.SetLastWriteTimeUtc(world.Path, timestamp);
        (await observed.CheckAsync(CancellationToken.None)).ShouldBe("workspace-continuity-lost");
    }

    [Fact]
    public async Task A_same_length_oracle_rewrite_with_preserved_mtime_is_rejected_without_restoring_it()
    {
        using var world = new World();
        var path = Path.Combine(world.Path, "verify.sh");
        await File.WriteAllTextAsync(path, "exit 7\n");
        var timestamp = File.GetLastWriteTimeUtc(path);
        using var observed = await LocalAcceptanceWorkspace.CaptureAsync(world.Path, ["verify.sh"], CancellationToken.None);
        await File.WriteAllTextAsync(path, "exit 0\n");
        File.SetLastWriteTimeUtc(path, timestamp);
        (await observed.CheckAsync(CancellationToken.None)).ShouldBe("oracle-integrity-changed");
        (await File.ReadAllTextAsync(path)).ShouldBe("exit 0\n", "the verifier must not rewrite the judge to buy a subsequent pass");
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(".")]
    [InlineData("missing.txt")]
    [InlineData(" ")]
    public async Task An_invalid_or_missing_literal_oracle_path_cannot_be_snapshotted(string relativePath)
    {
        using var world = new World();
        await Should.ThrowAsync<IOException>(() => LocalAcceptanceWorkspace.CaptureAsync(world.Path, [relativePath], CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oracle_symlinks_are_rejected_in_leaf_and_intermediate_components(bool intermediate)
    {
        using var world = new World();
        var outside = world.Path + "-outside";
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "judge"), "exit 0");
        if (intermediate) Directory.CreateSymbolicLink(Path.Combine(world.Path, "link"), outside);
        else File.CreateSymbolicLink(Path.Combine(world.Path, "link"), Path.Combine(outside, "judge"));
        await Should.ThrowAsync<IOException>(() => LocalAcceptanceWorkspace.CaptureAsync(world.Path, [intermediate ? "link/judge" : "link"], CancellationToken.None));
    }

    [Fact]
    public async Task Directory_deletion_disposal_and_cancellation_never_return_a_successful_observation()
    {
        using var world = new World();
        using var observed = await LocalAcceptanceWorkspace.CaptureAsync(world.Path, [], CancellationToken.None);
        Directory.Delete(world.Path);
        (await observed.CheckAsync(CancellationToken.None)).ShouldBe("workspace-continuity-lost");
        observed.Dispose();
        (await observed.CheckAsync(CancellationToken.None)).ShouldBe("workspace-context-disposed");
        await Should.ThrowAsync<OperationCanceledException>(() => observed.CheckAsync(new CancellationToken(true)));
    }

    private sealed class World : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-local-acceptance-" + Guid.NewGuid().ToString("N"));
        public World() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            foreach (var path in new[] { Path, Path + "-old", Path + "-outside" }) if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
