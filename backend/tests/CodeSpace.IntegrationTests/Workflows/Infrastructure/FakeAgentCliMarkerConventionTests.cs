using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The teeth behind <see cref="FakeAgentCliMarker"/>. The real-CLI gates now self-skip when the command env var
/// points at a path carrying the fake markers — which is only sound while EVERY fake actually carries them. A new
/// fake that names its script something else would be invisible to those gates, and the hazard would silently
/// reopen: a real-CLI test would resolve the stub, run it, and assert real-binary semantics against it.
///
/// <para>Scanned from SOURCE rather than by constructing the fakes: every one of them arms a PROCESS-WIDE env var in
/// its constructor, so instantiating them here would race whatever else is running — the exact hazard this file
/// exists to close. Two of them also take constructor arguments.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class FakeAgentCliMarkerConventionTests
{
    [Fact]
    public void Every_fake_cli_carries_the_markers_the_real_cli_gates_skip_on()
    {
        var sources = FakeCliSources();

        sources.Count.ShouldBeGreaterThan(10, "the scan must actually find the fakes, or this test passes by finding nothing");

        var offenders = sources
            .Where(f => !File.ReadAllText(f.FullName).Contains($"\"{FakeAgentCliMarker.ScriptNamePrefix}", StringComparison.Ordinal)
                     || !File.ReadAllText(f.FullName).Contains(FakeAgentCliMarker.DirectoryMarker, StringComparison.Ordinal))
            .Select(f => f.Name)
            .ToList();

        offenders.ShouldBeEmpty(
            $"every fake must write a script named '{FakeAgentCliMarker.ScriptNamePrefix}…' inside a temp dir carrying '{FakeAgentCliMarker.DirectoryMarker}', "
          + "or the real-CLI gates cannot tell it from a real binary and will run it while asserting real-binary semantics");
    }

    [Theory]
    [InlineData("/tmp/cs-dephandoff-fakecli-abc123/fake-agent.sh", true)]
    [InlineData("/tmp/cs-filewriting-fakecli-def456/fake-codex.sh", true)]
    [InlineData("/usr/local/bin/claude", false)]
    [InlineData("/opt/homebrew/bin/codex", false)]
    [InlineData("/Users/someone/.local/bin/claude", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void A_real_binary_path_is_never_mistaken_for_a_fake(string? path, bool expectFake)
    {
        FakeAgentCliMarker.IsFakeCli(path).ShouldBe(expectFake,
            "a false positive here would make a real-CLI test skip forever, which is a silent loss of coverage — the opposite failure of the one this closes");
    }

    private static IReadOnlyList<FileInfo> FakeCliSources()
    {
        var root = FindRepoRoot();

        return new[] { "backend/tests/CodeSpace.IntegrationTests/Workflows/Infrastructure", "backend/tests/CodeSpace.E2ETests/Infrastructure" }
            .Select(rel => new DirectoryInfo(Path.Combine(root, rel)))
            .Where(d => d.Exists)
            .SelectMany(d => d.GetFiles("*FakeCli.cs"))
            .ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
